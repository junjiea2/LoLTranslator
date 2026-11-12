using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlangGraph.Core;

public sealed record SlangGraphReleaseGateItem(
    string RecordId,
    string RecordType,
    bool Ready,
    IReadOnlyList<string> Reasons);

public sealed record SlangGraphReleaseGateReport(
    IReadOnlyList<SlangGraphReleaseGateItem> Items)
{
    public bool CanRelease => Items.Count > 0 && Items.All(item => item.Ready);
}

public sealed record SlangGraphReleaseSnapshot(
    string ReleaseId,
    DateTimeOffset CreatedAt,
    string ContentSha256,
    SlangGraphPack Graph);

/// <summary>
/// Evaluates whether a complete pack is safe to publish. A release is a copied graph
/// plus a content hash, never a mutable view over a working pack.
/// </summary>
public static class SlangGraphReleaseGate
{
    public static SlangGraphReleaseGateReport Evaluate(SlangGraphPack graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        IReadOnlyDictionary<string, ReviewRecord> latestReviews = graph.Reviews
            .GroupBy(review => review.SubjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(review => review.ReviewedAt)
                    .ThenByDescending(review => review.ReviewId, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var evidenceById = graph.Evidence.ToDictionary(item => item.EvidenceId, StringComparer.OrdinalIgnoreCase);
        var items = new List<SlangGraphReleaseGateItem>();

        AddItems(items, graph.Concepts.Select(item => (item.ConceptId, "concept", item.Status, item.EvidenceIds)), latestReviews, evidenceById);
        AddItems(items, graph.Expressions.Select(item => (item.ExpressionId, "expression", item.Status, item.EvidenceIds)), latestReviews, evidenceById);
        AddItems(items, graph.Patterns.Select(item => (item.PatternId, "pattern", item.Status, item.EvidenceIds)), latestReviews, evidenceById);
        AddItems(items, graph.Entities.Select(item => (item.EntityId, "entity", item.Status, item.EvidenceIds)), latestReviews, evidenceById);
        AddItems(items, graph.Relations.Select(item => (item.RelationId, "relation", item.Status, item.EvidenceIds)), latestReviews, evidenceById);
        AddItems(items, graph.Renderings.Select(item => (item.RenderingId, "rendering", item.Status, item.EvidenceIds)), latestReviews, evidenceById);

        return new SlangGraphReleaseGateReport(items);
    }

    public static bool TryCreateSnapshot(
        SlangGraphPack graph,
        string releaseId,
        DateTimeOffset createdAt,
        out SlangGraphReleaseSnapshot? snapshot,
        out SlangGraphReleaseGateReport report)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            throw new ArgumentException("Release ID cannot be empty.", nameof(releaseId));
        }

        report = Evaluate(graph);

        if (!report.CanRelease)
        {
            snapshot = null;
            return false;
        }

        SlangGraphPack frozenGraph = CopyGraph(graph);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(frozenGraph);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        snapshot = new SlangGraphReleaseSnapshot(releaseId, createdAt, hash, frozenGraph);
        return true;
    }

    private static void AddItems(
        ICollection<SlangGraphReleaseGateItem> items,
        IEnumerable<(string Id, string Type, string Status, IReadOnlyList<string> EvidenceIds)> records,
        IReadOnlyDictionary<string, ReviewRecord> latestReviews,
        IReadOnlyDictionary<string, EvidenceRecord> evidenceById)
    {
        foreach ((string id, string type, string status, IReadOnlyList<string> evidenceIds) in records)
        {
            var reasons = new List<string>();

            if (!string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("record_status_is_not_verified");
            }

            if (evidenceIds.Count == 0)
            {
                reasons.Add("record_has_no_evidence");
            }

            foreach (string evidenceId in evidenceIds)
            {
                if (!evidenceById.ContainsKey(evidenceId))
                {
                    reasons.Add($"missing_evidence:{evidenceId}");
                    continue;
                }

                if (!latestReviews.TryGetValue(evidenceId, out ReviewRecord? review))
                {
                    reasons.Add($"evidence_has_no_review:{evidenceId}");
                }
                else if (!string.Equals(review.Decision, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add($"evidence_not_approved:{evidenceId}");
                }
            }

            items.Add(new SlangGraphReleaseGateItem(id, type, reasons.Count == 0, reasons));
        }
    }

    private static SlangGraphPack CopyGraph(SlangGraphPack graph)
    {
        return new SlangGraphPack(
            graph.Header with { Dependencies = graph.Header.Dependencies.ToArray() },
            graph.Domains.Select(domain => domain with { Tags = domain.Tags.ToArray() }).ToArray(),
            graph.Concepts.Select(concept => concept with
            {
                SlotSchema = concept.SlotSchema.Select(slot => slot with { EnumValues = slot.EnumValues?.ToArray() }).ToArray(),
                Tags = concept.Tags.ToArray(),
                EvidenceIds = concept.EvidenceIds.ToArray()
            }).ToArray(),
            graph.Expressions.Select(expression => expression with { EvidenceIds = expression.EvidenceIds.ToArray() }).ToArray(),
            graph.Patterns.Select(pattern => pattern with { Slots = pattern.Slots.ToArray(), EvidenceIds = pattern.EvidenceIds.ToArray() }).ToArray(),
            graph.Entities.Select(entity => entity with
            {
                CanonicalNames = new Dictionary<string, string>(entity.CanonicalNames, StringComparer.OrdinalIgnoreCase),
                Aliases = entity.Aliases.ToArray(),
                EvidenceIds = entity.EvidenceIds.ToArray()
            }).ToArray(),
            graph.Relations.Select(relation => relation with { EvidenceIds = relation.EvidenceIds.ToArray() }).ToArray(),
            graph.Renderings.Select(rendering => rendering with { EvidenceIds = rendering.EvidenceIds.ToArray() }).ToArray(),
            graph.Sources.ToArray(),
            graph.Evidence.ToArray(),
            graph.Reviews.ToArray(),
            graph.Observations.Select(observation => observation with
            {
                SourceIds = observation.SourceIds.ToArray(),
                Labels = observation.Labels.Select(label => label with
                {
                    Slots = new Dictionary<string, string>(label.Slots, StringComparer.OrdinalIgnoreCase)
                }).ToArray()
            }).ToArray());
    }
}
