namespace LoLTranslator.App;

/// <summary>
/// Last-mile guard for a recurring OCR failure mode: an already displayed chat
/// line can later lose its channel/sender envelope and look like a new row. A
/// low-identity near-duplicate is suppressed; a new line with a trusted team or
/// explicit sender relation remains eligible even when players repeat text.
/// </summary>
public sealed class TranslationSidecarDuplicateGuard
{
    private static readonly System.Text.RegularExpressions.Regex LeadingTimestamp = new(
        @"^\s*[0-9OoIl]{1,3}\s*[:：.]\s*[0-9OoIl]{2}\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex RepeatedPing = new(
        @"(?:signals?\s+(?:that\s+)?(?:enemy|enemies|enernies)\s+are\s+(?:missing|rnissing)|signals?\s+enemy\s+has\s+vision|signals?\s+to\s+be\s+cautious|is\s+on\s+the\s+way|is\s+asking\s+for\s+(?:assistance|vision)|enemy\s+has\s+vision\s+here|正在路上|请求协助|請求協助|敌人消失|敵人消失|敌方.*视野|敵方.*視野|가고\s*있음|지원\s*요청|적이\s*사라졌다고\s*알림)",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private sealed record Recent(
        string Source,
        string Output,
        string Identity,
        string? PingKey,
        int? GameSeconds,
        bool HasRenderIdentity,
        DateTimeOffset At);
    private readonly List<Recent> _recent = new();
    private readonly TimeSpan _retention;

    public TranslationSidecarDuplicateGuard(TimeSpan? retention = null) =>
        _retention = retention ?? TimeSpan.FromSeconds(45);

    public void Reset() => _recent.Clear();

    public bool ShouldDisplay(IncrementalGeneralChatShadowLine line, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);
        _recent.RemoveAll(item => now - item.At > _retention);
        string source = Normalize(line.MessageBody);
        string output = Normalize(line.FinalOutput ?? string.Empty);
        string identity = Normalize(LeadingTimestamp.Replace(line.ChatPrefix ?? string.Empty, string.Empty));
        string? pingKey = ResolvePingKey(line);
        // A damaged full-row OCR reading may put the timestamp in MessageBody
        // instead of ChatPrefix. Recover it from either location before making
        // ordering and quality-dominance decisions.
        int? gameSeconds = ResolveGameSeconds(line.ChatPrefix) ?? ResolveGameSeconds(line.MessageBody);
        bool hasRenderIdentity = line.SenderRelation is "ally" or "enemy";

        // A damaged OCR pass can rediscover a row after a newer row has already
        // been rendered. These late fallbacks have no trustworthy colour/side
        // relation and are more harmful than useful: they appear out of order
        // and are normally translated as plain white text.
        if (!hasRenderIdentity && gameSeconds is not null && _recent.Any(item =>
                item.GameSeconds is not null &&
                item.GameSeconds.Value > gameSeconds.Value + 3 &&
                now - item.At <= TimeSpan.FromSeconds(20)))
            return false;

        // Prefer the structured, coloured observation of a source row. The
        // same timestamp may contain multiple messages, so timestamp alone is
        // insufficient; require the same event family or near-identical text.
        bool lowerQualityDuplicate = !hasRenderIdentity && _recent.Any(item =>
            item.HasRenderIdentity &&
            gameSeconds is not null && item.GameSeconds == gameSeconds &&
            (pingKey is not null && item.PingKey == pingKey ||
             EquivalentSource(item.Source, source) || output.Length >= 2 && Similar(item.Output, output)));
        if (lowerQualityDuplicate) return false;

        if (pingKey is not null)
        {
            int recentPing = _recent.FindLastIndex(item =>
                item.PingKey == pingKey &&
                (gameSeconds is not null && item.GameSeconds == gameSeconds &&
                    (identity.Length == 0 || item.Identity.Length == 0 || Similar(item.Identity, identity)) ||
                 identity.Length > 0 && item.Identity == identity ||
                 identity.Length == 0 && item.Identity.Length == 0 && now - item.At <= TimeSpan.FromSeconds(4)) &&
                now - item.At <= TimeSpan.FromSeconds(12));
            if (recentPing >= 0)
            {
                // Extend the burst window from the latest observed ping so a
                // rapid ping chain remains one translated notification.
                _recent[recentPing] = _recent[recentPing] with { At = now };
                return false;
            }
        }
        bool trustedIdentity = HasTrustedIdentity(line);
        bool repeatedLowIdentity = !trustedIdentity && _recent.Any(item =>
            EquivalentSource(item.Source, source) || output.Length >= 2 && Similar(item.Output, output));
        if (repeatedLowIdentity) return false;
        _recent.Add(new Recent(source, output, identity, pingKey, gameSeconds, hasRenderIdentity, now));
        return true;
    }

