using System.Text.RegularExpressions;

namespace LoLTranslator.App;

public enum RuntimeTextColor { Unknown, Blue, Red, Gold, White }
public enum RuntimeColorEvidence { Unknown, ExplicitTeamChannel, PixelRegion, SessionMemory }
public sealed record RuntimeIdentityColorHint(string SourceChampion, RuntimeTextColor Color, RuntimeColorEvidence Evidence);
public sealed record RuntimeSourceStyle(RuntimeTextColor ActorColor, RuntimeColorEvidence ActorEvidence,
    bool HasSystemGoldText, IReadOnlyList<RuntimeIdentityColorHint> Targets);
public sealed record RuntimeDisplayIdentity(string CanonicalId, string Name, TranslationSidecarTextRole Role,
    RuntimeColorEvidence Evidence);
public sealed record SafeRuntimeDisplayMessage(string EventId, long Sequence, TranslationSidecarDisplayLine Display)
{
    public RuntimeFrameTiming? FrameTiming { get; init; }
    public DateTimeOffset? FirstObservedCaptureAtUtc { get; init; }
}
public sealed record RuntimePresentationRejection(string Code, bool BeforeTranslation);

/// <summary>Internal preparation data, not a drawable payload. Raw IDs are retained only for leak checks.</summary>
public sealed record PreparedRuntimePresentation(RuntimeChatEvent Event, ChatMessageEnvelope TranslationEnvelope,
    string Channel, RuntimeDisplayIdentity? Actor, IReadOnlyList<RuntimeDisplayIdentity> Targets,
    IReadOnlyList<string> ForbiddenIdentifiers, IReadOnlyList<OfficialTrilingualRuntimeTermMatch> SourceTerms,
    TranslationSidecarTextRole BodyRole);
public sealed record RuntimePresentationPreparation(PreparedRuntimePresentation? Plan, string? FailureReason);

