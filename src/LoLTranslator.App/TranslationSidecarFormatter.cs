using System.IO;
using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

public enum TranslationSidecarTextRole
{
    Normal,
    Muted,
    AlliedIdentity,
    EnemyIdentity,
    Item,
    System
}

public sealed record TranslationSidecarSegment(string Text, TranslationSidecarTextRole Role);

public sealed record TranslationSidecarDisplayLine(IReadOnlyList<TranslationSidecarSegment> Segments);

/// <summary>
/// Presentation-only formatter. Candidate item data may color an exact official
/// target-language display name, but it never matches source chat, changes a
/// translation, protects a model span, or promotes candidate data.
/// </summary>
public sealed class TranslationSidecarFormatter
{
    private static readonly Regex TimestampPrefix = new(
        @"^(?<timestamp>\s*\d{1,2}\s*[:：]\s*\d{2}\s*)(?<identity>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemMarker = new(
        @"\[(?:系统|系統|System)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PlayerChampionPrefix = new(
        @"^(?<lead>\s*(?:\d{1,2}\s*[:：]\s*\d{2}\s*)?(?:\[[^\]\r\n]{1,24}\]\s*)?)[^:：\r\n]{0,112}?[\(（](?<champion>[^\)）\r\n]{1,48})[\)）]\s*[:：]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemGoldWording = new(
        @"(?:已断开连接|已斷開連線|已重新连接|已重新連線|第一滴血|终结了|終結了|连续击杀|連續擊殺|赏金|賞金|摧毁了|摧毀了|势不可挡|勢不可擋|强化回城|強化回城|已将目标锁定为|已將目標鎖定為|锁定了|鎖定了|使用了\s*[QWERDF]\b(?:\s*[-—–][^，。;；]*)?|\b[QWERDF]\b(?=\s*[-—–])|has\s+disconnected|has\s+reconnected|first\s+blood|has\s+shut\s+down|is\s+on\s+(?:a\s+)?rampage|enhanced\s+recall|has\s+targeted|used\s+[QWERDF]\b(?:\s*[-—–][^,.;]*)?|\b[QWERDF]\b(?=\s*[-—–])|bounty|연결.*종료|다시.*연결|처치|제압|연속\s*킬)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _itemNamesByLocale;

    private TranslationSidecarFormatter(IReadOnlyDictionary<string, IReadOnlyList<string>> itemNamesByLocale)
    {
        _itemNamesByLocale = itemNamesByLocale;
    }

    public static TranslationSidecarFormatter Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public static TranslationSidecarFormatter TryLoadOfficialItemDisplayCandidate(string path, string? runtimeAliasPath = null)
    {
        try
        {
            DomainLexicon lexicon = DomainLexiconLoader.LoadJsonl(path);
            Dictionary<string, List<string>> mutableNames = lexicon.Entries
                .Where(entry => string.Equals(entry.Kind, "entity", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(entry.Type, "item", StringComparison.OrdinalIgnoreCase))
                .SelectMany(entry => entry.CanonicalNames)
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value.Trim()).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (RuntimeItemDisplayAlias alias in RuntimeItemDisplayAliasLoader.LoadApproved(runtimeAliasPath))
            {
                if (!mutableNames.TryGetValue(alias.Locale, out List<string>? values))
                    mutableNames[alias.Locale] = values = new List<string>();
                values.Add(alias.Surface);
            }
            Dictionary<string, IReadOnlyList<string>> names = mutableNames.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(value => value.Length).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return new TranslationSidecarFormatter(names);
        }
        catch (IOException)
        {
            return Empty;
        }
        catch (InvalidDataException)
        {
            return Empty;
        }
    }

    public TranslationSidecarDisplayLine Format(
        string? envelopePrefix,
        string translatedBody,
        string targetLocale,
        string? senderRelation = null,
        IReadOnlyList<string>? opposingIdentities = null,
        bool suppressDelimiter = false,
        bool sourceGoldEmphasis = false)
    {
        ArgumentNullException.ThrowIfNull(translatedBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);

        var segments = new List<TranslationSidecarSegment>();
        bool systemMessage = !string.IsNullOrWhiteSpace(envelopePrefix) && SystemMarker.IsMatch(envelopePrefix);
        string prefix = NormalizePrefix(envelopePrefix, suppressDelimiter);
        Match timestamp = TimestampPrefix.Match(prefix);
        if (timestamp.Success)
        {
            segments.Add(new TranslationSidecarSegment(timestamp.Groups["timestamp"].Value, TranslationSidecarTextRole.Muted));
            AddIdentity(segments, timestamp.Groups["identity"].Value, prefix, senderRelation);
        }
        else
        {
            AddIdentity(segments, prefix, prefix, senderRelation);
        }

        (translatedBody, opposingIdentities) = ProjectOpposingIdentities(translatedBody, opposingIdentities);
        AddBodySegments(segments, translatedBody, targetLocale, systemMessage, senderRelation, opposingIdentities,
            sourceGoldEmphasis);
        return new TranslationSidecarDisplayLine(segments);
    }

    private static (string Body, IReadOnlyList<string>? Identities) ProjectOpposingIdentities(
        string body,
        IReadOnlyList<string>? identities)
    {
        if (identities is null || identities.Count == 0) return (body, identities);
        var projected = new List<string>();
        foreach (string identity in identities.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            int open = identity.LastIndexOfAny(['(', '（']);
            int close = identity.LastIndexOfAny([')', '）']);
            string champion = open >= 0 && close > open + 1
                ? identity[(open + 1)..close].Trim()
                : identity.Trim();
            if (champion.Length == 0) continue;
            body = body.Replace(identity, champion, StringComparison.OrdinalIgnoreCase);
            projected.Add(champion);
        }
        return (body, projected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddIdentity(
        ICollection<TranslationSidecarSegment> segments,
        string text,
        string fullPrefix,
        string? senderRelation)
    {
        // A visible Team channel is stronger evidence than every upstream
        // relation hint. This presentation-level guard prevents an OCR colour
        // misclassification from ever rendering a teammate's identity red.
        TranslationSidecarTextRole role = IsTeamPrefix(fullPrefix)
            ? TranslationSidecarTextRole.AlliedIdentity
            : senderRelation?.ToLowerInvariant() switch
        {
            "ally" => TranslationSidecarTextRole.AlliedIdentity,
            "enemy" => TranslationSidecarTextRole.EnemyIdentity,
            "gold" => TranslationSidecarTextRole.System,
            "white" => TranslationSidecarTextRole.Normal,
            _ => TranslationSidecarTextRole.Normal
        };
        if (text.Length > 0) segments.Add(new TranslationSidecarSegment(text, role));
    }

    private void AddBodySegments(
        ICollection<TranslationSidecarSegment> segments,
        string body,
        string targetLocale,
        bool systemMessage,
        string? senderRelation,
        IReadOnlyList<string>? opposingIdentities,
        bool sourceGoldEmphasis)
    {
        IReadOnlyList<string> names = ResolveLocale(targetLocale);
        var matches = new List<(int Start, int Length, TranslationSidecarTextRole Role)>();
        TranslationSidecarTextRole baseRole = systemMessage &&
                                               string.Equals(senderRelation, "gold", StringComparison.OrdinalIgnoreCase)
            ? TranslationSidecarTextRole.System
            : TranslationSidecarTextRole.Normal;
        TranslationSidecarTextRole opposingRole = senderRelation?.ToLowerInvariant() switch
        {
            "ally" => TranslationSidecarTextRole.EnemyIdentity,
            "enemy" => TranslationSidecarTextRole.AlliedIdentity,
            "gold" => TranslationSidecarTextRole.System,
            _ => baseRole
        };
        foreach (string identity in opposingIdentities ?? Array.Empty<string>())
        {
            int offset = 0;
            while (identity.Length > 0 && offset < body.Length)
            {
                int start = body.IndexOf(identity, offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                matches.Add((start, identity.Length, opposingRole));
                offset = start + identity.Length;
            }
        }
        foreach (string name in names)
        {
            int offset = 0;
            while (offset < body.Length)
            {
                int start = body.IndexOf(name, offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                if (HasSafeBoundary(body, start, name.Length)) matches.Add((start, name.Length, TranslationSidecarTextRole.Item));
                offset = start + Math.Max(1, name.Length);
            }
        }

        if (systemMessage && sourceGoldEmphasis)
        {
            matches.AddRange(SystemGoldWording.Matches(body).Cast<Match>()
                .Select(match => (match.Index, match.Length, TranslationSidecarTextRole.System)));
        }

        var accepted = new List<(int Start, int Length, TranslationSidecarTextRole Role)>();
        foreach ((int Start, int Length, TranslationSidecarTextRole Role) candidate in matches.OrderBy(match => match.Start).ThenByDescending(match => match.Length))
        {
            if (accepted.All(existing => candidate.Start >= existing.Start + existing.Length || existing.Start >= candidate.Start + candidate.Length))
            {
                accepted.Add(candidate);
            }
        }

        int cursor = 0;
        foreach ((int start, int length, TranslationSidecarTextRole role) in accepted.OrderBy(match => match.Start))
        {
            if (start > cursor) segments.Add(new TranslationSidecarSegment(body[cursor..start], baseRole));
            segments.Add(new TranslationSidecarSegment(body.Substring(start, length), role));
            cursor = start + length;
        }
        if (cursor < body.Length) segments.Add(new TranslationSidecarSegment(body[cursor..], baseRole));
        if (body.Length == 0) segments.Add(new TranslationSidecarSegment(string.Empty, baseRole));
    }

    private IReadOnlyList<string> ResolveLocale(string targetLocale)
    {
        if (_itemNamesByLocale.TryGetValue(targetLocale, out IReadOnlyList<string>? exact)) return exact;
        string language = targetLocale.Split('-', '_')[0];
        return _itemNamesByLocale.FirstOrDefault(pair =>
            string.Equals(pair.Key.Split('-', '_')[0], language, StringComparison.OrdinalIgnoreCase)).Value
            ?? Array.Empty<string>();
    }

    private static bool HasSafeBoundary(string text, int start, int length)
    {
        bool left = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        int end = start + length;
        bool right = end == text.Length || !char.IsLetterOrDigit(text[end]);
        bool latinName = text.AsSpan(start, length).IndexOfAnyInRange('A', 'z') >= 0;
        return !latinName || left && right;
    }

    private static bool IsTeamPrefix(string prefix) =>
        prefix.Contains("[Team]", StringComparison.OrdinalIgnoreCase) ||
        prefix.Contains("[队伍]", StringComparison.OrdinalIgnoreCase) ||
        prefix.Contains("[隊伍]", StringComparison.OrdinalIgnoreCase) ||
        prefix.Contains("[WE]", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePrefix(string? envelopePrefix, bool suppressDelimiter)
    {
        // Never invent a team identity when OCR did not recover an envelope.
        // A fabricated blue prefix can make an unsafe full-row translation look
        // trustworthy and defeats the presentation fail-closed gate.
        if (string.IsNullOrWhiteSpace(envelopePrefix)) return string.Empty;
        string normalized = envelopePrefix.Trim().Replace("[Team]", "[队伍]", StringComparison.OrdinalIgnoreCase);
        normalized = SystemMarker.Replace(normalized, string.Empty)
            .Replace("(游戏)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("（游戏）", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        Match playerChampion = PlayerChampionPrefix.Match(normalized);
        if (playerChampion.Success)
        {
            // Player identifiers are intentionally omitted from the overlay.
            // The parenthesized champion slot has already passed official OCR
            // repair/localized rendering upstream and is the stable identity.
            normalized = playerChampion.Groups["lead"].Value.TrimEnd() + " " +
                         playerChampion.Groups["champion"].Value.Trim();
            normalized = normalized.Trim();
        }
        if (Regex.IsMatch(normalized, @"^\d{1,2}\s*[:：]\s*\d{2}$", RegexOptions.CultureInvariant))
            return normalized + " ";
        if (suppressDelimiter)
            return normalized.TrimEnd(':', '：').TrimEnd() + " ";
        return normalized.EndsWith(':') || normalized.EndsWith('：') ? normalized : normalized + "：";
    }
}
