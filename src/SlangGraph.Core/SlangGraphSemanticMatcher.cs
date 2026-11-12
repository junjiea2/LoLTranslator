using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlangGraph.Core;

/// <summary>
/// A data-driven semantic candidate. It explains the data records and slot resolutions
/// that produced it; it is not a claim that the interpretation is final or verified.
/// </summary>
public sealed record SlangGraphSemanticMatch(
    string ConceptId,
    string Definition,
    string MatchKind,
    string SourceRecordId,
    string MatchedText,
    IReadOnlyDictionary<string, IReadOnlyList<SlangGraphSlotValue>> Slots,
    double Confidence,
    IReadOnlyList<SlangGraphMatchEvidence> Evidence);

public sealed record SlangGraphSlotValue(
    string Value,
    string ValueKind,
    string MatchedText,
    string ExtractionKind,
    string? EntityId = null);

/// <summary>
/// A data-backed entity surface resolution. It is intentionally separate from
/// concept matching so a caller can preserve a verified typed reference while
/// still routing the rest of a message to a general translation provider.
/// </summary>
public sealed record SlangGraphEntityResolution(
    string EntityId,
    string EntityType,
    string MatchedText,
    string ExtractionKind,
    int Start,
    int Length);

public sealed record SlangGraphMatchEvidence(
    string Kind,
    string RecordId,
    string Detail);

/// <summary>
/// Optional provenance supplied by the caller for a message being matched. Scoped
/// expressions and patterns require a compatible context, so an abbreviation can
/// remain valid inside one community without becoming a global interpretation.
/// </summary>
public sealed record SlangGraphMatchContext(
    string? Community,
    string? Channel);

