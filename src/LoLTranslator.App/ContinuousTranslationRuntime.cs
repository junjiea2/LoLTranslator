using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace LoLTranslator.App;

public enum ContinuousTranslationState { Stopped, Running, Paused }

/// <summary>Presentation is real-time only; OCR timestamps are never ordering truth.</summary>
public static class ContinuousOverlayPresentationPolicy
{
    // A translation older than four seconds is no longer realtime chat.  It is
    // more useful to suppress it with a correlated reason than to place an old
    // row behind newer game messages.
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(4);
    public static bool IsStale(VisibleChatMessage message, DateTimeOffset now, TimeSpan? deadline = null) =>
        message.DetectedAtUtc is { } detected && now - detected > (deadline ?? DefaultDeadline);
}

public sealed class LocalModelReadinessStateMachine
{
    public string State { get; private set; } = "checking";
    public bool Ready => State == "ready";
    public void Apply(LocalModelStatus status) => State = status.State;
}

/// <summary>UI-independent state and non-reentrancy gate for the capture loop.</summary>
public sealed class ContinuousTranslationSessionCoordinator
{
    public ContinuousTranslationState State { get; private set; } = ContinuousTranslationState.Stopped;
    public bool IsTickRunning { get; private set; }
    public int NewCount { get; private set; }
    public int DeduplicatedCount { get; private set; }
    public int FailedCount { get; private set; }

    public void Start() => State = ContinuousTranslationState.Running;
    public void Pause() { if (State == ContinuousTranslationState.Running) State = ContinuousTranslationState.Paused; }
    public void Resume() { if (State == ContinuousTranslationState.Paused) State = ContinuousTranslationState.Running; }
    public void Stop() => State = ContinuousTranslationState.Stopped;
    public bool TryBeginTick()
    {
        if (State != ContinuousTranslationState.Running || IsTickRunning) return false;
        IsTickRunning = true;
        return true;
    }
    public void CompleteTick(IncrementalGeneralChatShadowResult? result, bool failed)
    {
        if (result is not null)
        {
            NewCount += result.DisplayedNewCount;
            DeduplicatedCount += result.DeduplicatedCount;
            FailedCount += result.FailClosedCount;
        }
        if (failed) FailedCount++;
        IsTickRunning = false;
    }
    public void RecordResult(IncrementalGeneralChatShadowResult result)
    {
        NewCount += result.DisplayedNewCount;
        DeduplicatedCount += result.DeduplicatedCount;
    }
    public void RecordTerminalFailures(int count) => FailedCount += Math.Max(0, count);
    public void ResetForRegionChange()
    {
        Stop();
        IsTickRunning = false;
        NewCount = 0;
        DeduplicatedCount = 0;
        FailedCount = 0;
    }
}

public sealed record ContinuousChatGateDecision(string Status, IReadOnlyList<OcrShadowLineResult> AcceptedLines, int StableCount);

/// <summary>
/// In-memory, low-cost visual change gate.  It deliberately knows nothing about
/// League: callers supply a compact grayscale/text-band fingerprint.  A changed
/// frame is debounced so animated backgrounds and a blinking caret do not cause
/// OCR.  A stable capture is emitted exactly once per visual change burst.
/// </summary>
public sealed class VisualChatChangeWatcher
{
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _maximumWait;
    private readonly int _differenceThreshold;
    private byte[]? _stable;
    private byte[]? _pending;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset _burstSince;

    public VisualChatChangeWatcher(
        TimeSpan? debounce = null,
        int differenceThreshold = 10,
        TimeSpan? maximumWait = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(350);
        _maximumWait = maximumWait ?? TimeSpan.FromSeconds(1);
        _differenceThreshold = Math.Max(1, differenceThreshold);
    }

    public VisualChatWatchDecision Observe(ReadOnlySpan<byte> fingerprint, DateTimeOffset now)
    {
        byte[] current = fingerprint.ToArray();
        if (_stable is null)
        {
            _stable = current;
            return new VisualChatWatchDecision("baseline", false);
        }
        if (_pending is null && Difference(_stable, current) < _differenceThreshold)
            return new VisualChatWatchDecision("frame_unchanged", false);

        if (_pending is null || Difference(_pending, current) >= _differenceThreshold)
        {
            if (_pending is null) _burstSince = now;
            _pending = current;
            _pendingSince = now;
            if (now - _burstSince >= _maximumWait)
            {
                _stable = current;
                _pending = null;
                return new VisualChatWatchDecision("forced_capture_after_animation", true);
            }
            return new VisualChatWatchDecision("change_detected", false);
        }
        if (now - _pendingSince < _debounce)
            return new VisualChatWatchDecision("debouncing", false);

        _stable = _pending;
        _pending = null;
        return new VisualChatWatchDecision("stable_capture", true);
    }

    public void Reset() { _stable = null; _pending = null; _pendingSince = default; _burstSince = default; }

    private static int Difference(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return int.MaxValue;
        int total = 0;
        for (int index = 0; index < left.Length; index++) total += Math.Abs(left[index] - right[index]);
        return total;
    }
}

public sealed record VisualChatWatchDecision(string Status, bool ShouldRunOcr);

public sealed record VisibleChatMessage(
    OcrShadowLineResult Line,
    string BodyKey,
    string OccurrenceKey,
    string? VisualHash = null,
    OcrBoundingBox? Bounds = null,
    string? TimestampKey = null,
    DateTimeOffset? DetectedAtUtc = null)
{
    public RuntimeChatEventKind? EventKind => Line.RuntimeEvent?.Kind;
}
public sealed record VisibleMessageDecision(
    int LineNumber,
    string OcrText,
    string ExtractedBody,
    bool HasHighConfidencePrefix,
    string Decision,
    string MessageCategory = "unknown");
public sealed record VisibleMessageDiffResult(
    IReadOnlyList<VisibleChatMessage> Inserted,
    int VisibleCount,
    int PendingConfirmationCount = 0,
    IReadOnlyList<VisibleMessageDecision>? Decisions = null,
    bool InitialBaselineEstablished = false);