    private static string? ResolvePingKey(IncrementalGeneralChatShadowLine line)
    {
        if (line.Route == "official_item_ping_runtime")
            return "item:" + string.Join('|', line.CanonicalIds.OrderBy(value => value, StringComparer.Ordinal));
        string eventText = $"{line.ChatPrefix} {line.MessageBody}";
        if (!RepeatedPing.IsMatch(eventText)) return null;
        string normalized = Normalize(eventText);
        if (normalized.Contains("ontheway", StringComparison.Ordinal) || normalized.Contains("正在路上", StringComparison.Ordinal) ||
            normalized.Contains("가고있음", StringComparison.Ordinal)) return "on_the_way";
        if (normalized.Contains("missing", StringComparison.Ordinal) || normalized.Contains("消失", StringComparison.Ordinal) ||
            normalized.Contains("사라졌다고", StringComparison.Ordinal)) return "enemy_missing";
        if (normalized.Contains("vision", StringComparison.Ordinal) || normalized.Contains("视野", StringComparison.Ordinal) ||
            normalized.Contains("視野", StringComparison.Ordinal)) return "vision";
        if (normalized.Contains("assistance", StringComparison.Ordinal) || normalized.Contains("协助", StringComparison.Ordinal) ||
            normalized.Contains("協助", StringComparison.Ordinal) || normalized.Contains("지원요청", StringComparison.Ordinal)) return "assistance";
        return "ping:" + normalized;
    }

    private static int? ResolveGameSeconds(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        var match = LeadingTimestamp.Match(prefix);
        if (!match.Success) return null;
        string normalized = new(match.Value
            .Select(character => character is 'O' or 'o' ? '0' : character is 'I' or 'l' ? '1' : character)
            .Where(character => char.IsDigit(character) || character is ':' or '：' or '.')
            .Select(character => character is '：' or '.' ? ':' : character)
            .ToArray());
        string[] parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out int minutes) &&
               int.TryParse(parts[1], out int seconds) && seconds is >= 0 and < 60
            ? minutes * 60 + seconds
            : null;
    }

    public static bool HasTrustedIdentity(IncrementalGeneralChatShadowLine line)
    {
        if (line.SenderRelation is "ally" or "enemy") return true;
        string prefix = line.ChatPrefix ?? string.Empty;
        return prefix.Contains("[Team]", StringComparison.OrdinalIgnoreCase) ||
               prefix.Contains("[队伍]", StringComparison.OrdinalIgnoreCase) ||
               prefix.Contains("[隊伍]", StringComparison.OrdinalIgnoreCase) ||
               prefix.Contains("[WE]", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool Similar(string left, string right)
    {
        if (left == right) return true;
        int maximum = Math.Max(left.Length, right.Length);
        if (Math.Min(left.Length, right.Length) < 4 || Math.Abs(left.Length - right.Length) > Math.Max(3, maximum / 3))
            return false;
        int allowed = Math.Max(1, (int)Math.Ceiling(maximum * .28));
        return EditDistance(left, right, allowed) <= allowed;
    }

    private static bool EquivalentSource(string left, string right)
    {
        if (Similar(left, right)) return true;
        if (Math.Min(left.Length, right.Length) < 7) return false;
        string shorter = left.Length <= right.Length ? left : right;
        string longer = left.Length <= right.Length ? right : left;
        // The low-quality form often contains the original action unchanged,
        // but incorrectly keeps timestamp, channel, player and champion in the
        // translatable body. Treat that envelope-expanded form as one source.
        return longer.Length <= shorter.Length * 5 && longer.Contains(shorter, StringComparison.Ordinal);
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
}
