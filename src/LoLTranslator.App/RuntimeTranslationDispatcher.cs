namespace LoLTranslator.App;

public enum RuntimeTranslationLane { Deterministic, Model }
public enum RuntimeDispatchState { Queued, Processing, Released, Rejected, Failed, Expired, Superseded, Cancelled }

public sealed record RuntimeTranslationWork(IReadOnlyList<VisibleChatMessage> Messages,
    ShadowMatchContextOption Context, string TargetLocale);
public sealed record RuntimeDispatchTransition(VisibleChatMessage Message, RuntimeTranslationLane Lane,
    RuntimeDispatchState State, string Reason, long AgeMilliseconds)
{
    public bool IsFailure => State is RuntimeDispatchState.Rejected or RuntimeDispatchState.Failed or
        RuntimeDispatchState.Expired or RuntimeDispatchState.Superseded or RuntimeDispatchState.Cancelled;
}
public sealed record RuntimeDispatchDelivery(VisibleChatMessage Message, IncrementalGeneralChatShadowResult Result);
public sealed record RuntimeDispatchUpdate(IReadOnlyList<RuntimeDispatchTransition> Transitions,
    IReadOnlyList<RuntimeDispatchDelivery> Deliveries);

/// <summary>
/// Single-owner (dispatcher thread) state machine. Workers return values only.
/// One physical task per lane, including cancelled providers that have not yet
/// returned. A timeout never spawns another concurrent model request.
/// </summary>
public sealed class RuntimeTranslationDispatcher : IDisposable
{
    private sealed class Entry(VisibleChatMessage message, ShadowMatchContextOption context, string locale,
        RuntimeTranslationLane lane, DateTimeOffset observedAt)
    {
        public VisibleChatMessage Message { get; } = message;
        public ShadowMatchContextOption Context { get; } = context;
        public string Locale { get; } = locale;
        public RuntimeTranslationLane Lane { get; } = lane;
        public DateTimeOffset ObservedAt { get; } = observedAt;
        public RuntimeDispatchState State { get; set; }
        public bool Terminal => State is not (RuntimeDispatchState.Queued or RuntimeDispatchState.Processing);
    }
    private sealed record Flight(long Session, Entry[] Entries, CancellationTokenSource Cancellation,
        Task<IncrementalGeneralChatShadowResult> Task);
    private sealed class Lane(int capacity, int batchSize,
        Func<RuntimeTranslationWork, CancellationToken, Task<IncrementalGeneralChatShadowResult>> processor)
    {
        public int Capacity { get; } = capacity;
        public int BatchSize { get; } = batchSize;
        public Func<RuntimeTranslationWork, CancellationToken, Task<IncrementalGeneralChatShadowResult>> Processor { get; } = processor;
        public Queue<Entry> Pending { get; } = new();
        public Flight? Active { get; set; }
    }
    private readonly Lane _fast;
    private readonly Lane _model;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _deadline;
    private readonly HashSet<string> _knownIds = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _activeEntries = new();
    private readonly List<RuntimeDispatchTransition> _transitions = new();
    private long _session;
    private bool _disposed;

    public RuntimeTranslationDispatcher(
        Func<RuntimeTranslationWork, CancellationToken, Task<IncrementalGeneralChatShadowResult>> deterministic,
        Func<RuntimeTranslationWork, CancellationToken, Task<IncrementalGeneralChatShadowResult>> model,
        int deterministicCapacity = 128, int modelCapacity = 32, TimeSpan? deadline = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(deterministic);
        ArgumentNullException.ThrowIfNull(model);
        if (deterministicCapacity < 1 || modelCapacity < 1) throw new ArgumentOutOfRangeException(nameof(modelCapacity));
        _deadline = deadline ?? ContinuousOverlayPresentationPolicy.DefaultDeadline;
        if (_deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _fast = new(deterministicCapacity, 16, deterministic);
        _model = new(modelCapacity, 3, model);
    }
    public int FastQueued => _fast.Pending.Count(entry => !entry.Terminal);
    public int ModelQueued => _model.Pending.Count(entry => !entry.Terminal);
    public int Count => FastQueued + ModelQueued;
    public int RetainedMessageCount => _fast.Pending.Count + _model.Pending.Count +
        (_fast.Active?.Entries.Length ?? 0) + (_model.Active?.Entries.Length ?? 0);
    public int ActiveWorkerCount => (_fast.Active is null ? 0 : 1) + (_model.Active is null ? 0 : 1);
    public int CompletedWorkerCount => (_fast.Active?.Task.IsCompleted == true ? 1 : 0) +
        (_model.Active?.Task.IsCompleted == true ? 1 : 0);
    public int OverflowRejectedCount { get; private set; }

    public void Enqueue(IEnumerable<VisibleChatMessage> messages, ShadowMatchContextOption context, string targetLocale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);
        foreach (VisibleChatMessage message in messages)
        {
            if (message.Line.RuntimeEvent is not { } contract || contract.Id != message.OccurrenceKey)
                throw new ArgumentException("Only ledger-admitted events may be dispatched.", nameof(messages));
            if (!_knownIds.Add(contract.Id)) continue;
            var kind = contract.Kind == RuntimeChatEventKind.PlayerText ? RuntimeTranslationLane.Model : RuntimeTranslationLane.Deterministic;
            // End-to-end freshness starts at the image, not at model admission.
            var entry = new Entry(message, context, targetLocale, kind,
                contract.FirstObservedCaptureAtUtc ?? contract.FrameTiming?.CapturedAtUtc ?? message.DetectedAtUtc ?? contract.ObservedAtUtc);
            Lane lane = kind == RuntimeTranslationLane.Model ? _model : _fast;
            if (contract.Kind == RuntimeChatEventKind.Unknown)
                Transition(entry, RuntimeDispatchState.Rejected, "unclassified_event");
            else if (lane.Pending.Count(item => !item.Terminal) >= lane.Capacity)
            {
                OverflowRejectedCount++;
                Transition(entry, RuntimeDispatchState.Rejected, "lane_capacity_exceeded");
            }
            else
            {
                lane.Pending.Enqueue(entry);
                Transition(entry, RuntimeDispatchState.Queued, "admitted");
            }
        }
    }