public sealed record TranslationCoverageSnapshot(
    int EstimatedMessageCandidates,
    int QueuedMessages,
    int DisplayedMessages,
    int FailedMessages)
{
    public int EstimatedSystemCandidates { get; init; }
    public int QueuedSystemMessages { get; init; }
    public int DisplayedSystemMessages { get; init; }
    public int FailedSystemMessages { get; init; }
    public int MeasurableDenominator => Math.Max(EstimatedMessageCandidates, QueuedMessages);
    public double MeasurableCoveragePercent => MeasurableDenominator == 0
        ? 0
        : DisplayedMessages * 100d / MeasurableDenominator;
    public double TranslationSuccessPercent => QueuedMessages == 0
        ? 0
        : DisplayedMessages * 100d / QueuedMessages;
}

/// <summary>
/// Privacy-safe session counters for the visible-chat pipeline. The candidate
/// denominator begins at timestamped OCR rows, before stability and envelope
/// filters. Completely OCR-invisible rows require a controlled ground-truth
/// test and are intentionally not claimed by this estimate.
/// </summary>
public sealed class TranslationCoverageTracker
{
    private sealed record Candidate(string Timestamp, string Content, string Category);
    private readonly List<Candidate> _candidates = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly HashSet<string> _displayed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fingerprintCategories = new(StringComparer.Ordinal);

    public TranslationCoverageSnapshot Snapshot => new(
        _candidates.Count, _queued.Count, _displayed.Count, _failed.Count)
    {
        EstimatedSystemCandidates = _candidates.Count(candidate => candidate.Category == "system"),
        QueuedSystemMessages = _queued.Count(identity =>
            _fingerprintCategories.TryGetValue(identity, out string? category) && category == "system"),
        DisplayedSystemMessages = _displayed.Count(identity =>
            _fingerprintCategories.TryGetValue(identity, out string? category) && category == "system"),
        FailedSystemMessages = _failed.Count(identity =>
            _fingerprintCategories.TryGetValue(identity, out string? category) && category == "system")
    };

    public void ObserveFrame(VisibleMessageDiffResult diff)
    {
        foreach (VisibleMessageDecision decision in diff.Decisions ?? Array.Empty<VisibleMessageDecision>())
        {
            string? timestamp = OcrShadowLineFusion.ExtractTimestamp(decision.OcrText);
            string content = Normalize(RemoveLeadingTimestamp(decision.OcrText));
            if (timestamp is null || content.Length < 4 || IsApplicationNoise(decision.OcrText)) continue;
            string normalizedTimestamp = NormalizeTimestamp(timestamp);
            if (_candidates.Any(candidate =>
                    candidate.Timestamp == normalizedTimestamp && SameRelaxed(candidate.Content, content))) continue;
            _candidates.Add(new Candidate(normalizedTimestamp, content, decision.MessageCategory));
        }

        foreach (VisibleChatMessage message in diff.Inserted)
        {
            string queueIdentity = MessageIdentity(message);
            string category = message.Line.RuntimeEvent is { } contract
                ? contract.Kind switch
                {
                    RuntimeChatEventKind.PlayerText => "player",
                    RuntimeChatEventKind.Unknown => "unclassified",
                    _ => "system"
                }
                : message.Line.Text.Contains("[系统]", StringComparison.Ordinal) ||
                  message.Line.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal)
                    ? "system" : "player";
            _queued.Add(queueIdentity);
            _fingerprintCategories[queueIdentity] = category;
            _fingerprintCategories[message.OccurrenceKey] = category;
        }
    }

    public void RecordTranslation(IncrementalGeneralChatShadowResult result)
    {
        foreach (IncrementalGeneralChatShadowLine line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.SessionFingerprint) || line.Route == "deduplicated") continue;
            if (!string.IsNullOrWhiteSpace(line.FinalOutput))
            {
                _displayed.Add(line.SessionFingerprint);
                _failed.Remove(line.SessionFingerprint);
            }
            else if (!_displayed.Contains(line.SessionFingerprint))
            {
                _failed.Add(line.SessionFingerprint);
            }
        }
    }

    public void RecordSuperseded(IEnumerable<VisibleChatMessage> messages)
    {
        foreach (VisibleChatMessage message in messages)
        {
            if (!_displayed.Contains(message.OccurrenceKey))
                _failed.Add(message.OccurrenceKey);
        }
    }

    public void Reset()
    {
        _candidates.Clear();
        _queued.Clear();
        _displayed.Clear();
        _failed.Clear();
        _fingerprintCategories.Clear();
    }

    private static string MessageIdentity(VisibleChatMessage message) => message.OccurrenceKey;

    private static string RemoveLeadingTimestamp(string text) => Regex.Replace(
        text,
        @"^\s*[^\p{L}\p{N}]?\s*[0-9OoIl]{1,3}\s*[:：.]\s*[0-9OoIl]{2}\s*",
        string.Empty,
        RegexOptions.CultureInvariant);

    private static string NormalizeTimestamp(string timestamp) => new(timestamp
        .Select(character => character is 'O' or 'o' ? '0' : character)
        .Where(character => char.IsDigit(character) || character is ':' or '：')
        .Select(character => character == '：' ? ':' : character)
        .ToArray());

    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool IsApplicationNoise(string text) =>
        text.Contains("输入 /help", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("輸入 /help", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("本地模型", StringComparison.OrdinalIgnoreCase);

    private static bool SameRelaxed(string left, string right)
    {
        if (left == right) return true;
        int maximumLength = Math.Max(left.Length, right.Length);
        if (Math.Min(left.Length, right.Length) < 4 ||
            Math.Abs(left.Length - right.Length) > Math.Max(3, maximumLength / 3)) return false;
        int allowed = Math.Max(1, (int)Math.Ceiling(maximumLength * .30));
        return EditDistance(left, right, allowed) <= allowed;
    }

    private static int EditDistance(string left, string right, int cutoff)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];
            for (int j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > cutoff) return cutoff + 1;
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}

