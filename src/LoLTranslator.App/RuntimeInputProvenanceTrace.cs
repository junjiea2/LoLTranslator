using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace LoLTranslator.App;

/// <summary>
/// An observation origin is deliberately independent of OCR.  A reviewed
/// structured game-event source can later emit the same observation shape, but
/// this application currently creates only <see cref="OcrFrame"/> observations.
/// </summary>
public enum RuntimeObservationSourceKind
{
    OcrFrame,
    StructuredGameEvent
}

/// <summary>Stable local join key for a single observed screen row.</summary>
public sealed record RuntimeInputCorrelation(
    string SessionId,
    long FrameId,
    int RowIndex,
    string RowId,
    string? OccurrenceId);

/// <summary>
/// One input boundary shared by current OCR and a future audited structured
/// source.  No Riot API is connected or implied by this contract.
/// </summary>
public sealed record RuntimeObservationInput(
    RuntimeObservationSourceKind SourceKind,
    long FrameId,
    string SourceDescriptor,
    IReadOnlyList<StructuredOcrLine> CandidateLines);

public sealed record RuntimeInputEvidenceRow(
    int LineNumber,
    OcrShadowLineResult Result,
    OcrBoundingBox? Bounds,
    IReadOnlyList<StructuredOcrLine> Candidates,
    ChatMessageEnvelope Envelope);

/// <summary>
/// Explicitly enabled, local-only raw-input trace.  It is intentionally
/// separate from the always-on aggregate runtime trace: this trace may contain
/// chat text, player identifiers, and selected-region imagery.  It is never
/// uploaded, exported, or eligible for Pack/data intake automatically.
/// </summary>
public sealed class RuntimeInputProvenanceTrace
{
    private const long DefaultMaximumBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly TimeSpan _retention;
    private readonly long _maximumBytes;
    private string? _sessionDirectory;
    private string? _sessionId;
    private long? _runtimeSessionId;

    public RuntimeInputProvenanceTrace(
        string? rootDirectory = null,
        TimeSpan? retention = null,
        long? maximumBytes = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLTranslator", "input-provenance");
        _retention = retention ?? DefaultRetention;
        _maximumBytes = Math.Max(8L * 1024 * 1024, maximumBytes ?? DefaultMaximumBytes);
    }

    public string RootDirectory => _rootDirectory;
    public string? SessionDirectory => _sessionDirectory;
    public string? TraceFilePath => _sessionDirectory is null ? null : Path.Combine(_sessionDirectory, "trace.jsonl");
    public bool Enabled => _sessionDirectory is not null;

    public void BeginSession(long runtimeSessionId)
    {
        lock (_gate)
        {
            EnforceRetentionLocked();
            _runtimeSessionId = runtimeSessionId;
            _sessionId = $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            _sessionDirectory = Path.Combine(_rootDirectory, "sessions", _sessionId);
            Directory.CreateDirectory(Path.Combine(_sessionDirectory, "frames"));
            Directory.CreateDirectory(Path.Combine(_sessionDirectory, "rows"));
            File.WriteAllText(Path.Combine(_sessionDirectory, "export-redaction-checklist.md"), ExportChecklistText);
            AppendLocked(new
            {
                record_type = "trace_session",
                timestamp_utc = DateTimeOffset.UtcNow,
                session_id = _sessionId,
                runtime_session_id = runtimeSessionId,
                origin = "local_runtime_diagnostic_explicit_opt_in",
                formal_eligibility = "not_eligible",
                intake_status = "local_only_not_for_data_import",
                export_status = "not_export_ready",
                raw_image_capture_enabled = true,
                retention_days = _retention.TotalDays,
                maximum_bytes = _maximumBytes,
                pii_warning = "May contain player IDs, chat text, and selected-region imagery. Manual review/redaction is required before any export."
            });
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            _sessionDirectory = null;
            _sessionId = null;
            _runtimeSessionId = null;
        }
    }

