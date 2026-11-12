using System.Text.RegularExpressions;

namespace LoLTranslator.App;

public enum RuntimeChatEventKind { PlayerText, Ping, System, Unknown }
public enum RuntimeSystemEnvelopeKind { Unspecified, NeutralObjectiveAnnouncement, KnownNeutralAnnouncement }

/// <summary>Immutable contract carried from admission through translation. Raw OCR stays on the source row.</summary>
public sealed record RuntimeChatEvent(
    string Id,
    long Sequence,
    RuntimeChatEventKind Kind,
    string ActorKey,
    string? Timestamp,
    DateTimeOffset ObservedAtUtc,
    ChatMessageEnvelope Envelope,
    bool SourceHasDelimiter)
{
    public RuntimeFrameTiming? FrameTiming { get; init; }
    // Earliest matching accepted OCR observation, not ground-truth screen onset.
    public DateTimeOffset? FirstObservedCaptureAtUtc { get; init; }
    public RuntimeSourceStyle? SourceStyle { get; init; }
    public RuntimeSystemEnvelopeKind SystemEnvelopeKind { get; init; }
}

public sealed record ClassifiedChatObservation(
    RuntimeChatEventKind Kind,
    string ActorKey,
    string? Timestamp,
    int? GameSeconds,
    ChatMessageEnvelope Envelope,
    bool SourceHasDelimiter)
{
    public RuntimeSystemEnvelopeKind SystemEnvelopeKind { get; init; }
}

/// <summary>Parse the envelope once. An explicit player channel wins over words inside the body.</summary>
public static class RuntimeChatEventClassifier
{
    private static readonly Regex LeadingTime = Pattern(@"^\s*[0-9OoIl]{1,3}\s*[:：.]\s*[0-9OoIl]{2}\s*");
    private static readonly Regex ActorAction = Pattern(
        @"^(?<actor>[^\r\n]{1,112}?[\(（][^\)）\r\n]{1,48}[\)）]|[^\r\n:：]{1,80}?)\s+(?<action>(?:is\s+on\b|signals?\b|is\s+asking\b|has\s+(?:targeted|slain|stolen|shut\s+down|drawn|disconnected|reconnected)\b|was\s+slain\b|purchased\b|used\b|destroyed\b|님이\b).*)$");
    private static readonly Regex PingAction = Pattern(
        @"^(?:is\s+on\s+the\s+way\b|signals?\b|is\s+asking\s+for\b|has\s+targeted\b|enemy\s+has\s+vision\b|(?:님이\s*)?가고\s*있음|正在路上|敌人消失|敵人消失)");
    private static readonly Regex TargetSpellPing = Pattern(
        @"^[\p{L}\p{N} .'’\-]{1,64}\s+used\s+(?:[QWERDF]\b|Ignite\b|Teleport\b|Flash\b|ultimate\b).*$");
    private static readonly Regex NeutralEvent = Pattern(
        @"^(?:(?:An?\s+(?:enemy\s+)?)?Objective\s+Bount(?:y|ies)\b|Bonus\s+Bounty\b)");
    private static readonly Regex KnownNeutralEvent = Pattern(
        @"^(?:A\s+surrender\s+vote\s+has\s+been\s+started\b|The\s+enemy\s+team\s+has\s+agreed\s+to\s+surrender\b)");
    private static readonly Regex KnownColonPing = Pattern(
        @"^(?:[\p{L}\p{N} .'’\-&]+\s+(?:used\s+)?(?:Flash|Exhaust|Ghost|Heal|Barrier|Smite|Ignite|Teleport|Cleanse)\b|[\p{L}\p{N} .'’\-&]+\s+-\s+Alive\b|[\p{L}\p{N} .'’\-&]+\s+R\s*-\s*Ready\b|Wait\s+For\s+.+|Jungle\s+Quest\s+Complete\b|(?:Infernal|Cloud|Mountain|Ocean|Chemtech|Hextech|Elder)\s+(?:Drake|Dragon)\s*-\s*Spawning\s+in\s+\d{1,2}:\d{2}\b|Rift\s+Herald\s*-\s*Spawning\s+in\s+\d{1,2}:\d{2}\b)");