    /// <summary>Call every UI tick, even if the image is unchanged or OCR is busy.</summary>
    public RuntimeDispatchUpdate Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _utcNow();
        foreach (Entry entry in _activeEntries.ToArray())
            if (now - entry.ObservedAt >= _deadline)
                Transition(entry, RuntimeDispatchState.Expired,
                    entry.State == RuntimeDispatchState.Queued ? "queue_deadline_exceeded" : "processing_deadline_exceeded");

        var completed = new List<(Entry Entry, IncrementalGeneralChatShadowResult Result, IncrementalGeneralChatShadowLine Line)>();
        Collect(_fast, completed);
        Collect(_model, completed);
        var deliveries = new List<RuntimeDispatchDelivery>();
        // Both lanes may have completed since the last tick. Merge that set by
        // ledger sequence, not by OCR timestamp or arbitrary task poll order.
        foreach (var item in completed.OrderBy(item => item.Entry.Message.Line.RuntimeEvent!.Sequence))
        {
            Entry entry = item.Entry;
            if (entry.Terminal) continue;
            if (string.IsNullOrWhiteSpace(item.Line.FinalOutput))
            {
                Transition(entry, RuntimeDispatchState.Failed,
                    item.Line.PresentationRejection is { } rejected ? "presentation_" + rejected.Code :
                    DiagnosticFailure(item.Line.FailureReason));
                continue;
            }
            if (item.Line.SafeDisplay is not { } safe || safe.EventId != entry.Message.OccurrenceKey ||
                safe.Sequence != entry.Message.Line.RuntimeEvent!.Sequence)
            {
                Transition(entry, RuntimeDispatchState.Failed, "missing_safe_display_contract");
                continue;
            }
            // Completion order is NOT display order. The bounded view inserts
            // by immutable ledger sequence; a fast Ping cannot cancel prose.
            Transition(entry, RuntimeDispatchState.Released, "released_for_presentation");
            // Safety projection is complete. Released still isn't a screen
            // acknowledgement: the UI must report that it accepted the view.
            deliveries.Add(new(entry.Message, item.Result with
            {
                Lines = new[] { item.Line }, ModelCallCount = item.Line.ModelCalled ? 1 : 0,
                DeduplicatedCount = 0, NoOutputCount = 0, DisplayedNewCount = 1
            }));
        }
        Start(_fast);
        Start(_model);
        var update = new RuntimeDispatchUpdate(_transitions.ToArray(), deliveries);
        _transitions.Clear();
        return update;
    }

    private void Collect(Lane lane, List<(Entry, IncrementalGeneralChatShadowResult, IncrementalGeneralChatShadowLine)> completed)
    {
        Flight? flight = lane.Active;
        if (flight is null || !flight.Task.IsCompleted) return;
        lane.Active = null;
        flight.Cancellation.Dispose();
        try
        {
            IncrementalGeneralChatShadowResult result = flight.Task.GetAwaiter().GetResult();
            if (flight.Session != _session) return;
            // A malformed/reordered provider adapter must never attach a result
            // to an unrelated event. Validate the whole batch before release.
            if (result.Lines.Count != flight.Entries.Length || result.Lines.Select(line => line.SessionFingerprint).Distinct().Count() != flight.Entries.Length ||
                result.Lines.Any(line => !flight.Entries.Any(entry =>
                    line.SessionFingerprint == entry.Message.OccurrenceKey && line.RuntimeEvent == entry.Message.Line.RuntimeEvent)))
            {
                Fail(flight, "invalid_event_result_contract");
                return;
            }
            if (ReferenceEquals(lane, _fast) && (result.ModelCallCount != 0 || result.Lines.Any(line => line.ModelCalled)))
            {
                Fail(flight, "model_work_in_deterministic_lane");
                return;
            }
            foreach (Entry entry in flight.Entries)
                completed.Add((entry, result, result.Lines.Single(line => line.SessionFingerprint == entry.Message.OccurrenceKey)));
        }
        catch
        {
            // No exception text or provider payload enters operational traces.
            Fail(flight, "processor_failure");
        }
    }

    private void Fail(Flight flight, string reason)
    {
        if (flight.Session != _session) return;
        foreach (Entry entry in flight.Entries.Where(entry => !entry.Terminal))
            Transition(entry, RuntimeDispatchState.Failed, reason);
    }

    // Only fixed operational codes, never provider output or exception details.
    private static string DiagnosticFailure(string? reason) => reason switch
    {
        "untranslated_source_echo" or "blocked_official_term_ambiguity_no_model_guess" or
        "ordinary_english_common_word_context_no_provider_call" or "unknown_ping_template_no_provider_call" or
        "unknown_system_template_no_provider_call" or "hybrid_locked_marker_not_preserved" or
        "model_added_chat_envelope" or "output_ungrounded_desktop_ui_term" or "local_provider_unavailable" or
        "normalization_approximate_surface_requires_review" or "player_identifier_in_model_body" => reason,
        _ => "translation_no_output"
    };

    private void Start(Lane lane)
    {
        // Expired/superseded queue entries must release their text payload even
        // while an uncooperative provider still occupies the worker slot.
        if (lane.Pending.Any(entry => entry.Terminal))
        {
            Entry[] live = lane.Pending.Where(entry => !entry.Terminal).ToArray();
            lane.Pending.Clear();
            foreach (Entry entry in live) lane.Pending.Enqueue(entry);
        }
        if (lane.Active is { } active)
        {
            if (active.Entries.All(entry => entry.Terminal)) active.Cancellation.Cancel();
            return; // Retain the physical slot until the provider actually exits.
        }
        while (lane.Pending.TryPeek(out Entry? stale) && stale.Terminal) lane.Pending.Dequeue();
        if (!lane.Pending.TryPeek(out Entry? first)) return;
        var batch = new List<Entry>();
        while (batch.Count < lane.BatchSize && lane.Pending.TryPeek(out Entry? next))
        {
            if (next.Terminal) { lane.Pending.Dequeue(); continue; }
            if (next.Context != first.Context || next.Locale != first.Locale) break;
            batch.Add(lane.Pending.Dequeue());
            Transition(next, RuntimeDispatchState.Processing, "worker_started");
        }
        var cancellation = new CancellationTokenSource();
        var work = new RuntimeTranslationWork(batch.Select(entry => entry.Message).ToArray(), first.Context, first.Locale);
        lane.Active = new(_session, batch.ToArray(), cancellation,
            Task.Run(() => lane.Processor(work, cancellation.Token)));
    }

    private void Transition(Entry entry, RuntimeDispatchState state, string reason)
    {
        entry.State = state;
        if (entry.Terminal) _activeEntries.Remove(entry);
        else _activeEntries.Add(entry);
        _transitions.Add(new(entry.Message, entry.Lane, state, reason,
            Math.Max(0, (long)(_utcNow() - entry.ObservedAt).TotalMilliseconds)));
    }

    /// <summary>Cancel logical work; do not abandon the bounded physical workers.</summary>
    public RuntimeDispatchUpdate Reset()
    {
        foreach (Entry entry in _activeEntries.ToArray())
            Transition(entry, RuntimeDispatchState.Cancelled, "session_cancelled");
        _session++;
        foreach (Lane lane in new[] { _fast, _model })
        {
            lane.Pending.Clear();
            lane.Active?.Cancellation.Cancel();
        }
        _knownIds.Clear();
        OverflowRejectedCount = 0;
        var update = new RuntimeDispatchUpdate(_transitions.ToArray(), Array.Empty<RuntimeDispatchDelivery>());
        _transitions.Clear();
        return update;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        foreach (Lane lane in new[] { _fast, _model })
            if (lane.Active is { } flight)
                _ = flight.Task.ContinueWith(task => { _ = task.Exception; flight.Cancellation.Dispose(); }, TaskScheduler.Default);
    }
}
