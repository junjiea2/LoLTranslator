using System.Text.Json.Serialization;

namespace LoLTranslator.App;

/// <summary>
/// 跨语言数据层的根模型。JSONL 中的每条记录对应其中一种记录类型。
/// 所有字段都使用字符串，便于后续扩展新语言、地区和数据来源。
/// </summary>
public sealed record LolSlangDataset(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("dataset_id")] string DatasetId,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("concepts")] IReadOnlyList<SlangConcept> Concepts,
    [property: JsonPropertyName("expressions")] IReadOnlyList<SlangExpression> Expressions,
    [property: JsonPropertyName("templates")] IReadOnlyList<SlangTemplate> Templates,
    [property: JsonPropertyName("entities")] IReadOnlyList<GameEntity> Entities,
    [property: JsonPropertyName("relations")] IReadOnlyList<SemanticRelation> Relations,
    [property: JsonPropertyName("examples")] IReadOnlyList<TextExample> Examples,
    [property: JsonPropertyName("sources")] IReadOnlyList<DataSource> Sources);

public sealed record SlangDatasetHeader(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("dataset_id")] string DatasetId,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion);

public sealed record SlangConcept(
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("slots")] IReadOnlyList<string> Slots,
    [property: JsonPropertyName("urgency")] string Urgency,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record SlangExpression(
    [property: JsonPropertyName("expression_id")] string ExpressionId,
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("normalized")] string Normalized,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("register")] string Register,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record SlangTemplate(
    [property: JsonPropertyName("template_id")] string TemplateId,
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("slots")] IReadOnlyList<string> Slots,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record GameEntity(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("canonical_names")] IReadOnlyDictionary<string, string> CanonicalNames,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record SemanticRelation(
    [property: JsonPropertyName("relation_id")] string RelationId,
    [property: JsonPropertyName("from_id")] string FromId,
    [property: JsonPropertyName("relation_type")] string RelationType,
    [property: JsonPropertyName("to_id")] string ToId,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record TextExample(
    [property: JsonPropertyName("example_id")] string ExampleId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("concept_id")] string? ConceptId,
    [property: JsonPropertyName("slots")] IReadOnlyDictionary<string, string> Slots,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("status")] string Status = "draft");

public sealed record DataSource(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("game_version")] string? GameVersion,
    [property: JsonPropertyName("usage_context")] string UsageContext,
    [property: JsonPropertyName("reliability")] string Reliability,
    [property: JsonPropertyName("human_verified")] bool HumanVerified,
    [property: JsonPropertyName("collection_policy")] string CollectionPolicy = "manual_review_only",
    [property: JsonPropertyName("collected_at")] DateTimeOffset? CollectedAt = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public static class LolSlangDatasetValidator
{
    public static IReadOnlyList<string> Validate(LolSlangDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var errors = new List<string>();
        var conceptIds = dataset.Concepts.Select(item => item.ConceptId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceIds = dataset.Sources.Select(item => item.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allIds = new HashSet<string>(conceptIds, StringComparer.OrdinalIgnoreCase);

        AddDuplicateErrors(errors, "concept_id", dataset.Concepts.Select(item => item.ConceptId));
        AddDuplicateErrors(errors, "expression_id", dataset.Expressions.Select(item => item.ExpressionId));
        AddDuplicateErrors(errors, "template_id", dataset.Templates.Select(item => item.TemplateId));
        AddDuplicateErrors(errors, "entity_id", dataset.Entities.Select(item => item.EntityId));
        AddDuplicateErrors(errors, "relation_id", dataset.Relations.Select(item => item.RelationId));
        AddDuplicateErrors(errors, "example_id", dataset.Examples.Select(item => item.ExampleId));
        AddDuplicateErrors(errors, "source_id", dataset.Sources.Select(item => item.SourceId));

        foreach (SlangExpression expression in dataset.Expressions)
        {
            RequireConcept(errors, conceptIds, expression.ConceptId, $"expression {expression.ExpressionId}");
            RequireSources(errors, sourceIds, expression.SourceIds, $"expression {expression.ExpressionId}");
        }

        foreach (SlangTemplate template in dataset.Templates)
        {
            RequireConcept(errors, conceptIds, template.ConceptId, $"template {template.TemplateId}");
            RequireSources(errors, sourceIds, template.SourceIds, $"template {template.TemplateId}");
        }

        foreach (GameEntity entity in dataset.Entities)
        {
            RequireSources(errors, sourceIds, entity.SourceIds, $"entity {entity.EntityId}");
            allIds.Add(entity.EntityId);
        }

        foreach (SemanticRelation relation in dataset.Relations)
        {
            if (!allIds.Contains(relation.FromId))
            {
                errors.Add($"relation {relation.RelationId} references unknown from_id '{relation.FromId}'.");
            }

            if (!allIds.Contains(relation.ToId))
            {
                errors.Add($"relation {relation.RelationId} references unknown to_id '{relation.ToId}'.");
            }
        }

        foreach (TextExample example in dataset.Examples)
        {
            if (example.ConceptId is not null)
            {
                RequireConcept(errors, conceptIds, example.ConceptId, $"example {example.ExampleId}");
            }

            RequireSources(errors, sourceIds, example.SourceIds, $"example {example.ExampleId}");
        }

        return errors;
    }

    private static void AddDuplicateErrors(
        ICollection<string> errors,
        string fieldName,
        IEnumerable<string> values)
    {
        foreach (string duplicate in values
                     .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate {fieldName} '{duplicate}'.");
        }
    }

    private static void RequireConcept(
        ICollection<string> errors,
        ISet<string> conceptIds,
        string conceptId,
        string owner)
    {
        if (!conceptIds.Contains(conceptId))
        {
            errors.Add($"{owner} references unknown concept_id '{conceptId}'.");
        }
    }

    private static void RequireSources(
        ICollection<string> errors,
        ISet<string> sourceIds,
        IEnumerable<string> referencedIds,
        string owner)
    {
        foreach (string sourceId in referencedIds)
        {
            if (!sourceIds.Contains(sourceId))
            {
                errors.Add($"{owner} references unknown source_id '{sourceId}'.");
            }
        }
    }
}
