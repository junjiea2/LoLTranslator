using System.Text.RegularExpressions;

namespace LoLTranslator.App;


/// <summary>
/// Sole semantic-dedup owner. Other stages consume the immutable ID; they must
/// not invent another identity from timestamp/body or translate original OCR.
/// </summary>
public sealed class RuntimeChatEventLedger
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Batch, DateTimeOffset? FirstCapture)> _weakObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Seconds, DateTimeOffset At)> _pingBursts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private sealed record VisualObservation(string Identity, string Timestamp, RuntimeChatEventKind Kind,
        string ChampionSlot, string Body, string Side, OcrBoundingBox Bounds);
    private IReadOnlyList<VisualObservation> _previousVisible = Array.Empty<VisualObservation>();
    private string _sessionId = Guid.NewGuid().ToString("N");
    private long _batch;
    private long _sequence;
    private int? _latestSeconds;
    private DateTimeOffset _latestObservedAt;

    public RuntimeChatEventLedger(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public VisibleMessageDiffResult Observe(
        IReadOnlyList<OcrShadowLineResult> lines,
        IReadOnlyDictionary<int, (string Hash, OcrBoundingBox Bounds)>? visualRows = null,
        bool suppressInitialBacklog = false,
        bool allowImmediateTimestampedRelease = true)
    {
        long batch = ++_batch;
        DateTimeOffset observedAt = _utcNow();
        var inserted = new List<VisibleChatMessage>();
        var decisions = new List<VisibleMessageDecision>();
        var currentVisible = new List<VisualObservation>();
        int pending = 0, visible = 0, baselined = 0;
        foreach (OcrShadowLineResult source in lines.OrderBy(row => row.LineNumber))
        {
            if (source.Fusion.IsAmbiguous || !(source.Fusion.Status.Equals("fused", StringComparison.OrdinalIgnoreCase) ||
                source.Fusion.Reasons.Contains("selected_chat_region_coordinate_row", StringComparer.Ordinal) ||
                source.Fusion.Reasons.Contains("structured_chat_envelope_anchor", StringComparer.Ordinal)))
            {
                decisions.Add(new(source.LineNumber, source.Text, string.Empty, false, "rejected_unreliable_row"));
                continue;
            }
            ClassifiedChatObservation? parsed = RuntimeChatEventClassifier.Classify(source);
            if (parsed is null || (parsed.Envelope.Body.Count(char.IsLetterOrDigit) < 2 &&
                !(parsed.Kind == RuntimeChatEventKind.PlayerText && ChatEmoticonPolicy.IsRecognized(parsed.Envelope.Body))) ||
                source.Fusion.Reasons.Contains("lol_system_continuation_row", StringComparer.Ordinal))
            {
                decisions.Add(new(source.LineNumber, source.Text, string.Empty, false, "rejected_no_chat_prefix"));
                continue;
            }
            visible++;
            string body = parsed.Envelope.Body;
            string category = parsed.Kind switch
            {
                RuntimeChatEventKind.PlayerText => "player_text",
                RuntimeChatEventKind.Ping => "ping",
                RuntimeChatEventKind.Unknown => "unclassified",
                _ => "system"
            };
            void Decide(string reason) => decisions.Add(new(source.LineNumber, source.Text, body, true, reason, category));
            // An actor is opaque: do not fuzzy-match different player names.
            // Only recognized deterministic Pings have a canonical action key;
            // player prose is never merged by edit distance (go mid != no mid).
            string signature = parsed.Kind == RuntimeChatEventKind.Ping &&
                RuntimeStructuredEventRenderer.TryRenderPing(body, "en-US", out string canonical)
                    ? canonical : Regex.Replace(body.Trim(), @"\s+", " ").ToLowerInvariant();
            string identity = $"{parsed.Timestamp}\u001f{parsed.Kind}\u001f{parsed.ActorKey}\u001f{signature}";
            if (parsed.Timestamp is not null && visualRows is not null && visualRows.TryGetValue(source.LineNumber, out var rowVisual))
            {
                string slot = Regex.Match(parsed.Envelope.PlayerOrRole ?? "", @"[\(（](?<slot>[^\)）]+)[\)）]\s*$").Groups["slot"].Value.Trim().ToUpperInvariant();
                string side = string.Equals(parsed.Envelope.Channel, "[Team]", StringComparison.OrdinalIgnoreCase) ||
                    parsed.Envelope.Channel is "[队伍]" or "[隊伍]" or "[팀]" ? "team" :
                    source.SourceStyle is { ActorEvidence: not RuntimeColorEvidence.Unknown, ActorColor: RuntimeTextColor.Blue } ? "blue" :
                    source.SourceStyle is { ActorEvidence: not RuntimeColorEvidence.Unknown, ActorColor: RuntimeTextColor.Red } ? "red" : "";
                var observation = new VisualObservation(identity, parsed.Timestamp, parsed.Kind, slot, signature, side, rowVisual.Bounds);
                currentVisible.Add(observation);
                // Opaque OCR player names can drift. Associate only consecutive
                // visible rows with exact body/slot/time/side and unique geometry.
                // Never merge different prose (go mid != no mid), by time alone,
                // or using the existing coarse pixel hash as proof of identity.
                if (!_seen.Contains(identity) && slot.Length > 0 && side.Length > 0 &&
                    _previousVisible.Count(priorRow => _seen.Contains(priorRow.Identity) && SameVisibleMessage(priorRow, observation)) == 1)
                {
                    _seen.Add(identity);
                    Decide("suppressed_visible_actor_ocr_variant");
                    continue;
                }
            }
            if (_seen.Contains(identity))
            {
                Decide("suppressed_event_ledger_duplicate");
                continue;
            }
            bool strong = allowImmediateTimestampedRelease && parsed.Timestamp is not null;
            bool repeated = _weakObservations.TryGetValue(identity, out var prior) && prior.Batch == batch - 1;
            DateTimeOffset? firstCapture = repeated ? prior.FirstCapture : source.FrameTiming?.CapturedAtUtc;
            if (!strong && !repeated)
            {
                // Repeated rows in one image are not a second observation.
                _weakObservations[identity] = (batch, source.FrameTiming?.CapturedAtUtc);
                pending++;
                Decide("waiting_second_ocr_frame");
                continue;
            }
            if (!suppressInitialBacklog && parsed.GameSeconds is int seconds && _latestSeconds is int latest)
            {
                if (seconds < latest)
                {
                    _seen.Add(identity);
                    Decide("late_historical_event");
                    continue;
                }
                // A single 2->7 OCR minute error must not move the session clock
                // forward and silence subsequent valid rows. Seek/replay requires
                // an explicit session reset; only wall-time-plausible advances pass.
                double elapsed = Math.Max(0, (observedAt - _latestObservedAt).TotalSeconds);
                if (seconds - latest > Math.Max(10, elapsed + 5))
                {
                    pending++;
                    Decide("held_implausible_timestamp_jump");
                    continue;
                }
            }
            // Claim the identity during the scan, not after the entire viewport:
            // two OCR rows in this observation must not emit the same event twice.
            _seen.Add(identity);
            if (parsed.GameSeconds is int admittedTime && (_latestSeconds is null || admittedTime > _latestSeconds))
            {
                _latestSeconds = admittedTime;
                _latestObservedAt = observedAt;
            }
            if (suppressInitialBacklog)
            {
                baselined++;
                Decide("established_startup_baseline");
                continue;
            }
            if (parsed.Kind == RuntimeChatEventKind.Ping && parsed.GameSeconds is int pingTime)
            {
                string burstKey = $"{parsed.ActorKey}\u001f{signature}";
                bool repeat = _pingBursts.TryGetValue(burstKey, out var priorPing) &&
                    pingTime >= priorPing.Seconds && pingTime - priorPing.Seconds <= 12 &&
                    observedAt - priorPing.At <= TimeSpan.FromSeconds(12);
                _pingBursts[burstKey] = (pingTime, observedAt);
                if (repeat)
                {
                    Decide("suppressed_ping_burst");
                    continue;
                }
            }
            long sequence = ++_sequence;
            string id = $"event:{_sessionId}:{sequence}";
            var contract = new RuntimeChatEvent(id, sequence, parsed.Kind, parsed.ActorKey,
                parsed.Timestamp, observedAt, parsed.Envelope, parsed.SourceHasDelimiter)
            { SourceStyle = source.SourceStyle, SystemEnvelopeKind = parsed.SystemEnvelopeKind, FrameTiming = source.FrameTiming,
                FirstObservedCaptureAtUtc = firstCapture };
            // Preserve raw text; all later interpretation uses RuntimeEvent.
            OcrShadowLineResult line = source with { RuntimeEvent = contract };
            (string Hash, OcrBoundingBox Bounds)? visual = visualRows is not null &&
                visualRows.TryGetValue(source.LineNumber, out var found) ? found : null;
            inserted.Add(new VisibleChatMessage(line, signature, id, visual?.Hash, visual?.Bounds,
                parsed.Timestamp, observedAt));
            Decide("inserted_typed_event");
        }
        foreach (string key in _weakObservations.Where(pair => batch - pair.Value.Batch > 2).Select(pair => pair.Key).ToArray())
            _weakObservations.Remove(key);
        _previousVisible = currentVisible;
        return new VisibleMessageDiffResult(inserted, visible, pending, decisions, baselined > 0);
    }

    private static bool SameVisibleMessage(VisualObservation left, VisualObservation right)
    {
        if (left.Timestamp != right.Timestamp || left.Kind != right.Kind || left.ChampionSlot != right.ChampionSlot ||
            left.Side != right.Side || left.Body != right.Body) return false;
        var a = left.Bounds; var b = right.Bounds;
        double overlap = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return overlap >= Math.Max(a.Height, b.Height) * .7 &&
            Math.Abs(a.X - b.X) <= Math.Max(a.Height, b.Height) * .5;
    }

    public void Reset()
    {
        _seen.Clear();
        _weakObservations.Clear();
        _pingBursts.Clear();
        _previousVisible = Array.Empty<VisualObservation>();
        _sessionId = Guid.NewGuid().ToString("N");
        _batch = _sequence = 0;
        _latestSeconds = null;
        _latestObservedAt = default;
    }
}
