using System.Drawing;

namespace LoLTranslator.App;

public sealed record TextBand(int Top, int Height, int Signal);
public interface ITextBandDetector { IReadOnlyList<TextBand> Detect(string imagePath); }

/// <summary>Lightweight Windows default: horizontal edge projection, with the
/// lowest input-caret area excluded. It detects candidate text bands only; OCR
/// remains bounded to a single fast-engine retry per unmatched band.</summary>
public sealed class WindowsTextBandDetector : ITextBandDetector
{
    public IReadOnlyList<TextBand> Detect(string imagePath)
    {
        using var bitmap = new Bitmap(imagePath);
        int usableBottom = Math.Max(1, (int)(bitmap.Height * .82));
        var active = new List<int>();
        for (int y = 1; y < usableBottom; y++)
        {
            int signal = 0;
            for (int x = 1; x < bitmap.Width; x += 2)
            {
                Color a = bitmap.GetPixel(x, y), b = bitmap.GetPixel(x, y - 1);
                int delta = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                if (delta > 90) signal++;
            }
            if (signal >= Math.Max(4, bitmap.Width / 70)) active.Add(y);
        }
        var bands = new List<TextBand>();
        foreach (int y in active)
        {
            if (bands.Count == 0 || y > bands[^1].Top + bands[^1].Height + 2)
                bands.Add(new TextBand(y, 1, 1));
            else bands[^1] = bands[^1] with { Height = y - bands[^1].Top + 1, Signal = bands[^1].Signal + 1 };
        }
        return bands.Where(band => band.Height is >= 6 and <= 42).ToArray();
    }
}
