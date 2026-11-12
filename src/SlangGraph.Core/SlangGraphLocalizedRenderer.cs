using System.Text.RegularExpressions;

namespace SlangGraph.Core;

public sealed record SlangGraphRenderedConcept(
    string ConceptId,
    string RequestedLocale,
    string ResolvedLocale,
    string Text,
    string Explanation,
    string? RenderingId,
    bool UsedFallback);

public sealed record SlangGraphRenderedEntity(
    string EntityId,
    string RequestedLocale,
    string ResolvedLocale,
    string Text,
    bool UsedFallback);

/// <summary>
/// Resolves a semantic concept into reviewed target-language text. This stage is
/// independent from OCR and expression matching locales.
/// </summary>
public sealed class SlangGraphLocalizedRenderer
{
    private static readonly Regex PlaceholderRegex =
        new(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private readonly SlangGraphPack _graph;
    private readonly IReadOnlyDictionary<string, ConceptRecord> _concepts;
    private readonly IReadOnlyDictionary<string, EntityRecord> _entities;

    public SlangGraphLocalizedRenderer(SlangGraphPack graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _concepts = graph.Concepts.ToDictionary(
            concept => concept.ConceptId,
            StringComparer.OrdinalIgnoreCase);
        _entities = graph.Entities.ToDictionary(
            entity => entity.EntityId,
            StringComparer.OrdinalIgnoreCase);
    }

    public SlangGraphRenderedConcept Render(
        string conceptId,
        string targetLocale,
        IReadOnlyDictionary<string, IReadOnlyList<SlangGraphSlotValue>>? slots = null,
        bool includeDraft = false)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            throw new ArgumentException("Concept ID cannot be empty.", nameof(conceptId));
        }

        if (string.IsNullOrWhiteSpace(targetLocale))
        {
            throw new ArgumentException("Target locale cannot be empty.", nameof(targetLocale));
        }

        if (!_concepts.TryGetValue(conceptId, out ConceptRecord? concept))
        {
            throw new KeyNotFoundException($"Unknown concept '{conceptId}'.");
        }

        LocalizedRenderingRecord? rendering = ResolveRendering(conceptId, targetLocale, includeDraft);

        if (rendering is null)
        {
            return new SlangGraphRenderedConcept(
                conceptId,
                targetLocale,
                "und",
                concept.Definition,
                concept.Definition,
                null,
                true);
        }

        string text = RenderTemplate(rendering, slots);

        return new SlangGraphRenderedConcept(
            conceptId,
            targetLocale,
            rendering.TargetLocale,
            text,
            rendering.Explanation,
            rendering.RenderingId,
            !string.Equals(rendering.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renders a verified entity's canonical display name without inventing a
    /// concept or relying on a domain-specific localization table.
    /// </summary>
    public SlangGraphRenderedEntity RenderEntity(
        string entityId,
        string targetLocale,
        bool includeDraft = false)
    {
        if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentException("Entity ID cannot be empty.", nameof(entityId));
        if (string.IsNullOrWhiteSpace(targetLocale)) throw new ArgumentException("Target locale cannot be empty.", nameof(targetLocale));
        if (!_entities.TryGetValue(entityId, out EntityRecord? entity)) throw new KeyNotFoundException($"Unknown entity '{entityId}'.");
        if (!includeDraft && !string.Equals(entity.Status, "verified", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Entity '{entityId}' is not verified.");
        }

        if (entity.CanonicalNames.TryGetValue(targetLocale, out string? exact))
        {
            return new SlangGraphRenderedEntity(entityId, targetLocale, targetLocale, exact, false);
        }

        string language = GetLanguageFamily(targetLocale);
        KeyValuePair<string, string>? sameLanguage = entity.CanonicalNames
            .Where(item => string.Equals(GetLanguageFamily(item.Key), language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Cast<KeyValuePair<string, string>?>()
            .FirstOrDefault();
        if (sameLanguage is { } localized)
        {
            return new SlangGraphRenderedEntity(entityId, targetLocale, localized.Key, localized.Value, true);
        }

        if (entity.CanonicalNames.TryGetValue("en-US", out string? english))
        {
            return new SlangGraphRenderedEntity(entityId, targetLocale, "en-US", english, true);
        }

        KeyValuePair<string, string> fallback = entity.CanonicalNames.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).First();
        return new SlangGraphRenderedEntity(entityId, targetLocale, fallback.Key, fallback.Value, true);
    }

    private LocalizedRenderingRecord? ResolveRendering(
        string conceptId,
        string targetLocale,
        bool includeDraft)
    {
        LocalizedRenderingRecord[] eligible = _graph.Renderings
            .Where(rendering =>
                string.Equals(rendering.ConceptId, conceptId, StringComparison.OrdinalIgnoreCase) &&
                (includeDraft || string.Equals(rendering.Status, "verified", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        LocalizedRenderingRecord? exact = eligible.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        string language = GetLanguageFamily(targetLocale);
        LocalizedRenderingRecord? sameLanguage = eligible
            .Where(rendering => string.Equals(
                GetLanguageFamily(rendering.TargetLocale),
                language,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(rendering => rendering.TargetLocale, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return sameLanguage ?? eligible.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, "en-US", StringComparison.OrdinalIgnoreCase));
    }

    private string RenderTemplate(
        LocalizedRenderingRecord rendering,
        IReadOnlyDictionary<string, IReadOnlyList<SlangGraphSlotValue>>? slots)
    {
        if (string.IsNullOrWhiteSpace(rendering.SlotTemplate) || slots is null)
        {
            return rendering.ShortText;
        }

        string[] placeholders = PlaceholderRegex.Matches(rendering.SlotTemplate)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (placeholders.Any(name =>
                !slots.TryGetValue(name, out IReadOnlyList<SlangGraphSlotValue>? values) ||
                values.Count == 0))
        {
            return rendering.ShortText;
        }

        return PlaceholderRegex.Replace(rendering.SlotTemplate, match =>
        {
            string name = match.Groups["name"].Value;
            IReadOnlyList<SlangGraphSlotValue> values = slots[name];
            string separator = GetLanguageFamily(rendering.TargetLocale) is "zh" or "ja" ? "、" : ", ";
            return string.Join(separator, values.Select(value => ResolveSlotValue(value, rendering.TargetLocale)));
        });
    }

    private string ResolveSlotValue(SlangGraphSlotValue value, string targetLocale)
    {
        if (value.EntityId is null || !_entities.TryGetValue(value.EntityId, out EntityRecord? entity))
        {
            return value.Value;
        }

        if (entity.CanonicalNames.TryGetValue(targetLocale, out string? exact))
        {
            return exact;
        }

        string language = GetLanguageFamily(targetLocale);
        string? sameLanguage = entity.CanonicalNames
            .Where(item => string.Equals(GetLanguageFamily(item.Key), language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Value)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(sameLanguage))
        {
            return sameLanguage;
        }

        return entity.CanonicalNames.TryGetValue("en-US", out string? english)
            ? english
            : value.MatchedText;
    }

    private static string GetLanguageFamily(string locale)
    {
        int separator = locale.IndexOf('-');
        return separator < 0 ? locale : locale[..separator];
    }
}
