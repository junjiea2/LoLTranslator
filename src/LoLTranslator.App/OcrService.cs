using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace LoLTranslator.App;

public sealed record OcrLanguageOption(
    string DisplayName,
    string LanguageTag);

public sealed record OcrRecognitionResult(
    string Text,
    string RecognizerLanguageTag,
    IReadOnlyList<StructuredOcrLine>? StructuredLines = null);

/// <summary>Coordinate-preserving contract for realtime chat OCR.  It keeps
/// physical-line identity separate from text so OCR drift cannot create a new
/// chat occurrence.  Windows OCR and a future ONNX engine share this boundary.</summary>
public sealed record OcrBoundingBox(double X, double Y, double Width, double Height);
public sealed record StructuredOcrWord(string Text, OcrBoundingBox Bounds);
public sealed record StructuredOcrLine(string Text, OcrBoundingBox Bounds, IReadOnlyList<StructuredOcrWord> Words, string EngineLocale, long FrameId);
public interface IStructuredChatOcrEngine
{
    Task<IReadOnlyList<StructuredOcrLine>> RecognizeLinesAsync(string imagePath, string locale, long frameId, CancellationToken cancellationToken);
}

public static class OcrService
{
    public const string MultiLanguageTag = "multi";
    private const double PreferredScale = 2.0;

