using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// A user-approved shadow result intake record. It is append-only local feedback,
/// not an Observation, Evidence, Review, or a change to a published Pack.
/// </summary>
public sealed record ShadowRuntimeFeedbackRecord(
    [property: JsonPropertyName("feedback_id")] string FeedbackId,
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("capture_reference")] string CaptureReference,
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("pack_version")] string PackVersion,
    [property: JsonPropertyName("pack_sha256")] string PackSha256,
    [property: JsonPropertyName("rendering_pack_id")] string RenderingPackId,
    [property: JsonPropertyName("rendering_pack_version")] string RenderingPackVersion,
    [property: JsonPropertyName("rendering_pack_sha256")] string RenderingPackSha256,
    [property: JsonPropertyName("record_origin")] string RecordOrigin,
    [property: JsonPropertyName("fixture_status")] string FixtureStatus,
    [property: JsonPropertyName("formal_eligibility")] string FormalEligibility,
    [property: JsonPropertyName("ocr_candidate_locales")] IReadOnlyList<string> OcrCandidateLocales,
    [property: JsonPropertyName("ocr_candidate_outputs")] IReadOnlyList<string> OcrCandidateOutputs,
    [property: JsonPropertyName("fusion_mode")] string FusionMode,
    [property: JsonPropertyName("fused_line_count")] int FusedLineCount,
    [property: JsonPropertyName("fusion_ambiguity_count")] int FusionAmbiguityCount,
    [property: JsonPropertyName("fused_lines")] IReadOnlyList<ShadowRuntimeFeedbackFusedLine> FusedLines,
    [property: JsonPropertyName("semantic_locale")] string SemanticLocale,
    [property: JsonPropertyName("target_locale")] string TargetLocale,
    [property: JsonPropertyName("community")] string? Community,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("feedback_kind")] string FeedbackKind,
    [property: JsonPropertyName("matches")] IReadOnlyList<ShadowRuntimeFeedbackMatch> Matches,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("redaction_status")] string RedactionStatus,
    [property: JsonPropertyName("intake_status")] string IntakeStatus);

public sealed record ShadowRuntimeFeedbackMatch(
    [property: JsonPropertyName("concept_id")] string ConceptId,
    [property: JsonPropertyName("source_record_id")] string SourceRecordId,
    [property: JsonPropertyName("slots")] IReadOnlyDictionary<string, IReadOnlyList<string>> Slots,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("ocr_sources")] IReadOnlyList<ShadowRuntimeFeedbackOcrSource> OcrSources,
    [property: JsonPropertyName("fusion_status")] string FusionStatus,
    [property: JsonPropertyName("fusion_confidence")] double FusionConfidence,
    [property: JsonPropertyName("fusion_reasons")] IReadOnlyList<string> FusionReasons);

public sealed record ShadowRuntimeFeedbackFusedLine(
    [property: JsonPropertyName("line_number")] int LineNumber,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("fusion_status")] string FusionStatus,
    [property: JsonPropertyName("fusion_confidence")] double FusionConfidence,
    [property: JsonPropertyName("fusion_reasons")] IReadOnlyList<string> FusionReasons,
    [property: JsonPropertyName("ocr_sources")] IReadOnlyList<ShadowRuntimeFeedbackOcrSource> OcrSources);

public sealed record ShadowRuntimeFeedbackOcrSource(
    [property: JsonPropertyName("candidate_index")] int CandidateIndex,
    [property: JsonPropertyName("recognizer_language_tag")] string RecognizerLanguageTag,
    [property: JsonPropertyName("source_line_number")] int SourceLineNumber,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("text")] string Text);

public sealed record ShadowRuntimeFeedbackDraft(
    string CaptureReference,
    IReadOnlyList<OcrRecognitionResult> OcrCandidates,
    string SemanticLocale,
    string TargetLocale,
    ShadowMatchContextOption Context,
    IReadOnlyList<OcrShadowLineResult> Results);

public sealed record ShadowRuntimeFeedbackKindOption(string DisplayName, string Value);
public sealed record ShadowRuntimeFeedbackOriginOption(string DisplayName, string Value);

public static class ShadowRuntimeFeedbackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLTranslator",
        "shadow-runtime-feedback.v0.1.jsonl");

    public static string Append(
        ShadowRuntimeFeedbackDraft draft,
        string feedbackKind,
        string recordOrigin,
        string? notes)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(feedbackKind))
        {
            throw new ArgumentException("反馈类别不能为空。", nameof(feedbackKind));
        }

        ShadowRuntimeFeedbackRecord record = CreateRecord(draft, feedbackKind, recordOrigin, notes);
        string path = DefaultPath;
        string? directory = Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定本地反馈目录。");
        }

        Directory.CreateDirectory(directory);
        File.AppendAllText(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        return path;
    }

    public static ShadowRuntimeFeedbackRecord CreateRecord(
        ShadowRuntimeFeedbackDraft draft,
        string feedbackKind,
        string recordOrigin,
        string? notes)
    {
        ArgumentNullException.ThrowIfNull(draft);

        (string fixtureStatus, string formalEligibility) = OriginAttributes(recordOrigin);

        return new ShadowRuntimeFeedbackRecord(
            $"shadow-runtime.{Guid.NewGuid():N}",
            DateTimeOffset.Now,
            draft.CaptureReference,
            OcrShadowSemanticService.ExpectedPackId,
            OcrShadowSemanticService.ExpectedPackVersion,
            OcrShadowSemanticService.ExpectedPackSha256,
            OcrShadowSemanticService.ExpectedRenderingPackId,
            OcrShadowSemanticService.ExpectedRenderingPackVersion,
            OcrShadowSemanticService.ExpectedRenderingPackSha256,
            recordOrigin,
            fixtureStatus,
            formalEligibility,
            draft.OcrCandidates.Select(candidate => candidate.RecognizerLanguageTag).ToArray(),
            draft.OcrCandidates.Select(candidate => candidate.Text).ToArray(),
            "per_message_multi_ocr_v1",
            draft.Results.Count,
            draft.Results.Count(line => line.Fusion.IsAmbiguous),
            draft.Results.Select(line => new ShadowRuntimeFeedbackFusedLine(
                line.LineNumber,
                line.Text,
                line.Fusion.Status,
                line.Fusion.Confidence,
                line.Fusion.Reasons,
                ToFeedbackSources(line.Sources))).ToArray(),
            draft.SemanticLocale,
            draft.TargetLocale,
            draft.Context.Community,
            draft.Context.Channel,
            feedbackKind,
            draft.Results
                .SelectMany(line => line.Matches.Select(match => new { Line = line, Match = match }))
                .Select(item => new ShadowRuntimeFeedbackMatch(
                    item.Match.ConceptId,
                    item.Match.SourceRecordId,
                    item.Match.Slots.ToDictionary(
                        slot => slot.Key,
                        slot => (IReadOnlyList<string>)slot.Value.Select(value => value.Value).ToArray(),
                        StringComparer.OrdinalIgnoreCase),
                    item.Match.Evidence
                        .Where(evidence => evidence.Kind == "evidence_record")
                        .Select(evidence => evidence.RecordId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    item.Line.Sources.Select(source => new ShadowRuntimeFeedbackOcrSource(
                        source.CandidateIndex,
                        source.RecognizerLanguageTag,
                        source.SourceLineNumber,
                        source.Timestamp,
                        source.Text)).ToArray(),
                    item.Line.Fusion.Status,
                    item.Line.Fusion.Confidence,
                    item.Line.Fusion.Reasons))
                .ToArray(),
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            "unknown",
            "new");
    }

    private static (string FixtureStatus, string FormalEligibility) OriginAttributes(string recordOrigin) =>
        recordOrigin switch
        {
            "synthetic_fixture" => ("synthetic_only", "not_eligible"),
            "controlled_test" => ("controlled_only", "not_eligible"),
            "real_source" => ("real", "pending_gate"),
            _ => ("unknown", "unknown")
        };

    private static IReadOnlyList<ShadowRuntimeFeedbackOcrSource> ToFeedbackSources(
        IEnumerable<OcrShadowLineSource> sources) =>
        sources.Select(source => new ShadowRuntimeFeedbackOcrSource(
            source.CandidateIndex,
            source.RecognizerLanguageTag,
            source.SourceLineNumber,
            source.Timestamp,
            source.Text)).ToArray();
}
