using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LoLTranslator.App;

public sealed record OfficialTrilingualRuntimeTermMatch(
    string CanonicalId,
    string TermType,
    string SourceText,
    string TargetText,
    int Start,
    int Length);

/// <summary>
/// Loads the frozen Riot Data Dragon 16.15.1 tri-locale candidate under the
/// product owner's official-source approval policy. The semantic/rendering
/// packs remain unchanged. Matching is exact, conflicts fail closed, player
/// identifiers are excluded, and unsupported objective shorthand is absent.
/// </summary>
public sealed class OfficialTrilingualRuntimeTerms
{
    public const string ExpectedSha256 = "A7FF4640D54A0307A20E4C4F9BFD99FF7462DBB20369F465F3EA745550075830";
    public const string ExpectedAmbiguitySha256 = "07F6273A9AB6D48ACC32CF4AA903E5A405DFD0A867CB6ED3C15BC8FC5C4C6EE0";
    public const string ExpectedPatch = "16.15.1";
    public const int ExpectedTermCount = 566;

    private static readonly string[] RequiredLocales = ["en-US", "zh-CN", "ko-KR"];
    private static readonly HashSet<string> AllowedTermTypes = new(StringComparer.Ordinal)
    {
        "champion", "item", "summoner_spell", "rune_tree", "rune"
    };

    private sealed record Term(
        string CanonicalId,
        string TermType,
        IReadOnlyDictionary<string, string> Names);

    private sealed record Surface(Term Term, string Locale, string Text);

    private readonly IReadOnlyList<Surface> _surfaces;
    private readonly IReadOnlyList<string> _blockedAmbiguousSurfaces;

    private OfficialTrilingualRuntimeTerms(IReadOnlyList<Surface> surfaces, IReadOnlyList<string> blockedAmbiguousSurfaces)
    {
        _surfaces = surfaces;
        _blockedAmbiguousSurfaces = blockedAmbiguousSurfaces;
    }

    public static OfficialTrilingualRuntimeTerms LoadOfficialSourceApproved(string path, string ambiguityPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Official tri-locale runtime terms were not found.", path);

        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Official tri-locale runtime terms SHA-256 mismatch: {digest}.");

        string[] lines = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length != ExpectedTermCount + 1)
            throw new InvalidDataException($"Expected {ExpectedTermCount} official terms, found {lines.Length - 1}.");

