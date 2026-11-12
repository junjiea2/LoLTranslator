using System.IO;
using System.Text.Json;

namespace SlangGraph.Core;

public static class SlangGraphJsonl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static SlangGraphPack Load(string filePath, bool validate = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Pack path cannot be empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Slang graph pack was not found.", filePath);
        }

        PackHeader? header = null;
        var domains = new List<DomainRecord>();
        var concepts = new List<ConceptRecord>();
        var expressions = new List<ExpressionRecord>();
        var patterns = new List<PatternRecord>();
        var entities = new List<EntityRecord>();
        var relations = new List<RelationRecord>();
        var renderings = new List<LocalizedRenderingRecord>();
        var sources = new List<SourceRecord>();
        var evidence = new List<EvidenceRecord>();
        var reviews = new List<ReviewRecord>();
        var observations = new List<ObservationRecord>();
        int lineNumber = 0;

        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);

                if (!document.RootElement.TryGetProperty("record_type", out JsonElement typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("record_type is required.");
                }

                switch (typeElement.GetString())
                {
                    case "pack":
                        if (header is not null)
                        {
                            throw new FormatException("only one pack header is allowed.");
                        }

                        header = Deserialize<PackHeader>(line);
                        break;
                    case "domain":
                        domains.Add(Deserialize<DomainRecord>(line));
                        break;
                    case "concept":
                        concepts.Add(Deserialize<ConceptRecord>(line));
                        break;
                    case "expression":
                        expressions.Add(Deserialize<ExpressionRecord>(line));
                        break;
                    case "pattern":
                        patterns.Add(Deserialize<PatternRecord>(line));
                        break;
                    case "entity":
                        entities.Add(Deserialize<EntityRecord>(line));
                        break;
                    case "relation":
                        relations.Add(Deserialize<RelationRecord>(line));
                        break;
                    case "rendering":
                        renderings.Add(Deserialize<LocalizedRenderingRecord>(line));
                        break;
                    case "source":
                        sources.Add(Deserialize<SourceRecord>(line));
                        break;
                    case "evidence":
                        evidence.Add(Deserialize<EvidenceRecord>(line));
                        break;
                    case "review":
                        reviews.Add(Deserialize<ReviewRecord>(line));
                        break;
                    case "observation":
                        observations.Add(Deserialize<ObservationRecord>(line));
                        break;
                    default:
                        throw new FormatException($"unsupported record_type '{typeElement.GetString()}'.");
                }
            }
            catch (JsonException exception)
            {
                throw new FormatException($"Invalid JSON on line {lineNumber}.", exception);
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Invalid pack record on line {lineNumber}: {exception.Message}", exception);
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("The pack header is missing.");
        }

        var pack = new SlangGraphPack(
            header,
            domains,
            concepts,
            expressions,
            patterns,
            entities,
            relations,
            renderings,
            sources,
            evidence,
            reviews,
            observations);

        if (!validate)
        {
            return pack;
        }

        IReadOnlyList<string> errors = SlangGraphValidator.Validate(pack);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Slang graph validation failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return pack;
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new FormatException($"record cannot be deserialized as {typeof(T).Name}.");
    }
}

