using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoLTranslator.App;

/// <summary>
/// Performs a local-only, read-only preflight of shadow feedback JSONL.
/// Its manifest deliberately contains no OCR/chat content and is never an
/// Observation, Evidence, Review, or data-intake record.
/// </summary>
public static class ShadowRuntimeFeedbackPreflight
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly HashSet<string> FeedbackKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "unrecognized_text", "false_positive", "slot_missing", "context_missing",
        "ocr_surface_variant", "fusion_ambiguous", "expected_behavior"
    };

    private static readonly HashSet<string> FusionStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "single_source", "fused", "ambiguous"
    };

    private static readonly HashSet<string> RedactionStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "redacted", "contains_identifiers"
    };

    private static readonly HashSet<string> IntakeStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "manifested", "blocked"
    };

    private static readonly Regex AbsolutePathPattern = new(
        @"(?ix)(?:\b[a-z]:[\\/]|\\\\[^\s\\/]+[\\/][^\s]+|file://|/(?:home|users|tmp)/|%userprofile%|~[\\/])",
        RegexOptions.Compiled);

    private static readonly Regex ChatNicknamePattern = new(
        @"(?x)(?:\[[^\]\r\n]{2,24}\]\s*[^:\r\n]{2,40}[:：]|\([^\)\r\n]{2,40}\)\s*[:：])",
        RegexOptions.Compiled);

    public static string DefaultManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLTranslator",
        "shadow-runtime-feedback-export-manifest.v0.1.json");

    public static ShadowRuntimeFeedbackExportManifest InspectFile(string feedbackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackPath);

        var entries = new List<ShadowRuntimeFeedbackPreflightEntry>();
        int sourceLineNumber = 0;

        foreach (string jsonLine in File.ReadLines(feedbackPath))
        {
            sourceLineNumber++;
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                continue;
            }

            try
            {
                ShadowRuntimeFeedbackRecord? record = JsonSerializer.Deserialize<ShadowRuntimeFeedbackRecord>(jsonLine, JsonOptions);
                if (record is null)
                {
                    entries.Add(InvalidJsonEntry(sourceLineNumber, "empty_json_record"));
                    continue;
                }

                entries.Add(InspectRecord(sourceLineNumber, record));
            }
            catch (JsonException)
            {
                entries.Add(InvalidJsonEntry(sourceLineNumber, "invalid_json"));
            }
        }

        return CreateManifest(Path.GetFileName(feedbackPath), entries);
    }

    public static ShadowRuntimeFeedbackExportManifest InspectRecords(
        string sourceFileName,
        IEnumerable<ShadowRuntimeFeedbackRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        ArgumentNullException.ThrowIfNull(records);

        return CreateManifest(
            Path.GetFileName(sourceFileName),
            records.Select((record, index) => InspectRecord(index + 1, record)).ToArray());
    }

    /// <summary>
    /// Writes a separate, content-free manifest. The feedback JSONL is only read and is never changed.
    /// </summary>
    public static string WriteManifest(ShadowRuntimeFeedbackExportManifest manifest, string? outputPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string path = outputPath ?? DefaultManifestPath;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Unable to determine local manifest directory.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    private static ShadowRuntimeFeedbackExportManifest CreateManifest(
        string sourceFileName,
        IReadOnlyList<ShadowRuntimeFeedbackPreflightEntry> entries) =>
        new(
            "shadow-runtime-feedback-export-manifest.v0.1",
            DateTimeOffset.UtcNow,
            sourceFileName,
            entries.Count(entry => entry.Disposition == "export_ready"),
            entries.Count(entry => entry.Disposition == "blocked"),
            entries);

    private static ShadowRuntimeFeedbackPreflightEntry InvalidJsonEntry(int sourceLineNumber, string reason) =>
        new(sourceLineNumber, null, "blocked", "unknown", "unknown",
            new[] { "manifest_only_not_observation_or_evidence" }, new[] { reason });

    private static ShadowRuntimeFeedbackPreflightEntry InspectRecord(
        int sourceLineNumber,
        ShadowRuntimeFeedbackRecord record)
    {
        var checklist = new List<string> { "manifest_only_not_observation_or_evidence" };
        var blockedReasons = new List<string>();

        CheckRequired(record, blockedReasons);
        CheckPackIdentity(record, checklist, blockedReasons);
        CheckOriginContract(record, checklist, blockedReasons);
        CheckOcrAndFusion(record, checklist, blockedReasons);
        CheckWorkflowFields(record, checklist, blockedReasons);
        CheckLocalIdentifiers(record, checklist, blockedReasons);

        if (blockedReasons.Count == 0)
        {
            checklist.Add("export_manifest_only");
        }

        return new ShadowRuntimeFeedbackPreflightEntry(
            sourceLineNumber,
            EmptyToNull(record.FeedbackId),
            blockedReasons.Count == 0 ? "export_ready" : "blocked",
            EmptyToUnknown(record.RecordOrigin),
            EmptyToUnknown(record.FormalEligibility),
            checklist.Distinct(StringComparer.Ordinal).ToArray(),
            blockedReasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void CheckRequired(ShadowRuntimeFeedbackRecord record, ICollection<string> blocked)
    {
        if (string.IsNullOrWhiteSpace(record.FeedbackId) || record.CapturedAt == default ||
            string.IsNullOrWhiteSpace(record.CaptureReference) ||
            string.IsNullOrWhiteSpace(record.PackId) || string.IsNullOrWhiteSpace(record.PackVersion) ||
            string.IsNullOrWhiteSpace(record.PackSha256) ||
            string.IsNullOrWhiteSpace(record.RenderingPackId) || string.IsNullOrWhiteSpace(record.RenderingPackVersion) ||
            string.IsNullOrWhiteSpace(record.RenderingPackSha256) ||
            string.IsNullOrWhiteSpace(record.SemanticLocale) || string.IsNullOrWhiteSpace(record.TargetLocale) ||
            string.IsNullOrWhiteSpace(record.Community) || string.IsNullOrWhiteSpace(record.Channel) ||
            string.IsNullOrWhiteSpace(record.FeedbackKind) ||
            record.OcrCandidateLocales is null || record.OcrCandidateLocales.Count == 0 ||
            record.OcrCandidateOutputs is null || record.OcrCandidateOutputs.Count == 0 ||
            record.FusedLines is null || record.FusedLines.Count == 0 || record.Matches is null)
        {
            blocked.Add("missing_required_field");
        }
    }

    private static void CheckPackIdentity(
        ShadowRuntimeFeedbackRecord record,
        ICollection<string> checklist,
        ICollection<string> blocked)
    {
        if (record.PackId == OcrShadowSemanticService.ExpectedPackId &&
            record.PackVersion == OcrShadowSemanticService.ExpectedPackVersion &&
            record.PackSha256 == OcrShadowSemanticService.ExpectedPackSha256 &&
            record.RenderingPackId == OcrShadowSemanticService.ExpectedRenderingPackId &&
            record.RenderingPackVersion == OcrShadowSemanticService.ExpectedRenderingPackVersion &&
            record.RenderingPackSha256 == OcrShadowSemanticService.ExpectedRenderingPackSha256)
        {
            checklist.Add("published_semantic_and_rendering_pack_identity_verified");
        }
        else
        {
            blocked.Add("published_pack_identity_mismatch_or_missing_hash");
        }
    }

    private static void CheckOriginContract(
        ShadowRuntimeFeedbackRecord record,
        ICollection<string> checklist,
        ICollection<string> blocked)
    {
        bool valid = record.RecordOrigin switch
        {
            "synthetic_fixture" => record.FixtureStatus == "synthetic_only" && record.FormalEligibility == "not_eligible",
            "controlled_test" => record.FixtureStatus == "controlled_only" && record.FormalEligibility == "not_eligible",
            "real_source" => record.FixtureStatus == "real" && record.FormalEligibility == "pending_gate",
            _ => false
        };

        if (!valid)
        {
            blocked.Add(string.IsNullOrWhiteSpace(record.RecordOrigin) || record.RecordOrigin == "unknown"
                ? "unknown_record_origin"
                : "record_origin_contract_mismatch");
            return;
        }

        checklist.Add("record_origin_and_formal_eligibility_contract_verified");
    }

    private static void CheckOcrAndFusion(
        ShadowRuntimeFeedbackRecord record,
        ICollection<string> checklist,
        ICollection<string> blocked)
    {
        if (record.OcrCandidateLocales is null || record.OcrCandidateOutputs is null ||
            record.OcrCandidateLocales.Count != record.OcrCandidateOutputs.Count ||
            record.OcrCandidateLocales.Any(string.IsNullOrWhiteSpace) || record.OcrCandidateOutputs.Any(string.IsNullOrWhiteSpace))
        {
            blocked.Add("ocr_candidate_sources_incomplete");
        }

        IReadOnlyList<ShadowRuntimeFeedbackFusedLine> lines = record.FusedLines ?? Array.Empty<ShadowRuntimeFeedbackFusedLine>();
        int ambiguousCount = 0;
        foreach (ShadowRuntimeFeedbackFusedLine line in lines)
        {
            if (line.LineNumber <= 0 || string.IsNullOrWhiteSpace(line.Text) ||
                !FusionStatuses.Contains(line.FusionStatus) || !IsUnitConfidence(line.FusionConfidence) ||
                line.FusionReasons is null ||
                line.OcrSources is null || line.OcrSources.Count == 0 ||
                line.OcrSources.Any(source => source.CandidateIndex < 0 ||
                    source.CandidateIndex >= (record.OcrCandidateLocales?.Count ?? 0) || source.SourceLineNumber <= 0 ||
                    string.IsNullOrWhiteSpace(source.RecognizerLanguageTag) || string.IsNullOrWhiteSpace(source.Text)))
            {
                blocked.Add("fusion_or_ocr_source_incomplete");
            }

            if (line.FusionStatus == "ambiguous")
            {
                ambiguousCount++;
                if (line.FusionReasons is null || line.FusionReasons.Count == 0)
                {
                    blocked.Add("fusion_ambiguous_reason_missing");
                }
            }
        }

        if (record.FusedLineCount != lines.Count || record.FusionAmbiguityCount != ambiguousCount)
        {
            blocked.Add("fusion_summary_count_mismatch");
        }

        foreach (ShadowRuntimeFeedbackMatch match in record.Matches ?? Array.Empty<ShadowRuntimeFeedbackMatch>())
        {
            if (string.IsNullOrWhiteSpace(match.ConceptId) || string.IsNullOrWhiteSpace(match.SourceRecordId) ||
                !FusionStatuses.Contains(match.FusionStatus) || !IsUnitConfidence(match.FusionConfidence) ||
                match.FusionReasons is null ||
                match.OcrSources is null || match.OcrSources.Count == 0 ||
                (match.FusionStatus == "ambiguous" && (match.FusionReasons is null || match.FusionReasons.Count == 0)))
            {
                blocked.Add("match_fusion_metadata_incomplete");
            }
        }

        checklist.Add(ambiguousCount > 0 ? "fusion_ambiguous_retained" : "fusion_metadata_verified");
    }

    private static void CheckWorkflowFields(
        ShadowRuntimeFeedbackRecord record,
        ICollection<string> checklist,
        ICollection<string> blocked)
    {
        if (!FeedbackKinds.Contains(record.FeedbackKind))
        {
            blocked.Add("unknown_feedback_kind");
        }

        if (!RedactionStatuses.Contains(record.RedactionStatus))
        {
            blocked.Add("unknown_redaction_status");
        }
        else if (record.RedactionStatus == "contains_identifiers")
        {
            blocked.Add("redaction_status_contains_identifiers");
        }
        else if (record.RecordOrigin == "real_source" && record.RedactionStatus != "redacted")
        {
            blocked.Add("real_source_requires_confirmed_redaction");
        }

        if (!IntakeStatuses.Contains(record.IntakeStatus))
        {
            blocked.Add("unknown_intake_status");
        }

        checklist.Add("feedback_kind_redaction_and_intake_status_checked");
    }

    private static void CheckLocalIdentifiers(
        ShadowRuntimeFeedbackRecord record,
        ICollection<string> checklist,
        ICollection<string> blocked)
    {
        IEnumerable<string> values = EnumerateSensitiveFields(record);
        foreach (string value in values)
        {
            if (AbsolutePathPattern.IsMatch(value))
            {
                blocked.Add("absolute_path_or_local_location_detected");
            }

            if (ContainsLocalMachineMarker(value))
            {
                blocked.Add("local_username_or_machine_marker_detected");
            }

            if (ChatNicknamePattern.IsMatch(value))
            {
                blocked.Add("unredacted_chat_nickname_detected");
            }
        }

        if (!blocked.Any(reason => reason.EndsWith("_detected", StringComparison.Ordinal)))
        {
            checklist.Add("local_identifier_scan_passed");
        }
    }

    private static IEnumerable<string> EnumerateSensitiveFields(ShadowRuntimeFeedbackRecord record)
    {
        yield return record.CaptureReference ?? string.Empty;
        yield return record.Notes ?? string.Empty;

        foreach (string output in record.OcrCandidateOutputs ?? Array.Empty<string>())
        {
            yield return output ?? string.Empty;
        }

        foreach (ShadowRuntimeFeedbackFusedLine line in record.FusedLines ?? Array.Empty<ShadowRuntimeFeedbackFusedLine>())
        {
            yield return line.Text ?? string.Empty;
            foreach (ShadowRuntimeFeedbackOcrSource source in line.OcrSources ?? Array.Empty<ShadowRuntimeFeedbackOcrSource>())
            {
                yield return source.Text ?? string.Empty;
            }
        }
    }

    private static bool ContainsLocalMachineMarker(string value)
    {
        string userName = Environment.UserName;
        string machineName = Environment.MachineName;
        return (!string.IsNullOrWhiteSpace(userName) && value.Contains(userName, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(machineName) && value.Contains(machineName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnitConfidence(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value is >= 0 and <= 1;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string EmptyToUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}

public sealed record ShadowRuntimeFeedbackExportManifest(
    string ManifestVersion,
    DateTimeOffset GeneratedAt,
    string SourceFileName,
    int ExportReadyCount,
    int BlockedCount,
    IReadOnlyList<ShadowRuntimeFeedbackPreflightEntry> Entries);

public sealed record ShadowRuntimeFeedbackPreflightEntry(
    int SourceLineNumber,
    string? FeedbackId,
    string Disposition,
    string RecordOrigin,
    string FormalEligibility,
    IReadOnlyList<string> Checklist,
    IReadOnlyList<string> BlockingReasons);