/// <summary>
/// Ordered diff for the chat viewport.  The LCS preserves occurrences, so two
/// adjacent identical bodies remain two messages while an unchanged screenshot
/// produces no inserts. Timestamps never determine ordering, but a timestamp
/// plus body forms a stable identity when the same visible line scrolls to a
/// different screen coordinate.
/// </summary>
public sealed class VisibleMessageSequenceDiffer
{
    private static readonly Regex CanonicalSystemEvent = new(
        @"(?<missing>signals?\s+(?:that\s+)?(?:enemy|enemies|enernies)\s+are\s+(?:missing|rnissing)|敌人消失|敵人消失|적이\s*사라졌다고\s*알림)|" +
        @"(?<onway>is\s+on\s+the\s+way|正在路上|가고\s*있음)|" +
        @"(?<caution>signals?\s+to\s+be\s+cautious|小心|조심)|" +
        @"(?<vision>enemy\s+has\s+vision|is\s+asking\s+for\s+vision|需要视野|需要視野|시야)|" +
        @"(?<assist>is\s+asking\s+for\s+assistance|请求协助|請求協助|지원\s*요청)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TranslatableSystemNotification = new(
        @"(?:发出信号|發出信號|击杀了|擊殺了|完成双杀|完成雙殺|完成三杀|完成三殺|四杀|四殺|五杀|五殺|第一滴血|摧毁了|摧毀了|终结了|終結了|已选择路线|已選擇路線|请求协助|請求協助|正在路上|需要视野|需要視野|敌方有视野|敵方有視野|赏金|賞金|has\s+targeted|has\s+slain|was\s+slain|double\s+kill|triple\s+kill|quadra\s+kill|penta\s*kill|first\s+blood|has\s+drawn\s+first\s+blood|has\s+shut\s+down|is\s+on\s+(?:a\s+)?(?:rampage|killing\s+spree)|destroyed\s+(?:a|the)|signals?\s+(?:that|to)|is\s+(?:on\s+the\s+way|asking\s+for\s+(?:assistance|vision))|enemy\s+has\s+vision|objective\s+bount(?:y|ies)|bonus\s+bounty|used\s+(?:ignite|teleport|r\b)|ultimate|연속\s*킬|제압|처치|도움을\s*요청|신호|시야|추가\s*골드)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingTimestamp = new(
        @"^\s*[^\p{L}\p{N}]?\s*[0-9OoIl]{1,3}\s*[:：.]\s*[0-9OoIl]{2}\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingSystemIdentity = new(
        @"^(?<identity>[\p{L}\p{N}_.'’\- ]{1,72}(?:[\(（][^\)）\r\n]{1,32}[\)）])?)\s*(?<action>(?:signals?\b|is\s+(?:on\s+the\s+way|asking\s+for|on\s+(?:a\s+)?(?:rampage|killing\s+spree))\b|has\s*(?:targeted|disconnected|reconnected|slain|shut\s+down|drawn\s+first\s+blood)\b|purchased\b|used\b|destroyed\b|님이\b).*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly List<VisibleChatMessage> _previous = new();
    private readonly List<VisibleChatMessage> _previousObserved = new();
    private readonly List<(VisibleChatMessage Message, long Batch)> _recentlyReleased = new();
    private long _batch;

    public VisibleMessageDiffResult Observe(IReadOnlyList<OcrShadowLineResult> lines)
        => Observe(lines, null, false, false);

    public VisibleMessageDiffResult Observe(IReadOnlyList<OcrShadowLineResult> lines,
        IReadOnlyDictionary<int, (string Hash, OcrBoundingBox Bounds)>? visualRows,
        bool suppressInitialBacklog = false,
        bool allowImmediateTimestampedRelease = false)
    {
        long batch = ++_batch;
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        // Keep duplicate memory bounded. A permanent session-wide memory turns
        // later OCR repairs and legitimate repeated short messages into false
        // duplicates during a long match.
        _recentlyReleased.RemoveAll(item => batch - item.Batch >
            (item.Message.TimestampKey is null ? 180 : 600));
        var observed = new List<VisibleChatMessage>();
        var current = new List<VisibleChatMessage>();
        var decisions = new List<VisibleMessageDecision>();
        int pendingConfirmation = 0;
        string? inheritedSystemTimestamp = null;
        foreach (OcrShadowLineResult line in lines.OrderBy(line => line.LineNumber))
        {
            string? ownTimestamp = OcrShadowLineFusion.ExtractTimestamp(line.Text);
            bool systemWrapped = TryWrapSystemNotification(line, inheritedSystemTimestamp, out OcrShadowLineResult? wrapped);
            OcrShadowLineResult queueLine = systemWrapped ? wrapped! : line;
            if (ownTimestamp is not null)
                inheritedSystemTimestamp = systemWrapped ? ownTimestamp : null;
            else if (!systemWrapped)
                inheritedSystemTimestamp = null;
            ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(queueLine.Text);
            string messageCategory = systemWrapped ? "system" : envelope.HasHighConfidencePrefix ? "player" : "unknown";
            if (queueLine.Fusion.Reasons.Contains("lol_system_continuation_row", StringComparer.Ordinal))
            {
                // Wrapped bounty/kill continuations share the previous row's
                // timestamp. Until they can be stitched with coordinate-level
                // confidence, never translate them as an independent message:
                // OCR fragments such as "C8650]" otherwise appear after the
                // correctly translated parent announcement.
                decisions.Add(new(line.LineNumber, line.Text, string.Empty, false,
                    "suppressed_system_continuation", messageCategory));
                continue;
            }
            if (!IsReliableForQueue(line))
            {
                decisions.Add(new(line.LineNumber, line.Text, envelope.Body, envelope.HasHighConfidencePrefix, "rejected_unreliable_row", messageCategory));
                continue;
            }
            if (IsIgnorableSystemLine(line.Text))
            {
                decisions.Add(new(line.LineNumber, line.Text, envelope.Body, envelope.HasHighConfidencePrefix, "rejected_empty_or_system", messageCategory));
                continue;
            }
            string? body = ResolveBody(queueLine, envelope);
            if (string.IsNullOrWhiteSpace(body) || IsIgnorableSystemBody(body))
            {
                decisions.Add(new(line.LineNumber, line.Text, body ?? string.Empty, envelope.HasHighConfidencePrefix, "rejected_empty_or_system", messageCategory));
                continue;
            }
            if (!envelope.HasHighConfidencePrefix && !HasPlausibleChatPrefix(queueLine.Text))
            {
                decisions.Add(new(line.LineNumber, line.Text, body, false, "rejected_no_chat_prefix", messageCategory));
                continue;
            }
            string bodyKey = Normalize(body);
            if (bodyKey.Length < 2)
            {
                decisions.Add(new(line.LineNumber, line.Text, body, envelope.HasHighConfidencePrefix, "rejected_too_short", messageCategory));
                continue;
            }
            string? timestampKey = ResolveTimestampKey(envelope, queueLine.Text);
            VisibleChatMessage message = visualRows is not null && visualRows.TryGetValue(line.LineNumber, out var visual)
                ? new VisibleChatMessage(queueLine, bodyKey, string.Empty, visual.Hash, visual.Bounds, timestampKey, observedAtUtc)
                : new VisibleChatMessage(queueLine, bodyKey, string.Empty, TimestampKey: timestampKey, DetectedAtUtc: observedAtUtc);
            observed.Add(message);
            // A complete timestamped LoL chat envelope is already a strong
            // identity: it has timestamp, sender/channel and body delimiters.
            // After startup baseline handling, release it on the first OCR pass
            // instead of waiting for a second noisy recognition of the same
            // glyphs. Unanchored/damaged rows retain the safer two-pass rule.
            bool stronglyAnchored = allowImmediateTimestampedRelease &&
                                  envelope.HasHighConfidencePrefix &&
                                  (timestampKey is not null || IsReliableKoreanEnvelope(queueLine, envelope));
            if (!stronglyAnchored && !_previousObserved.Any(previous => SameForConfirmation(previous, message)))
            {
                pendingConfirmation++;
                decisions.Add(new(line.LineNumber, line.Text, body, envelope.HasHighConfidencePrefix, "waiting_second_ocr_frame", messageCategory));
                continue;
            }
            current.Add(message);
            decisions.Add(new(line.LineNumber, line.Text, body, envelope.HasHighConfidencePrefix,
                stronglyAnchored ? "confirmed_timestamped_chat_row" : "confirmed_candidate", messageCategory));
        }
        // A populated chat box that is already visible when monitoring starts is
        // history, not a burst of new work. Establish every non-empty initial
        // viewport as the sequence baseline after normal two-frame confirmation.
        // The caller separately completes baseline mode after an observed empty
        // viewport, so the first later live message is not swallowed.
        if (suppressInitialBacklog && _previous.Count == 0 && current.Count >= 1)
        {
            for (int index = 0; index < current.Count; index++)
            {
                VisibleChatMessage item = current[index] with
                {
                    OccurrenceKey = $"baseline:{batch}:{index}:{current[index].BodyKey}"
                };
                current[index] = item;
                _recentlyReleased.Add((item, batch));
                ReplaceDecision(decisions, item.Line.LineNumber, "established_startup_baseline");
            }
            _previous.Clear();
            _previous.AddRange(current);
            _previousObserved.Clear();
            _previousObserved.AddRange(observed);
            return new VisibleMessageDiffResult(
                Array.Empty<VisibleChatMessage>(),
                current.Count,
                pendingConfirmation,
                decisions,
                InitialBaselineEstablished: true);
        }
        int[,] matched = BuildLcs(_previous, current);
        // A continuing viewport has a concrete visual/text anchor.  Chat adds
        // live rows at the bottom; OCR variants occurring before the first
        // retained prior row are historical re-recognition, not a new event.
        // This deliberately does not use the game timestamp, so same-second
        // messages at the append tail remain independent occurrences.
        int appendTailStart = FindFirstCurrentAnchor(_previous, current);
        int? priorLatestTimestamp = _previous.Concat(_recentlyReleased.Select(item => item.Message))
            .Select(message => ParseTimestampSeconds(message.TimestampKey))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty(-1)
            .Max() is int maximum && maximum >= 0 ? maximum : null;
        int previousIndex = 0;
        int currentIndex = 0;
        var inserted = new List<VisibleChatMessage>();
        while (previousIndex < _previous.Count || currentIndex < current.Count)
        {
            if (previousIndex < _previous.Count && currentIndex < current.Count && Same(_previous[previousIndex], current[currentIndex]))
            {
                currentIndex++; previousIndex++; continue;
            }
            if (currentIndex < current.Count && (previousIndex == _previous.Count || matched[previousIndex, currentIndex + 1] >= matched[previousIndex + 1, currentIndex]))
            {
                VisibleChatMessage item = current[currentIndex] with { OccurrenceKey = $"{batch}:{currentIndex}:{current[currentIndex].BodyKey}" };
                current[currentIndex] = item;
                bool historicalOccurrenceObserved = item.TimestampKey is not null &&
                    _previous.Concat(_recentlyReleased.Select(recent => recent.Message)).Any(previous =>
                        string.Equals(previous.TimestampKey, item.TimestampKey, StringComparison.Ordinal) &&
                        (previous.Line.LineNumber == item.Line.LineNumber ||
                         SameRecentlyReleased(previous, item)));
                // OCR can temporarily reorder coordinate rows. Only classify a
                // row before the first retained anchor as historical when its
                // timestamp was actually observed before. A new timestamp must
                // never be silenced merely because row ordering was unstable.
                if (appendTailStart > 0 && currentIndex < appendTailStart && historicalOccurrenceObserved)
                {
                    ReplaceDecision(decisions, item.Line.LineNumber, "suppressed_preexisting_viewport_variant");
                }
                else if (_recentlyReleased.Any(recent => recent.Batch < batch && SameRecentlyReleased(recent.Message, item)))
                {
                    ReplaceDecision(decisions, item.Line.LineNumber, "suppressed_recent_ocr_variant");
                }
                else
                {
                    inserted.Add(item);
                    _recentlyReleased.Add((item, batch));
                    ReplaceDecision(decisions, item.Line.LineNumber, "inserted_new_message");
                }
                currentIndex++; continue;
            }
            previousIndex++;
        }
        // If one OCR pass suddenly claims that most of a populated viewport is
        // new, this is a row realignment/re-recognition failure, not a plausible
        // chat append. Establish the latest viewport without translating the
        // entire historical block. Normal one-to-three message bursts remain.
        if (current.Count >= 5 && inserted.Count >= 4 && inserted.Count * 5 >= current.Count * 3)
        {
            VisibleChatMessage[] rejected = inserted.Where(item =>
                priorLatestTimestamp is null ||
                ParseTimestampSeconds(item.TimestampKey) is not int seconds ||
                seconds <= priorLatestTimestamp.Value).ToArray();
            foreach (VisibleChatMessage item in rejected)
            {
                _recentlyReleased.RemoveAll(recent => recent.Batch == batch &&
                    string.Equals(recent.Message.OccurrenceKey, item.OccurrenceKey, StringComparison.Ordinal));
                ReplaceDecision(decisions, item.Line.LineNumber, "suppressed_bulk_ocr_realignment");
                inserted.Remove(item);
            }
        }
        _previous.Clear();
        _previous.AddRange(current);
        _previousObserved.Clear();
        _previousObserved.AddRange(observed);
        return new VisibleMessageDiffResult(inserted, current.Count, pendingConfirmation, decisions);
    }

    public void Reset() { _previous.Clear(); _previousObserved.Clear(); _recentlyReleased.Clear(); _batch = 0; }

    private static int[,] BuildLcs(IReadOnlyList<VisibleChatMessage> previous, IReadOnlyList<VisibleChatMessage> current)
    {
        var values = new int[previous.Count + 1, current.Count + 1];
        for (int i = previous.Count - 1; i >= 0; i--)
            for (int j = current.Count - 1; j >= 0; j--)
                values[i, j] = Same(previous[i], current[j])
                    ? values[i + 1, j + 1] + 1
                    : Math.Max(values[i + 1, j], values[i, j + 1]);
        return values;
    }
    private static int FindFirstCurrentAnchor(IReadOnlyList<VisibleChatMessage> previous, IReadOnlyList<VisibleChatMessage> current)
    {
        if (previous.Count == 0 || current.Count == 0) return 0;
        for (int currentIndex = 0; currentIndex < current.Count; currentIndex++)
            if (previous.Any(previousItem => Same(previousItem, current[currentIndex]))) return currentIndex;
        return 0;
    }
    private static bool Same(string left, string right)
    {
        if (left == right) return true;
        int maximumLength = Math.Max(left.Length, right.Length);
        if (Math.Min(left.Length, right.Length) < 5 || Math.Abs(left.Length - right.Length) > Math.Max(2, maximumLength / 5)) return false;
        return EditDistance(left, right, Math.Max(1, (int)Math.Ceiling(maximumLength * .18))) <=
               Math.Max(1, (int)Math.Ceiling(maximumLength * .18));
    }
    private static bool Same(VisibleChatMessage left, VisibleChatMessage right) =>
        !string.IsNullOrWhiteSpace(left.VisualHash) && string.Equals(left.VisualHash, right.VisualHash, StringComparison.Ordinal)
            ? true : Same(left.BodyKey, right.BodyKey);
    private static bool SameForConfirmation(VisibleChatMessage left, VisibleChatMessage right)
    {
        if (left.Bounds is { } leftBounds && right.Bounds is { } rightBounds)
        {
            double centerDistance = Math.Abs((leftBounds.Y + leftBounds.Height / 2) - (rightBounds.Y + rightBounds.Height / 2));
            if (centerDistance > Math.Max(leftBounds.Height, rightBounds.Height) * 1.5) return false;
        }
        return SameRelaxed(left.BodyKey, right.BodyKey);
    }
    private static bool SameRecentlyReleased(VisibleChatMessage left, VisibleChatMessage right)
    {
        string? leftEvent = ResolveCanonicalSystemEvent(left.Line.Text);
        string? rightEvent = ResolveCanonicalSystemEvent(right.Line.Text);
        if (leftEvent is not null && leftEvent == rightEvent)
        {
            if (left.TimestampKey is not null && right.TimestampKey is not null)
                return string.Equals(left.TimestampKey, right.TimestampKey, StringComparison.Ordinal);
            // Losing the timestamp/envelope is a common second OCR reading of
            // the same still-visible Ping. Prefer the already released event.
            if (left.TimestampKey is null || right.TimestampKey is null) return true;
        }
        if (!SameRelaxed(left.BodyKey, right.BodyKey)) return false;
        if (left.TimestampKey is not null && right.TimestampKey is not null)
            return string.Equals(left.TimestampKey, right.TimestampKey, StringComparison.Ordinal);
        // If OCR loses the timestamp on either observation, prefer suppressing
        // a recent near-identical body over presenting the old line again. A
        // genuine repeated player message normally has two distinct timestamps
        // and is therefore allowed by the branch above.
        return true;
    }
    private static string? ResolveCanonicalSystemEvent(string text)
    {
        Match match = CanonicalSystemEvent.Match(text);
        if (!match.Success) return null;
        foreach (string name in new[] { "missing", "onway", "caution", "vision", "assist" })
            if (match.Groups[name].Success) return name;
        return null;
    }
    private static bool SameRelaxed(string left, string right)
    {
        if (left == right) return true;
        int maximumLength = Math.Max(left.Length, right.Length);
        if (Math.Min(left.Length, right.Length) < 4 || Math.Abs(left.Length - right.Length) > Math.Max(3, maximumLength / 3)) return false;
        int allowed = Math.Max(1, (int)Math.Ceiling(maximumLength * .30));
        return EditDistance(left, right, allowed) <= allowed;
    }
    private static string Normalize(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static bool IsReliableForQueue(OcrShadowLineResult line) =>
        !line.Fusion.IsAmbiguous &&
        (string.Equals(line.Fusion.Status, "fused", StringComparison.OrdinalIgnoreCase) ||
         line.Fusion.Reasons.Contains("structured_chat_envelope_anchor", StringComparer.Ordinal) ||
         line.Fusion.Reasons.Contains("selected_chat_region_coordinate_row", StringComparer.Ordinal));
    private static bool IsReliableKoreanEnvelope(OcrShadowLineResult line, ChatMessageEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.Channel) || string.IsNullOrWhiteSpace(envelope.PlayerOrRole)) return false;
        bool koreanChannel = envelope.Channel.Contains("팀", StringComparison.Ordinal) ||
                             envelope.Channel.Contains("전체", StringComparison.Ordinal) ||
                             envelope.Channel.Contains("모두", StringComparison.Ordinal);
        bool hasHangul = line.Text.Count(character => character is >= '\u1100' and <= '\u11ff' or
            >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af') >= 4;
        bool trustedColour = line.Fusion.Reasons.Contains("lol_allied_identity_row", StringComparer.Ordinal) ||
                             line.Fusion.Reasons.Contains("lol_enemy_identity_row", StringComparer.Ordinal) ||
                             line.Fusion.Reasons.Contains("lol_source_identity_blue", StringComparer.Ordinal) ||
                             line.Fusion.Reasons.Contains("lol_source_identity_red", StringComparer.Ordinal);
        return koreanChannel && hasHangul && trustedColour;
    }
    private static string? ResolveBody(OcrShadowLineResult line, ChatMessageEnvelope envelope)
    {
        if (envelope.HasHighConfidencePrefix && !string.IsNullOrWhiteSpace(envelope.Body)) return envelope.Body;
        if (!line.Fusion.Reasons.Contains("selected_chat_region_coordinate_row", StringComparer.Ordinal)) return null;
        return TryExtractDamagedChatBody(line.Text, out string body) ? body : null;
    }
    private static bool HasPlausibleChatPrefix(string text)
    {
        string value = text.TrimStart();
        int index = 0;
        int leadingDigits = 0;
        while (index < value.Length && (char.IsDigit(value[index]) || value[index] is 'O' or 'o') && leadingDigits < 2)
        { index++; leadingDigits++; }
        if (leadingDigits > 0 && index < value.Length && value[index] is ':' or '：' or '.') return true;
        if (value.Length > 1 && value[0] is ':' or '：' or '.' && (char.IsDigit(value[1]) || value[1] is 'O' or 'o')) return true;
        return value.StartsWith("[", StringComparison.Ordinal) && value.Contains(':');
    }
    private static bool IsIgnorableSystemBody(string body) =>
        body.Contains("输入 /help", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("輸入 /help", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("本地模型", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnorableSystemLine(string text) => IsIgnorableSystemBody(text);

    private static bool TryWrapSystemNotification(
        OcrShadowLineResult line,
        string? inheritedTimestamp,
        out OcrShadowLineResult? wrapped)
    {
        wrapped = null;
        string? timestamp = OcrShadowLineFusion.ExtractTimestamp(line.Text);
        bool continuation = timestamp is null && inheritedTimestamp is not null &&
                            line.Fusion.Reasons.Contains("lol_system_gold_row", StringComparer.Ordinal);
        if (timestamp is null && !continuation) return false;
        ChatMessageEnvelope originalEnvelope = ChatMessageEnvelopeExtractor.Extract(line.Text);
        bool knownSystemWording = TranslatableSystemNotification.IsMatch(line.Text);
        // In a user-selected LoL chat rectangle, a stable timestamped row that
        // does not have a normal player-chat envelope is a system notification.
        // This is what covers localized pings and announcements without an
        // endless language-specific keyword list.
        if (!continuation && originalEnvelope.HasHighConfidencePrefix && !knownSystemWording) return false;
        OcrFusionMetadata systemFusion = line.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal)
            ? line.Fusion
            : line.Fusion with { Reasons = line.Fusion.Reasons.Concat(new[] { "lol_system_notification" }).ToArray() };
        if (continuation && !systemFusion.Reasons.Contains("lol_system_continuation_row", StringComparer.Ordinal))
        {
            systemFusion = systemFusion with
            {
                Reasons = systemFusion.Reasons.Concat(new[] { "lol_system_continuation_row" }).ToArray()
            };
        }
        if (!originalEnvelope.HasHighConfidencePrefix &&
            !systemFusion.Reasons.Contains("lol_system_action_without_delimiter", StringComparer.Ordinal))
        {
            // League action Pings such as "is on the way", "signals ..." and
            // "has targeted ..." are rendered without a colon. The synthetic
            // internal envelope still uses one to isolate the actor safely, but
            // presentation must remember that the source did not contain it.
            systemFusion = systemFusion with
            {
                Reasons = systemFusion.Reasons.Concat(new[] { "lol_system_action_without_delimiter" }).ToArray()
            };
        }
        bool trustedOriginalEnvelope = originalEnvelope.HasHighConfidencePrefix &&
            (!string.IsNullOrWhiteSpace(originalEnvelope.Channel) ||
             originalEnvelope.PlayerOrRole?.IndexOfAny(['(', '（']) >= 0);
        if (!continuation && trustedOriginalEnvelope)
        {
            // A player/system envelope already separates the sender from the
            // translatable action. Keep it intact so an arbitrary player ID is
            // never sent to the translation model.
            wrapped = line with { Fusion = systemFusion };
            return true;
        }
        string body = timestamp is null
            ? line.Text.Trim()
            : LeadingTimestamp.Replace(line.Text, string.Empty, 1).Trim();
        if (body.Count(char.IsLetterOrDigit) < 4) return false;
        string effectiveTimestamp = timestamp ?? inheritedTimestamp!;
        Match identity = LeadingSystemIdentity.Match(body);
        string wrappedText = identity.Success
            ? $"{effectiveTimestamp} [系统] {identity.Groups["identity"].Value.Trim()}：{identity.Groups["action"].Value.Trim()}"
            : $"{effectiveTimestamp} [系统] (游戏)：{body}";
        wrapped = line with { Text = wrappedText, Fusion = systemFusion };
        return true;
    }

    private static bool TryExtractDamagedChatBody(string text, out string body)
    {
        body = string.Empty;
        if (OcrShadowLineFusion.ExtractTimestamp(text) is null) return false;
        int channelEnd = Math.Max(text.LastIndexOf(']'), text.LastIndexOf('】'));
        int speakerEnd = Math.Max(text.LastIndexOf(')'), text.LastIndexOf('）'));
        int delimiter = Math.Max(text.LastIndexOf(':'), text.LastIndexOf('：'));
        if (channelEnd < 0 || speakerEnd <= channelEnd || delimiter <= speakerEnd) return false;
        string candidate = text[(delimiter + 1)..].Trim();
        if (candidate.Count(char.IsLetterOrDigit) < 2) return false;
        body = candidate;
        return true;
    }

    private static string? ResolveTimestampKey(ChatMessageEnvelope envelope, string text)
    {
        string? value = !string.IsNullOrWhiteSpace(envelope.Timestamp)
            ? envelope.Timestamp
            : OcrShadowLineFusion.ExtractTimestamp(text);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = new(value
            .Select(character => character is 'O' or 'o' ? '0' : character)
            .Where(character => char.IsDigit(character) || character is ':' or '：')
            .Select(character => character == '：' ? ':' : character)
            .ToArray());
        string[] parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out int minutes) && int.TryParse(parts[1], out int seconds)
            ? $"{minutes:D2}:{seconds:D2}"
            : null;
    }

    private static int? ParseTimestampSeconds(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;
        string[] parts = timestamp.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out int minutes) &&
               int.TryParse(parts[1], out int seconds) && seconds is >= 0 and < 60
            ? minutes * 60 + seconds
            : null;
    }

    private static int EditDistance(string left, string right, int stopAfter)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];
            for (int j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > stopAfter) return rowMinimum;
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static void ReplaceDecision(List<VisibleMessageDecision> decisions, int lineNumber, string decision)
    {
        int index = decisions.FindLastIndex(item => item.LineNumber == lineNumber);
        if (index >= 0) decisions[index] = decisions[index] with { Decision = decision };
    }
}