/// <summary>
/// Matches expressions and patterns from a SlangGraph pack. Extraction is selected by
/// each concept's slot schema, so this type has no knowledge of any particular domain.
/// </summary>
public sealed class SlangGraphSemanticMatcher
{
    private static readonly Regex SlotPlaceholderRegex =
        new(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static readonly Regex NumberRegex =
        new(@"(?<![\p{L}\p{N}_])(?<number>[+-]?\d+(?:[\.,]\d+)?)(?![\p{L}\p{N}_])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationWithUnitRegex =
        new(
            @"(?<![\p{L}\p{N}_])(?<number>[+-]?\d+(?:[\.,]\d+)?)\s*(?<unit>ms|msec|millisecond(?:s)?|s|sec(?:ond)?s?|m|min(?:ute)?s?|h|hr|hour(?:s)?|毫秒|秒钟?|秒|分钟?|分|小时?|초|밀리초|분|시간)(?![\p{L}\p{N}_])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClockDurationRegex =
        new(@"(?<!\d)(?<minutes>\d{1,3}):(?<seconds>[0-5]?\d)(?!\d)", RegexOptions.Compiled);

    private readonly SlangGraphPack _graph;
    private readonly SlangGraphSemanticIndex _index;

    public SlangGraphSemanticMatcher(SlangGraphPack graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _index = new SlangGraphSemanticIndex(_graph);
    }

    public IReadOnlyList<SlangGraphSemanticMatch> Match(
        string text,
        string locale,
        bool includeDraft = false,
        SlangGraphMatchContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(locale))
        {
            return Array.Empty<SlangGraphSemanticMatch>();
        }

        string normalizedText = NormalizeText(text);
        var matches = new List<SlangGraphSemanticMatch>();

        foreach (ExpressionRecord expression in _index.GetExpressions(locale))
        {
            if (!CanUse(
                    expression.Locale,
                    expression.Status,
                    locale,
                    includeDraft,
                    expression.Community,
                    expression.Channel,
                    context) ||
                !ContainsPhrase(normalizedText, expression.Normalized))
            {
                continue;
            }

            ConceptRecord? concept = FindConcept(expression.ConceptId);

            if (concept is null)
            {
                continue;
            }

            Dictionary<string, IReadOnlyList<SlangGraphSlotValue>> slots = ExtractSlots(
                concept,
                normalizedText,
                locale,
                captures: null);

            AddMatchIfComplete(
                matches,
                concept,
                "expression",
                expression.ExpressionId,
                normalizedText,
                slots,
                ConfidenceFor(expression.Status),
                CreateRecordEvidence("expression", expression.ExpressionId, expression.EvidenceIds));
        }

        foreach (PatternRecord pattern in _index.GetPatterns(locale))
        {
            if (!CanUse(
                    pattern.Locale,
                    pattern.Status,
                    locale,
                    includeDraft,
                    pattern.Community,
                    pattern.Channel,
                    context))
            {
                continue;
            }

            Match patternMatch = CreatePatternRegex(pattern.Pattern).Match(normalizedText);

            if (!patternMatch.Success)
            {
                continue;
            }

            ConceptRecord? concept = FindConcept(pattern.ConceptId);

            if (concept is null)
            {
                continue;
            }

            var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string slotName in pattern.Slots)
            {
                Group group = patternMatch.Groups[slotName];

                if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
                {
                    captures[slotName] = group.Value;
                }
            }

            Dictionary<string, IReadOnlyList<SlangGraphSlotValue>> slots = ExtractSlots(
                concept,
                normalizedText,
                locale,
                captures);

            AddMatchIfComplete(
                matches,
                concept,
                "pattern",
                pattern.PatternId,
                patternMatch.Value,
                slots,
                ConfidenceFor(pattern.Status),
                CreateRecordEvidence("pattern", pattern.PatternId, pattern.EvidenceIds));
        }

        return matches
            .GroupBy(match => new
            {
                match.ConceptId,
                match.MatchKind,
                match.SourceRecordId,
                Slots = string.Join("|", match.Slots
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={string.Join(',', item.Value.Select(value => value.Value))}"))
            })
            .Select(group => group.First())
            .OrderByDescending(match => match.Confidence)
            .ThenBy(match => match.ConceptId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves only entity surfaces already present in the loaded graph. This
    /// has no domain-specific branches: locale, verified status, and requested
    /// entity types are supplied by the caller.
    /// </summary>
    public IReadOnlyList<SlangGraphEntityResolution> ResolveEntities(
        string text,
        string locale,
        bool includeDraft = false,
        IReadOnlyList<string>? entityTypes = null)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(locale))
        {
            return Array.Empty<SlangGraphEntityResolution>();
        }

        return _index.GetEntitySurfaces(locale, entityTypes)
            .Where(surface => includeDraft || string.Equals(surface.Entity.Status, "verified", StringComparison.OrdinalIgnoreCase))
            // Spans feed a protected-input replacer, so search the original
            // text rather than a whitespace-normalized copy. An OCR spacing
            // variation safely becomes no resolution instead of a shifted lock.
            .Select(surface => (Surface: surface, Index: FindPhraseIndex(text, surface.Surface)))
            .Where(item => item.Index >= 0)
            .Select(item => new SlangGraphEntityResolution(
                item.Surface.Entity.EntityId,
                item.Surface.Entity.EntityType,
                item.Surface.Surface,
                item.Surface.ExtractionKind,
                item.Index,
                item.Surface.Surface.Length))
            .GroupBy(match => match.EntityId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.MatchedText.Length).First())
            .OrderByDescending(match => match.MatchedText.Length)
            .ToArray();
    }

    private void AddMatchIfComplete(
        ICollection<SlangGraphSemanticMatch> matches,
        ConceptRecord concept,
        string matchKind,
        string sourceRecordId,
        string matchedText,
        Dictionary<string, IReadOnlyList<SlangGraphSlotValue>> slots,
        double confidence,
        IReadOnlyList<SlangGraphMatchEvidence> recordEvidence)
    {
        if (!HasRequiredSlots(concept, slots))
        {
            return;
        }

        var evidence = new List<SlangGraphMatchEvidence>(recordEvidence);

        foreach (SlangGraphSlotValue slotValue in slots.Values.SelectMany(values => values))
        {
            if (slotValue.EntityId is not null)
            {
                evidence.Add(new SlangGraphMatchEvidence(
                    "entity_resolution",
                    slotValue.EntityId,
                    $"{slotValue.ExtractionKind}: {slotValue.MatchedText}"));
            }
        }

        matches.Add(new SlangGraphSemanticMatch(
            concept.ConceptId,
            concept.Definition,
            matchKind,
            sourceRecordId,
            matchedText,
            slots,
            confidence,
            evidence));
    }

    private Dictionary<string, IReadOnlyList<SlangGraphSlotValue>> ExtractSlots(
        ConceptRecord concept,
        string text,
        string locale,
        IReadOnlyDictionary<string, string>? captures)
    {
        var slots = new Dictionary<string, IReadOnlyList<SlangGraphSlotValue>>(StringComparer.OrdinalIgnoreCase);

        foreach (SlotDefinition definition in concept.SlotSchema)
        {
            string? captured = null;
            bool hasCapture = captures is not null && captures.TryGetValue(definition.Name, out captured);

            if (captures is not null && !hasCapture &&
                string.Equals(definition.ValueKind, "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string extractionText = hasCapture ? captured! : text;
            IReadOnlyList<SlangGraphSlotValue> values = ExtractSlotValues(definition, extractionText, locale);

            if (values.Count > 0)
            {
                slots[definition.Name] = definition.Multiple ? values : new[] { values[0] };
            }
        }

        return slots;
    }

    private IReadOnlyList<SlangGraphSlotValue> ExtractSlotValues(
        SlotDefinition definition,
        string text,
        string locale)
    {
        return definition.ValueKind.ToLowerInvariant() switch
        {
            "entity_ref" => ExtractEntities(definition, text, locale),
            "number" => ExtractNumbers(text),
            "duration" => ExtractDurations(text),
            "enum" => ExtractEnumValues(definition, text),
            "text" => ExtractText(text),
            _ => Array.Empty<SlangGraphSlotValue>()
        };
    }

    private IReadOnlyList<SlangGraphSlotValue> ExtractEntities(
        SlotDefinition definition,
        string text,
        string locale)
    {
        var matches = new List<SlangGraphSlotValue>();

        foreach (SlangGraphIndexedEntitySurface surface in _index.GetEntitySurfaces(
                     locale,
                     definition.EffectiveEntityTypes))
        {
            if (ContainsPhrase(text, surface.Surface))
            {
                matches.Add(new SlangGraphSlotValue(
                    surface.Entity.EntityId,
                    "entity_ref",
                    surface.Surface,
                    surface.ExtractionKind,
                    surface.Entity.EntityId));
            }
        }

        return matches
            .GroupBy(match => match.EntityId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.MatchedText.Length).First())
            .OrderByDescending(match => match.MatchedText.Length)
            .ToArray();
    }

    private static IReadOnlyList<SlangGraphSlotValue> ExtractNumbers(string text)
    {
        return NumberRegex.Matches(text)
            .Select(match => NormalizeNumber(match.Groups["number"].Value, match.Value))
            .Where(value => value is not null)
            .Cast<SlangGraphSlotValue>()
            .ToArray();
    }

    private static SlangGraphSlotValue? NormalizeNumber(string rawValue, string matchedText)
    {
        string normalized = rawValue.Replace(',', '.');

        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }

        return new SlangGraphSlotValue(
            value.ToString("0.############################", CultureInfo.InvariantCulture),
            "number",
            matchedText,
            "number_literal");
    }

    private static IReadOnlyList<SlangGraphSlotValue> ExtractDurations(string text)
    {
        var matches = new List<SlangGraphSlotValue>();

        foreach (Match match in DurationWithUnitRegex.Matches(text))
        {
            SlangGraphSlotValue? duration = NormalizeDuration(
                match.Groups["number"].Value,
                match.Groups["unit"].Value,
                match.Value);

            if (duration is not null)
            {
                matches.Add(duration);
            }
        }

        foreach (Match match in ClockDurationRegex.Matches(text))
        {
            if (decimal.TryParse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture, out decimal minutes) &&
                decimal.TryParse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture, out decimal seconds))
            {
                matches.Add(CreateDuration((minutes * 60) + seconds, match.Value));
            }
        }