        using JsonDocument headerDocument = JsonDocument.Parse(lines[0]);
        JsonElement header = headerDocument.RootElement;
        RequireString(header, "record_type", "official_trilingual_runtime_terms_projection");
        RequireString(header, "status", "candidate_only");
        RequireString(header, "source_patch", ExpectedPatch);
        if (header.GetProperty("runtime_enabled").ValueKind != JsonValueKind.False ||
            header.GetProperty("explicit_opt_in_required").ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The official terms candidate activation boundary is invalid.");
        string[] headerLocales = header.GetProperty("required_locales").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        if (!RequiredLocales.All(locale => headerLocales.Contains(locale, StringComparer.Ordinal)))
            throw new InvalidDataException("The official terms candidate does not declare all required locales.");

        var terms = new List<Term>(ExpectedTermCount);
        var canonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            using JsonDocument document = JsonDocument.Parse(lines[index]);
            JsonElement root = document.RootElement;
            RequireString(root, "record_type", "official_trilingual_runtime_term");
            RequireString(root, "status", "candidate_only");
            RequireString(root, "review_status", "pending");
            RequireString(root, "match_policy", "exact_official_surface_only");
            RequireString(root, "incomplete_surface_policy", "fail_closed");
            RequireString(root, "player_identifier_policy", "never_a_term");
            RequireString(root, "source_version", ExpectedPatch);
            if (root.GetProperty("runtime_enabled").ValueKind != JsonValueKind.False)
                throw new InvalidDataException($"Line {index + 1} unexpectedly enables itself.");

            string canonicalId = RequiredString(root, "canonical_id");
            string termType = RequiredString(root, "term_type");
            if (!canonicalIds.Add(canonicalId)) throw new InvalidDataException($"Duplicate canonical ID '{canonicalId}'.");
            if (!AllowedTermTypes.Contains(termType)) throw new InvalidDataException($"Unsupported official term type '{termType}'.");

            JsonElement namesElement = root.GetProperty("canonical_names");
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string locale in RequiredLocales)
            {
                if (!namesElement.TryGetProperty(locale, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                    throw new InvalidDataException($"'{canonicalId}' is missing official locale '{locale}'.");
                names[locale] = value.GetString()!.Trim();
            }

            string[] scopes = root.GetProperty("context_scope").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray();
            if (!scopes.Contains("league_of_legends", StringComparer.Ordinal))
                throw new InvalidDataException($"'{canonicalId}' is not scoped to League of Legends.");
            ValidateProvenance(root.GetProperty("provenance"), canonicalId);
            terms.Add(new Term(canonicalId, termType, names));
        }

        // A surface shared by multiple canonical IDs is deliberately omitted.
        // This makes exact lookup deterministic and fail-closed.
        Surface[] all = terms.SelectMany(term => term.Names.Select(pair => new Surface(term, pair.Key, pair.Value))).ToArray();
        HashSet<string> conflicts = all.GroupBy(surface => SurfaceKey(surface.Locale, surface.Text), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Term.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Surface[] safe = all.Where(surface => !conflicts.Contains(SurfaceKey(surface.Locale, surface.Text)))
            .OrderByDescending(surface => surface.Text.Length)
            .ToArray();
        return new OfficialTrilingualRuntimeTerms(safe, LoadBlockedAmbiguities(ambiguityPath));
    }

    public bool ContainsBlockedAmbiguousSurface(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string surface in _blockedAmbiguousSurfaces)
        {
            int start = 0;
            while ((start = text.IndexOf(surface, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = start + surface.Length;
                if (HasSafeBoundary(text, start, end, surface)) return true;
                start = Math.Max(end, start + 1);
            }
        }
        return false;
    }

    public IReadOnlyList<OfficialTrilingualRuntimeTermMatch> MatchExact(
        string text,
        IReadOnlyList<string> sourceLocales,
        string targetLocale)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceLocales);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);
        if (string.IsNullOrWhiteSpace(text) || sourceLocales.Count == 0) return Array.Empty<OfficialTrilingualRuntimeTermMatch>();

        IReadOnlyList<(int Start, int Length)> playerSpans = PlayerIdentifierSpans(text);
        var candidates = new List<OfficialTrilingualRuntimeTermMatch>();
        foreach (Surface surface in _surfaces.Where(surface => sourceLocales.Any(locale => SameLanguageOrExact(surface.Locale, locale))))
        {
            int offset = 0;
            while ((offset = text.IndexOf(surface.Text, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = offset + surface.Text.Length;
                bool overlapsPlayer = playerSpans.Any(span => offset < span.Start + span.Length && span.Start < end);
                if (!overlapsPlayer && HasSafeBoundary(text, offset, end, surface.Text))
                {
                    string target = ResolveTarget(surface.Term.Names, targetLocale);
                    candidates.Add(new OfficialTrilingualRuntimeTermMatch(
                        surface.Term.CanonicalId, surface.Term.TermType, text.Substring(offset, surface.Text.Length), target,
                        offset, surface.Text.Length));
                }
                offset = Math.Max(end, offset + 1);
            }
        }

        var accepted = new List<OfficialTrilingualRuntimeTermMatch>();
        foreach (OfficialTrilingualRuntimeTermMatch candidate in candidates
                     .OrderByDescending(item => item.Length).ThenBy(item => item.Start))
        {
            if (accepted.Any(item => candidate.Start < item.Start + item.Length && item.Start < candidate.Start + candidate.Length))
                continue;
            accepted.Add(candidate);
        }
        return accepted.OrderBy(item => item.Start).ToArray();
    }

    public string RenderExactMatches(
        string text,
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(matches);
        string rendered = text;
        foreach (OfficialTrilingualRuntimeTermMatch match in matches.OrderByDescending(item => item.Start))
        {
            if (match.Start < 0 || match.Length <= 0 || match.Start + match.Length > rendered.Length) continue;
            rendered = rendered.Remove(match.Start, match.Length).Insert(match.Start, match.TargetText);
        }
        return rendered;
    }

    public string? RenderChampionSlotsExact(
        string? prefix,
        IReadOnlyList<string> sourceLocales,
        string targetLocale)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return prefix;
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> matches = MatchExact(prefix, sourceLocales, targetLocale)
            .Where(match => string.Equals(match.TermType, "champion", StringComparison.Ordinal) &&
                            IsInsideParentheses(prefix, match.Start, match.Length))
            .ToArray();
        return RenderExactMatches(prefix, matches);
    }

