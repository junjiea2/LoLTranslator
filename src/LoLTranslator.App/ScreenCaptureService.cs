using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace LoLTranslator.App;

public sealed record ChatRowVisualAnalysis(
    string Hash,
    int TextLikeSamples,
    int NeutralSamples,
    int AlliedBlueSamples,
    int EnemyRedSamples,
    int SystemGoldSamples)
{
    public bool HasSystemGoldText =>
        SystemGoldSamples >= 6 &&
        SystemGoldSamples * 10 >= Math.Max(1, TextLikeSamples);

    /// <summary>
    /// Direct source-pixel colour classification for the sampled text span.
    /// Chromatic evidence is compared only with other chromatic evidence:
    /// anti-aliased glyph edges are often near-white and must not erase a
    /// clearly detected blue, red, or gold glyph core.
    /// </summary>
    public string? DominantTextColor
    {
        get
        {
            int chromaticMaximum = Math.Max(AlliedBlueSamples, Math.Max(EnemyRedSamples, SystemGoldSamples));
            if (chromaticMaximum >= 2)
            {
                int winners = (AlliedBlueSamples == chromaticMaximum ? 1 : 0) +
                              (EnemyRedSamples == chromaticMaximum ? 1 : 0) +
                              (SystemGoldSamples == chromaticMaximum ? 1 : 0);
                if (winners == 1)
                {
                    if (AlliedBlueSamples == chromaticMaximum) return "blue";
                    if (EnemyRedSamples == chromaticMaximum) return "red";
                    return "gold";
                }
                // Conflicting chromatic evidence is unknown, not white. A
                // later neutral fallback must not erase a previously observed
                // blue/red sender relation.
                return null;
            }
            return NeutralSamples >= 2 ? "white" : null;
        }
    }

    public string? IdentityRelation
    {
        get
        {
            return DominantTextColor switch
            {
                "blue" => "ally",
                "red" => "enemy",
                _ => null
            };
        }
    }

    /// <summary>
    /// Sender spans use a narrower League-specific palette than complete rows.
    /// Warm anti-aliased enemy names can overlap the general gold threshold,
    /// but a player identity itself is never a gold system announcement.
    /// Combine red and gold-like warm cores when deciding the sender side.
    /// </summary>
    public string? DominantIdentityColor
    {
        get
        {
            int warmSamples = EnemyRedSamples + SystemGoldSamples;
            if (AlliedBlueSamples >= 1 && AlliedBlueSamples > warmSamples) return "blue";
            if (warmSamples >= 1 && warmSamples > AlliedBlueSamples) return "red";
            if (AlliedBlueSamples >= 2 && warmSamples >= 2) return null;
            return NeutralSamples >= 2 ? "white" : null;
        }
    }
}

public static class ScreenCaptureService
{
    /// <summary>Stable row fingerprint for coordinate OCR. The crop is normalized
    /// to a small binary grid so chat scroll Y and minor antialiasing do not
    /// alter identity.</summary>
    public static string ComputeRowVisualHash(string imagePath, OcrBoundingBox bounds)
    {
        using var source = new Bitmap(imagePath);
        return ComputeRowVisualAnalysis(source, bounds).Hash;
    }

    /// <summary>Computes identity and LoL chat color evidence in one bitmap pass.
    /// Callers processing several OCR rows should reuse the same source bitmap.</summary>
    public static ChatRowVisualAnalysis ComputeRowVisualAnalysis(Bitmap source, OcrBoundingBox bounds) =>
        ComputeRowVisualAnalysis(source, bounds, 0, 1);