public sealed record RuntimeQueueAdmission(
    int Accepted,
    IReadOnlyList<VisibleChatMessage> OverflowRejected,
    int AlreadyAdmitted);

/// <summary>Bounded delivery queue. Idempotency uses only the ledger ID, never content similarity.</summary>
public sealed class ContinuousTranslationMessageQueue
{
    private readonly Queue<VisibleChatMessage> _pending = new();
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminal = new(StringComparer.Ordinal);
    private readonly int _capacity;
    public ContinuousTranslationMessageQueue(int capacity = 128)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public int Count => _pending.Count;
    public int TerminalRejectedCount => _terminal.Count;
    public int OverflowRejectedCount { get; private set; }
    public int SuppressTerminal(IEnumerable<VisibleChatMessage> messages)
    {
        int count = 0;
        foreach (var message in messages) if (_terminal.Add(message.OccurrenceKey)) count++;
        return count;
    }
    public RuntimeQueueAdmission Enqueue(IEnumerable<VisibleChatMessage> messages)
    {
        int accepted = 0, alreadyAdmitted = 0;
        var rejected = new List<VisibleChatMessage>();
        foreach (var message in messages)
        {
            if (!_admitted.Add(message.OccurrenceKey)) { alreadyAdmitted++; continue; }
            if (_pending.Count >= _capacity)
            {
                rejected.Add(message);
                _terminal.Add(message.OccurrenceKey);
                OverflowRejectedCount++;
                continue;
            }
            _pending.Enqueue(message);
            accepted++;
        }
        return new(accepted, rejected, alreadyAdmitted);
    }
    public IReadOnlyList<VisibleChatMessage> Take(int maximum, DateTimeOffset now)
    {
        var selected = new List<VisibleChatMessage>();
        while (selected.Count < Math.Max(0, maximum) && _pending.TryDequeue(out var item))
            selected.Add(item);
        return selected;
    }
    public int Complete(IReadOnlyList<VisibleChatMessage> messages, IncrementalGeneralChatShadowResult result, DateTimeOffset now)
    {
        var succeeded = result.Lines.Where(line => !string.IsNullOrWhiteSpace(line.FinalOutput))
            .Select(line => line.SessionFingerprint).ToHashSet(StringComparer.Ordinal);
        return SuppressTerminal(messages.Where(message => !succeeded.Contains(message.OccurrenceKey)));
    }
    public int RejectOnException(IEnumerable<VisibleChatMessage> messages) => SuppressTerminal(messages);
    public void Reset()
    {
        _pending.Clear(); _admitted.Clear(); _terminal.Clear(); OverflowRejectedCount = 0;
    }

