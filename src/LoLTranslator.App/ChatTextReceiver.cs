using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LoLTranslator.App;

/// <summary>
/// OCR 端交给文本处理端的最小输入契约。
/// 后续替换 OCR 实现时，只需要继续生成这个对象即可。
/// </summary>
public sealed record OcrTextPayload(
    string Text,
    string SourceLanguageTag,
    DateTimeOffset ReceivedAt,
    string? SourceImagePath = null);

public sealed record ChatTextLine(
    int LineNumber,
    string OriginalText,
    string NormalizedText);

public sealed record ChatIntent(
    string ConceptId,
    string MatchedText,
    IReadOnlyDictionary<string, string> Slots,
    double Confidence,
    string Description);

public sealed record ChatTextAnalysis(
    OcrTextPayload Input,
    IReadOnlyList<ChatTextLine> Lines,
    IReadOnlyList<ChatIntent> Intents)
{
    public string ToDisplayText()
    {
        var builder = new StringBuilder();

        builder.AppendLine($"已接收 {Lines.Count} 行文本");
        builder.AppendLine($"来源语言：{Input.SourceLanguageTag}");
        builder.AppendLine();

        if (Intents.Count == 0)
        {
            builder.AppendLine("暂未识别出已收录的 LoL 局内意图。");
            builder.AppendLine("原文已保留，可继续补充词典或模板规则。");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("识别到的标准语义：");

        foreach (ChatIntent intent in Intents)
        {
            string slots = intent.Slots.Count == 0
                ? string.Empty
                : $" [{string.Join(", ", intent.Slots.Select(pair => $"{pair.Key}={pair.Value}"))}]";

            builder.AppendLine(
                $"- {intent.ConceptId}{slots}：{intent.Description} " +
                $"（匹配：{intent.MatchedText}，置信度 {intent.Confidence:0.00}）");
        }

        return builder.ToString().TrimEnd();
    }
}

public interface IChatTextReceiver
{
    ChatTextAnalysis Receive(OcrTextPayload payload);
}

/// <summary>
/// 接收 OCR 文本并转换为语言无关的 LoL 语义。
/// 这里故意不直接生成目标语言句子，避免把语义识别和翻译耦合在一起。
/// </summary>
public sealed class ChatTextReceiver : IChatTextReceiver
{
    private static readonly Regex LineBreakRegex =
        new(@"\r\n?|\u2028|\u2029", RegexOptions.Compiled);

    private static readonly Regex SpaceRegex =
        new(@"[ \t\u00A0\u3000]+", RegexOptions.Compiled);

    private static readonly Regex TimestampPrefixRegex =
        new(@"^\s*\[?\d{1,2}:\d{2}\]?\s*", RegexOptions.Compiled);

    private static readonly Regex LaneRegex =
        new(
            @"(?<![a-z])(?<lane>top|mid|middle|bot|bottom|adc|support|sup|jungle|jg)(?![a-z])|" +
            @"(?<lane>上路|中路|下路|辅助|打野)|" +
            @"(?<lane>탑|미드|바텀|원딜|서폿|정글)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecondsRegex =
        new(@"(?<seconds>\d{1,3})\s*(?:s|sec|secs|秒)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyList<IntentRule> Rules =
        new[]
        {
            new IntentRule("enemy.missing", "敌人消失或暂时不见", @"\b(?:enemy\s+)?(?:is\s+)?missing\b|\bmia\b|\bss\b|敌人(?:消失|不见)|不见了|没视野|사라짐|안 보여", 0.92),
            new IntentRule("lane.caution", "提醒队友小心某条线", @"\bcare\b|\bwatch\s+(?:out\s+)?(?:top|mid|bot|jg|jungle)\b|小心(?:上路|中路|下路|打野)|注意(?:上路|中路|下路|打野)|조심", 0.90),
            new IntentRule("assistance.request", "请求队友支援", @"\b(?:need\s+)?help\b|\bassist\b|支援|来帮|帮一下|도와|지원", 0.88),
            new IntentRule("objective.dragon.prepare", "准备争夺小龙", @"(?:dragon|drake|小龙|용).*(?:ready|prep|prepare|准备|开龙)|(?:ready|prep|prepare|准备|开龙).*(?:dragon|drake|小龙|용)", 0.94),
            new IntentRule("objective.baron.prepare", "准备争夺大龙", @"(?:baron|nashor|大龙|纳什).*(?:ready|prep|prepare|准备|开龙)|(?:ready|prep|prepare|准备|开龙).*(?:baron|nashor|大龙|纳什)", 0.94),
            new IntentRule("retreat", "撤退或拉开距离", @"\b(?:retreat|fall\s+back|back\s+off|disengage)\b|撤退|后撤|回撤|快走|빼", 0.93),
            new IntentRule("push", "推进兵线或推塔", @"\bpush(?:\s+(?:wave|mid|top|bot|tower))?\b|推线|推塔|推进|미세요", 0.90),
            new IntentRule("recall", "回城补给", @"\b(?:recall|back\s+to\s+base)\b|回城|回家|귀환", 0.91),
            new IntentRule("summoner.flash.unavailable", "闪现当前不可用", @"\b(?:no|without)\s+flash\b|\bflash\s+(?:down|used)\b|没闪|无闪|交闪|플래시 없음", 0.96),
            new IntentRule("ultimate.unavailable", "大招当前不可用", @"\b(?:no|without)\s+(?:ult|ultimate)\b|\bult\s+(?:down|used)\b|没大|无大|没R|궁 없음", 0.95),
            new IntentRule("summoner.teleport.unavailable", "传送当前不可用", @"\b(?:no|without)\s+(?:tp|teleport)\b|\btp\s+(?:down|used)\b|没传送|传送没好|텔포 없음", 0.95),
            new IntentRule("gank.request", "请求打野或队友抓人", @"\bgank(?:\s+me)?\b|来抓|抓(?:上|中|下)|帮抓|갱", 0.92),
            new IntentRule("target.focus", "集中攻击指定目标", @"\bfocus\b|集火|先打|集中火力|집중", 0.89),
            new IntentRule("backline.protect", "保护后排或核心输出", @"\b(?:peel|protect)\b|保护后排|保后排|保护(?:adc|射手)|딜러 보호", 0.90),
            new IntentRule("objective.respawn.countdown", "目标即将刷新或进入倒计时", @"\b(?:dragon|drake|baron|nashor)\b.*\d{1,3}\s*(?:s|sec|secs)|(?:小龙|大龙|纳什).*\d{1,3}\s*秒", 0.86)
        };

    public ChatTextAnalysis Receive(OcrTextPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(payload.Text))
        {
            return new ChatTextAnalysis(
                payload with { Text = string.Empty },
                Array.Empty<ChatTextLine>(),
                Array.Empty<ChatIntent>());
        }

        IReadOnlyList<ChatTextLine> lines = NormalizeLines(payload.Text);
        var intents = new List<ChatIntent>();

        foreach (ChatTextLine line in lines)
        {
            foreach (IntentRule rule in Rules)
            {
                Match match = rule.Pattern.Match(line.NormalizedText);

                if (!match.Success)
                {
                    continue;
                }

                var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? lane = ExtractLane(line.NormalizedText);

                if (lane is not null)
                {
                    slots["subject"] = lane;
                }

                Match secondsMatch = SecondsRegex.Match(line.NormalizedText);

                if (secondsMatch.Success)
                {
                    slots["seconds"] = secondsMatch.Groups["seconds"].Value;
                }

                intents.Add(new ChatIntent(
                    rule.ConceptId,
                    line.NormalizedText,
                    slots,
                    rule.Confidence,
                    rule.Description));
            }
        }

        return new ChatTextAnalysis(payload, lines, intents);
    }

    private static IReadOnlyList<ChatTextLine> NormalizeLines(string text)
    {
        string canonicalText = LineBreakRegex.Replace(text, "\n");
        var lines = new List<ChatTextLine>();

        foreach (string rawLine in canonicalText.Split('\n'))
        {
            string original = rawLine.Trim();

            if (original.Length == 0)
            {
                continue;
            }

            string normalized = SpaceRegex.Replace(original, " ").Trim();
            normalized = TimestampPrefixRegex.Replace(normalized, string.Empty);

            if (normalized.Length == 0)
            {
                continue;
            }

            lines.Add(new ChatTextLine(lines.Count + 1, original, normalized));
        }

        return lines;
    }

    private static string? ExtractLane(string text)
    {
        Match match = LaneRegex.Match(text);

        if (!match.Success)
        {
            return null;
        }

        string lane = match.Groups["lane"].Value.ToLower(CultureInfo.InvariantCulture);

        return lane switch
        {
            "top" or "上路" or "탑" => "top",
            "mid" or "middle" or "中路" or "미드" => "mid",
            "bot" or "bottom" or "adc" or "下路" or "원딜" or "바텀" => "bot",
            "support" or "sup" or "辅助" or "서폿" => "support",
            "jungle" or "jg" or "打野" or "정글" => "jungle",
            _ => lane
        };
    }

    private sealed record IntentRule(
        string ConceptId,
        string Description,
        string PatternText,
        double Confidence)
    {
        public Regex Pattern { get; } = new(
            PatternText,
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
