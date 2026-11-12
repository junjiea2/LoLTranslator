namespace LoLTranslator.App;

/// <summary>Historical diagnostic compatibility only. Continuous runtime uses RuntimePresentationProjector.</summary>
public static class LegacySidecarPresentationPolicy
{
    private static readonly System.Text.RegularExpressions.Regex LeadingGameTimestamp = new(
        @"^\s*[0-9OoIl]{1,3}\s*[:：.]\s*[0-9OoIl]{2}",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static bool HasTrustedPresentation(
        IncrementalGeneralChatShadowLine line,
        TranslationSidecarDisplayLine display)
    {
        bool hasTrustedColour = display.Segments.Any(segment => segment.Role is
            TranslationSidecarTextRole.AlliedIdentity or
            TranslationSidecarTextRole.EnemyIdentity or
            TranslationSidecarTextRole.Item or
            TranslationSidecarTextRole.System);
        if (!hasTrustedColour) return false;

        if (line.Route is not ("general_translation" or "hybrid")) return true;

        // Model output is eligible only when the input envelope reliably kept
        // the player identity outside MessageBody. A coloured pixel alone is
        // insufficient: it can belong to a target/item later in the same row.
        if (LeadingGameTimestamp.IsMatch(line.MessageBody)) return false;
        if (string.IsNullOrWhiteSpace(line.ChatPrefix)) return false;
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(
            line.ChatPrefix + " placeholder");
        bool reliableEnvelope = envelope.HasHighConfidencePrefix &&
                                !string.IsNullOrWhiteSpace(envelope.PlayerOrRole);
        bool reliableRelation = line.SenderRelation is "ally" or "enemy";
        bool explicitSystem = line.ChatPrefix.Contains("[系统]", StringComparison.OrdinalIgnoreCase) ||
                              line.ChatPrefix.Contains("[系統]", StringComparison.OrdinalIgnoreCase) ||
                              line.ChatPrefix.Contains("[System]", StringComparison.OrdinalIgnoreCase);
        bool hasSystemColour = display.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.System);
        return reliableEnvelope && (reliableRelation || explicitSystem && hasSystemColour);
    }

    public static bool TryCreatePrivacySafePrefix(IncrementalGeneralChatShadowLine line, out string? safePrefix)
    {
        safePrefix = null;
        if (string.IsNullOrWhiteSpace(line.ChatPrefix) || string.IsNullOrWhiteSpace(line.FinalOutput)) return false;
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(line.ChatPrefix + " placeholder");
        if (!envelope.HasHighConfidencePrefix) return false;
        // Never re-display OCR's player identity. Preserve the game timestamp
        // and project only the parenthesized champion/role slot. Upstream OCR
        // repair owns official entity correction; the free-form player id to
        // the left of the slot is intentionally discarded.
        string playerIdentity = envelope.PlayerOrRole ?? string.Empty;
        int championOpen = playerIdentity.LastIndexOfAny(['(', '（']);
        int championClose = playerIdentity.LastIndexOfAny([')', '）']);
        string playerId = (championOpen >= 0 ? playerIdentity[..championOpen] : playerIdentity).Trim();
        if (ContainsSenderEnvelope(line.MessageBody) ||
            (!string.IsNullOrWhiteSpace(playerIdentity) && line.FinalOutput.Contains(playerIdentity, StringComparison.OrdinalIgnoreCase)) ||
            (playerId.Length >= 3 && line.FinalOutput.Contains(playerId, StringComparison.OrdinalIgnoreCase)))
            return false;
        string champion = championOpen >= 0 && championClose > championOpen + 1
            ? playerIdentity[(championOpen + 1)..championClose].Trim()
            : string.Empty;
        if (champion.Equals("游戏", StringComparison.OrdinalIgnoreCase) ||
            champion.Equals("遊戲", StringComparison.OrdinalIgnoreCase) ||
            champion.Equals("game", StringComparison.OrdinalIgnoreCase))
            champion = string.Empty;
        if (string.IsNullOrWhiteSpace(envelope.Channel) && string.IsNullOrWhiteSpace(champion)) return false;
        string[] safeParts = [envelope.Timestamp?.Trim() ?? string.Empty, envelope.Channel?.Trim() ?? string.Empty, champion];
        safePrefix = string.Join(" ", safeParts.Where(part => !string.IsNullOrWhiteSpace(part))) + "：";
        return true;
    }

    private static bool ContainsSenderEnvelope(string value)
    {
        if (LeadingGameTimestamp.IsMatch(value)) return true;
        ChatMessageEnvelope possibleEnvelope = ChatMessageEnvelopeExtractor.Extract(value);
        return possibleEnvelope.HasHighConfidencePrefix && !string.IsNullOrWhiteSpace(possibleEnvelope.PlayerOrRole);
    }

}

