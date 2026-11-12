using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLTranslator.App;

/// <summary>
/// Privacy-safe operational trace for the continuous translation pipeline.
/// Records stages, counters, durations and exception types. Event adapters
/// project only operational fields; they never serialize the supplied message.
/// </summary>
public sealed class ContinuousRuntimeTrace
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _sessionId;

    public ContinuousRuntimeTrace(string? filePath = null, string? sessionId = null)
    {
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        FilePath = filePath ?? CreateDefaultPath(_sessionId);
    }

    public string FilePath { get; }
    public string? LastStage { get; private set; }
    public string? LastEvent { get; private set; }
    public string? LastStatus { get; private set; }

    public void AppendObservationDecisions(long frameId, VisibleMessageDiffResult diff) => WriteRecord(new
    {
        timestamp_utc = DateTimeOffset.UtcNow, session_id = _sessionId, build_id = RuntimeBuildInfo.Id,
        stage = "message_diff", @event = "decision_counts", status = "observed", frame_id = frameId,
        decisions = (diff.Decisions ?? Array.Empty<VisibleMessageDecision>()).GroupBy(d => d.Decision)
            .ToDictionary(g => g.Key, g => g.Count())
    }, "message_diff", "decision_counts", "observed");

    /// <summary>Per-event state with a hashed correlation key; never serializes the message or model text.</summary>
    public void AppendDispatch(RuntimeDispatchTransition transition, int fastQueued, int modelQueued)
    {
        var entry = new
        {
            timestamp_utc = DateTimeOffset.UtcNow, session_id = _sessionId,
            stage = "dispatch", @event = "event_state", event_key = EventKey(transition.Message),
            event_sequence = transition.Message.Line.RuntimeEvent?.Sequence,
            lane = transition.Lane.ToString(), state = transition.State.ToString(),
            status = transition.Reason, age_ms = transition.AgeMilliseconds,
            frame_id = transition.Message.Line.RuntimeEvent?.FrameTiming?.FrameId,
            captured_at_utc = transition.Message.Line.RuntimeEvent?.FrameTiming?.CapturedAtUtc,
            capture_age_ms = CaptureAge(transition.Message.Line.RuntimeEvent?.FrameTiming),
            first_observed_capture_at_utc = transition.Message.Line.RuntimeEvent?.FirstObservedCaptureAtUtc,
            fast_queued = fastQueued, model_queued = modelQueued
        };
        WriteRecord(entry, "dispatch", "event_state", transition.Reason);
    }

    private static string EventKey(VisibleChatMessage message) => EventKey(message.OccurrenceKey);
    private static string EventKey(string id) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..24];
    private static long? CaptureAge(RuntimeFrameTiming? timing) => timing is null ? null :
        Math.Max(0, (long)(DateTimeOffset.UtcNow - timing.CapturedAtUtc).TotalMilliseconds);

    public void AppendFrameTiming(RuntimeFrameTiming timing, int detectedLines)
    {
        WriteRecord(new
        {
            timestamp_utc = DateTimeOffset.UtcNow, session_id = _sessionId, build_id = RuntimeBuildInfo.Id,
            stage = "capture_timing", @event = "ocr_frame_completed", status = "completed",
            frame_id = timing.FrameId, captured_at_utc = timing.CapturedAtUtc,
            ocr_started_at_utc = timing.OcrStartedAtUtc, ocr_completed_at_utc = timing.OcrCompletedAtUtc,
            buffer_wait_ms = Math.Max(0, (long)(timing.OcrStartedAtUtc - timing.CapturedAtUtc).TotalMilliseconds),
            preprocessing_and_ocr_ms = Math.Max(0, (long)(timing.OcrCompletedAtUtc - timing.OcrStartedAtUtc).TotalMilliseconds),
            detected_lines = detectedLines
        }, "capture_timing", "ocr_frame_completed", "completed");
    }

    public void AppendFrameDisposition(long frameId, DateTimeOffset capturedAt, string status) => WriteRecord(new
    {
        timestamp_utc = DateTimeOffset.UtcNow, session_id = _sessionId, build_id = RuntimeBuildInfo.Id,
        stage = "capture_timing", @event = "frame_state", status, frame_id = frameId, captured_at_utc = capturedAt
    }, "capture_timing", "frame_state", status);

    public void AppendViewReceipt(RuntimeViewReceipt receipt) => WriteRecord(new
    {
        timestamp_utc = receipt.AtUtc, session_id = _sessionId, build_id = RuntimeBuildInfo.Id,
        stage = "overlay", @event = "view_lifecycle", status = receipt.Status,
        event_key = EventKey(receipt.EventId), event_sequence = receipt.Sequence,
        submitted_age_ms = receipt.SubmittedAgeMilliseconds, capture_age_ms = receipt.CaptureAgeMilliseconds,
        first_observed_age_ms = receipt.FirstObservedAgeMilliseconds
    }, "overlay", "view_lifecycle", receipt.Status);

    public void AppendPresentation(RuntimeDispatchDelivery delivery, TranslationSidecarApplyResult presentation)
    {
        string status = presentation.Enqueued > 0 ? "submitted_to_view" : "view_unavailable";
        var entry = new
        {
            timestamp_utc = DateTimeOffset.UtcNow, session_id = _sessionId,
            stage = "overlay", @event = "event_presentation", status,
            event_key = EventKey(delivery.Message), event_sequence = delivery.Message.Line.RuntimeEvent?.Sequence,
            frame_id = delivery.Message.Line.RuntimeEvent?.FrameTiming?.FrameId,
            capture_age_ms = CaptureAge(delivery.Message.Line.RuntimeEvent?.FrameTiming),
            accepted_for_display = presentation.Enqueued, submitted_now = presentation.DisplayedNow,
            missing_safe_prefix = presentation.MissingSafePrefix, untrusted_presentation = presentation.UntrustedPresentation
        };
        WriteRecord(entry, "overlay", "event_presentation", status);
    }

    public void Append(
        string stage,
        string eventName,
        string status,
        long? elapsedMilliseconds = null,
        int? detectedLines = null,
        int? visibleMessages = null,
        int? newMessages = null,
        int? queueCount = null,
        int? displayedMessages = null,
        int? failedMessages = null,
        int? estimatedMessageCandidates = null,
        int? queuedMessages = null,
        double? measurableCoveragePercent = null,
        int? estimatedSystemCandidates = null,
        int? queuedSystemMessages = null,
        int? displayedSystemMessages = null,
        int? rejectedUnreliableRows = null,
        int? rejectedNoChatPrefixRows = null,
        int? rejectedEmptyOrSystemRows = null,
        int? rejectedTooShortRows = null,
        int? waitingConfirmationRows = null,
        int? suppressedBulkRealignmentRows = null,
        int? suppressedPreexistingViewportRows = null,
        int? suppressedRecentOcrVariantRows = null,
        int? suppressedSystemContinuationRows = null,
        int? overlayMissingSafePrefixRows = null,
        int? overlayUntrustedPresentationRows = null,
        int? overlayDuplicateRows = null,
        long? oldestMessageAgeMilliseconds = null,
        Exception? exception = null)
    {
        var entry = new RuntimeTraceEntry(
            DateTimeOffset.UtcNow,
            _sessionId,
            stage,
            eventName,
            status,
            elapsedMilliseconds,
            detectedLines,
            visibleMessages,
            newMessages,
            queueCount,
            displayedMessages,
            failedMessages,
            estimatedMessageCandidates,
            queuedMessages,
            measurableCoveragePercent,
            estimatedSystemCandidates,
            queuedSystemMessages,
            displayedSystemMessages,
            rejectedUnreliableRows,
            rejectedNoChatPrefixRows,
            rejectedEmptyOrSystemRows,
            rejectedTooShortRows,
            waitingConfirmationRows,
            suppressedBulkRealignmentRows,
            suppressedPreexistingViewportRows,
            suppressedRecentOcrVariantRows,
            suppressedSystemContinuationRows,
            overlayMissingSafePrefixRows,
            overlayUntrustedPresentationRows,
            overlayDuplicateRows,
            oldestMessageAgeMilliseconds,
            exception?.GetType().Name,
            exception is NativePpOcrSidecarException native ? native.DiagnosticCode : null);

        WriteRecord(entry, stage, eventName, status);
    }

    private void WriteRecord(object entry, string stage, string eventName, string status)
    {
        lock (_gate)
        {
            LastStage = stage;
            LastEvent = eventName;
            LastStatus = status;
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
            catch
            {
                // Tracing must never stop OCR or translation.
            }
        }
    }

    private static string CreateDefaultPath(string sessionId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLTranslator",
        "runtime-traces",
        $"continuous-runtime-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{sessionId[..8]}.jsonl");

    private sealed record RuntimeTraceEntry(
        [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("elapsed_ms")] long? ElapsedMilliseconds,
        [property: JsonPropertyName("detected_lines")] int? DetectedLines,
        [property: JsonPropertyName("visible_messages")] int? VisibleMessages,
        [property: JsonPropertyName("new_messages")] int? NewMessages,
        [property: JsonPropertyName("queue_count")] int? QueueCount,
        [property: JsonPropertyName("displayed_messages")] int? DisplayedMessages,
        [property: JsonPropertyName("failed_messages")] int? FailedMessages,
        [property: JsonPropertyName("estimated_message_candidates")] int? EstimatedMessageCandidates,
        [property: JsonPropertyName("queued_messages")] int? QueuedMessages,
        [property: JsonPropertyName("measurable_coverage_percent")] double? MeasurableCoveragePercent,
        [property: JsonPropertyName("estimated_system_candidates")] int? EstimatedSystemCandidates,
        [property: JsonPropertyName("queued_system_messages")] int? QueuedSystemMessages,
        [property: JsonPropertyName("displayed_system_messages")] int? DisplayedSystemMessages,
        [property: JsonPropertyName("rejected_unreliable_rows")] int? RejectedUnreliableRows,
        [property: JsonPropertyName("rejected_no_chat_prefix_rows")] int? RejectedNoChatPrefixRows,
        [property: JsonPropertyName("rejected_empty_or_system_rows")] int? RejectedEmptyOrSystemRows,
        [property: JsonPropertyName("rejected_too_short_rows")] int? RejectedTooShortRows,
        [property: JsonPropertyName("waiting_confirmation_rows")] int? WaitingConfirmationRows,
        [property: JsonPropertyName("suppressed_bulk_realignment_rows")] int? SuppressedBulkRealignmentRows,
        [property: JsonPropertyName("suppressed_preexisting_viewport_rows")] int? SuppressedPreexistingViewportRows,
        [property: JsonPropertyName("suppressed_recent_ocr_variant_rows")] int? SuppressedRecentOcrVariantRows,
        [property: JsonPropertyName("suppressed_system_continuation_rows")] int? SuppressedSystemContinuationRows,
        [property: JsonPropertyName("overlay_missing_safe_prefix_rows")] int? OverlayMissingSafePrefixRows,
        [property: JsonPropertyName("overlay_untrusted_presentation_rows")] int? OverlayUntrustedPresentationRows,
        [property: JsonPropertyName("overlay_duplicate_rows")] int? OverlayDuplicateRows,
        [property: JsonPropertyName("oldest_message_age_ms")] long? OldestMessageAgeMilliseconds,
        [property: JsonPropertyName("error_type")] string? ErrorType,
        [property: JsonPropertyName("error_code")] string? ErrorCode);
}