        return matches
            .GroupBy(match => $"{match.Value}\u001F{match.MatchedText}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static SlangGraphSlotValue? NormalizeDuration(string rawValue, string unit, string matchedText)
    {
        if (!decimal.TryParse(rawValue.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }

        decimal seconds = unit.ToLowerInvariant() switch
        {
            "ms" or "msec" or "millisecond" or "milliseconds" or "毫秒" or "밀리초" => value / 1000m,
            "m" or "min" or "minute" or "minutes" or "分" or "分钟" or "分鐘" or "분" => value * 60m,
            "h" or "hr" or "hour" or "hours" or "小时" or "小時" or "시간" => value * 3600m,
            _ => value
        };

        return CreateDuration(seconds, matchedText);
    }

    private static SlangGraphSlotValue CreateDuration(decimal seconds, string matchedText)
    {
        return new SlangGraphSlotValue(
            $"PT{seconds.ToString("0.############################", CultureInfo.InvariantCulture)}S",
            "duration",
            matchedText,
            "duration_literal");
    }

    private static IReadOnlyList<SlangGraphSlotValue> ExtractEnumValues(SlotDefinition definition, string text)
    {
        if (definition.EnumValues is null)
        {
            return Array.Empty<SlangGraphSlotValue>();
        }

        return definition.EnumValues
            .Where(value => ContainsPhrase(text, value))
            .Select(value => new SlangGraphSlotValue(value, "enum", value, "enum_value"))
            .ToArray();
    }

    private static IReadOnlyList<SlangGraphSlotValue> ExtractText(string text)
    {
        string normalized = NormalizeText(text);
        return string.IsNullOrWhiteSpace(normalized)
            ? Array.Empty<SlangGraphSlotValue>()
            : new[] { new SlangGraphSlotValue(normalized, "text", normalized, "captured_text") };
    }

    private ConceptRecord? FindConcept(string conceptId)
    {
        return _graph.Concepts.FirstOrDefault(concept =>
            string.Equals(concept.ConceptId, conceptId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRequiredSlots(
        ConceptRecord concept,
        IReadOnlyDictionary<string, IReadOnlyList<SlangGraphSlotValue>> slots)
    {
        return concept.SlotSchema
            .Where(slot => slot.Required)
            .All(slot => slots.TryGetValue(slot.Name, out IReadOnlyList<SlangGraphSlotValue>? values) && values.Count > 0);
    }

    private static IReadOnlyList<SlangGraphMatchEvidence> CreateRecordEvidence(
        string recordKind,
        string recordId,
        IEnumerable<string> evidenceIds)
    {
        var evidence = new List<SlangGraphMatchEvidence>
        {
            new("match_record", recordId, recordKind)
        };

        evidence.AddRange(evidenceIds.Select(id => new SlangGraphMatchEvidence("evidence_record", id, recordKind)));
        return evidence;
    }

    private static double ConfidenceFor(string status)
    {
        return string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase) ? 0.90 : 0.75;
    }

    private static bool CanUse(
        string recordLocale,
        string status,
        string requestedLocale,
        bool includeDraft,
        string? recordCommunity,
        string? recordChannel,
        SlangGraphMatchContext? context)
    {
        bool statusAllowed = includeDraft
            ? !string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(status, "deprecated", StringComparison.OrdinalIgnoreCase)
            : string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase);

        return CanUseLocale(recordLocale, requestedLocale) &&
               statusAllowed &&
               (includeDraft || CanUseContext(recordCommunity, recordChannel, context));
    }

    private static bool CanUseContext(
        string? recordCommunity,
        string? recordChannel,
        SlangGraphMatchContext? context)
    {
        if (context is null)
        {
            return string.IsNullOrWhiteSpace(recordCommunity) &&
                   string.IsNullOrWhiteSpace(recordChannel);
        }

        bool communityAllowed = string.IsNullOrWhiteSpace(recordCommunity) ||
                                string.Equals(recordCommunity, context.Community, StringComparison.OrdinalIgnoreCase);
        bool channelAllowed = string.IsNullOrWhiteSpace(recordChannel) ||
                              string.Equals(recordChannel, context.Channel, StringComparison.OrdinalIgnoreCase);
        return communityAllowed && channelAllowed;
    }

    private static bool CanUseLocale(string recordLocale, string requestedLocale)
    {
        return string.Equals(recordLocale, "*", StringComparison.Ordinal) ||
               string.Equals(recordLocale, requestedLocale, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string text)
    {
        return Regex.Replace(text.Trim(), @"[ \t\u00A0\u3000]+", " ");
    }

    private static bool ContainsPhrase(string text, string phrase) => FindPhraseIndex(text, phrase) >= 0;

    private static int FindPhraseIndex(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return -1;
        }

        if (phrase.Any(IsCjkOrHangul))
        {
            return text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        }

        string pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}_])";
        Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
    }

    private static Regex CreatePatternRegex(string pattern)
    {
        var expression = new StringBuilder("^");
        int cursor = 0;

        foreach (Match placeholder in SlotPlaceholderRegex.Matches(pattern))
        {
            expression.Append(Regex.Escape(pattern[cursor..placeholder.Index]));
            expression.Append($"(?<{placeholder.Groups["name"].Value}>.+?)");
            cursor = placeholder.Index + placeholder.Length;
        }

        expression.Append(Regex.Escape(pattern[cursor..]));
        expression.Append('$');

        return new Regex(
            expression.ToString(),
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsCjkOrHangul(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF' or
               >= '\u4E00' and <= '\u9FFF' or
               >= '\u3040' and <= '\u30FF' or
               >= '\uAC00' and <= '\uD7AF';
    }
}