    public static ClassifiedChatObservation? Classify(OcrShadowLineResult source)
    {
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(source.Text);
        string? timestamp = NormalizeTimestamp(OcrShadowLineFusion.ExtractTimestamp(source.Text) ?? envelope.Timestamp);
        bool explicitPlayerChannel = !string.IsNullOrWhiteSpace(envelope.Channel) &&
            !envelope.Channel.Contains("系统", StringComparison.Ordinal) &&
            !envelope.Channel.Contains("系統", StringComparison.Ordinal) &&
            !envelope.Channel.Contains("System", StringComparison.OrdinalIgnoreCase);
        if (envelope.HasHighConfidencePrefix && explicitPlayerChannel)
            return Create(RuntimeChatEventKind.PlayerText, envelope, timestamp, !envelope.RecoveredMissingDelimiter);

        if (envelope.HasHighConfidencePrefix)
        {
            // A typed skill-target template may have a colon but no player-chat
            // channel. Ordinary sentences containing 'used'/'purchased' do not qualify.
            RuntimeChatEventKind kind = TargetSpellPing.IsMatch(envelope.Body) || KnownColonPing.IsMatch(envelope.Body)
                ? RuntimeChatEventKind.Ping
                : source.Fusion.Reasons.Contains("lol_system_gold_row", StringComparer.Ordinal)
                    ? RuntimeChatEventKind.System : RuntimeChatEventKind.Unknown;
            return Create(kind, envelope, timestamp, true);
        }

        string text = LeadingTime.Replace(source.Text, string.Empty, 1).Trim();
        Match actor = ActorAction.Match(text);
        if (actor.Success)
        {
            string identity = actor.Groups["actor"].Value.Trim();
            string body = actor.Groups["action"].Value.Trim();
            RuntimeChatEventKind kind = PingAction.IsMatch(body) ? RuntimeChatEventKind.Ping : RuntimeChatEventKind.System;
            // Prefix is a compatibility display projection, not rewritten OCR.
            envelope = new ChatMessageEnvelope($"{timestamp} [系统] {identity}：", body, true,
                timestamp, "[系统]", identity);
            return Create(kind, envelope, timestamp, false);
        }
        if (timestamp is not null && (NeutralEvent.IsMatch(text) || KnownNeutralEvent.IsMatch(text) ||
            source.Fusion.Reasons.Contains("lol_system_gold_row", StringComparer.Ordinal)))
        {
            envelope = new ChatMessageEnvelope($"{timestamp} [系统] (游戏)：", text, true,
                timestamp, "[系统]", "(游戏)");
            return Create(RuntimeChatEventKind.System, envelope, timestamp, false) with
            {
                SystemEnvelopeKind = NeutralEvent.IsMatch(text) ? RuntimeSystemEnvelopeKind.NeutralObjectiveAnnouncement
                    : KnownNeutralEvent.IsMatch(text) ? RuntimeSystemEnvelopeKind.KnownNeutralAnnouncement
                    : RuntimeSystemEnvelopeKind.Unspecified
            };
        }
        return null;
    }

    private static ClassifiedChatObservation Create(RuntimeChatEventKind kind, ChatMessageEnvelope envelope,
        string? timestamp, bool delimiter) => new(kind,
        (envelope.PlayerOrRole ?? string.Empty).Trim().ToUpperInvariant(), timestamp,
        timestamp is null ? null : int.Parse(timestamp.Split(':')[0]) * 60 + int.Parse(timestamp.Split(':')[1]),
        envelope with { Timestamp = timestamp }, delimiter);

    private static string? NormalizeTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string cleaned = new(value.Select(c => c is 'O' or 'o' ? '0' : c is 'I' or 'l' ? '1' : c)
            .Where(c => char.IsDigit(c) || c is ':' or '：' or '.').Select(c => c is '：' or '.' ? ':' : c).ToArray());
        string[] parts = cleaned.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s) && s is >= 0 and < 60
            ? $"{m:D2}:{s:D2}" : null;
    }
    private static Regex Pattern(string text) => new(text,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