    /// <summary>Analyzes a horizontal part of a recognized row. Sender colour
    /// must come from the identity span, not from a spell, target, item, or
    /// system suffix later in the same chat line.</summary>
    public static ChatRowVisualAnalysis ComputeRowVisualAnalysis(
        Bitmap source,
        OcrBoundingBox bounds,
        double startFraction,
        double endFraction)
    {
        int x = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, source.Width - 1));
        int y = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, source.Height - 1));
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, source.Width - x);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, source.Height - y);
        startFraction = Math.Clamp(startFraction, 0, .98);
        endFraction = Math.Clamp(endFraction, startFraction + .02, 1);
        int identityX = x + (int)Math.Floor(width * startFraction);
        int identityRight = x + (int)Math.Ceiling(width * endFraction);
        identityX = Math.Clamp(identityX, x, x + width - 1);
        identityRight = Math.Clamp(identityRight, identityX + 1, x + width);
        width = identityRight - identityX;
        x = identityX;
        using var crop = source.Clone(new Rectangle(x, y, width, height), PixelFormat.Format24bppRgb);
        var bits = new System.Text.StringBuilder(16 * 8);
        for (int row = 0; row < 8; row++)
        for (int column = 0; column < 16; column++)
        {
            Color pixel = crop.GetPixel(Math.Min(crop.Width - 1, column * crop.Width / 16), Math.Min(crop.Height - 1, row * crop.Height / 8));
            int brightness = (pixel.R * 30 + pixel.G * 59 + pixel.B * 11) / 100;
            bits.Append(brightness > 120 ? '1' : '0');
        }
        int textLike = 0;
        int neutral = 0;
        int alliedBlue = 0;
        int enemyRed = 0;
        int systemGold = 0;
        for (int sampleY = 0; sampleY < crop.Height; sampleY += 2)
        for (int sampleX = 0; sampleX < crop.Width; sampleX += 2)
        {
            Color pixel = crop.GetPixel(sampleX, sampleY);
            int brightness = (pixel.R * 30 + pixel.G * 59 + pixel.B * 11) / 100;
            int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            bool isNeutral = brightness >= 165 && maximum - minimum <= 80;
            // Keep these thresholds aligned with
            // OcrChatPreprocessor.CreateLolChatHighContrastCandidate. If OCR
            // retains a compressed red/gold glyph, presentation must be able
            // to classify the same glyph instead of dropping the row later.
            bool isSystemGold = pixel.R >= 125 && pixel.G >= 90 && pixel.G - pixel.B >= 15;
            bool isAlliedBlue = pixel.B >= 90 && pixel.G >= 75 && pixel.B - pixel.R >= 12;
            // League gold is warm enough to satisfy a naive red threshold.
            // Keep categories mutually exclusive so a gold disconnect/status
            // suffix cannot turn an allied blue sender into an enemy.
            bool isEnemyRed = !isSystemGold && pixel.R >= 115 && pixel.R - pixel.G >= 18;
            if (!isNeutral && !isAlliedBlue && !isEnemyRed && !isSystemGold) continue;
            textLike++;
            if (isNeutral) neutral++;
            if (isAlliedBlue) alliedBlue++;
            if (isEnemyRed) enemyRed++;
            if (isSystemGold) systemGold++;
        }
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(bits.ToString())))[..16];
        return new ChatRowVisualAnalysis(hash, textLike, neutral, alliedBlue, enemyRed, systemGold);
    }
    /// <summary>
    /// Returns an in-memory fingerprint for the newest-message band. League's
    /// chat surface is translucent, so raw grayscale follows the moving game
    /// world and used to invoke OCR continuously. This fingerprint counts only
    /// bright/colored text-like pixels in the lower message band, where new
    /// player and system lines appear. No image is written.
    /// </summary>
    public static byte[] CaptureVisualFingerprint(Rect region, int columns = 48, int rows = 16)
    {
        int x = (int)Math.Round(region.X);
        int fullHeight = Math.Max(1, (int)Math.Round(region.Height));
        int watchTop = Math.Clamp((int)Math.Round(fullHeight * .42), 0, fullHeight - 1);
        int watchBottom = Math.Clamp((int)Math.Round(fullHeight * .82), watchTop + 1, fullHeight);
        int y = (int)Math.Round(region.Y) + watchTop;
        int width = Math.Max(1, (int)Math.Round(region.Width));
        int height = Math.Max(1, watchBottom - watchTop);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        var result = new byte[columns * rows];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int textLike = 0;
            for (int sampleRow = 0; sampleRow < 3; sampleRow++)
            for (int sampleColumn = 0; sampleColumn < 3; sampleColumn++)
            {
                int sampleX = Math.Min(width - 1, (column * 3 + sampleColumn) * width / (columns * 3));
                int sampleY = Math.Min(height - 1, (row * 3 + sampleRow) * height / (rows * 3));
                Color pixel = bitmap.GetPixel(sampleX, sampleY);
                int brightness = (pixel.R * 30 + pixel.G * 59 + pixel.B * 11) / 100;
                int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                bool brightNeutral = brightness >= 175 && maximum - minimum <= 75;
                bool cyanTeamText = pixel.B >= 115 && pixel.G >= 95 && pixel.B - pixel.R >= 18;
                bool redEnemyText = pixel.R >= 135 && pixel.R - pixel.G >= 24;
                bool yellowSystemText = pixel.R >= 145 && pixel.G >= 115 && pixel.G - pixel.B >= 24;
                if (brightNeutral || cyanTeamText || redEnemyText || yellowSystemText) textLike++;
            }
            // Four coarse density levels tolerate antialiasing and tiny camera
            // motion while retaining the appearance/disappearance of glyphs.
            result[row * columns + column] = textLike switch { 0 => (byte)0, <= 2 => (byte)1, <= 5 => (byte)2, _ => (byte)3 };
        }
        return result;
    }

    public static string Capture(Rect region)
    {
        int x = (int)Math.Round(region.X);
        int y = (int)Math.Round(region.Y);

        int width =
            Math.Max(
                1,
                (int)Math.Round(region.Width));

        int height =
            Math.Max(
                1,
                (int)Math.Round(region.Height));

        string captureDirectory =
            Path.Combine(
                AppContext.BaseDirectory,
                "captures");

        Directory.CreateDirectory(
            captureDirectory);

        string fileName =
            $"chat-{DateTime.Now:yyyyMMdd-HHmmss}.png";

        string filePath =
            Path.Combine(
                captureDirectory,
                fileName);

        using var bitmap = new Bitmap(
            width,
            height,
            PixelFormat.Format32bppArgb);

        using Graphics graphics =
            Graphics.FromImage(bitmap);

        graphics.CopyFromScreen(
            x,
            y,
            0,
            0,
            new System.Drawing.Size(width, height),
            CopyPixelOperation.SourceCopy);

        bitmap.Save(
            filePath,
            ImageFormat.Png);

        return filePath;
    }

    /// <summary>Transient loop capture. Caller must delete the returned file.</summary>
    public static string CaptureTransient(Rect region)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LoLTranslator", "continuous-captures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"chat-{Guid.NewGuid():N}.png");
        CaptureToPath(region, path);
        return path;
    }

    public static void DeleteTransient(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string CreateBandCandidate(string sourcePath, TextBand band)
    {
        string path = Path.Combine(Path.GetDirectoryName(sourcePath)!, "ocr-band-" + Guid.NewGuid().ToString("N") + ".png");
        using var source = new Bitmap(sourcePath);
        int top = Math.Clamp(band.Top - 3, 0, source.Height - 1);
        int height = Math.Clamp(band.Height + 6, 1, source.Height - top);
        using var crop = source.Clone(new Rectangle(0, top, source.Width, height), PixelFormat.Format32bppArgb);
        using var scaled = new Bitmap(crop.Width * 2, crop.Height * 2, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(crop, new Rectangle(0, 0, scaled.Width, scaled.Height));
        scaled.Save(path, ImageFormat.Png);
        return path;
    }

    private static void CaptureToPath(Rect region, string filePath)
    {
        int x = (int)Math.Round(region.X);
        int y = (int)Math.Round(region.Y);
        int width = Math.Max(1, (int)Math.Round(region.Width));
        int height = Math.Max(1, (int)Math.Round(region.Height));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        bitmap.Save(filePath, ImageFormat.Png);
    }
}

public static class OcrChatPreprocessor
{
    /// <summary>Creates a transient 2x small-font candidate; caller deletes it with DeleteTransient.</summary>
    public static string CreateScaledCandidate(string sourcePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(sourcePath)!, "ocr-2x-" + Guid.NewGuid().ToString("N") + ".png");
        using var source = new Bitmap(sourcePath);
        using var scaled = new Bitmap(source.Width * 2, source.Height * 2, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, scaled.Width, scaled.Height));
        scaled.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Creates a 2x LoL-chat candidate that suppresses the moving game
    /// world while preserving neutral, allied-blue, enemy-red and system-gold
    /// glyphs as high-contrast text.</summary>
    public static string CreateLolChatHighContrastCandidate(string sourcePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(sourcePath)!, "ocr-lol-contrast-" + Guid.NewGuid().ToString("N") + ".png");
        using var loaded = new Bitmap(sourcePath);
        using var source = loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format24bppRgb);
        using var mask = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        Rectangle rectangle = new(0, 0, source.Width, source.Height);
        BitmapData sourceData = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        BitmapData maskData = mask.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] sourceBytes = new byte[Math.Abs(sourceData.Stride) * source.Height];
            byte[] maskBytes = new byte[Math.Abs(maskData.Stride) * mask.Height];
            System.Runtime.InteropServices.Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                int sourceOffset = y * sourceData.Stride + x * 3;
                byte blue = sourceBytes[sourceOffset];
                byte green = sourceBytes[sourceOffset + 1];
                byte red = sourceBytes[sourceOffset + 2];
                int brightness = (red * 30 + green * 59 + blue * 11) / 100;
                int maximum = Math.Max(red, Math.Max(green, blue));
                int minimum = Math.Min(red, Math.Min(green, blue));
                bool neutral = brightness >= 145 && maximum - minimum <= 95;
                bool alliedBlue = blue >= 90 && green >= 75 && blue - red >= 12;
                bool enemyRed = red >= 115 && red - green >= 18;
                bool systemGold = red >= 125 && green >= 90 && green - blue >= 15;
                byte value = neutral || alliedBlue || enemyRed || systemGold ? (byte)255 : (byte)0;
                int maskOffset = y * maskData.Stride + x * 3;
                maskBytes[maskOffset] = value;
                maskBytes[maskOffset + 1] = value;
                maskBytes[maskOffset + 2] = value;
            }
            System.Runtime.InteropServices.Marshal.Copy(maskBytes, 0, maskData.Scan0, maskBytes.Length);
        }
        finally
        {
            source.UnlockBits(sourceData);
            mask.UnlockBits(maskData);
        }
        using var scaled = new Bitmap(mask.Width * 2, mask.Height * 2, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(mask, new Rectangle(0, 0, scaled.Width, scaled.Height));
        // Hard binary thresholding helps coloured chat text, but it can erase
        // the lower curve of a small anti-aliased "2" and leave a "7"-like
        // timestamp glyph. Preserve a bicubic copy of the narrow left timestamp
        // strip; the body still uses the high-contrast colour mask.
        int timestampStripWidth = Math.Max(1, (int)Math.Round(source.Width * .12));
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, timestampStripWidth * 2, scaled.Height),
            new Rectangle(0, 0, timestampStripWidth, source.Height),
            GraphicsUnit.Pixel);
        scaled.Save(path, ImageFormat.Png);
        return path;
    }
}
