using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLTranslator.App;

/// <summary>
/// 一条待人工复核的真实聊天样本。它与词典记录分离，避免把未确认样本直接当成表达词典。
/// </summary>
public sealed record ChatSampleAnnotation(
    [property: JsonPropertyName("annotation_id")] string AnnotationId,
    [property: JsonPropertyName("sample_id")] string SampleId,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("game_version")] string? GameVersion,
    [property: JsonPropertyName("sample_kind")] string SampleKind,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("labels")] IReadOnlyList<ChatSemanticLabel> Labels,
    [property: JsonPropertyName("review")] ChatAnnotationReview Review);

public sealed record ChatSemanticLabel(
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("polarity")] string Polarity,
    [property: JsonPropertyName("slots")] IReadOnlyDictionary<string, string> Slots,
    [property: JsonPropertyName("annotator_confidence")] double AnnotatorConfidence,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record ChatAnnotationReview(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reviewer_id")] string? ReviewerId,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset? ReviewedAt,
    [property: JsonPropertyName("notes")] string? Notes = null);

public static class ChatAnnotationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<ChatSampleAnnotation> LoadFromJsonl(
        string filePath,
        LolSlangDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("标注文件路径不能为空。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到聊天样本标注文件。", filePath);
        }

        var annotations = new List<ChatSampleAnnotation>();
        int lineNumber = 0;

        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ChatSampleAnnotation annotation;

            try
            {
                annotation = JsonSerializer.Deserialize<ChatSampleAnnotation>(line, JsonOptions)
                    ?? throw new FormatException("无法读取标注对象。");
            }
            catch (JsonException exception)
            {
                throw new FormatException($"第 {lineNumber} 行标注 JSON 无效。", exception);
            }
            catch (FormatException exception)
            {
                throw new FormatException($"第 {lineNumber} 行标注无效：{exception.Message}", exception);
            }

            annotations.Add(annotation);
        }

        IReadOnlyList<string> errors = Validate(annotations, dataset);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "聊天样本标注校验失败：" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return annotations;
    }

    public static IReadOnlyList<string> Validate(
        IEnumerable<ChatSampleAnnotation> annotations,
        LolSlangDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(dataset);

        var items = annotations.ToArray();
        var errors = new List<string>();
        var conceptIds = dataset.Concepts
            .Select(concept => concept.ConceptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceIds = dataset.Sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddDuplicateErrors(errors, "annotation_id", items.Select(item => item.AnnotationId));
        AddDuplicateErrors(errors, "sample_id", items.Select(item => item.SampleId));

        foreach (ChatSampleAnnotation annotation in items)
        {
            if (string.IsNullOrWhiteSpace(annotation.RawText))
            {
                errors.Add($"annotation {annotation.AnnotationId} has empty raw_text.");
            }

            RequireSources(errors, sourceIds, annotation.SourceIds, $"annotation {annotation.AnnotationId}");

            if (annotation.Labels.Count == 0)
            {
                errors.Add($"annotation {annotation.AnnotationId} has no labels.");
            }

            foreach (ChatSemanticLabel label in annotation.Labels)
            {
                if (!conceptIds.Contains(label.ConceptId))
                {
                    errors.Add($"annotation {annotation.AnnotationId} references unknown concept_id '{label.ConceptId}'.");
                }

                if (label.Polarity is not ("positive" or "negative" or "uncertain"))
                {
                    errors.Add($"annotation {annotation.AnnotationId} has invalid polarity '{label.Polarity}'.");
                }

                if (label.AnnotatorConfidence is < 0 or > 1)
                {
                    errors.Add($"annotation {annotation.AnnotationId} has confidence outside [0, 1].");
                }
            }

            if (annotation.Review.Status is not ("pending" or "verified" or "rejected"))
            {
                errors.Add($"annotation {annotation.AnnotationId} has invalid review status '{annotation.Review.Status}'.");
            }

            if (annotation.Review.Status == "verified" &&
                string.IsNullOrWhiteSpace(annotation.Review.ReviewerId))
            {
                errors.Add($"annotation {annotation.AnnotationId} is verified without reviewer_id.");
            }
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
