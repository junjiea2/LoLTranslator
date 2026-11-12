namespace LoLTranslator.App;

public sealed record CoordinateLogicalOcrLine(StructuredOcrLine Preferred, IReadOnlyList<StructuredOcrLine> Candidates);

/// <summary>Coordinate-first fusion for a single captured chat frame. Different
/// OCR engines are aligned by physical row, never by timestamp or global body
/// similarity. Missing rows in one engine are preserved as insertions.</summary>
public static class CoordinateOcrLineFusion
{
    public static IReadOnlyList<CoordinateLogicalOcrLine> Fuse(IEnumerable<StructuredOcrLine> lines)
    {
        StructuredOcrLine[] physicalRows = MergeSameEngineFragments(lines).ToArray();
        var groups = new List<List<StructuredOcrLine>>();
        foreach (StructuredOcrLine line in physicalRows.OrderBy(line => line.Bounds.Y))
        {
            List<StructuredOcrLine>? group = groups.FirstOrDefault(existing =>
                !existing.Any(candidate => string.Equals(candidate.EngineLocale, line.EngineLocale, StringComparison.OrdinalIgnoreCase)) &&
                IsSamePhysicalRow(existing[0].Bounds, line.Bounds));
            if (group is null) groups.Add(new List<StructuredOcrLine> { line }); else group.Add(line);
        }
        return groups.OrderBy(group => group.Min(line => line.Bounds.Y)).Select(group =>
            new CoordinateLogicalOcrLine(group.OrderByDescending(Score).ThenByDescending(line => line.Text.Length).First(), group)).ToArray();
    }

    private static IEnumerable<StructuredOcrLine> MergeSameEngineFragments(IEnumerable<StructuredOcrLine> lines)
    {
        foreach (IGrouping<(string EngineLocale, long FrameId), StructuredOcrLine> source in lines
                     .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                     .GroupBy(line => (line.EngineLocale, line.FrameId)))
        {
            var rows = new List<List<StructuredOcrLine>>();
            foreach (StructuredOcrLine fragment in source.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X))
            {
                List<StructuredOcrLine>? row = rows.FirstOrDefault(existing =>
                    IsSamePhysicalRow(existing[0].Bounds, fragment.Bounds));
                if (row is null) rows.Add(new List<StructuredOcrLine> { fragment }); else row.Add(fragment);
            }
            foreach (List<StructuredOcrLine> row in rows) yield return MergeRow(row);
        }
    }

    private static StructuredOcrLine MergeRow(IReadOnlyList<StructuredOcrLine> fragments)
    {
        if (fragments.Count == 1) return fragments[0];
        StructuredOcrLine[] ordered = fragments.OrderBy(fragment => fragment.Bounds.X).ToArray();
        var text = new System.Text.StringBuilder();
        double priorRight = double.MinValue;
        foreach (StructuredOcrLine fragment in ordered)
        {
            if (text.Length > 0 && fragment.Bounds.X - priorRight > Math.Max(2, fragment.Bounds.Height * .12)) text.Append(' ');
            text.Append(fragment.Text.Trim());
            priorRight = Math.Max(priorRight, fragment.Bounds.X + fragment.Bounds.Width);
        }
        OcrBoundingBox bounds = UnionBounds(ordered.Select(fragment => fragment.Bounds));
        return new StructuredOcrLine(text.ToString(), bounds, ordered.SelectMany(fragment => fragment.Words).ToArray(),
            ordered[0].EngineLocale, ordered[0].FrameId);
    }

    private static OcrBoundingBox UnionBounds(IEnumerable<OcrBoundingBox> values)
    {
        OcrBoundingBox[] bounds = values.ToArray();
        double left = bounds.Min(item => item.X);
        double top = bounds.Min(item => item.Y);
        double right = bounds.Max(item => item.X + item.Width);
        double bottom = bounds.Max(item => item.Y + item.Height);
        return new OcrBoundingBox(left, top, right - left, bottom - top);
    }

    private static bool IsSamePhysicalRow(OcrBoundingBox left, OcrBoundingBox right)
    {
        double overlap = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        double minimumHeight = Math.Max(1, Math.Min(left.Height, right.Height));
        double centerDistance = Math.Abs((left.Y + left.Height / 2) - (right.Y + right.Height / 2));
        return overlap / minimumHeight >= .45 || centerDistance <= Math.Max(left.Height, right.Height) * .55;
    }

    private static double Score(StructuredOcrLine line)
    {
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(line.Text);
        string body = envelope.Body;
        int letters = body.Count(char.IsLetterOrDigit);
        double readable = body.Length == 0 ? 0 : (double)letters / body.Length;
        bool latin = body.Any(character => character is >= 'A' and <= 'z') && !body.Any(IsCjk);
        bool han = body.Any(character => character is >= '\u3400' and <= '\u9fff');
        bool hangul = body.Any(character => character is >= '\uac00' and <= '\ud7af');
        bool kana = body.Any(character => character is >= '\u3040' and <= '\u30ff');
        double script = latin && line.EngineLocale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        script += han && line.EngineLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        script += hangul && line.EngineLocale.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        script += kana && line.EngineLocale.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        double mojibakePenalty = body.Count(character => character is '�' or '锟' or '�') * 2;
        return (envelope.HasHighConfidencePrefix ? 4 : 0) + readable + script - mojibakePenalty;
    }

    private static bool IsCjk(char character) => character is >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' or >= '\u3040' and <= '\u30ff';
}
