namespace SlangGraph.Core;

internal sealed record SlangGraphIndexedEntitySurface(
    EntityRecord Entity,
    string Surface,
    string ExtractionKind);

/// <summary>
/// Read-only in-memory indexes for a loaded graph. JSONL remains the source of truth;
/// this only avoids re-scanning every record for each short text query.
/// </summary>
public sealed class SlangGraphSemanticIndex
{
    private readonly Dictionary<string, IReadOnlyList<ExpressionRecord>> _expressionsByLocale;
    private readonly Dictionary<string, IReadOnlyList<PatternRecord>> _patternsByLocale;
    private readonly Dictionary<string, IReadOnlyList<SlangGraphIndexedEntitySurface>> _entitySurfacesByLocale;

    public SlangGraphSemanticIndex(SlangGraphPack graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _expressionsByLocale = GroupByLocale(graph.Expressions, expression => expression.Locale);
        _patternsByLocale = GroupByLocale(graph.Patterns, pattern => pattern.Locale);

        var surfaces = new List<(string Locale, SlangGraphIndexedEntitySurface Surface)>();

        foreach (EntityRecord entity in graph.Entities)
        {
            foreach ((string locale, string canonicalName) in entity.CanonicalNames)
            {
                if (!string.IsNullOrWhiteSpace(canonicalName))
                {
                    surfaces.Add((locale, new SlangGraphIndexedEntitySurface(entity, canonicalName, "canonical_name")));
                }
            }

            foreach (EntityAlias alias in entity.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias.Normalized)))
            {
                surfaces.Add((alias.Locale, new SlangGraphIndexedEntitySurface(entity, alias.Normalized, "entity_alias")));
            }
        }

        _entitySurfacesByLocale = surfaces
            .GroupBy(item => item.Locale, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SlangGraphIndexedEntitySurface>)group
                    .Select(item => item.Surface)
                    .OrderByDescending(item => item.Surface.Length)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ExpressionRecord> GetExpressions(string locale) => GetLocaleValues(_expressionsByLocale, locale);

    public IReadOnlyList<PatternRecord> GetPatterns(string locale) => GetLocaleValues(_patternsByLocale, locale);

    internal IReadOnlyList<SlangGraphIndexedEntitySurface> GetEntitySurfaces(
        string locale,
        IReadOnlyList<string>? entityTypes)
    {
        return GetLocaleValues(_entitySurfacesByLocale, locale)
            .Where(surface => entityTypes is null ||
                              entityTypes.Count == 0 ||
                              entityTypes.Any(type =>
                                  string.Equals(surface.Entity.EntityType, type, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static Dictionary<string, IReadOnlyList<T>> GroupByLocale<T>(
        IEnumerable<T> records,
        Func<T, string> localeSelector)
    {
        return records
            .GroupBy(localeSelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<T>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<T> GetLocaleValues<T>(
        IReadOnlyDictionary<string, IReadOnlyList<T>> index,
        string locale)
    {
        var values = new List<T>();

        if (index.TryGetValue(locale, out IReadOnlyList<T>? exact))
        {
            values.AddRange(exact);
        }

        if (!string.Equals(locale, "*", StringComparison.Ordinal) &&
            index.TryGetValue("*", out IReadOnlyList<T>? wildcard))
        {
            values.AddRange(wildcard);
        }

        return values;
    }
}