    private static readonly Regex CjkInnerSpaceRegex =
        new(
            @"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\u3040-\u30FF])" +
            @"[ \u3000]+" +
            @"(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\u3040-\u30FF])",
            RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuationRegex =
        new(
            @"[ \u3000]+([，。！？；：、）》】」』])",
            RegexOptions.Compiled);

    private static readonly Regex SpaceAfterOpeningPunctuationRegex =
        new(
            @"([（《【「『])[ \u3000]+",
            RegexOptions.Compiled);

    private static readonly Regex RepeatedSpacesRegex =
        new(
            @"[ \t]{2,}",
            RegexOptions.Compiled);

public static IReadOnlyList<OcrLanguageOption>
    GetAvailableLanguages()
{
    var options =
        new List<OcrLanguageOption>
        {
            new(
                "多语言自动（系统 + 已安装的中英法韩日）",
                MultiLanguageTag),
            new(
                "系统语言优先",
                "auto")
        };

    foreach (Language language in
             OcrEngine.AvailableRecognizerLanguages
                 .OrderBy(language =>
                     language.DisplayName))
    {
        options.Add(
            new OcrLanguageOption(
                $"{language.DisplayName} " +
                $"({language.LanguageTag})",
                language.LanguageTag));
    }

    return options;
}

    public static async Task<IReadOnlyList<OcrRecognitionResult>> RecognizeCandidatesAsync(
        string imagePath,
        string languageTag)
    {
        if (!string.Equals(languageTag, MultiLanguageTag, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { await RecognizeAsync(imagePath, languageTag) };
        }

        string[] preferredLanguageFamilies =
        {
            CultureInfo.CurrentUICulture.Name,
            "zh",
            "en",
            "fr",
            "ko",
            "ja"
        };
        Language[] installed = OcrEngine.AvailableRecognizerLanguages.ToArray();
        var selectedTags = new List<string>();

        foreach (string preferred in preferredLanguageFamilies)
        {
            string family = preferred.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            Language? match = installed.FirstOrDefault(language =>
                string.Equals(language.LanguageTag, preferred, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(language =>
                    string.Equals(
                        language.LanguageTag.Split('-', StringSplitOptions.RemoveEmptyEntries)[0],
                        family,
                        StringComparison.OrdinalIgnoreCase));

            if (match is not null && !selectedTags.Contains(match.LanguageTag, StringComparer.OrdinalIgnoreCase))
            {
                selectedTags.Add(match.LanguageTag);
            }
        }

        if (selectedTags.Count == 0)
        {
            return new[] { await RecognizeAsync(imagePath, "auto") };
        }

        var results = new List<OcrRecognitionResult>();

        foreach (string selectedTag in selectedTags)
        {
            OcrRecognitionResult result = await RecognizeAsync(imagePath, selectedTag);

            if (!results.Any(existing =>
                    string.Equals(existing.Text, result.Text, StringComparison.Ordinal) &&
                    string.Equals(existing.RecognizerLanguageTag, result.RecognizerLanguageTag, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(result);
            }
        }

        return results;
    }

    /// <summary>Realtime path: at most the user profile and English engine, one
    /// source image, no diagnostic multi-language sweep.  The full candidate
    /// scan remains available only to the manual diagnostic workflow.</summary>
    public static async Task<IReadOnlyList<OcrRecognitionResult>> RecognizeRealtimeFastCandidatesAsync(string imagePath)
    {
        string[] tags = new[] { "auto", "en-US" };
        OcrRecognitionResult[] results = await Task.WhenAll(tags.Select(async tag =>
        {
            try { return await RecognizeAsync(imagePath, tag); }
            catch (InvalidOperationException) { return new OcrRecognitionResult(string.Empty, tag); }
        }));
        var retained = results.Where(result => !string.IsNullOrWhiteSpace(result.Text))
            .GroupBy(result => result.RecognizerLanguageTag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(result => result.Text.Length).First())
            .ToList();
        // Latin chat is released immediately.  Only visible CJK script asks for
        // one matching installed specialist, never a six-language sweep.
        string combined = string.Join('\n', retained.Select(result => result.Text));
        string? supplementary = SelectSupplementaryScript(combined);
        if (supplementary is not null)
        {
            Language? installed = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
                language.LanguageTag.StartsWith(supplementary, StringComparison.OrdinalIgnoreCase));
            if (installed is not null && !retained.Any(result => string.Equals(result.RecognizerLanguageTag, installed.LanguageTag, StringComparison.OrdinalIgnoreCase)))
            {
                try { retained.Add(await RecognizeAsync(imagePath, installed.LanguageTag)); }
                catch (InvalidOperationException) { }
            }
        }
        return retained;
    }

    private static string? SelectSupplementaryScript(string text)
    {
        if (text.Any(character => character is >= '\u3400' and <= '\u9fff')) return "zh";
        if (text.Any(character => character is >= '\uac00' and <= '\ud7af')) return "ko";
        if (text.Any(character => character is >= '\u3040' and <= '\u30ff')) return "ja";
        return null;
    }

    public static async Task<OcrRecognitionResult> RecognizeAsync(
        string imagePath,
        string languageTag)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException(
                "截图路径不能为空。",
                nameof(imagePath));
        }

        if (string.IsNullOrWhiteSpace(languageTag))
        {
            throw new ArgumentException(
                "OCR 语言不能为空。",
                nameof(languageTag));
        }

        StorageFile imageFile =
            await StorageFile.GetFileFromPathAsync(
                imagePath);

        using var imageStream =
            await imageFile.OpenAsync(
                FileAccessMode.Read);

        BitmapDecoder decoder =
            await BitmapDecoder.CreateAsync(
                imageStream);

        uint sourceWidth =
            decoder.PixelWidth;

        uint sourceHeight =
            decoder.PixelHeight;

        double widthScaleLimit =
            (double)OcrEngine.MaxImageDimension /
            sourceWidth;

        double heightScaleLimit =
            (double)OcrEngine.MaxImageDimension /
            sourceHeight;

        double actualScale =
            Math.Min(
                PreferredScale,
                Math.Min(
                    widthScaleLimit,
                    heightScaleLimit));

        actualScale =
            Math.Max(
                1.0,
                actualScale);

        uint scaledWidth =
            (uint)Math.Round(
                sourceWidth * actualScale);

        uint scaledHeight =
            (uint)Math.Round(
                sourceHeight * actualScale);

        var transform =
            new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                InterpolationMode =
                    BitmapInterpolationMode.Cubic
            };

        using SoftwareBitmap softwareBitmap =
            await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

        OcrEngine? ocrEngine =
            CreateOcrEngine(languageTag);

        if (ocrEngine is null)
        {
            throw new InvalidOperationException(
                $"Windows 未安装所选 OCR 语言：" +
                $"{languageTag}。请改用其他来源语言，" +
                "或安装相应的 Windows 语言包。");
        }

        OcrResult result =
            await ocrEngine.RecognizeAsync(
                softwareBitmap);

        var textBuilder =
            new StringBuilder();
        StructuredOcrLine[] structuredLines = result.Lines.Select(line =>
        {
            StructuredOcrWord[] words = line.Words.Select(word => new StructuredOcrWord(word.Text,
                new OcrBoundingBox(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height))).ToArray();
            double left = words.Min(word => word.Bounds.X), top = words.Min(word => word.Bounds.Y);
            double right = words.Max(word => word.Bounds.X + word.Bounds.Width), bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
            return new StructuredOcrLine(NormalizeRecognizedText(line.Text), new OcrBoundingBox(left, top, right - left, bottom - top), words,
                ocrEngine.RecognizerLanguage.LanguageTag, 0);
        }).ToArray();

        foreach (OcrLine line in result.Lines)
        {
            textBuilder.AppendLine(
                line.Text);
        }

        return new OcrRecognitionResult(
            NormalizeRecognizedText(textBuilder.ToString()),
            ocrEngine.RecognizerLanguage.LanguageTag,
            structuredLines);
    }

    private static OcrEngine? CreateOcrEngine(
        string languageTag)
    {
        if (string.Equals(
                languageTag,
                "auto",
                StringComparison.OrdinalIgnoreCase))
        {
            OcrEngine? profileEngine =
                OcrEngine.TryCreateFromUserProfileLanguages();

            profileEngine ??=
                TryCreateLanguageEngine("en-US");

            return profileEngine;
        }

        return TryCreateLanguageEngine(
            languageTag);
    }

    private static OcrEngine? TryCreateLanguageEngine(
        string languageTag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(
                new Language(languageTag));
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizeRecognizedText(
        string text)
    {
        string normalized =
            text.Replace(
                "\r\n",
                "\n");

        string[] lines =
            normalized.Split('\n');

        for (int index = 0;
             index < lines.Length;
             index++)
        {
            string line =
                lines[index];

            line =
                CjkInnerSpaceRegex.Replace(
                    line,
                    string.Empty);

            line =
                SpaceBeforePunctuationRegex.Replace(
                    line,
                    "$1");

            line =
                SpaceAfterOpeningPunctuationRegex.Replace(
                    line,
                    "$1");

            line =
                RepeatedSpacesRegex.Replace(
                    line,
                    " ");

            lines[index] =
                line.Trim();
        }

        return string.Join(
                Environment.NewLine,
                lines)
            .Trim();
    }
}