/// <summary>Pre-model identity validation and post-translation safe segment projection; no WPF dependency.</summary>
public sealed class RuntimePresentationProjector(OfficialTrilingualRuntimeTerms terms, OfficialGameEntityOcrRepair repair)
{
    private static readonly string[] Locales = ["en-US", "zh-CN", "ko-KR"];
    internal static readonly Regex TargetIdentity = new(
        @"(?:has\s+(?:targeted|slain|shut\s+down)|slain\s+by)\s+(?<player>[^\r\n()（）]{1,112}?)\s*[-–—]?\s*[\(（](?<champion>[^\)）\r\n]{1,48})[\)）]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SpellTarget = new(@"^\s*(?<champion>[\p{L} .'’\-]{2,48})\s+used\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IEnumerable<(string Champion, int Start, int Length)> TargetSlots(string rawText, ChatMessageEnvelope envelope)
    {
        foreach (Match match in TargetIdentity.Matches(rawText))
        {
            Group slot = match.Groups["champion"];
            yield return (slot.Value, slot.Index, slot.Length);
        }
        if (envelope.HasHighConfidencePrefix)
        {
            Match spell = SpellTarget.Match(envelope.Body);
            int bodyStart = rawText.LastIndexOf(envelope.Body, StringComparison.Ordinal);
            if (spell.Success && bodyStart >= 0)
            {
                Group slot = spell.Groups["champion"];
                yield return (slot.Value, bodyStart + slot.Index, slot.Length);
            }
        }
    }

    public RuntimePresentationPreparation Prepare(OcrShadowLineResult source, string locale)
    {
        RuntimeChatEvent? contract = source.RuntimeEvent;
        RuntimePresentationPreparation Reject(string reason) => new(null, reason);
        if (contract is null || contract.Kind == RuntimeChatEventKind.Unknown) return Reject("unclassified_event");
        ChatMessageEnvelope original = contract.Envelope;
        ChatMessageEnvelope envelope = repair.Repair(original);
        if (!envelope.HasHighConfidencePrefix) return Reject("unsafe_source_envelope");
        if (ChatMessageEnvelopeExtractor.Extract(envelope.Body).HasHighConfidencePrefix ||
            Regex.IsMatch(envelope.Body, @"^\s*\d{1,3}\s*[:：]\s*\d{2}\s*\[")) return Reject("sender_envelope_in_body");
        RuntimeSourceStyle style = contract.SourceStyle ?? LegacyStyle(source);
        bool team = IsTeam(original.Channel);
        bool neutral = contract.Kind == RuntimeChatEventKind.System &&
            original.PlayerOrRole is "(游戏)" or "（游戏）" or "(game)";
        if (neutral && contract.SystemEnvelopeKind is not (RuntimeSystemEnvelopeKind.NeutralObjectiveAnnouncement or
            RuntimeSystemEnvelopeKind.KnownNeutralAnnouncement))
            return Reject("neutral_event_structure_unresolved");
        var forbidden = new List<string>();
        string rawIdentity = original.PlayerOrRole ?? string.Empty;
        string? rawPlayer = PlayerPart(rawIdentity);
        if (!neutral && !string.IsNullOrWhiteSpace(rawPlayer)) forbidden.Add(rawPlayer);
        RuntimeDisplayIdentity? actor = null;
        if (!neutral)
        {
            var champion = Champion(Slot(envelope.PlayerOrRole), locale);
            // Only explicit player chat with a separated parenthesized identity
            // can degrade its sender label independently of its reliable body.
            // Pings/system actions still require resolved participants.
            bool independentPlayerBody = contract.Kind == RuntimeChatEventKind.PlayerText &&
                (contract.SourceHasDelimiter || original.RecoveredMissingDelimiter) && !string.IsNullOrWhiteSpace(original.Channel) &&
                !string.IsNullOrWhiteSpace(Slot(original.PlayerOrRole));
            bool independentStructuredBody = contract.Kind is RuntimeChatEventKind.Ping or RuntimeChatEventKind.System &&
                !neutral && !string.IsNullOrWhiteSpace(Slot(original.PlayerOrRole)) &&
                style.ActorEvidence != RuntimeColorEvidence.Unknown &&
                style.ActorColor is RuntimeTextColor.Blue or RuntimeTextColor.Red &&
                RuntimeStructuredEventRenderer.TryRenderPing(envelope.Body, locale, out _);
            bool independentBody = independentPlayerBody || independentStructuredBody;
            if (champion is null && !independentBody) return Reject("unresolved_official_actor");
            RuntimeTextColor color = team ? RuntimeTextColor.Blue : style.ActorEvidence == RuntimeColorEvidence.Unknown
                ? RuntimeTextColor.Unknown : style.ActorColor;
            if (color is not (RuntimeTextColor.Blue or RuntimeTextColor.Red) && !independentBody) return Reject("actor_color_unresolved");
            actor = new(champion?.CanonicalId ?? "unresolved:actor", champion?.TargetText ??
                (locale.StartsWith("zh") ? "未知英雄" : locale.StartsWith("ko") ? "알 수 없는 챔피언" : "Unknown champion"), Role(color),
                team ? RuntimeColorEvidence.ExplicitTeamChannel : style.ActorEvidence);
        }
        else if (!style.HasSystemGoldText) return Reject("neutral_system_color_unresolved");

        var targets = new List<RuntimeDisplayIdentity>();
        foreach (Match target in TargetIdentity.Matches(envelope.Body))
        {
            string player = target.Groups["player"].Value.Trim().TrimEnd('-', '–', '—').TrimEnd();
            if (player.Length > 0) forbidden.Add(player);
            if (!AddTarget(target.Groups["champion"].Value)) return Reject("unresolved_official_target");
        }
        Match spellTarget = SpellTarget.Match(envelope.Body);
        if (spellTarget.Success && !AddTarget(spellTarget.Groups["champion"].Value)) return Reject("unresolved_official_target");
        bool AddTarget(string surface)
        {
            OfficialTrilingualRuntimeTermMatch? target = Champion(surface, locale, targetSlot: true);
            if (target is null) return false;
            RuntimeIdentityColorHint[] hints = style.Targets.Where(hint => hint.Evidence != RuntimeColorEvidence.Unknown &&
                Champion(hint.SourceChampion, locale, targetSlot: true)?.CanonicalId == target.CanonicalId).ToArray();
            var colors = hints.Select(hint => hint.Color).Distinct().ToArray();
            RuntimeTextColor color = colors.Length == 1 ? colors[0] : RuntimeTextColor.Unknown;
            targets.Add(new(target.CanonicalId, target.TargetText, Role(color), hints.Length > 0 ? hints[0].Evidence : RuntimeColorEvidence.Unknown));
            return true;
        }
        if (contract.Kind == RuntimeChatEventKind.PlayerText && forbidden.Any(id => ContainsIdentifier(envelope.Body, id)))
            return Reject("player_identifier_in_model_body");

        // The translation service sees a champion-only source envelope. It must
        // never reconstruct an identity from its translated prefix later.
        string safeActor = actor is null ? "(游戏)" : actor.CanonicalId == "unresolved:actor" ? "(Unknown champion)" : $"({Slot(envelope.PlayerOrRole)})";
        var safeEnvelope = envelope with
        {
            Prefix = $"{contract.Timestamp} {original.Channel} {safeActor}：",
            PlayerOrRole = safeActor
        };
        bool goldAction = style.HasSystemGoldText && contract.Kind != RuntimeChatEventKind.PlayerText &&
            (neutral || RuntimeStructuredEventRenderer.TryRenderPing(envelope.Body, locale, out _) ||
             Regex.IsMatch(envelope.Body, @"^(?:has\s+(?:targeted|slain|shut\s+down|disconnected|reconnected)|.+\s+used\s+)", RegexOptions.IgnoreCase));
        return new(new(contract, safeEnvelope, Channel(original.Channel, locale), actor, targets.AsReadOnly(),
            forbidden.AsReadOnly(), terms.MatchExact(envelope.Body, Locales, locale),
            goldAction ? TranslationSidecarTextRole.System : TranslationSidecarTextRole.Normal), null);
    }

    public SafeRuntimeDisplayMessage? Compose(PreparedRuntimePresentation plan, string? output, string locale, out string? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(output)) { failure = "translation_no_output"; return null; }
        if (plan.ForbiddenIdentifiers.Any(id => ContainsIdentifier(output, id)) ||
            ChatMessageEnvelopeExtractor.Extract(output).HasHighConfidencePrefix)
        { failure = "output_identity_or_envelope_leak"; return null; }

        var segments = new List<TranslationSidecarSegment>();
        void Add(string text, TranslationSidecarTextRole role) { if (text.Length > 0) segments.Add(new(text, role)); }
        if (plan.Event.Timestamp is { } time) Add(time + " ", TranslationSidecarTextRole.Muted);
        if (plan.Channel.Length > 0) Add(plan.Channel + " ", plan.Actor?.Role ?? TranslationSidecarTextRole.Normal);
        if (plan.Actor is { } actor) Add(actor.Name, actor.Role);
        if (plan.Actor is not null || plan.Channel.Length > 0)
            Add(plan.Event.SourceHasDelimiter ? (locale.StartsWith("zh") ? "：" : ": ") : " ", TranslationSidecarTextRole.Normal);

        var spans = new List<(int Start, int Length, TranslationSidecarTextRole Role)>();
        foreach (var target in plan.Targets)
            AddSpans(target.Name, target.Role);
        // An invented item in model output does not become trusted merely by
        // matching a dictionary: source and target must share a canonical ID.
        var sourceIds = plan.SourceTerms.Select(term => term.CanonicalId).ToHashSet(StringComparer.Ordinal);
        foreach (var term in terms.MatchExact(output, new[] { locale }, locale))
            if (sourceIds.Contains(term.CanonicalId) && term.TermType is "item" or "summoner_spell")
                spans.Add((term.Start, term.Length, TranslationSidecarTextRole.Item));
        void AddSpans(string name, TranslationSidecarTextRole role)
        {
            foreach (Match match in Regex.Matches(output, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])"))
                spans.Add((match.Index, match.Length, role));
            // CJK sentences have no spaces around names; exact official names
            // remain spans even next to another CJK word.
            if (name.Any(c => c > 127))
                for (int offset = 0; (offset = output.IndexOf(name, offset, StringComparison.Ordinal)) >= 0; offset += name.Length)
                    spans.Add((offset, name.Length, role));
        }
        int cursor = 0;
        foreach (var span in spans.OrderBy(span => span.Start).ThenByDescending(span => span.Length))
        {
            if (span.Start < cursor) continue;
            Add(output[cursor..span.Start], plan.BodyRole);
            Add(output.Substring(span.Start, span.Length), span.Role);
            cursor = span.Start + span.Length;
        }
        Add(output[cursor..], plan.BodyRole);
        return new(plan.Event.Id, plan.Event.Sequence, new(segments.AsReadOnly()))
        { FrameTiming = plan.Event.FrameTiming, FirstObservedCaptureAtUtc = plan.Event.FirstObservedCaptureAtUtc };
    }

    private OfficialTrilingualRuntimeTermMatch? Champion(string? surface, string locale, bool targetSlot = false)
    {
        if (string.IsNullOrWhiteSpace(surface)) return null;
        var corrected = repair.Repair(new($"({surface}):", "", true, PlayerOrRole: $"({surface})"));
        string slot = Slot(corrected.PlayerOrRole) ?? surface;
        if (targetSlot)
        {
            // Use the existing target-slot correction policy (which may allow
            // two edits), also for its associated raw OCR colour evidence.
            var target = repair.Repair(new("", $"has targeted Target ({surface})", true));
            Match match = TargetIdentity.Match(target.Body);
            if (match.Success) slot = match.Groups["champion"].Value;
        }
        var matches = terms.MatchExact(slot, Locales, locale).Where(term => term.TermType == "champion" && term.Start == 0 && term.Length == slot.Length).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string? Slot(string? identity)
    {
        if (identity is null) return null;
        int open = identity.LastIndexOfAny(['(', '（']), close = identity.LastIndexOfAny([')', '）']);
        return open >= 0 && close > open ? identity[(open + 1)..close].Trim() : null;
    }
    private static string? PlayerPart(string identity)
    {
        int open = identity.LastIndexOfAny(['(', '（']);
        return (open < 0 ? identity : identity[..open]).Trim();
    }
    private static bool ContainsIdentifier(string text, string identity) => identity.Length > 0 && Regex.IsMatch(text,
        $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(identity)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static TranslationSidecarTextRole Role(RuntimeTextColor color) => color switch
    {
        RuntimeTextColor.Blue => TranslationSidecarTextRole.AlliedIdentity,
        RuntimeTextColor.Red => TranslationSidecarTextRole.EnemyIdentity,
        _ => TranslationSidecarTextRole.Normal
    };
    internal static RuntimeTextColor ParseColor(string? value) => value switch
    { "blue" => RuntimeTextColor.Blue, "red" => RuntimeTextColor.Red, "gold" => RuntimeTextColor.Gold, "white" => RuntimeTextColor.White, _ => RuntimeTextColor.Unknown };
    private static bool IsTeam(string? channel) => channel is not null && Regex.IsMatch(channel, @"Team|队伍|隊伍|\bWE\b|팀", RegexOptions.IgnoreCase);
    private static string Channel(string? channel, string locale)
    {
        if (IsTeam(channel)) return locale.StartsWith("zh") ? "[队伍]" : locale.StartsWith("ko") ? "[팀]" : "[Team]";
        if (channel?.Contains("All", StringComparison.OrdinalIgnoreCase) == true) return locale.StartsWith("zh") ? "[所有人]" : "[All]";
        return string.Empty; // Never display the synthetic [系统] compatibility prefix.
    }
    private static RuntimeSourceStyle LegacyStyle(OcrShadowLineResult source)
    {
        string? color = source.Fusion.Reasons.FirstOrDefault(reason => reason.StartsWith("lol_source_identity_", StringComparison.Ordinal))?["lol_source_identity_".Length..];
        return new(ParseColor(color), RuntimeColorEvidence.PixelRegion,
            source.Fusion.Reasons.Contains("lol_system_gold_row"), Array.Empty<RuntimeIdentityColorHint>());
    }
}