public static class SlangGraphValidator
{
    private static readonly HashSet<string> AllowedStatuses =
        new(new[] { "draft", "pending", "verified", "rejected", "deprecated" }, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedSlotValueKinds =
        new(new[] { "entity_ref", "number", "duration", "enum", "text" }, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedReviewDecisions =
        new(new[] { "approved", "rejected", "needs_changes" }, StringComparer.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex SlotPlaceholderRegex =
        new(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(SlangGraphPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var errors = new List<string>();
        var domainIds = pack.Domains.Select(item => item.DomainId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conceptIds = pack.Concepts.Select(item => item.ConceptId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceIds = pack.Sources.Select(item => item.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidenceIds = pack.Evidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewIds = pack.Reviews.Select(item => item.ReviewId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observationIds = pack.Observations.Select(item => item.ObservationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodeIds = new HashSet<string>(conceptIds, StringComparer.OrdinalIgnoreCase);

        nodeIds.UnionWith(pack.Expressions.Select(item => item.ExpressionId));
        nodeIds.UnionWith(pack.Patterns.Select(item => item.PatternId));
        nodeIds.UnionWith(pack.Entities.Select(item => item.EntityId));
        nodeIds.UnionWith(pack.Renderings.Select(item => item.RenderingId));
        nodeIds.UnionWith(pack.Observations.Select(item => item.ObservationId));

        AddDuplicateErrors(errors, "domain_id", pack.Domains.Select(item => item.DomainId));
        AddDuplicateErrors(errors, "concept_id", pack.Concepts.Select(item => item.ConceptId));
        AddDuplicateErrors(errors, "expression_id", pack.Expressions.Select(item => item.ExpressionId));
        AddDuplicateErrors(errors, "pattern_id", pack.Patterns.Select(item => item.PatternId));
        AddDuplicateErrors(errors, "entity_id", pack.Entities.Select(item => item.EntityId));
        AddDuplicateErrors(errors, "relation_id", pack.Relations.Select(item => item.RelationId));
        AddDuplicateErrors(errors, "rendering_id", pack.Renderings.Select(item => item.RenderingId));
        AddDuplicateErrors(errors, "source_id", pack.Sources.Select(item => item.SourceId));
        AddDuplicateErrors(errors, "evidence_id", pack.Evidence.Select(item => item.EvidenceId));
        AddDuplicateErrors(errors, "review_id", pack.Reviews.Select(item => item.ReviewId));
        AddDuplicateErrors(errors, "observation_id", pack.Observations.Select(item => item.ObservationId));

        foreach (DomainRecord domain in pack.Domains)
        {
            if (domain.ParentDomainId is not null && !domainIds.Contains(domain.ParentDomainId))
            {
                errors.Add($"domain {domain.DomainId} references unknown parent_domain_id '{domain.ParentDomainId}'.");
            }
        }

        foreach (ConceptRecord concept in pack.Concepts)
        {
            RequireDomain(errors, domainIds, concept.DomainId, $"concept {concept.ConceptId}");
            RequireEvidence(errors, evidenceIds, concept.EvidenceIds, $"concept {concept.ConceptId}");
            RequireStatus(errors, concept.Status, $"concept {concept.ConceptId}");
            AddDuplicateErrors(errors, $"slot name in {concept.ConceptId}", concept.SlotSchema.Select(slot => slot.Name));

            foreach (SlotDefinition slot in concept.SlotSchema)
            {
                if (!AllowedSlotValueKinds.Contains(slot.ValueKind))
                {
                    errors.Add($"concept {concept.ConceptId} slot '{slot.Name}' has unsupported value_kind '{slot.ValueKind}'.");
                }

                if (string.Equals(slot.ValueKind, "enum", StringComparison.OrdinalIgnoreCase) &&
                    (slot.EnumValues is null || slot.EnumValues.Count == 0))
                {
                    errors.Add($"concept {concept.ConceptId} enum slot '{slot.Name}' must declare enum_values.");
                }

                if (!string.Equals(slot.ValueKind, "enum", StringComparison.OrdinalIgnoreCase) &&
                    slot.EnumValues is { Count: > 0 })
                {
                    errors.Add($"concept {concept.ConceptId} non-enum slot '{slot.Name}' cannot declare enum_values.");
                }

                if (slot.EntityTypes is { Count: > 0 })
                {
                    if (slot.EntityTypes.Any(type => string.IsNullOrWhiteSpace(type)))
                    {
                        errors.Add($"concept {concept.ConceptId} slot '{slot.Name}' cannot declare an empty entity type.");
                    }

                    AddDuplicateErrors(
                        errors,
                        $"entity type in {concept.ConceptId} slot '{slot.Name}'",
                        slot.EntityTypes);
                }

                if (!string.Equals(slot.ValueKind, "entity_ref", StringComparison.OrdinalIgnoreCase) &&
                    (slot.EntityTypes is { Count: > 0 } || !string.IsNullOrWhiteSpace(slot.EntityType)))
                {
                    errors.Add($"concept {concept.ConceptId} non-entity_ref slot '{slot.Name}' cannot declare entity types.");
                }
            }
        }

        foreach (ExpressionRecord expression in pack.Expressions)
        {
            RequireDomain(errors, domainIds, expression.DomainId, $"expression {expression.ExpressionId}");
            RequireConcept(errors, conceptIds, expression.ConceptId, $"expression {expression.ExpressionId}");
            RequireEvidence(errors, evidenceIds, expression.EvidenceIds, $"expression {expression.ExpressionId}");
            RequireStatus(errors, expression.Status, $"expression {expression.ExpressionId}");
        }

        foreach (PatternRecord pattern in pack.Patterns)
        {
            RequireDomain(errors, domainIds, pattern.DomainId, $"pattern {pattern.PatternId}");
            RequireConcept(errors, conceptIds, pattern.ConceptId, $"pattern {pattern.PatternId}");
            RequireEvidence(errors, evidenceIds, pattern.EvidenceIds, $"pattern {pattern.PatternId}");
            RequireStatus(errors, pattern.Status, $"pattern {pattern.PatternId}");

            ConceptRecord? concept = pack.Concepts.FirstOrDefault(item =>
                string.Equals(item.ConceptId, pattern.ConceptId, StringComparison.OrdinalIgnoreCase));
            HashSet<string> declaredSlots = concept?.SlotSchema
                .Select(slot => slot.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            foreach (string slot in pattern.Slots.Where(slot => !declaredSlots.Contains(slot)))
            {
                errors.Add($"pattern {pattern.PatternId} uses undeclared slot '{slot}'.");
            }

            AddDuplicateErrors(errors, $"slot in pattern {pattern.PatternId}", pattern.Slots);
            string[] placeholders = SlotPlaceholderRegex.Matches(pattern.Pattern)
                .Select(match => match.Groups["name"].Value)
                .ToArray();
            AddDuplicateErrors(errors, $"placeholder in pattern {pattern.PatternId}", placeholders);

            foreach (string placeholder in placeholders.Where(slot => !pattern.Slots.Contains(slot, StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add($"pattern {pattern.PatternId} has placeholder '{placeholder}' missing from slots.");
            }

            foreach (string slot in pattern.Slots.Where(slot => !placeholders.Contains(slot, StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add($"pattern {pattern.PatternId} declares slot '{slot}' but has no matching placeholder.");
            }
        }

        foreach (EntityRecord entity in pack.Entities)
        {
            RequireDomain(errors, domainIds, entity.DomainId, $"entity {entity.EntityId}");
            RequireEvidence(errors, evidenceIds, entity.EvidenceIds, $"entity {entity.EntityId}");
            RequireStatus(errors, entity.Status, $"entity {entity.EntityId}");
        }

        foreach (RelationRecord relation in pack.Relations)
        {
            if (!nodeIds.Contains(relation.FromId))
            {
                errors.Add($"relation {relation.RelationId} references unknown from_id '{relation.FromId}'.");
            }

            if (!nodeIds.Contains(relation.ToId))
            {
                errors.Add($"relation {relation.RelationId} references unknown to_id '{relation.ToId}'.");
            }

            RequireEvidence(errors, evidenceIds, relation.EvidenceIds, $"relation {relation.RelationId}");
            RequireStatus(errors, relation.Status, $"relation {relation.RelationId}");
        }

        foreach (LocalizedRenderingRecord rendering in pack.Renderings)
        {
            RequireConcept(errors, conceptIds, rendering.ConceptId, $"rendering {rendering.RenderingId}");
            RequireEvidence(errors, evidenceIds, rendering.EvidenceIds, $"rendering {rendering.RenderingId}");
            RequireStatus(errors, rendering.Status, $"rendering {rendering.RenderingId}");

            if (string.IsNullOrWhiteSpace(rendering.TargetLocale))
            {
                errors.Add($"rendering {rendering.RenderingId} must declare target_locale.");
            }

            if (string.IsNullOrWhiteSpace(rendering.ShortText))
            {
                errors.Add($"rendering {rendering.RenderingId} must declare short_text.");
            }

            if (string.IsNullOrWhiteSpace(rendering.Explanation))
            {
                errors.Add($"rendering {rendering.RenderingId} must declare explanation.");
            }

            ConceptRecord? concept = pack.Concepts.FirstOrDefault(item =>
                string.Equals(item.ConceptId, rendering.ConceptId, StringComparison.OrdinalIgnoreCase));
            HashSet<string> declaredSlots = concept?.SlotSchema
                .Select(slot => slot.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            string[] placeholders = SlotPlaceholderRegex.Matches(rendering.SlotTemplate ?? string.Empty)
                .Select(match => match.Groups["name"].Value)
                .ToArray();

            AddDuplicateErrors(errors, $"placeholder in rendering {rendering.RenderingId}", placeholders);

            foreach (string placeholder in placeholders.Where(slot => !declaredSlots.Contains(slot)))
            {
                errors.Add($"rendering {rendering.RenderingId} uses undeclared slot '{placeholder}'.");
            }
        }

        foreach (IGrouping<string, LocalizedRenderingRecord> duplicate in pack.Renderings
                     .GroupBy(
                         rendering => $"{rendering.ConceptId}\u001F{rendering.TargetLocale}",
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            LocalizedRenderingRecord first = duplicate.First();
            errors.Add(
                $"duplicate rendering for concept '{first.ConceptId}' and target_locale '{first.TargetLocale}'.");
        }

        foreach (EvidenceRecord item in pack.Evidence)
        {
            if (!nodeIds.Contains(item.SubjectId))
            {
                errors.Add($"evidence {item.EvidenceId} references unknown subject_id '{item.SubjectId}'.");
            }

            if (!sourceIds.Contains(item.SourceId))
            {
                errors.Add($"evidence {item.EvidenceId} references unknown source_id '{item.SourceId}'.");
            }

            RequireStatus(errors, item.ReviewStatus, $"evidence {item.EvidenceId}");
        }

        foreach (ReviewRecord review in pack.Reviews)
        {
            if (!nodeIds.Contains(review.SubjectId) && !evidenceIds.Contains(review.SubjectId))
            {
                errors.Add($"review {review.ReviewId} references unknown subject_id '{review.SubjectId}'.");
            }

            if (!AllowedReviewDecisions.Contains(review.Decision))
            {
                errors.Add($"review {review.ReviewId} has invalid decision '{review.Decision}'.");
            }

            if (string.IsNullOrWhiteSpace(review.ReviewerId))
            {
                errors.Add($"review {review.ReviewId} must declare reviewer_id.");
            }

            if (review.SupersedesReviewId is not null && !reviewIds.Contains(review.SupersedesReviewId))
            {
                errors.Add($"review {review.ReviewId} references unknown supersedes_review_id '{review.SupersedesReviewId}'.");
            }
        }

        foreach (ReviewRecord review in pack.Reviews.Where(item => item.SupersedesReviewId is not null))
        {
            ReviewRecord? superseded = pack.Reviews.FirstOrDefault(item =>
                string.Equals(item.ReviewId, review.SupersedesReviewId, StringComparison.OrdinalIgnoreCase));

            if (superseded is not null && !string.Equals(superseded.SubjectId, review.SubjectId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"review {review.ReviewId} cannot supersede a review for another subject.");
            }
        }

        foreach (ObservationRecord observation in pack.Observations)
        {
            RequireDomain(errors, domainIds, observation.DomainId, $"observation {observation.ObservationId}");
            RequireSources(errors, sourceIds, observation.SourceIds, $"observation {observation.ObservationId}");
            RequireStatus(errors, observation.ReviewStatus, $"observation {observation.ObservationId}");

            if (observation.SupersedesObservationId is not null &&
                !observationIds.Contains(observation.SupersedesObservationId))
            {
                errors.Add(
                    $"observation {observation.ObservationId} references unknown supersedes_observation_id " +
                    $"'{observation.SupersedesObservationId}'.");
            }

            if (string.Equals(
                    observation.ObservationId,
                    observation.SupersedesObservationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"observation {observation.ObservationId} cannot supersede itself.");
            }

            foreach (ObservationLabel label in observation.Labels)
            {
                RequireConcept(errors, conceptIds, label.ConceptId, $"observation {observation.ObservationId}");

                if (label.Polarity is not ("positive" or "negative" or "uncertain"))
                {
                    errors.Add($"observation {observation.ObservationId} has invalid polarity '{label.Polarity}'.");
                }

                if (label.Confidence is < 0 or > 1)
                {
                    errors.Add($"observation {observation.ObservationId} has confidence outside [0, 1].");
                }
            }
        }

        return errors;
    }

    private static void AddDuplicateErrors(ICollection<string> errors, string field, IEnumerable<string> values)
    {
        foreach (string duplicate in values
                     .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate {field} '{duplicate}'.");
        }
    }

    private static void RequireDomain(ICollection<string> errors, ISet<string> ids, string id, string owner)
    {
        if (!ids.Contains(id)) errors.Add($"{owner} references unknown domain_id '{id}'.");
    }

    private static void RequireConcept(ICollection<string> errors, ISet<string> ids, string id, string owner)
    {
        if (!ids.Contains(id)) errors.Add($"{owner} references unknown concept_id '{id}'.");
    }

    private static void RequireEvidence(ICollection<string> errors, ISet<string> ids, IEnumerable<string> references, string owner)
    {
        foreach (string id in references.Where(id => !ids.Contains(id)))
        {
            errors.Add($"{owner} references unknown evidence_id '{id}'.");
        }
    }

    private static void RequireSources(ICollection<string> errors, ISet<string> ids, IEnumerable<string> references, string owner)
    {
        foreach (string id in references.Where(id => !ids.Contains(id)))
        {
            errors.Add($"{owner} references unknown source_id '{id}'.");
        }
    }

    private static void RequireStatus(ICollection<string> errors, string status, string owner)
    {
        if (!AllowedStatuses.Contains(status)) errors.Add($"{owner} has invalid status '{status}'.");
    }
}
