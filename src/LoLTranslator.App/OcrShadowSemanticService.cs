using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

public sealed record ShadowMatchContextOption(
    string DisplayName,
    string? Community,
    string? Channel,
    IReadOnlyList<string>? CompatibleCommunities = null);

public sealed record ShadowSemanticLocaleOption(
    string DisplayName,
    string LanguageTag);

public sealed record OcrShadowLineResult(
    int LineNumber,
    string Text,
    IReadOnlyList<SlangGraphSemanticMatch> Matches,
    IReadOnlyList<OcrShadowLineSource> Sources,
    OcrFusionMetadata Fusion)
{
    public RuntimeChatEvent? RuntimeEvent { get; init; }
    public RuntimeFrameTiming? FrameTiming { get; init; }
    public RuntimeSourceStyle? SourceStyle { get; init; }
    public PreparedRuntimePresentation? PreparedPresentation { get; init; }
}

/// <summary>
/// Describes confidence in associating OCR candidates into one message. This is
/// operational metadata only; it never changes the verification state of a Pack.
/// </summary>
public sealed record OcrFusionMetadata(
    string Status,
    double Confidence,
    IReadOnlyList<string> Reasons)
{
    public bool IsAmbiguous => string.Equals(Status, "ambiguous", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Identifies the OCR outputs that were fused into one displayed message. The
/// original text is retained verbatim for review; its comparison key is never a
/// semantic normalization and is not written into a Pack.
/// </summary>
public sealed record OcrShadowLineSource(
    int CandidateIndex,
    string RecognizerLanguageTag,
    int SourceLineNumber,
    string Text,
    string? Timestamp);

/// <summary>
/// One-shot local shadow adapter between OCR text and the frozen SlangGraph release.
/// It never uses draft data and does not send text or images over the network.
/// </summary>
public sealed class OcrShadowSemanticService
{
    private static readonly Regex CareAboutRegex = new(
        @"\bcare\s+about\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const string AutoSemanticLocale = "auto";
    public const string ExpectedPackId = "pack.game.lol.chat";
    public const string ExpectedPackVersion = "0.3.0";
    public const string ExpectedPackSha256 =
        "6C8FB1F090956F0F26A92836D3E8C160E02E992A389AC74CA2263CAAE8424FAB";
    public const string ExpectedRenderingPackId = "pack.game.lol.renderings";
    public const string ExpectedRenderingPackVersion = "0.1.0";
    public const string ExpectedRenderingPackSha256 =
        "54E9826497A84D19D2B5054B42C9D0AF7A348558FD40325495856B52C39DC755";

    private readonly SlangGraphSemanticMatcher _matcher;
    private readonly SlangGraphLocalizedRenderer _renderer;
    private readonly IReadOnlyList<string> _supportedLocales;

    private OcrShadowSemanticService(
        SlangGraphSemanticMatcher matcher,
        SlangGraphLocalizedRenderer renderer,
        IReadOnlyList<string> supportedLocales)
    {
        _matcher = matcher;
        _renderer = renderer;
        _supportedLocales = supportedLocales;
    }

    public static OcrShadowSemanticService LoadFromApplicationDirectory(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("应用目录不能为空。", nameof(applicationDirectory));
        }

        string dataDirectory = Path.Combine(applicationDirectory, "data", "packs");
        string corePackPath = Path.Combine(dataDirectory, "core", "communication.v0.2.jsonl");
        string releasePackPath = Path.Combine(dataDirectory, "lol", "slang-graph.v0.3.0.jsonl");
        string renderingPackPath = Path.Combine(
            dataDirectory,
            "localization",
            "lol-renderings.v0.1.0.jsonl");

        string actualHash = ComputeSha256(releasePackPath);
        string actualRenderingHash = ComputeSha256(renderingPackPath);

        if (!string.Equals(actualHash, ExpectedPackSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"0.3.0 Pack 哈希不匹配。期望 {ExpectedPackSha256}，实际 {actualHash}。");
        }

        if (!string.Equals(
                actualRenderingHash,
                ExpectedRenderingPackSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"正式 Rendering Pack 哈希不匹配。期望 {ExpectedRenderingPackSha256}，实际 {actualRenderingHash}。");
        }

        SlangGraphPackSet packSet = SlangGraphPackSetLoader.Load(
            new[] { corePackPath, releasePackPath, renderingPackPath });
        SlangGraphPack releasePack = packSet.GetPack(ExpectedPackId);
        SlangGraphPack renderingPack = packSet.GetPack(ExpectedRenderingPackId);

        if (!string.Equals(releasePack.Header.PackVersion, ExpectedPackVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"正式 Pack 版本不匹配。期望 {ExpectedPackVersion}，实际 {releasePack.Header.PackVersion}。");
        }

        SlangGraphReleaseGateReport releaseReport = SlangGraphReleaseGate.Evaluate(releasePack);

        if (!releaseReport.CanRelease)
        {
            throw new InvalidDataException("正式 0.3.0 Pack 未通过 release gate，拒绝用于 shadow 匹配。");
        }

        if (!string.Equals(
                renderingPack.Header.PackVersion,
                ExpectedRenderingPackVersion,
                StringComparison.OrdinalIgnoreCase) ||
            !SlangGraphReleaseGate.Evaluate(renderingPack).CanRelease)
        {
            throw new InvalidDataException("正式 Rendering 0.1.0 未通过版本或 release gate 检查。");
        }

        string[] supportedLocales = releasePack.Expressions
            .Where(expression => string.Equals(expression.Status, "verified", StringComparison.OrdinalIgnoreCase))
            .Select(expression => expression.Locale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OcrShadowSemanticService(
            new SlangGraphSemanticMatcher(packSet.CombinedGraph),
            new SlangGraphLocalizedRenderer(packSet.CombinedGraph),
            supportedLocales);
    }

    public IReadOnlyList<OcrShadowLineResult> Match(
        string recognizedText,
        string locale,
        ShadowMatchContextOption context)
    {
        return MatchCandidates(
            new[] { new OcrRecognitionResult(recognizedText, "direct-input") },
            locale,
            context);
    }

    /// <summary>
    /// Renders a semantic candidate using only the verified rendering release.
    /// This is intentionally separate from any general translation capability.
    /// </summary>
    public SlangGraphRenderedConcept Render(
        SlangGraphSemanticMatch match,
        string targetLocale)
    {
        ArgumentNullException.ThrowIfNull(match);
        return _renderer.Render(
            match.ConceptId,
            targetLocale,
            match.Slots,
            includeDraft: false);
    }

    /// <summary>
    /// Resolves verified typed entities from the frozen Pack without asserting
    /// a concept. Callers must still supply and enforce their own context.
    /// </summary>
    public IReadOnlyList<SlangGraphEntityResolution> ResolveVerifiedEntities(
        string text,
        string locale,
        ShadowMatchContextOption context,
        IReadOnlyList<string>? entityTypes = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _matcher.ResolveEntities(text, locale, includeDraft: false, entityTypes);
    }

    public SlangGraphRenderedEntity RenderVerifiedEntity(string entityId, string targetLocale) =>
        _renderer.RenderEntity(entityId, targetLocale, includeDraft: false);

    /// <summary>
    /// Matches each OCR engine output independently, then fuses only equivalent
    /// message lines. This preserves language-specific wins without concatenating
    /// duplicate copies of the same chat message.
    /// </summary>
    public IReadOnlyList<OcrShadowLineResult> MatchCandidates(
        IReadOnlyList<OcrRecognitionResult> recognitionCandidates,
        string locale,
        ShadowMatchContextOption context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recognitionCandidates);

        if (recognitionCandidates.Count == 0)
        {
            return Array.Empty<OcrShadowLineResult>();
        }

        var lines = new List<OcrShadowLineResult>();

        for (int candidateIndex = 0; candidateIndex < recognitionCandidates.Count; candidateIndex++)
        {
            OcrRecognitionResult candidate = recognitionCandidates[candidateIndex];

            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                continue;
            }

            string[] candidateLines = candidate.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string? pendingTimestamp = null;

            for (int lineIndex = 0; lineIndex < candidateLines.Length; lineIndex++)
            {
                string line = candidateLines[lineIndex];
                string? explicitTimestamp = OcrShadowLineFusion.ExtractTimestamp(line);

                if (OcrShadowLineFusion.IsTimestampOnly(line, explicitTimestamp))
                {
                    pendingTimestamp = explicitTimestamp;
                    continue;
                }

                string? timestamp = explicitTimestamp ?? pendingTimestamp;
                pendingTimestamp = null;
                var source = new OcrShadowLineSource(
                    candidateIndex,
                    candidate.RecognizerLanguageTag,
                    lineIndex + 1,
                    line,
                    timestamp);
                lines.Add(new OcrShadowLineResult(
                    0,
                    line,
                    MatchLine(line, locale, context),
                    new[] { source },
                    new OcrFusionMetadata("single_source", 1.0, Array.Empty<string>())));
            }
        }

        return OcrShadowLineFusion.Fuse(lines);
    }

    private IReadOnlyList<SlangGraphSemanticMatch> MatchLine(
        string text,
        string locale,
        ShadowMatchContextOption context)
    {
        IEnumerable<string> locales = string.Equals(locale, AutoSemanticLocale, StringComparison.OrdinalIgnoreCase)
            ? _supportedLocales
            : new[] { locale };
        IEnumerable<string?> communities = context.CompatibleCommunities is { Count: > 0 }
            ? context.CompatibleCommunities.Select(community => (string?)community)
            : new string?[] { context.Community };

        return locales
            .SelectMany(candidateLocale => communities.SelectMany(community => _matcher.Match(
                text,
                candidateLocale,
                includeDraft: false,
                new SlangGraphMatchContext(community, context.Channel))))
            // The published expression "care" is valid as a short LoL warning.
            // Ordinary English "care about ..." is a protected runtime boundary,
            // not a lane-caution message. This leaves "care", "top care", and
            // "bot care" available to the frozen Pack matcher without changing it.
            .Where(match => !(
                string.Equals(match.ConceptId, "game.lol:lane.caution", StringComparison.OrdinalIgnoreCase) &&
                CareAboutRegex.IsMatch(text)))
            .GroupBy(match => $"{match.ConceptId}\u001F{match.SourceRecordId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(match => match.Confidence)
            .ToArray();
    }

    public string FormatDisplayText(
        IReadOnlyList<OcrShadowLineResult> results,
        string ocrLocale,
        string semanticLocale,
        ShadowMatchContextOption context,
        string targetLocale)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Pack: {ExpectedPackId} {ExpectedPackVersion}");
        builder.AppendLine($"SHA-256: {ExpectedPackSha256}");
        builder.AppendLine($"OCR locale: {ocrLocale}");
        builder.AppendLine($"语义 locale: {semanticLocale}");
        builder.AppendLine($"目标 locale: {targetLocale}");
        builder.AppendLine(
            $"Rendering Pack: {ExpectedRenderingPackId} {ExpectedRenderingPackVersion}");
        builder.AppendLine($"上下文: {context.DisplayName}");
        builder.AppendLine();

        if (results.Count == 0)
        {
            builder.Append("没有可供 shadow 匹配的 OCR 文本。");
            return builder.ToString();
        }

        foreach (OcrShadowLineResult line in results)
        {
            builder.AppendLine($"第 {line.LineNumber} 行：{line.Text}");
            builder.AppendLine(
                "  OCR 来源: " +
                string.Join(
                    "; ",
                    line.Sources.Select(source =>
                        $"{source.RecognizerLanguageTag} 候选 {source.CandidateIndex + 1} / 行 {source.SourceLineNumber}" +
                        (source.Timestamp is null ? string.Empty : $" / {source.Timestamp}"))));
            builder.AppendLine(
                $"  融合状态: {FormatFusionStatus(line.Fusion)}（置信 {line.Fusion.Confidence:0.00}）");

            if (line.Fusion.IsAmbiguous)
            {
                builder.AppendLine(
                    "  注意: 此行存在 OCR 融合歧义；以下仅是各原始 OCR 行的已发布语义候选，不构成确认的融合结论。");
            }

            if (line.Matches.Count == 0)
            {
                builder.AppendLine("  无已发布语义匹配");
                continue;
            }

            foreach (SlangGraphSemanticMatch match in line.Matches)
            {
                SlangGraphRenderedConcept rendered = _renderer.Render(
                    match.ConceptId,
                    targetLocale,
                    match.Slots,
                    includeDraft: false);
                string slots = match.Slots.Count == 0
                    ? "无"
                    : string.Join(
                        ", ",
                        match.Slots.Select(slot =>
                            $"{slot.Key}={string.Join('|', slot.Value.Select(value => value.Value))}"));

                builder.AppendLine($"  Concept: {match.ConceptId}");
                builder.AppendLine($"  输出: {rendered.Text}");
                builder.AppendLine($"  解释: {rendered.Explanation}");
                builder.AppendLine(
                    $"  输出 locale: {rendered.ResolvedLocale}" +
                    (rendered.UsedFallback ? "（回退）" : string.Empty));
                builder.AppendLine($"  slots: {slots}");
                builder.AppendLine($"  来源记录: {match.SourceRecordId}");
                builder.AppendLine($"  置信度: {match.Confidence:0.00}");
                builder.AppendLine(
                    "  证据链: " +
                    string.Join(
                        "; ",
                        match.Evidence.Select(evidence =>
                            $"{evidence.Kind}={evidence.RecordId}")));
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatFusionStatus(OcrFusionMetadata fusion)
    {
        string status = fusion.Status switch
        {
            "fused" => "已可靠融合",
            "single_source" => "单一 OCR 来源",
            "ambiguous" => "fusion_ambiguous: " + string.Join(", ", fusion.Reasons),
            _ => fusion.Status
        };

        return status;
    }

    private static string ComputeSha256(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到正式 SlangGraph Pack。", path);
        }

        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// Pure, provenance-preserving OCR line fusion. It never changes the selected
/// line's text: comparison keys are used only to recognize duplicate messages.
/// </summary>
public static class OcrShadowLineFusion
{
    private enum RelationshipKind
    {
        Separate,
        SameMessage,
        Ambiguous
    }

    private sealed record LineRelationship(
        RelationshipKind Kind,
        double Confidence,
        string? Reason = null);

    private sealed class FusionGroup
    {
        public List<OcrShadowLineResult> Lines { get; } = [];
        public List<double> Confidences { get; } = [];
        public List<string> Reasons { get; } = [];
        public bool IsAmbiguous { get; private set; }

        public void Add(OcrShadowLineResult line, LineRelationship? relationship = null)
        {
            Lines.Add(line);

            if (relationship is null)
            {
                return;
            }

            Confidences.Add(relationship.Confidence);

            if (!string.IsNullOrWhiteSpace(relationship.Reason))
            {
                Reasons.Add(relationship.Reason);
            }

            IsAmbiguous |= relationship.Kind == RelationshipKind.Ambiguous;
        }

        public void MarkAmbiguous(string reason, double confidence)
        {
            IsAmbiguous = true;
            Reasons.Add(reason);
            Confidences.Add(confidence);
        }

        public void MarkAnchored(string reason, double confidence)
        {
            Reasons.Add(reason);
            Confidences.Add(confidence);
        }
    }

    private static readonly Regex TimestampRegex = new(
        @"(?<![\p{L}\p{N}])(?<minutes>[0-9OoIl]{1,3})\s*[:：]\s*(?<seconds>[0-5OoIl]?[0-9OoIl])(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BracketedPrefixRegex = new(
        @"^\s*[\[【(（].*?[\]】)）]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ComparableCharacterRegex = new(
        @"[\p{L}\p{N}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OcrShadowLineResult> Fuse(
        IEnumerable<OcrShadowLineResult> candidateLines)
    {
        ArgumentNullException.ThrowIfNull(candidateLines);

        OcrShadowLineResult[] lines = candidateLines.ToArray();
        int candidateCount = lines
            .SelectMany(line => line.Sources)
            .Select(source => source.CandidateIndex)
            .Distinct()
            .Count();
        var groups = new List<FusionGroup>();

        foreach (OcrShadowLineResult line in lines)
        {
            FusionGroup? group = null;
            LineRelationship? relationship = null;

            foreach (FusionGroup existing in groups)
            {
                // A source candidate may contain the same body twice.  Do not let
                // its second occurrence collapse into the first occurrence merely
                // because their text (or OCR timestamp) is identical.
                int candidateIndex = line.Sources[0].CandidateIndex;
                if (existing.Lines.SelectMany(existingLine => existingLine.Sources)
                    .Any(source => source.CandidateIndex == candidateIndex))
                {
                    continue;
                }
                LineRelationship comparison = CompareMessages(existing.Lines[0], line);

                if (comparison.Kind == RelationshipKind.Separate)
                {
                    continue;
                }

                group = existing;
                relationship = comparison;
                break;
            }

            if (group is null)
            {
                var newGroup = new FusionGroup();
                newGroup.Add(line);
                groups.Add(newGroup);
            }
            else
            {
                group.Add(line, relationship);
            }
        }

        foreach (FusionGroup group in groups)
        {
            bool hasTimestamp = group.Lines
                .SelectMany(line => line.Sources)
                .Any(source => source.Timestamp is not null);

            if (!group.IsAmbiguous && !hasTimestamp && group.Lines.Count == 1)
            {
                ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(group.Lines[0].Text);
                if (envelope.HasHighConfidencePrefix && !string.IsNullOrWhiteSpace(envelope.Body))
                {
                    group.MarkAnchored("structured_chat_envelope_anchor", 0.90);
                }
                else
                {
                    group.MarkAmbiguous("unanchored_no_timestamp", 0.40);
                }
            }
        }

        return groups
            .Select((group, index) => CreateFusedLine(group, index))
            // OCR timestamps are display metadata, not a message identity.  Screen
            // line order is the only safe ordering basis when several messages share
            // the same second or a timestamp is damaged.
            .OrderBy(line => line.Sources.Min(source => source.SourceLineNumber))
            .ThenBy(line => line.Sources.Min(source => source.CandidateIndex))
            .Select((line, index) => line with { LineNumber = index + 1 })
            .ToArray();
    }

    public static string? ExtractTimestamp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = TimestampRegex.Match(text);

        static string NormalizeDigits(string value) => new(value.Select(character => character switch
        {
            'O' or 'o' => '0',
            'I' or 'l' => '1',
            _ => character
        }).ToArray());

        if (!match.Success ||
            !int.TryParse(NormalizeDigits(match.Groups["minutes"].Value), out int minutes) ||
            !int.TryParse(NormalizeDigits(match.Groups["seconds"].Value), out int seconds) ||
            seconds > 59)
        {
            return null;
        }

        return $"{minutes:D2}:{seconds:D2}";
    }

    public static bool IsTimestampOnly(string text, string? timestamp)
    {
        if (timestamp is null)
        {
            return false;
        }

        string withoutTimestamp = TimestampRegex.Replace(text, string.Empty, 1).Trim();
        return string.IsNullOrWhiteSpace(withoutTimestamp) ||
               withoutTimestamp is "-" or "—";
    }

    private static OcrShadowLineResult CreateFusedLine(
        FusionGroup group,
        int groupIndex)
    {
        OcrShadowLineResult preferred = group.Lines
            .OrderByDescending(Score)
            .ThenBy(line => line.Text.Length)
            .ThenBy(line => line.Sources[0].CandidateIndex)
            .First();
        SlangGraphSemanticMatch[] matches = group.Lines
            .SelectMany(line => line.Matches)
            .GroupBy(match => $"{match.ConceptId}\u001F{match.SourceRecordId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Slots.Count).First())
            .OrderByDescending(match => match.Confidence)
            .ToArray();
        OcrShadowLineSource[] sources = group.Lines
            .SelectMany(line => line.Sources)
            .GroupBy(source =>
                $"{source.CandidateIndex}\u001F{source.SourceLineNumber}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(source => source.CandidateIndex)
            .ThenBy(source => source.SourceLineNumber)
            .ToArray();

        int sourceCount = sources
            .Select(source => source.CandidateIndex)
            .Distinct()
            .Count();
        OcrFusionMetadata fusion = group.IsAmbiguous
            ? new OcrFusionMetadata(
                "ambiguous",
                group.Confidences.Count == 0 ? 0.0 : group.Confidences.Min(),
                group.Reasons.Distinct(StringComparer.Ordinal).ToArray())
            : new OcrFusionMetadata(
                sourceCount > 1 ? "fused" : "single_source",
                group.Confidences.Count == 0 ? 1.0 : group.Confidences.Average(),
                group.Reasons.Distinct(StringComparer.Ordinal).ToArray());

        return new OcrShadowLineResult(groupIndex + 1, preferred.Text, matches, sources, fusion);
    }

    private static LineRelationship CompareMessages(OcrShadowLineResult left, OcrShadowLineResult right)
    {
        string leftKey = BuildComparisonKey(left.Text);
        string rightKey = BuildComparisonKey(right.Text);

        if (leftKey.Length < 3 || rightKey.Length < 3)
        {
            return new LineRelationship(RelationshipKind.Separate, 0.0);
        }

        if (string.Equals(leftKey, rightKey, StringComparison.Ordinal))
        {
            return new LineRelationship(RelationshipKind.SameMessage, 0.95);
        }

        double similarity = Similarity(leftKey, rightKey);
        return similarity >= 0.86
            ? new LineRelationship(RelationshipKind.SameMessage, similarity)
            : new LineRelationship(RelationshipKind.Separate, similarity);
    }

    private static string BuildComparisonKey(string text)
    {
        // Prefer the shared, structural envelope parser.  OCR may disagree about
        // timestamp, channel glyphs, speaker name or full-width punctuation, but
        // those are not the visible-message body used for sequence alignment.
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(text);
        string value = envelope.HasHighConfidencePrefix && !string.IsNullOrWhiteSpace(envelope.Body)
            ? envelope.Body
            : TimestampRegex.Replace(text, string.Empty, 1).Trim();
        value = BracketedPrefixRegex.Replace(value, string.Empty).Trim();

        int finalColon = Math.Max(value.LastIndexOf(':'), value.LastIndexOf('：'));
        if (finalColon >= 0 && finalColon < value.Length - 1 &&
            value[..finalColon].Any(character => character is ']' or '】' or ')' or '）'))
        {
            value = value[(finalColon + 1)..];
        }

        return string.Concat(ComparableCharacterRegex.Matches(value)
                .Select(match => match.Value.ToLowerInvariant()))
            .Replace('0', 'o');
    }

    private static double Similarity(string left, string right)
    {
        int distance = LevenshteinDistance(left, right);
        int length = Math.Max(left.Length, right.Length);
        return length == 0 ? 1.0 : 1.0 - ((double)distance / length);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];

        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;

            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = previous[rightIndex - 1] +
                    (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static int Score(OcrShadowLineResult line) =>
        (line.Matches.Count * 100) +
        (line.Matches.Sum(match => match.Slots.Count) * 10) +
        Math.Min(line.Text.Length, 40);

    private static int SortTimestamp(IEnumerable<OcrShadowLineSource> sources)
    {
        string? timestamp = sources.Select(source => source.Timestamp).FirstOrDefault(value => value is not null);
        return timestamp is not null && TimeSpan.TryParseExact(timestamp, "mm\\:ss", null, out TimeSpan value)
            ? (int)value.TotalSeconds
            : int.MaxValue;
    }
}