    // Legacy diagnostic caller; the active queue always preserves ledger order.
    public bool HasNewerPendingThan(IReadOnlyList<VisibleChatMessage> inFlight) =>
        inFlight.Count > 0 && _pending.Any(item => inFlight.All(previous =>
            string.CompareOrdinal(item.TimestampKey, previous.TimestampKey) > 0));
}

/// <summary>Rejects unchanged screenshots and waits for a second observation of a new, weakly anchored body.</summary>
public sealed class ContinuousChatChangeGate
{
    private string? _lastSnapshot;
    private readonly Dictionary<string, int> _pendingObservations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _released = new(StringComparer.Ordinal);
    public ContinuousChatGateDecision Observe(IReadOnlyList<OcrShadowLineResult> lines, bool allowAccept = true)
    {
        var current = lines.Select(line => (Line: line, Envelope: ChatMessageEnvelopeExtractor.Extract(line.Text)))
            .Where(item => item.Envelope.HasHighConfidencePrefix && !string.IsNullOrWhiteSpace(item.Envelope.Body))
            .Select(item => (item.Line, Key: ResolveKnownKey(Normalize(item.Envelope.Body))))
            .ToArray();
        string[] bodies = current.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        string snapshot = string.Join("\u001f", bodies);
        bool snapshotUnchanged = string.Equals(snapshot, _lastSnapshot, StringComparison.Ordinal);
        _lastSnapshot = snapshot;
        var accepted = new List<OcrShadowLineResult>();
        int pending = 0;
        foreach ((OcrShadowLineResult line, string key) in current)
        {
            if (_released.Contains(key)) continue;
            int count = _pendingObservations.TryGetValue(key, out int prior) ? prior + 1 : 1;
            _pendingObservations[key] = count;
            bool crossCandidate = line.Sources.Select(source => source.CandidateIndex).Distinct().Count() > 1;
            if (crossCandidate || count >= 2)
            {
                if (allowAccept)
                {
                    accepted.Add(line);
                    _released.Add(key);
                    _pendingObservations.Remove(key);
                }
                else
                {
                    pending++;
                }
            }
            else pending++;
        }
        if (accepted.Count > 0)
            return new ContinuousChatGateDecision("new_message_detected", accepted, accepted.Count);
        if (pending > 0)
            return new ContinuousChatGateDecision("waiting_text_stability", Array.Empty<OcrShadowLineResult>(), pending);
        if (snapshotUnchanged)
            return new ContinuousChatGateDecision("frame_unchanged", Array.Empty<OcrShadowLineResult>(), 0);
        return accepted.Count == 0
            ? new ContinuousChatGateDecision("waiting_text_stability", Array.Empty<OcrShadowLineResult>(), 0)
            : new ContinuousChatGateDecision("new_message_detected", accepted, accepted.Count);
    }
    public void Reset() { _lastSnapshot = null; _pendingObservations.Clear(); _released.Clear(); }
    private string ResolveKnownKey(string normalized)
    {
        foreach (string known in _pendingObservations.Keys.Concat(_released))
        {
            if (IsNear(known, normalized)) return known;
        }
        return normalized;
    }
    private static string Normalize(string body) => string.Concat(body.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static bool IsNear(string left, string right)
    {
        if (left == right) return true;
        if (left.Length < 8 || right.Length < 8 || Math.Abs(left.Length - right.Length) > 2) return false;
        int mismatches = left.Zip(right, (a, b) => a == b ? 0 : 1).Sum() + Math.Abs(left.Length - right.Length);
        return mismatches <= 1;
    }
}

public sealed record PersistedTranslationRegion(
    double X, double Y, double Width, double Height,
    double WorkAreaX, double WorkAreaY, double WorkAreaWidth, double WorkAreaHeight,
    double VirtualWidth, double VirtualHeight, double DpiScale);

public static class ContinuousTranslationRegionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLTranslator", "continuous-translation-region.v0.1.json");
    public static void Save(Rect region, Rect workArea, double dpiScale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(new PersistedTranslationRegion(
            region.X, region.Y, region.Width, region.Height, workArea.X, workArea.Y, workArea.Width, workArea.Height,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, dpiScale), JsonOptions));
    }
    public static bool TryLoadCompatible(Rect workArea, double dpiScale, out Rect region)
    {
        region = Rect.Empty;
        if (!File.Exists(DefaultPath)) return false;
        try
        {
            PersistedTranslationRegion? saved = JsonSerializer.Deserialize<PersistedTranslationRegion>(File.ReadAllText(DefaultPath), JsonOptions);
            if (saved is null || !IsCompatible(saved, workArea, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, dpiScale)) return false;
            var candidate = new Rect(saved.X, saved.Y, saved.Width, saved.Height);
            region = candidate;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    public static bool IsCompatible(PersistedTranslationRegion saved, Rect workArea, double virtualWidth, double virtualHeight, double dpiScale)
    {
        if (Math.Abs(saved.VirtualWidth - virtualWidth) > 1 || Math.Abs(saved.VirtualHeight - virtualHeight) > 1 || Math.Abs(saved.DpiScale - dpiScale) > 0.05)
            return false;
        var candidate = new Rect(saved.X, saved.Y, saved.Width, saved.Height);
        return candidate.Width >= 8 && candidate.Height >= 8 && workArea.Contains(candidate);
    }
}

public static class TranslationSidecarPositioner
{
    public static Rect Calculate(Rect chatRegion, Rect workArea, double width, double height, double gap = 12)
    {
        double y = Math.Clamp(chatRegion.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        double right = chatRegion.Right + gap;
        if (right + width <= workArea.Right) return new Rect(right, y, width, height);
        double left = chatRegion.Left - gap - width;
        if (left >= workArea.Left) return new Rect(left, y, width, height);
        return new Rect(Math.Clamp(right, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)), y, width, height);
    }
}

public static class TranslationSidecarPositionStore
{
    private sealed record Saved(double Left, double Top, double VirtualWidth, double VirtualHeight, double DpiScale);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLTranslator", "translation-sidecar-position.v0.1.json");
    public static void Save(double left, double top, double dpiScale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(new Saved(left, top, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, dpiScale)));
    }
    public static bool TryLoad(Rect workArea, double width, double height, double dpiScale, out Point point)
    {
        point = default;
        try
        {
            if (!File.Exists(DefaultPath)) return false;
            Saved? saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(DefaultPath));
            if (saved is null || Math.Abs(saved.VirtualWidth - SystemParameters.VirtualScreenWidth) > 1 || Math.Abs(saved.VirtualHeight - SystemParameters.VirtualScreenHeight) > 1 || Math.Abs(saved.DpiScale - dpiScale) > .05) return false;
            Rect candidate = new(saved.Left, saved.Top, width, height);
            if (!workArea.Contains(candidate)) return false;
            point = new Point(saved.Left, saved.Top);
            return true;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }
}
