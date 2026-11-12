using System.Text.Json.Serialization;

namespace SlangGraph.Core;

public sealed record SlangGraphPack(
    PackHeader Header,
    IReadOnlyList<DomainRecord> Domains,
    IReadOnlyList<ConceptRecord> Concepts,
    IReadOnlyList<ExpressionRecord> Expressions,
    IReadOnlyList<PatternRecord> Patterns,
    IReadOnlyList<EntityRecord> Entities,
    IReadOnlyList<RelationRecord> Relations,
    IReadOnlyList<LocalizedRenderingRecord> Renderings,
    IReadOnlyList<SourceRecord> Sources,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyList<ReviewRecord> Reviews,
    IReadOnlyList<ObservationRecord> Observations);

public sealed record PackHeader(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("pack_version")] string PackVersion,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<PackDependency> Dependencies,
    [property: JsonPropertyName("status")] string Status);

public sealed record PackDependency(
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("version_range")] string VersionRange);

public sealed record DomainRecord(
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parent_domain_id")] string? ParentDomainId,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

public sealed record SlotDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value_kind")] string ValueKind,
    [property: JsonPropertyName("entity_type")] string? EntityType,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("multiple")] bool Multiple = false,
    [property: JsonPropertyName("enum_values")] IReadOnlyList<string>? EnumValues = null,
    [property: JsonPropertyName("entity_types")] IReadOnlyList<string>? EntityTypes = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveEntityTypes =>
        (EntityTypes ?? Array.Empty<string>())
        .Concat(EntityType is null ? Array.Empty<string>() : new[] { EntityType })
        .Where(type => !string.IsNullOrWhiteSpace(type))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record ConceptRecord(
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("slot_schema")] IReadOnlyList<SlotDefinition> SlotSchema,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record ExpressionRecord(
    [property: JsonPropertyName("expression_id")] string ExpressionId,
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("normalized")] string Normalized,
    [property: JsonPropertyName("community")] string? Community,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("register")] string Register,
    [property: JsonPropertyName("tone")] string? Tone,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record PatternRecord(
    [property: JsonPropertyName("pattern_id")] string PatternId,
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("slots")] IReadOnlyList<string> Slots,
    [property: JsonPropertyName("community")] string? Community,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record EntityAlias(
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("normalized")] string Normalized,
    [property: JsonPropertyName("community")] string? Community = null);

public sealed record EntityRecord(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("canonical_names")] IReadOnlyDictionary<string, string> CanonicalNames,
    [property: JsonPropertyName("aliases")] IReadOnlyList<EntityAlias> Aliases,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record RelationRecord(
    [property: JsonPropertyName("relation_id")] string RelationId,
    [property: JsonPropertyName("from_id")] string FromId,
    [property: JsonPropertyName("relation_type")] string RelationType,
    [property: JsonPropertyName("to_id")] string ToId,
    [property: JsonPropertyName("weight")] double? Weight,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// A reviewed target-language presentation for a stable semantic concept.
/// Expression locale and target locale are intentionally independent.
/// </summary>
public sealed record LocalizedRenderingRecord(
    [property: JsonPropertyName("rendering_id")] string RenderingId,
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("target_locale")] string TargetLocale,
    [property: JsonPropertyName("short_text")] string ShortText,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("slot_template")] string? SlotTemplate,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("status")] string Status);

public sealed record SourceRecord(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("collection_policy")] string CollectionPolicy,
    [property: JsonPropertyName("collected_at")] DateTimeOffset? CollectedAt,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record EvidenceRecord(
    [property: JsonPropertyName("evidence_id")] string EvidenceId,
    [property: JsonPropertyName("subject_id")] string SubjectId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("evidence_kind")] string EvidenceKind,
    [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("reviewer_id")] string? ReviewerId,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>
/// An append-only review decision. A newer decision may supersede an earlier review,
/// but the older record remains part of the pack's audit history.
/// </summary>
public sealed record ReviewRecord(
    [property: JsonPropertyName("review_id")] string ReviewId,
    [property: JsonPropertyName("subject_id")] string SubjectId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reviewer_id")] string ReviewerId,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset ReviewedAt,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("supersedes_review_id")] string? SupersedesReviewId = null);

public sealed record ObservationLabel(
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("polarity")] string Polarity,
    [property: JsonPropertyName("slots")] IReadOnlyDictionary<string, string> Slots,
    [property: JsonPropertyName("confidence")] double Confidence);

public sealed record ObservationRecord(
    [property: JsonPropertyName("observation_id")] string ObservationId,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("community")] string? Community,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("redaction_status")] string RedactionStatus,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("labels")] IReadOnlyList<ObservationLabel> Labels,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("context")] string? Context = null,
    [property: JsonPropertyName("supersedes_observation_id")] string? SupersedesObservationId = null);
