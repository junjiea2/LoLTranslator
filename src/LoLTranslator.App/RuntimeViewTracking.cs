namespace LoLTranslator.App;

public sealed record RuntimeViewReceipt(string EventId, long Sequence, string Status,
    DateTimeOffset AtUtc, long SubmittedAgeMilliseconds, long? CaptureAgeMilliseconds)
{
    public long? FirstObservedAgeMilliseconds { get; init; }
}

/// <summary>
/// Observational bookkeeping only: no queue, dedup decision, ordering or release authority.
/// A render opportunity means WPF reached its render callback with a fully laid-out
/// row inside the visible window. It does not prove desktop visibility or human viewing.
/// </summary>
public sealed class RuntimeViewTracking(Func<DateTimeOffset>? utcNow = null)
{
    private sealed record Entry(SafeRuntimeDisplayMessage Message, DateTimeOffset SubmittedAt)
    {
        public bool HadRenderOpportunity { get; set; }
    }
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    public event Action<RuntimeViewReceipt>? Receipt;
    public int ActiveCount => _active.Count;

    public void Submitted(SafeRuntimeDisplayMessage message)
    {
        // Tracking is keyed by the admitted event, never by text similarity.
        if (_active.ContainsKey(message.EventId)) return;
        _active.Add(message.EventId, new(message, _utcNow()));
    }

    public void RenderOpportunity(IEnumerable<string> fullyVisibleEventIds)
    {
        foreach (string id in fullyVisibleEventIds)
        {
            if (!_active.TryGetValue(id, out Entry? entry) || entry.HadRenderOpportunity) continue;
            entry.HadRenderOpportunity = true;
            Emit(entry, "render_opportunity");
        }
    }

    public void Removed(string eventId, string reason)
    {
        if (!_active.Remove(eventId, out Entry? entry)) return;
        Emit(entry, reason + (entry.HadRenderOpportunity ? "_after_render_opportunity" : "_before_render_opportunity"));
    }

    public void Reset()
    {
        foreach (string id in _active.Keys.ToArray()) Removed(id, "session_reset");
    }

    private void Emit(Entry entry, string status)
    {
        DateTimeOffset now = _utcNow();
        Receipt?.Invoke(new(entry.Message.EventId, entry.Message.Sequence, status, now,
            Math.Max(0, (long)(now - entry.SubmittedAt).TotalMilliseconds),
            entry.Message.FrameTiming is { } timing ? Math.Max(0, (long)(now - timing.CapturedAtUtc).TotalMilliseconds) : null)
        { FirstObservedAgeMilliseconds = entry.Message.FirstObservedCaptureAtUtc is { } first
            ? Math.Max(0, (long)(now - first).TotalMilliseconds) : null });
    }
}
