using System.Globalization;
using System.Text.RegularExpressions;

namespace LoLTranslator.App;

/// <summary>
/// 基于数据集 Expression 和 Template 的语义候选匹配结果。
/// 结果是候选，不等同于最终翻译或最终人工判定。
/// </summary>
public sealed record CatalogSemanticMatch(
    string ConceptId,
    string Description,
    string MatchKind,
    string SourceRecordId,
    string MatchedText,
    IReadOnlyDictionary<string, string> Slots,
    double Confidence);

public sealed class CatalogSemanticMatcher
{
    private static readonly Regex SlotPlaceholderRegex =
        new(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static readonly Regex LaneRegex =
        new(
            @"(?<![a-z])(?<lane>top|mid|middle|bot|bottom|adc|support|sup|jungle|jg)(?![a-z])|" +
            @"(?<lane>上路|中路|下路|辅助|打野)|" +
            @"(?<lane>탑|미드|바텀|원딜|서폿|정글)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecondsRegex =
        new(@"(?<seconds>\d{1,3})\s*(?:s|sec|secs|秒|초)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly LolSlangCatalog _catalog;

    public CatalogSemanticMatcher(LolSlangCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<CatalogSemanticMatch> Match(
        string text,
        string locale,
        bool includeDraft = false)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(locale))
        {
            return Array.Empty<CatalogSemanticMatch>();
        }

        string normalizedText = NormalizeText(text);
        var matches = new List<CatalogSemanticMatch>();

        foreach (SlangExpression expression in _catalog.Dataset.Expressions)
        {
            if (!CanUse(expression.Locale, expression.Status, locale, includeDraft) ||
                !ContainsExpression(normalizedText, expression.Normalized))
            {
                continue;
            }

            SlangConcept? concept = _catalog.FindConcept(expression.ConceptId);

            if (concept is null)
            {
                continue;
            }

            matches.Add(new CatalogSemanticMatch(
                concept.ConceptId,
                concept.Definition,
                "expression",
                expression.ExpressionId,
                normalizedText,
                ExtractImplicitSlots(normalizedText),
                expression.Confidence));
        }

        foreach (SlangTemplate template in _catalog.Dataset.Templates)
        {
            if (!CanUse(template.Locale, template.Status, locale, includeDraft))
            {
                continue;
            }

            Match templateMatch = CreateTemplateRegex(template.Pattern).Match(normalizedText);

            if (!templateMatch.Success)
            {
                continue;
            }

            SlangConcept? concept = _catalog.FindConcept(template.ConceptId);

            if (concept is null)
            {
                continue;
            }

            matches.Add(new CatalogSemanticMatch(
                concept.ConceptId,
                concept.Definition,
                "template",
                template.TemplateId,
                normalizedText,
                ExtractTemplateSlots(template, templateMatch),
                string.Equals(template.Status, "verified", StringComparison.OrdinalIgnoreCase) ? 0.90 : 0.75));
        }

        return matches
            .GroupBy(match => new
            {
                match.ConceptId,
                match.MatchKind,
                match.SourceRecordId,
                Slots = string.Join("|", match.Slots.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}"))
            })
            .Select(group => group.First())
            .OrderByDescending(match => match.Confidence)
            .ToArray();
    }

    private static bool CanUse(
        string recordLocale,
        string status,
        string requestedLocale,
        bool includeDraft)
    {
        return string.Equals(recordLocale, requestedLocale, StringComparison.OrdinalIgnoreCase) &&
               (includeDraft || string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeText(string text)
    {
        return Regex.Replace(text.Trim(), @"[ \t\u00A0\u3000]+", " ");
    }

    private static bool ContainsExpression(string text, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        if (expression.Any(IsCjkOrHangul))
        {
            return text.Contains(expression, StringComparison.OrdinalIgnoreCase);
        }

        string pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(expression)}(?![\p{{L}}\p{{N}}_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsCjkOrHangul(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF' or
               >= '\u4E00' and <= '\u9FFF' or
               >= '\u3040' and <= '\u30FF' or
               >= '\uAC00' and <= '\uD7AF';
    }

    private static Regex CreateTemplateRegex(string template)
    {
        var pattern = new System.Text.StringBuilder("^");
        int cursor = 0;

        foreach (Match placeholder in SlotPlaceholderRegex.Matches(template))
        {
            pattern.Append(Regex.Escape(template[cursor..placeholder.Index]));
            string slotName = placeholder.Groups["name"].Value;
            pattern.Append($"(?<{slotName}>.+?)");
            cursor = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(template[cursor..]));
        pattern.Append('$');

        return new Regex(
            pattern.ToString(),
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private Dictionary<string, string> ExtractTemplateSlots(
        SlangTemplate template,
        Match match)
    {
        var slots = ExtractImplicitSlots(match.Value);

        foreach (string slotName in template.Slots)
        {
            Group group = match.Groups[slotName];

            if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
            {
                slots[slotName] = NormalizeSlotValue(slotName, group.Value);
            }
        }

        return slots;
    }

    private Dictionary<string, string> ExtractImplicitSlots(string text)
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Match laneMatch = LaneRegex.Match(text);

        if (laneMatch.Success)
        {
            slots["subject"] = NormalizeLane(laneMatch.Groups["lane"].Value);
        }

        Match secondsMatch = SecondsRegex.Match(text);

        if (secondsMatch.Success)
        {
            slots["seconds"] = secondsMatch.Groups["seconds"].Value;
        }

        return slots;
    }

    private string NormalizeSlotValue(string slotName, string value)
    {
        string trimmed = value.Trim();

        if (string.Equals(slotName, "subject", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeLane(trimmed);
        }

        if (string.Equals(slotName, "objective", StringComparison.OrdinalIgnoreCase))
        {
            GameEntity? entity = _catalog.FindEntityByName(trimmed);
            return entity?.EntityId ?? trimmed;
        }

        return trimmed;
    }

    private static string NormalizeLane(string lane)
    {
        return lane.ToLower(CultureInfo.InvariantCulture) switch
        {
            "top" or "上路" or "탑" => "top",
            "mid" or "middle" or "中路" or "미드" => "mid",
            "bot" or "bottom" or "adc" or "下路" or "원딜" or "바텀" => "bot",
            "support" or "sup" or "辅助" or "서폿" => "support",
            "jungle" or "jg" or "打野" or "정글" => "jungle",
            _ => lane
        };
    }
}

public sealed record ChatAnnotationEvaluationItem(
    string AnnotationId,
    bool Passed,
    IReadOnlyList<string> Problems);

public sealed record ChatAnnotationEvaluation(
    IReadOnlyList<ChatAnnotationEvaluationItem> Items)
{
    public int PassedCount => Items.Count(item => item.Passed);

    public int FailedCount => Items.Count - PassedCount;
}

public static class ChatAnnotationEvaluator
{
    public static ChatAnnotationEvaluation Evaluate(
        LolSlangCatalog catalog,
        IEnumerable<ChatSampleAnnotation> annotations,
        bool includeDraft = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(annotations);

        var matcher = new CatalogSemanticMatcher(catalog);
        var items = new List<ChatAnnotationEvaluationItem>();

        foreach (ChatSampleAnnotation annotation in annotations)
        {
            IReadOnlyList<CatalogSemanticMatch> matches = matcher.Match(
                annotation.RawText,
                annotation.Locale,
                includeDraft);
            var problems = new List<string>();

            foreach (ChatSemanticLabel label in annotation.Labels)
            {
                bool matched = matches.Any(match =>
                    string.Equals(match.ConceptId, label.ConceptId, StringComparison.OrdinalIgnoreCase) &&
                    SlotsMatch(label.Slots, match.Slots));

                if (label.Polarity == "positive" && !matched)
                {
                    problems.Add($"未匹配正例概念 {label.ConceptId}。");
                }
                else if (label.Polarity == "negative" && matched)
                {
                    problems.Add($"错误匹配负例概念 {label.ConceptId}。");
                }
            }

            items.Add(new ChatAnnotationEvaluationItem(
                annotation.AnnotationId,
                problems.Count == 0,
                problems));
        }

        return new ChatAnnotationEvaluation(items);
    }

    private static bool SlotsMatch(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        return expected.All(pair =>
            actual.TryGetValue(pair.Key, out string? value) &&
            string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase));
    }
}
