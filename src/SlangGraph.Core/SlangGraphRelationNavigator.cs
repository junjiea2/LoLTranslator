namespace SlangGraph.Core;

public sealed record SlangGraphRelationStep(
    string RelationId,
    string FromId,
    string RelationType,
    string ToId,
    string Direction);

public sealed record SlangGraphConceptConnection(
    string ConceptId,
    IReadOnlyList<SlangGraphRelationStep> Steps);

/// <summary>
/// Traverses semantic relations without assuming a domain or relation vocabulary.
/// Callers can use the returned path to explain cross-pack recommendations.
/// </summary>
public sealed class SlangGraphRelationNavigator
{
    private readonly SlangGraphPack _graph;
    private readonly HashSet<string> _conceptIds;

    public SlangGraphRelationNavigator(SlangGraphPack graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _conceptIds = graph.Concepts
            .Select(concept => concept.ConceptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SlangGraphConceptConnection> FindRelatedConcepts(string conceptId, int maxHops = 2)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            throw new ArgumentException("Concept ID cannot be empty.", nameof(conceptId));
        }

        if (maxHops < 1)
        {
            return Array.Empty<SlangGraphConceptConnection>();
        }

        if (!_conceptIds.Contains(conceptId))
        {
            return Array.Empty<SlangGraphConceptConnection>();
        }

        var connections = new List<SlangGraphConceptConnection>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { conceptId };
        var pending = new Queue<(string NodeId, IReadOnlyList<SlangGraphRelationStep> Steps)>();
        pending.Enqueue((conceptId, Array.Empty<SlangGraphRelationStep>()));

        while (pending.Count > 0)
        {
            (string nodeId, IReadOnlyList<SlangGraphRelationStep> steps) = pending.Dequeue();

            if (steps.Count >= maxHops)
            {
                continue;
            }

            foreach (RelationRecord relation in _graph.Relations)
            {
                string? neighbor = null;
                SlangGraphRelationStep? step = null;

                if (string.Equals(relation.FromId, nodeId, StringComparison.OrdinalIgnoreCase))
                {
                    neighbor = relation.ToId;
                    step = new SlangGraphRelationStep(
                        relation.RelationId,
                        relation.FromId,
                        relation.RelationType,
                        relation.ToId,
                        "outgoing");
                }
                else if (string.Equals(relation.ToId, nodeId, StringComparison.OrdinalIgnoreCase))
                {
                    neighbor = relation.FromId;
                    step = new SlangGraphRelationStep(
                        relation.RelationId,
                        relation.FromId,
                        relation.RelationType,
                        relation.ToId,
                        "incoming");
                }

                if (neighbor is null || step is null || !_conceptIds.Contains(neighbor) || !visited.Add(neighbor))
                {
                    continue;
                }

                IReadOnlyList<SlangGraphRelationStep> path = steps.Append(step).ToArray();
                connections.Add(new SlangGraphConceptConnection(neighbor, path));
                pending.Enqueue((neighbor, path));
            }
        }

        return connections;
    }
}
