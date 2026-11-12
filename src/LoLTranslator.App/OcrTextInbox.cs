using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLTranslator.App;

/// <summary>
/// OCR 层交给数据层的一条原始文本收件记录。
/// 不保存截图绝对路径、玩家 ID 或其他不必要的可识别信息。
/// </summary>
public sealed record OcrTextInboxRecord(
    [property: JsonPropertyName("inbox_id")] string InboxId,
    [property: JsonPropertyName("capture_reference")] string CaptureReference,
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("source_language_tag")] string SourceLanguageTag,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("game_version")] string? GameVersion,
    [property: JsonPropertyName("redaction_status")] string RedactionStatus,
    [property: JsonPropertyName("intake_status")] string IntakeStatus,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("notes")] string? Notes = null);

/// <summary>
/// 进入人工标注前的候选项。它不包含 semantic labels，不能被误当作已标注数据。
/// </summary>
public sealed record ChatAnnotationCandidate(
    string CandidateId,
    string SampleId,
    string RawText,
    string Locale,
    string? Region,
    string? GameVersion,
    IReadOnlyList<string> SourceIds,
    string IntakeReference);

/// <summary>
/// 供人工审核的候选语义。Suggestion 不会修改主词典、标注集或复核状态。
/// </summary>
public sealed record ChatSemanticSuggestion(
    string ConceptId,
    string MatchKind,
    string SourceRecordId,
    IReadOnlyDictionary<string, string> Slots,
    double Confidence,
    bool RequiresHumanReview = true);

public sealed record InboxTriageResult(
    ChatAnnotationCandidate Candidate,
    IReadOnlyList<ChatSemanticSuggestion> Suggestions);

public static class OcrTextInboxLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<OcrTextInboxRecord> LoadFromJsonl(
        string filePath,
        LolSlangDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("OCR 文本收件箱路径不能为空。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到 OCR 文本收件箱。", filePath);
        }

        var items = new List<OcrTextInboxRecord>();
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
                items.Add(
                    JsonSerializer.Deserialize<OcrTextInboxRecord>(line, JsonOptions)
                    ?? throw new FormatException("无法读取 OCR 收件记录。"));
            }
            catch (JsonException exception)
            {
                throw new FormatException($"第 {lineNumber} 行 OCR 收件 JSON 无效。", exception);
            }
            catch (FormatException exception)
            {
                throw new FormatException($"第 {lineNumber} 行 OCR 收件记录无效：{exception.Message}", exception);
            }
        }

        IReadOnlyList<string> errors = Validate(items, dataset);

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "OCR 文本收件箱校验失败：" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return items;
    }

    public static IReadOnlyList<ChatAnnotationCandidate> CreateCandidates(
        IEnumerable<OcrTextInboxRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records
            .Where(record =>
                record.RedactionStatus == "redacted" &&
                record.IntakeStatus is "new" or "queued")
            .Select(record => new ChatAnnotationCandidate(
                record.InboxId,
                $"sample.{record.InboxId}",
                record.RawText,
                record.SourceLanguageTag,
                record.Region,
                record.GameVersion,
                record.SourceIds,
                record.CaptureReference))
            .ToArray();
    }

    public static IReadOnlyList<string> Validate(
        IEnumerable<OcrTextInboxRecord> records,
        LolSlangDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(dataset);

        OcrTextInboxRecord[] items = records.ToArray();
        var errors = new List<string>();
        var sourceIds = dataset.Sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string duplicate in items
                     .GroupBy(item => item.InboxId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate inbox_id '{duplicate}'.");
        }

        foreach (OcrTextInboxRecord item in items)
        {
            if (string.IsNullOrWhiteSpace(item.RawText))
            {
                errors.Add($"inbox {item.InboxId} has empty raw_text.");
            }

            if (string.IsNullOrWhiteSpace(item.SourceLanguageTag))
            {
                errors.Add($"inbox {item.InboxId} has empty source_language_tag.");
            }

            if (item.RedactionStatus is not ("redacted" or "unknown" or "contains_identifiers"))
            {
                errors.Add($"inbox {item.InboxId} has invalid redaction_status '{item.RedactionStatus}'.");
            }

            if (item.IntakeStatus is not ("new" or "queued" or "rejected"))
            {
                errors.Add($"inbox {item.InboxId} has invalid intake_status '{item.IntakeStatus}'.");
            }

            foreach (string sourceId in item.SourceIds)
            {
                if (!sourceIds.Contains(sourceId))
                {
                    errors.Add($"inbox {item.InboxId} references unknown source_id '{sourceId}'.");
                }
            }
        }

        return errors;
    }
}

public static class OcrTextInboxTriageService
{
    public static IReadOnlyList<InboxTriageResult> CreateSuggestions(
        LolSlangCatalog catalog,
        IEnumerable<OcrTextInboxRecord> records,
        bool includeDraft = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(records);

        var matcher = new CatalogSemanticMatcher(catalog);
        IReadOnlyList<ChatAnnotationCandidate> candidates =
            OcrTextInboxLoader.CreateCandidates(records);

        return candidates
            .Select(candidate => new InboxTriageResult(
                candidate,
                matcher.Match(candidate.RawText, candidate.Locale, includeDraft)
                    .Select(match => new ChatSemanticSuggestion(
                        match.ConceptId,
                        match.MatchKind,
                        match.SourceRecordId,
                        match.Slots,
                        match.Confidence))
                    .ToArray()))
            .ToArray();
    }
}
