namespace LoLTranslator.App;

/// <summary>
/// 由覆盖率缺口生成的数据采集任务。它只描述“该补什么”，不擅自改变数据状态。
/// </summary>
public sealed record DataCurationWorkItem(
    string ConceptId,
    string Urgency,
    int Priority,
    IReadOnlyList<string> RequiredWork);

public static class ChatDataCurationQueue
{
    public static IReadOnlyList<DataCurationWorkItem> Create(
        LolSlangDataset dataset,
        DataCoverageReport coverage)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(coverage);

        var urgencyByConcept = dataset.Concepts.ToDictionary(
            concept => concept.ConceptId,
            concept => concept.Urgency,
            StringComparer.OrdinalIgnoreCase);

        return coverage.Concepts
            .Where(concept => !concept.ReadyForRuntime)
            .Select(concept => new DataCurationWorkItem(
                concept.ConceptId,
                urgencyByConcept.GetValueOrDefault(concept.ConceptId, "low"),
                CalculatePriority(
                    urgencyByConcept.GetValueOrDefault(concept.ConceptId, "low"),
                    concept.Gaps),
                TranslateGaps(concept.Gaps)))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.ConceptId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CalculatePriority(string urgency, IReadOnlyList<string> gaps)
    {
        int urgencyScore = urgency.ToLowerInvariant() switch
        {
            "high" => 300,
            "medium" => 200,
            _ => 100
        };

        return urgencyScore + gaps.Count * 10;
    }

    private static IReadOnlyList<string> TranslateGaps(IEnumerable<string> gaps)
    {
        return gaps.Select(gap => gap switch
            {
                "缺少 Expression 或 Template。" => "收集至少一个带来源的表达或带参数模板。",
                "缺少正例标注。" => "收集并标注至少一个真实正例。",
                "缺少已复核的表达或模板。" => "完成人工复核后，将可靠表达或模板标为 verified。",
                "缺少已复核的正例标注。" => "由指定审核人复核至少一个正例，并记录 reviewer_id 和日期。",
                _ => gap
            })
            .ToArray();
    }
}