    public IReadOnlyDictionary<string, RuntimeInputCorrelation> RecordOcrObservation(
        long runtimeSessionId,
        long frameId,
        Rect captureRegion,
        string? rawCapturePath,
        IReadOnlyList<RuntimeInputEvidenceRow> rows,
        VisibleMessageDiffResult diff)
    {
        lock (_gate)
        {
            if (!EnsureSessionLocked(runtimeSessionId)) return new Dictionary<string, RuntimeInputCorrelation>();
            string? frameArtifact = SaveFrameLocked(rawCapturePath, frameId);
            var insertedByLine = diff.Inserted
                .GroupBy(message => message.Line.LineNumber)
                .ToDictionary(group => group.Key, group => group.First());
            var decisionsByLine = (diff.Decisions ?? Array.Empty<VisibleMessageDecision>())
                .GroupBy(decision => decision.LineNumber)
                .ToDictionary(group => group.Key, group => group.Last());
            var correlations = new Dictionary<string, RuntimeInputCorrelation>(StringComparer.Ordinal);

            foreach (RuntimeInputEvidenceRow row in rows)
            {
                insertedByLine.TryGetValue(row.LineNumber, out VisibleChatMessage? inserted);
                decisionsByLine.TryGetValue(row.LineNumber, out VisibleMessageDecision? decision);
                string rowId = $"{_sessionId}:f{frameId}:r{row.LineNumber:D3}";
                string? occurrenceId = inserted is null ? null : BuildOccurrenceId(_sessionId!, inserted.OccurrenceKey);
                var correlation = new RuntimeInputCorrelation(_sessionId!, frameId, row.LineNumber, rowId, occurrenceId);
                if (inserted is not null) correlations[inserted.OccurrenceKey] = correlation;
                string? cropArtifact = SaveRowCropLocked(rawCapturePath, row.Bounds, frameId, row.LineNumber);

                AppendLocked(new
                {
                    record_type = "ocr_observation",
                    timestamp_utc = DateTimeOffset.UtcNow,
                    correlation = new { session_id = correlation.SessionId, frame_id = correlation.FrameId, row_index = correlation.RowIndex, row_id = correlation.RowId, occurrence_id = correlation.OccurrenceId },
                    source = new { kind = RuntimeObservationSourceKind.OcrFrame.ToString(), descriptor = "selected_chat_region", capture_bounds = new { x = captureRegion.X, y = captureRegion.Y, width = captureRegion.Width, height = captureRegion.Height }, frame_artifact = frameArtifact, row_crop_artifact = cropArtifact },
                    ocr = new
                    {
                        candidates = row.Candidates.Select(candidate => new
                        {
                            engine_locale = candidate.EngineLocale,
                            frame_id = candidate.FrameId,
                            raw_text = candidate.Text,
                            bounds = Bounds(candidate.Bounds),
                            words = candidate.Words.Select(word => new { text = word.Text, bounds = Bounds(word.Bounds) }).ToArray()
                        }).ToArray(),
                        adopted_text = row.Result.Text,
                        adopted_bounds = row.Bounds is null ? null : Bounds(row.Bounds),
                        fusion = new { status = row.Result.Fusion.Status, confidence = row.Result.Fusion.Confidence, reasons = row.Result.Fusion.Reasons }
                    },
                    envelope = new { prefix = row.Envelope.Prefix, body = row.Envelope.Body, high_confidence = row.Envelope.HasHighConfidencePrefix, timestamp = row.Envelope.Timestamp, channel = row.Envelope.Channel, player_or_role = row.Envelope.PlayerOrRole, delimiter_repaired = row.Envelope.RepairedMisplacedDelimiter },
                    message_decision = decision is null ? null : new { decision = decision.Decision, category = decision.MessageCategory, extracted_body = decision.ExtractedBody, high_confidence_prefix = decision.HasHighConfidencePrefix },
                    origin = "local_runtime_diagnostic_explicit_opt_in",
                    formal_eligibility = "not_eligible",
                    intake_status = "local_only_not_for_data_import",
                    export_status = "not_export_ready"
                });
            }
            EnforceRetentionLocked();
            return correlations;
        }
    }

