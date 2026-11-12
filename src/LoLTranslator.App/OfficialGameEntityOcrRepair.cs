using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// Conservative OCR repair for official League entities only. It never tries
/// to infer a player name: a repair is allowed only in a champion slot in the
/// chat prefix/official target template, or in a recognised item mention.
/// Ambiguous and ordinary-language candidates are left untouched.
/// </summary>
public sealed class OfficialGameEntityOcrRepair
{
    private static readonly Regex PrefixChampion = new(
        @"(?<head>[\(（])(?<champion>[A-Za-z][A-Za-z'’\-]{2,31})(?<tail>[\)）])(?=\s*[:：]?\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TargetChampion = new(
        @"(?<head>\b(?:has\s+(?:targeted|shut\s+down|slain)|slain\s+by)\s+[\p{L}\p{N}_.'’\- ]{1,72}\s*[-–—]?\s*\()(?<champion>[A-Za-z][A-Za-z'’\-]{2,31})(?<tail>\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ItemPing = new(
        @"^\s*(?<item>[A-Za-z][A-Za-z'’\-]*(?:\s+[A-Za-z][A-Za-z'’\-]*){0,3})\s*[-—–]\s*(?=.*\b(?:gold|buy|purchase|needed|required)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ItemAction = new(
        @"\b(?:purchased|bought|buying|buy)\s+(?:an?\s+)?(?<item>[A-Za-z][A-Za-z'’\-]*(?:\s+[A-Za-z][A-Za-z'’\-]*){0,3})(?=$|[.!?,;]|\s+(?:for|at|with)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record OfficialSurface(string Kind, string Text, string Normalized);

    private readonly IReadOnlyList<OfficialSurface> _champions;
    private readonly IReadOnlyList<OfficialSurface> _items;

    private OfficialGameEntityOcrRepair(IReadOnlyList<OfficialSurface> champions, IReadOnlyList<OfficialSurface> items)
    {
        _champions = champions;
        _items = items;
    }

    public static OfficialGameEntityOcrRepair Load(string championLexiconPath, string itemLexiconPath)
    {
        DomainLexicon champions = DomainLexiconLoader.LoadJsonl(championLexiconPath);
        DomainLexicon items = DomainLexiconLoader.LoadJsonl(itemLexiconPath);
        return new OfficialGameEntityOcrRepair(
            Collect(champions, "champion"),
            Collect(items, "item"));
    }

    public ChatMessageEnvelope Repair(ChatMessageEnvelope envelope)
    {
        string prefix = RepairPrefix(envelope.Prefix);
        string body = RepairBody(envelope.Body);
        string? identity = string.IsNullOrWhiteSpace(envelope.PlayerOrRole)
            ? envelope.PlayerOrRole
            : PrefixChampion.Replace(envelope.PlayerOrRole, match => ReplaceCapture(match, "champion", _champions));
        return envelope with { Prefix = prefix, Body = body, PlayerOrRole = identity };
    }

    private string RepairPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return prefix ?? string.Empty;
        return PrefixChampion.Replace(prefix, match => ReplaceCapture(match, "champion", _champions));
    }

    private string RepairBody(string body)
    {
        // The official target template is a stronger structural anchor than a
        // free text mention, so it may repair up to two OCR edits when the
        // nearest official champion is still unique.
        string repaired = TargetChampion.Replace(body, match => ReplaceCapture(match, "champion", _champions, 2));
        repaired = ItemPing.Replace(repaired, match => ReplaceCapture(match, "item", _items));
        return ItemAction.Replace(repaired, match => ReplaceCapture(match, "item", _items));
    }

    private static string ReplaceCapture(
        Match match,
        string groupName,
        IReadOnlyList<OfficialSurface> candidates,
        int? maximumDistance = null)
    {
        Group group = match.Groups[groupName];
        if (!group.Success || !TryFindUniqueRepair(group.Value, candidates, maximumDistance, out string? official)) return match.Value;
        return match.Value[..(group.Index - match.Index)] + official + match.Value[(group.Index - match.Index + group.Length)..];
    }

    private static IReadOnlyList<OfficialSurface> Collect(DomainLexicon lexicon, string type) =>
        lexicon.Entries
            .Where(entry => string.Equals(entry.Kind, "entity", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.CanonicalNames.GetValueOrDefault("en-US"))
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.All(character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or ' ' or '\'' or '’' or '-'))
            .Select(name => new OfficialSurface(type, name!.Trim(), Normalize(name)))
            .DistinctBy(surface => surface.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool TryFindUniqueRepair(
        string observed,
        IReadOnlyList<OfficialSurface> candidates,
        int? requestedMaximumDistance,
        out string? official)
    {
        official = null;
        string normalized = Normalize(observed);
        if (normalized.Length < 4) return false;
        int maximumDistance = requestedMaximumDistance ?? (normalized.Length >= 9 ? 2 : 1);
        OfficialSurface[] nearest = candidates
            .Where(candidate => candidate.Normalized.Length >= 4 &&
                                candidate.Normalized[0] == normalized[0] &&
                                Math.Abs(candidate.Normalized.Length - normalized.Length) <= maximumDistance)
            .Select(candidate => (Candidate: candidate, Distance: DamerauLevenshtein(normalized, candidate.Normalized, maximumDistance)))
            .Where(item => item.Distance is >= 1 && item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Candidate)
            .ToArray();
        if (nearest.Length == 0) return false;
        int bestDistance = DamerauLevenshtein(normalized, nearest[0].Normalized, maximumDistance);
        OfficialSurface[] equallyNear = nearest
            .Where(candidate => DamerauLevenshtein(normalized, candidate.Normalized, maximumDistance) == bestDistance)
            .ToArray();
        if (equallyNear.Length != 1) return false;
        official = equallyNear[0].Text;
        return true;
    }

    private static string Normalize(string value) => new(value
        .Where(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        .Select(char.ToLowerInvariant)
        .ToArray());

    private static int DamerauLevenshtein(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return maximum + 1;
        int[,] matrix = new int[left.Length + 1, right.Length + 1];
        for (int row = 0; row <= left.Length; row++) matrix[row, 0] = row;
        for (int column = 0; column <= right.Length; column++) matrix[0, column] = column;
        for (int row = 1; row <= left.Length; row++)
        {
            int rowMinimum = maximum + 1;
            for (int column = 1; column <= right.Length; column++)
            {
                int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                int value = Math.Min(
                    Math.Min(matrix[row - 1, column] + 1, matrix[row, column - 1] + 1),
                    matrix[row - 1, column - 1] + cost);
                if (row > 1 && column > 1 && left[row - 1] == right[column - 2] && left[row - 2] == right[column - 1])
                    value = Math.Min(value, matrix[row - 2, column - 2] + cost);
                matrix[row, column] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }
            if (rowMinimum > maximum) return maximum + 1;
        }
        return matrix[left.Length, right.Length];
    }
}