    private static IReadOnlyList<(int Start, int Length)> PlayerIdentifierSpans(string text)
    {
        // In official chat/system identity forms, only the text before the
        // parenthesised champion is a player identifier. It is never repaired,
        // translated, or treated as a game term.
        var spans = new List<(int, int)>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     @"(?<player>[\p{L}\p{N}_.'’\-]+(?:\s+[\p{L}\p{N}_.'’\-]+){0,4})\s*(?:[-–—]\s*)?[\(（][^\)）\r\n]{1,32}[\)）]",
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            System.Text.RegularExpressions.Group player = match.Groups["player"];
            if (player.Success) spans.Add((player.Index, player.Length));
        }
        return spans;
    }

    private static IReadOnlyList<string> LoadBlockedAmbiguities(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Official-term ambiguity queue was not found.", path);
        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(digest, ExpectedAmbiguitySha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Official-term ambiguity queue SHA-256 mismatch: {digest}.");

        var blocked = new List<string>();
        foreach (string line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Skip(1))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("surface", out JsonElement surface) || surface.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("status", out JsonElement status) ||
                !string.Equals(status.GetString(), "blocked", StringComparison.Ordinal))
                continue;
            string value = surface.GetString()!.Trim();
            if (value.Length > 0) blocked.Add(value);
        }
        if (blocked.Count == 0) throw new InvalidDataException("No exact blocked official-term ambiguities were loaded.");
        return blocked.OrderByDescending(value => value.Length).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasSafeBoundary(string text, int start, int end, string surface)
    {
        bool containsCjkOrHangul = surface.Any(character =>
            character is >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF');
        if (containsCjkOrHangul) return true;
        return (start == 0 || IsSafeLatinTermEdge(text[start - 1])) &&
               (end == text.Length || IsSafeLatinTermEdge(text[end]));
    }

    private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsSafeLatinTermEdge(char adjacent) =>
        !IsWord(adjacent) || adjacent is >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF';

    private static bool IsInsideParentheses(string text, int start, int length)
    {
        int open = text.LastIndexOfAny(['(', '（'], Math.Max(0, start));
        int close = text.IndexOfAny([')', '）'], start + length);
        return open >= 0 && open < start && close >= start + length;
    }

    private static string ResolveTarget(IReadOnlyDictionary<string, string> names, string locale)
    {
        if (names.TryGetValue(locale, out string? exact)) return exact;
        string language = Language(locale);
        return names.FirstOrDefault(pair => string.Equals(Language(pair.Key), language, StringComparison.OrdinalIgnoreCase)).Value
               ?? names["en-US"];
    }

    private static bool SameLanguageOrExact(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Language(left), Language(right), StringComparison.OrdinalIgnoreCase);

    private static string Language(string locale) => locale.Split('-', '_')[0];
    private static string SurfaceKey(string locale, string text) => locale + "\u001F" + text.Trim();

    private static void ValidateProvenance(JsonElement provenance, string canonicalId)
    {
        JsonElement refs = provenance.GetProperty("source_refs");
        foreach ((string key, string locale) in new[] { ("en", "en-US"), ("zh", "zh-CN"), ("ko", "ko-KR") })
        {
            JsonElement source = refs.GetProperty(key);
            RequireString(source, "locale", locale);
            RequireString(source, "source_version", ExpectedPatch);
            string uri = RequiredString(source, "uri");
            string digest = RequiredString(source, "content_sha256");
            if (!uri.StartsWith("https://ddragon.leagueoflegends.com/", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 64 || !digest.All(Uri.IsHexDigit))
                throw new InvalidDataException($"'{canonicalId}' has invalid Riot provenance for '{locale}'.");
        }
    }

    private static void RequireString(JsonElement root, string property, string expected)
    {
        string actual = RequiredString(root, property);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected {property}='{expected}', found '{actual}'.");
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Required string '{property}' is missing.");
}