    public void RecordTranslation(IncrementalGeneralChatShadowResult result,
        IReadOnlyDictionary<string, RuntimeInputCorrelation> correlations)
    {
        lock (_gate)
        {
            if (_sessionDirectory is null) return;
            foreach (IncrementalGeneralChatShadowLine line in result.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.SessionFingerprint) && correlations.TryGetValue(line.SessionFingerprint, out RuntimeInputCorrelation? correlation))
                {
                    AppendLocked(new
                    {
                        record_type = "translation_outcome",
                        timestamp_utc = DateTimeOffset.UtcNow,
                        correlation = new { session_id = correlation.SessionId, frame_id = correlation.FrameId, row_index = correlation.RowIndex, row_id = correlation.RowId, occurrence_id = correlation.OccurrenceId },
                        routing = new { route = line.Route, dedup_status = line.DedupStatus, sender_relation = line.SenderRelation },
                        terminology_and_protection = new { canonical_ids = line.CanonicalIds, semantic_concept_ids = line.SemanticConceptIds, verified_typed_entity_ids = line.VerifiedTypedEntityIds, locked_renderings = line.LockedRenderings, protected_input = line.ProtectedInput },
                        translation = new { raw_provider_output = line.RawProviderOutput, final_output = line.FinalOutput, elapsed_milliseconds = line.ElapsedMilliseconds },
                        outcome = new { status = string.IsNullOrWhiteSpace(line.FinalOutput) ? "fail_closed" : "rendered", failure_reason = line.FailureReason },
                        origin = "local_runtime_diagnostic_explicit_opt_in",
                        formal_eligibility = "not_eligible",
                        intake_status = "local_only_not_for_data_import",
                        export_status = "not_export_ready"
                    });
                }
            }
        }
    }

    public void RecordPipelineFailure(long runtimeSessionId, long frameId, string stage, string reason)
    {
        lock (_gate)
        {
            if (!EnsureSessionLocked(runtimeSessionId)) return;
            AppendLocked(new
            {
                record_type = "pipeline_failure",
                timestamp_utc = DateTimeOffset.UtcNow,
                correlation = new { session_id = _sessionId, frame_id = frameId, row_index = 0, row_id = $"{_sessionId}:f{frameId}:r000", occurrence_id = (string?)null },
                stage,
                reason,
                outcome = new { status = "fail_closed" },
                origin = "local_runtime_diagnostic_explicit_opt_in",
                formal_eligibility = "not_eligible",
                intake_status = "local_only_not_for_data_import",
                export_status = "not_export_ready"
            });
        }
    }

    public void RecordSuppression(RuntimeInputCorrelation correlation, string stage, string reason)
    {
        lock (_gate)
        {
            if (_sessionDirectory is null || !string.Equals(correlation.SessionId, _sessionId, StringComparison.Ordinal)) return;
            AppendLocked(new
            {
                record_type = "pipeline_suppression",
                timestamp_utc = DateTimeOffset.UtcNow,
                correlation = new { session_id = correlation.SessionId, frame_id = correlation.FrameId, row_index = correlation.RowIndex, row_id = correlation.RowId, occurrence_id = correlation.OccurrenceId },
                stage,
                reason,
                outcome = new { status = "suppressed_not_presented" },
                origin = "local_runtime_diagnostic_explicit_opt_in",
                formal_eligibility = "not_eligible",
                intake_status = "local_only_not_for_data_import",
                export_status = "not_export_ready"
            });
        }
    }

    public void RecordOverlayPresentation(IncrementalGeneralChatShadowResult result,
        IReadOnlyDictionary<string, RuntimeInputCorrelation> correlations, bool anyDisplayed)
    {
        lock (_gate)
        {
            if (_sessionDirectory is null) return;
            foreach (IncrementalGeneralChatShadowLine line in result.Lines.Where(line => !string.IsNullOrWhiteSpace(line.SessionFingerprint)))
            {
                if (!correlations.TryGetValue(line.SessionFingerprint!, out RuntimeInputCorrelation? correlation)) continue;
                AppendLocked(new
                {
                    record_type = "overlay_presentation",
                    timestamp_utc = DateTimeOffset.UtcNow,
                    correlation = new { session_id = correlation.SessionId, frame_id = correlation.FrameId, row_index = correlation.RowIndex, row_id = correlation.RowId, occurrence_id = correlation.OccurrenceId },
                    presentation = new { status = !string.IsNullOrWhiteSpace(line.FinalOutput) && anyDisplayed ? "overlay_accepted" : "not_presented", final_rendered_text = line.FinalOutput },
                    origin = "local_runtime_diagnostic_explicit_opt_in",
                    formal_eligibility = "not_eligible",
                    intake_status = "local_only_not_for_data_import",
                    export_status = "not_export_ready"
                });
            }
        }
    }

    private bool EnsureSessionLocked(long runtimeSessionId)
    {
        if (_sessionDirectory is null || _runtimeSessionId != runtimeSessionId) return false;
        return true;
    }

    private string? SaveFrameLocked(string? sourcePath, long frameId)
    {
        if (_sessionDirectory is null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            string relative = Path.Combine("frames", $"frame-{frameId:D8}.png");
            File.Copy(sourcePath, Path.Combine(_sessionDirectory, relative), overwrite: true);
            return relative.Replace('\\', '/');
        }
        catch { return null; }
    }

    private string? SaveRowCropLocked(string? sourcePath, OcrBoundingBox? bounds, long frameId, int rowIndex)
    {
        if (_sessionDirectory is null || bounds is null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            using var source = new Bitmap(sourcePath);
            int x = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, source.Width - 1));
            int y = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, source.Height - 1));
            int width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, source.Width - x);
            int height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, source.Height - y);
            using var crop = source.Clone(new Rectangle(x, y, width, height), source.PixelFormat);
            string relative = Path.Combine("rows", $"frame-{frameId:D8}-row-{rowIndex:D3}.png");
            crop.Save(Path.Combine(_sessionDirectory, relative), System.Drawing.Imaging.ImageFormat.Png);
            return relative.Replace('\\', '/');
        }
        catch { return null; }
    }

    private void EnforceRetentionLocked()
    {
        try
        {
            string sessionsRoot = Path.Combine(_rootDirectory, "sessions");
            Directory.CreateDirectory(sessionsRoot);
            DateTime cutoff = DateTime.UtcNow - _retention;
            DirectoryInfo[] sessions = new DirectoryInfo(sessionsRoot).GetDirectories("local-*");
            foreach (DirectoryInfo session in sessions.Where(directory => directory.LastWriteTimeUtc < cutoff))
                DeleteSessionDirectory(session);
            sessions = new DirectoryInfo(sessionsRoot).GetDirectories("local-*")
                .OrderBy(directory => directory.LastWriteTimeUtc).ToArray();
            long total = sessions.Sum(DirectorySize);
            foreach (DirectoryInfo session in sessions)
            {
                if (total <= _maximumBytes) break;
                long size = DirectorySize(session);
                DeleteSessionDirectory(session);
                total -= size;
            }
        }
        catch { }
    }

    private static long DirectorySize(DirectoryInfo directory)
    {
        try { return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length); }
        catch { return 0; }
    }

    private static void DeleteSessionDirectory(DirectoryInfo session)
    {
        try { session.Delete(recursive: true); }
        catch { }
    }

    private void AppendLocked(object entry)
    {
        if (TraceFilePath is null) return;
        try { File.AppendAllText(TraceFilePath, JsonSerializer.Serialize(entry, Options) + Environment.NewLine, Encoding.UTF8); }
        catch { }
    }

    private static object Bounds(OcrBoundingBox bounds) => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };

    private static string BuildOccurrenceId(string sessionId, string occurrenceKey)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + occurrenceKey));
        return "o-" + Convert.ToHexString(digest)[..20].ToLowerInvariant();
    }

    private const string ExportChecklistText = """
# Local diagnostic export checklist

This folder is local-only diagnostic material. It may contain player IDs, chat text,
and selected-region images. It is not Observation, Evidence, or a data-Pack input.

Before any manual export:
1. Review every frame and crop for player names, chat text, and incidental screen content.
2. Remove/redact player identifiers and any unrelated personal information.
3. Keep only the minimum rows needed for the defect being investigated.
4. Export a separate redacted copy; do not move this source folder into data collection.
5. Record the recipient and purpose outside this application. Nothing is uploaded automatically.
""";
}
