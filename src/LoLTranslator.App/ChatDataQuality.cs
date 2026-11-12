namespace LoLTranslator.App;

/// <summary>
/// 单个语义概念的当前数据覆盖情况。
/// ReadyForRuntime 为 false 时，该概念不应默认参与正式翻译结果生成。
/// </summary>
public sealed record ConceptCoverage(
    string ConceptId,
    int ExpressionCount,
    int TemplateCount,
    int PositiveAnnotationCount,
    int NegativeAnnotationCount,
    int VerifiedExpressionCount,
    int VerifiedTemplateCount,
    int VerifiedPositiveAnnotationCount,
    bool ReadyForRuntime,
    IReadOnlyList<string> Gaps);

public sealed record DataCoverageReport(
    IReadOnlyList<ConceptCoverage> Concepts)
{
    public int ReadyConceptCount => Concepts.Count(concept => concept.ReadyForRuntime);

    public int BlockedConceptCount => Concepts.Count - ReadyConceptCount;
}

public static class ChatDataQualityAnalyzer
{
    public static DataCoverageReport Analyze(
        LolSlangDataset dataset,
        IEnumerable<ChatSampleAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(annotations);

        ChatSampleAnnotation[] annotationItems = annotations.ToArray();
        var coverage = new List<ConceptCoverage>();

        foreach (SlangConcept concept in dataset.Concepts.OrderBy(item => item.ConceptId))
        {
            SlangExpression[] expressions = dataset.Expressions
                .Where(item => string.Equals(item.ConceptId, concept.ConceptId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            SlangTemplate[] templates = dataset.Templates
                .Where(item => string.Equals(item.ConceptId, concept.ConceptId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ChatSemanticLabel[] labels = annotationItems
                .SelectMany(annotation => annotation.Labels.Select(label => new
                {
                    Annotation = annotation,
                    Label = label
                }))
                .Where(item => string.Equals(item.Label.ConceptId, concept.ConceptId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Label)
                .ToArray();
            int verifiedPositiveAnnotations = annotationItems
                .Where(annotation => string.Equals(annotation.Review.Status, "verified", StringComparison.OrdinalIgnoreCase))
                .SelectMany(annotation => annotation.Labels)
                .Count(label =>
                    label.Polarity == "positive" &&
                    string.Equals(label.ConceptId, concept.ConceptId, StringComparison.OrdinalIgnoreCase));
            int verifiedExpressions = expressions.Count(item =>
                string.Equals(item.Status, "verified", StringComparison.OrdinalIgnoreCase));
            int verifiedTemplates = templates.Count(item =>
                string.Equals(item.Status, "verified", StringComparison.OrdinalIgnoreCase));
            var gaps = new List<string>();

            if (expressions.Length == 0 && templates.Length == 0)
            {
                gaps.Add("缺少 Expression 或 Template。");
            }

            if (labels.Count(label => label.Polarity == "positive") == 0)
            {
                gaps.Add("缺少正例标注。");
            }

            if (verifiedExpressions == 0 && verifiedTemplates == 0)
            {
                gaps.Add("缺少已复核的表达或模板。");
            }

            if (verifiedPositiveAnnotations == 0)
            {
                gaps.Add("缺少已复核的正例标注。");
            }

            coverage.Add(new ConceptCoverage(
                concept.ConceptId,
                expressions.Length,
                templates.Length,
                labels.Count(label => label.Polarity == "positive"),
                labels.Count(label => label.Polarity == "negative"),
                verifiedExpressions,
                verifiedTemplates,
                verifiedPositiveAnnotations,
                gaps.Count == 0,
                gaps));
        }

        return new DataCoverageReport(coverage);
    }
}
