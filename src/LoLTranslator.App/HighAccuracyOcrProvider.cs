using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Clipper2Lib;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LoLTranslator.App;

public sealed record HighAccuracyOcrCapability(string State, string Message, bool Ready, string? Diagnostic = null);
public sealed record HighAccuracyOcrCrop(double X, double Y, double Width, double Height);
public sealed record HighAccuracyOcrRunMetrics(string DetectionShape, float DetectionMapMaximum, int Contours, int MinimumSizeCandidates, int ScoreCandidates, int AcceptedBoxes);

/// <summary>Distribution boundary for the selected PP-OCRv5 pipeline. The WPF
/// process uses application-packaged ONNX/DirectML assets only; it never shells
/// out to a user Python, Paddle, or Ollama installation.</summary>
public interface IHighAccuracyChatOcrProvider : IStructuredChatOcrEngine, IDisposable
{
    HighAccuracyOcrCapability Capability { get; }
    Task<HighAccuracyOcrCapability> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Minimal native PP-OCRv5 pipeline for the realtime chat crop. It uses the
/// official mobile detector and server recognizer ONNX releases, DirectML, and
/// retains line/word coordinates for the existing coordinate differ.
/// </summary>
public sealed class PackagedPpOcrV5Provider : IHighAccuracyChatOcrProvider, IDisposable
{
    public const string LoadingMessage = "高精度识别模型正在加载…";
    public const string ReadyMessage = "高精度识别模型已就绪。";
    public const string UnavailableMessage = "此设备暂无法使用高精度识别模型。";
    public const string DownloadRequiredMessage = "需要下载高精度识别模型后才能开始翻译。";

    private const string DetectorRelativePath = "mobile-det\\model.onnx";
    private const string RecognizerRelativePath = "server-rec\\model.onnx";
    private const string RecognizerConfigRelativePath = "server-rec\\inference.yml";
    private const float DetectionThreshold = 0.30f;
    private const int RecognitionHeight = 48;
    private const int RecognitionMaximumWidth = 320;

    private readonly object _gate = new();
    private readonly string _modelRoot;
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private IReadOnlyList<string>? _characterDictionary;
    private bool _disposed;

    public PackagedPpOcrV5Provider(string? modelRoot = null)
    {
        _modelRoot = modelRoot
            ?? Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_MODEL_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "models", "ppocrv5");
    }

    public HighAccuracyOcrCapability Capability { get; private set; } =
        new("checking", LoadingMessage, false);
    public HighAccuracyOcrRunMetrics? LastRunMetrics { get; private set; }

    public Task<HighAccuracyOcrCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        string detectorPath = Path.Combine(_modelRoot, DetectorRelativePath);
        string recognizerPath = Path.Combine(_modelRoot, RecognizerRelativePath);
        string recognizerConfigPath = Path.Combine(_modelRoot, RecognizerConfigRelativePath);
        if (!File.Exists(detectorPath) || !File.Exists(recognizerPath) || !File.Exists(recognizerConfigPath))
        {
            Capability = new HighAccuracyOcrCapability(
                "model_download_required",
                DownloadRequiredMessage,
                false,
                "ppocrv5_onnx_assets_or_recognizer_dictionary_missing");
            return Task.FromResult(Capability);
        }

        try
        {
            lock (_gate)
            {
                EnsureSessionsLoaded(detectorPath, recognizerPath, recognizerConfigPath);
            }

            Capability = new HighAccuracyOcrCapability(
                "ready",
                ReadyMessage,
                true,
                "ppocrv5_mobile_det_server_rec_directml");
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or DllNotFoundException or FileLoadException or InvalidOperationException)
        {
            DisposeSessions();
            Capability = new HighAccuracyOcrCapability(
                "native_runtime_unavailable",
                UnavailableMessage,
                false,
                $"directml_session_load_failed:{exception.GetType().Name}");
        }

        return Task.FromResult(Capability);
    }

    public async Task<IReadOnlyList<StructuredOcrLine>> RecognizeLinesAsync(
        string imagePath,
        string locale,
        long frameId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("截图路径不能为空。", nameof(imagePath));
        }

        HighAccuracyOcrCapability capability = await ProbeAsync(cancellationToken);
        if (!capability.Ready)
        {
            throw new InvalidOperationException(capability.Diagnostic ?? "high_accuracy_ocr_not_ready");
        }

        byte[] imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        return await RecognizeLinesAsync(imageBytes, locale, frameId, null, cancellationToken);
    }

    /// <summary>In-memory entry point for realtime capture and privacy-preserving
    /// benchmarks. The optional crop is normalized to the input image and is never
    /// materialized as a file.</summary>
    public async Task<IReadOnlyList<StructuredOcrLine>> RecognizeLinesAsync(
        ReadOnlyMemory<byte> imageBytes,
        string locale,
        long frameId,
        HighAccuracyOcrCrop? crop,
        CancellationToken cancellationToken)
    {
        HighAccuracyOcrCapability capability = await ProbeAsync(cancellationToken);
        if (!capability.Ready)
        {
            throw new InvalidOperationException(capability.Diagnostic ?? "high_accuracy_ocr_not_ready");
        }

        byte[] ownedBytes = imageBytes.ToArray();
        return await Task.Run(
            () => RecognizeImageBytes(ownedBytes, locale, frameId, crop, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<StructuredOcrLine> RecognizeImageBytes(
        byte[] imageBytes,
        string locale,
        long frameId,
        HighAccuracyOcrCrop? normalizedCrop,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImagePixels original = DecodeRgba(imageBytes).GetAwaiter().GetResult();
        if (normalizedCrop is not null)
        {
            original = CropNormalized(original, normalizedCrop);
        }
        ImagePixels detectorImage = ResizeForDetector(original);
        IReadOnlyList<DetectionBox> boxes;

        lock (_gate)
        {
            if (_detector is null || _recognizer is null || _characterDictionary is null)
            {
                throw new InvalidOperationException("high_accuracy_ocr_sessions_not_loaded");
            }

            DetectionMap detectionMap = RunDetector(_detector, detectorImage);
            var extraction = ExtractTextBoxes(detectionMap, original.Width, original.Height);
            boxes = extraction.Boxes;
            LastRunMetrics = extraction.Metrics;
        }

        var lines = new List<StructuredOcrLine>();
        foreach (DetectionBox box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            lock (_gate)
            {
                text = RecognizeBox(_recognizer!, original, box, _characterDictionary!);
            }

            text = OcrService.NormalizeRecognizedText(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            OcrBoundingBox bounds = new(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
            lines.Add(new StructuredOcrLine(
                text,
                bounds,
                CreateApproximateWordBoxes(text, bounds),
                "ppocrv5-directml",
                frameId));
        }

        return lines;
    }

    private void EnsureSessionsLoaded(string detectorPath, string recognizerPath, string recognizerConfigPath)
    {
        if (_detector is not null && _recognizer is not null && _characterDictionary is not null)
        {
            return;
        }

        IReadOnlyList<string> characterDictionary = LoadCharacterDictionary(recognizerConfigPath);
        if (characterDictionary.Count < 100)
        {
            throw new InvalidOperationException("ppocrv5_recognizer_dictionary_invalid");
        }

        var options = new SessionOptions();
        options.AppendExecutionProvider_DML(0);
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _detector = new InferenceSession(detectorPath, options);
        _recognizer = new InferenceSession(recognizerPath, options);
        _characterDictionary = characterDictionary;
    }

    private static async Task<ImagePixels> DecodeRgba(byte[] imageBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        return new ImagePixels((int)decoder.PixelWidth, (int)decoder.PixelHeight, provider.DetachPixelData());
    }

    private static ImagePixels ResizeForDetector(ImagePixels original)
    {
        // The packaged chat crop is much smaller than a full frame. Preserve the
        // detector's 960-side working resolution so DB contours retain their
        // official minimum-size semantics instead of collapsing into glyph pixels.
        double ratio = 960.0 / Math.Max(original.Width, original.Height);
        int width = Math.Max(32, (int)Math.Round(original.Width * ratio / 32.0) * 32);
        int height = Math.Max(32, (int)Math.Round(original.Height * ratio / 32.0) * 32);
        return Resize(original, width, height);
    }

    private static DetectionMap RunDetector(InferenceSession detector, ImagePixels image)
    {
        float[] pixels = new float[3 * image.Width * image.Height];
        int plane = image.Width * image.Height;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int source = (y * image.Width + x) * 4;
                int destination = y * image.Width + x;
                // Paddle's detector configuration decodes BGR then normalizes by ImageNet statistics.
                pixels[destination] = (image.Rgba[source + 2] / 255f - 0.485f) / 0.229f;
                pixels[plane + destination] = (image.Rgba[source + 1] / 255f - 0.456f) / 0.224f;
                pixels[2 * plane + destination] = (image.Rgba[source] / 255f - 0.406f) / 0.225f;
            }
        }

        var input = new DenseTensor<float>(pixels, new[] { 1, 3, image.Height, image.Width });
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = detector.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", input)
        });
        Tensor<float> tensor = outputs.Single().AsTensor<float>();
        int height = tensor.Dimensions[^2];
        int width = tensor.Dimensions[^1];
        return new DetectionMap(tensor.ToArray(), width, height, string.Join("x", tensor.Dimensions.ToArray()));
    }

    // Mirrors PaddleOCR deploy/cpp_infer/src/postprocess_op.cpp: threshold map,
    // findContours, minAreaRect, fast polygon score, Clipper round unclip, then
    // a second minAreaRect. It deliberately has no text-specific correction.
    private static (IReadOnlyList<DetectionBox> Boxes, HighAccuracyOcrRunMetrics Metrics) ExtractTextBoxes(
        DetectionMap detectionMap,
        int originalWidth,
        int originalHeight)
    {
        if (detectionMap.Height <= 0 || detectionMap.Width <= 0 ||
            detectionMap.Height * detectionMap.Width > detectionMap.Values.Length)
        {
            return (Array.Empty<DetectionBox>(), new HighAccuracyOcrRunMetrics("invalid", 0, 0, 0, 0, 0));
        }

        using var probabilityVector = Mat.FromArray(detectionMap.Values);
        using var probabilities = probabilityVector.Reshape(1, detectionMap.Height);
        using var bitmap = new Mat();
        Cv2.Threshold(probabilities, bitmap, DetectionThreshold, 255, ThresholdTypes.Binary);
        bitmap.ConvertTo(bitmap, MatType.CV_8UC1);
        // Official DBDetector exposes this 2x2 morphology switch before contour
        // extraction. The compact chat crop uses the enabled form so anti-aliased
        // glyph strokes remain connected at the detector's native resolution.
        using (var dilation = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)))
        {
            Cv2.Dilate(bitmap, bitmap, dilation);
        }
        Cv2.FindContours(bitmap, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        double scaleX = (double)originalWidth / detectionMap.Width;
        double scaleY = (double)originalHeight / detectionMap.Height;
        var result = new List<DetectionBox>();
        int minimumSizeCandidates = 0;
        int scoreCandidates = 0;
        foreach (Point[] contour in contours.Take(1000))
        {
            if (contour.Length <= 2) continue;
            RotatedRect firstRect = Cv2.MinAreaRect(contour);
            Point2f[] first = OrderClockwise(firstRect.Points());
            if (LargestSide(first) < 3) continue;
            minimumSizeCandidates++;
            if (BoxScoreFast(first, probabilities) < 0.60f) continue;
            scoreCandidates++;

            Point2f[] expanded = Unclip(first, 1.5);
            if (expanded.Length < 3) continue;
            RotatedRect finalRect = Cv2.MinAreaRect(expanded);
            Point2f[] corners = OrderClockwise(finalRect.Points());
            if (LargestSide(corners) < 5) continue;

            Point2f[] sourceCorners = corners.Select(point => new Point2f(
                (float)Math.Clamp(point.X * scaleX, 0, originalWidth - 1),
                (float)Math.Clamp(point.Y * scaleY, 0, originalHeight - 1))).ToArray();
            int left = Math.Max(0, (int)Math.Floor(sourceCorners.Min(point => point.X)));
            int top = Math.Max(0, (int)Math.Floor(sourceCorners.Min(point => point.Y)));
            int right = Math.Min(originalWidth, (int)Math.Ceiling(sourceCorners.Max(point => point.X)) + 1);
            int bottom = Math.Min(originalHeight, (int)Math.Ceiling(sourceCorners.Max(point => point.Y)) + 1);
            if (right - left <= 4 || bottom - top <= 4) continue;
            result.Add(new DetectionBox(sourceCorners, left, top, right, bottom));
        }

        return (result.OrderBy(box => box.Top).ThenBy(box => box.Left).ToArray(),
            new HighAccuracyOcrRunMetrics(detectionMap.Shape, detectionMap.Values.Max(), contours.Length, minimumSizeCandidates, scoreCandidates, result.Count));
    }

    private static Point2f[] Unclip(IReadOnlyList<Point2f> points, double ratio)
    {
        double area = Math.Abs(Cv2.ContourArea(points.ToArray()));
        double perimeter = Cv2.ArcLength(points.ToArray(), true);
        if (area <= 0 || perimeter <= 0) return Array.Empty<Point2f>();
        double distance = area * ratio / perimeter;
        var path = new Path64(points.Select(point => new Point64(
            (long)Math.Round(point.X), (long)Math.Round(point.Y))));
        Paths64 expanded = Clipper.InflatePaths(new Paths64 { path }, distance, JoinType.Round, EndType.Polygon);
        return expanded.SelectMany(candidate => candidate)
            .Select(point => new Point2f(point.X, point.Y)).ToArray();
    }

    private static float BoxScoreFast(IReadOnlyList<Point2f> corners, Mat probabilities)
    {
        int left = Math.Clamp((int)Math.Floor(corners.Min(point => point.X)), 0, probabilities.Width - 1);
        int top = Math.Clamp((int)Math.Floor(corners.Min(point => point.Y)), 0, probabilities.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(corners.Max(point => point.X)), 0, probabilities.Width - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(corners.Max(point => point.Y)), 0, probabilities.Height - 1);
        if (right <= left || bottom <= top) return 0;
        using var mask = new Mat(bottom - top + 1, right - left + 1, MatType.CV_8UC1, Scalar.Black);
        Point[] polygon = corners.Select(point => new Point(
            (int)Math.Round(point.X) - left, (int)Math.Round(point.Y) - top)).ToArray();
        Cv2.FillConvexPoly(mask, polygon, Scalar.White);
        using Mat crop = new(probabilities, new Rect(left, top, right - left + 1, bottom - top + 1));
        return (float)Cv2.Mean(crop, mask).Val0;
    }

    private static Point2f[] OrderClockwise(Point2f[] points)
    {
        return points.OrderBy(point => point.X).ThenBy(point => point.Y).ToArray() switch
        {
            [var leftA, var leftB, var rightA, var rightB] => new[]
            {
                leftA.Y <= leftB.Y ? leftA : leftB,
                rightA.Y <= rightB.Y ? rightA : rightB,
                rightA.Y <= rightB.Y ? rightB : rightA,
                leftA.Y <= leftB.Y ? leftB : leftA
            },
            _ => points
        };
    }

    private static float LargestSide(IReadOnlyList<Point2f> points) => points
        .Select((point, index) => Distance(point, points[(index + 1) % points.Count]))
        .Max();

    private static string RecognizeBox(
        InferenceSession recognizer,
        ImagePixels source,
        DetectionBox box,
        IReadOnlyList<string> characterDictionary)
    {
        ImagePixels crop = PerspectiveCrop(source, box.Corners);
        double ratio = (double)RecognitionHeight / crop.Height;
        int width = Math.Clamp((int)Math.Ceiling(crop.Width * ratio), 8, RecognitionMaximumWidth);
        ImagePixels resized = Resize(crop, width, RecognitionHeight);
        float[] pixels = new float[3 * resized.Width * resized.Height];
        int plane = resized.Width * resized.Height;
        for (int y = 0; y < resized.Height; y++)
        {
            for (int x = 0; x < resized.Width; x++)
            {
                int sourceIndex = (y * resized.Width + x) * 4;
                int destination = y * resized.Width + x;
                pixels[destination] = (resized.Rgba[sourceIndex + 2] / 255f - 0.5f) / 0.5f;
                pixels[plane + destination] = (resized.Rgba[sourceIndex + 1] / 255f - 0.5f) / 0.5f;
                pixels[2 * plane + destination] = (resized.Rgba[sourceIndex] / 255f - 0.5f) / 0.5f;
            }
        }

        var input = new DenseTensor<float>(pixels, new[] { 1, 3, resized.Height, resized.Width });
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = recognizer.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", input)
        });
        return DecodeCtc(outputs.Single().AsEnumerable<float>().ToArray(), characterDictionary);
    }

    private static string DecodeCtc(float[] logits, IReadOnlyList<string> characterDictionary)
    {
        int classes = characterDictionary.Count + 1;
        if (classes <= 1 || logits.Length % classes != 0)
        {
            return string.Empty;
        }

        int previous = 0;
        var builder = new StringBuilder();
        for (int step = 0; step < logits.Length / classes; step++)
        {
            int offset = step * classes;
            int index = 0;
            float highest = logits[offset];
            for (int candidate = 1; candidate < classes; candidate++)
            {
                if (logits[offset + candidate] > highest)
                {
                    index = candidate;
                    highest = logits[offset + candidate];
                }
            }

            if (index > 0 && index != previous)
            {
                builder.Append(characterDictionary[index - 1]);
            }

            previous = index;
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> LoadCharacterDictionary(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        int start = Array.FindIndex(lines, line => line.Trim() == "character_dict:");
        if (start < 0)
        {
            throw new InvalidOperationException("ppocrv5_character_dictionary_not_found");
        }

        var dictionary = new List<string>();
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (!line.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(line)) break;
                continue;
            }

            string value = line[4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            dictionary.Add(value);
        }

        return dictionary;
    }

    private static IReadOnlyList<StructuredOcrWord> CreateApproximateWordBoxes(string text, OcrBoundingBox bounds)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return new[] { new StructuredOcrWord(text, bounds) };
        }

        int units = words.Sum(word => Math.Max(1, word.Length));
        double cursor = bounds.X;
        var result = new List<StructuredOcrWord>();
        foreach (string word in words)
        {
            double width = bounds.Width * Math.Max(1, word.Length) / units;
            result.Add(new StructuredOcrWord(word, new OcrBoundingBox(cursor, bounds.Y, width, bounds.Height)));
            cursor += width;
        }

        return result;
    }

    private static ImagePixels CropNormalized(ImagePixels source, HighAccuracyOcrCrop crop)
    {
        int left = Math.Clamp((int)Math.Floor(source.Width * crop.X), 0, source.Width - 1);
        int top = Math.Clamp((int)Math.Floor(source.Height * crop.Y), 0, source.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(source.Width * (crop.X + crop.Width)), left + 1, source.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(source.Height * (crop.Y + crop.Height)), top + 1, source.Height);
        int width = right - left;
        int height = bottom - top;
        byte[] bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(source.Rgba, ((top + y) * source.Width + left) * 4, bytes, y * width * 4, width * 4);
        }

        return new ImagePixels(width, height, bytes);
    }

    // Equivalent to Utility::GetRotateCropImage in the official C++ runtime.
    private static ImagePixels PerspectiveCrop(ImagePixels source, IReadOnlyList<Point2f> corners)
    {
        int width = Math.Max(1, (int)Math.Round(Distance(corners[0], corners[1])));
        int height = Math.Max(1, (int)Math.Round(Distance(corners[0], corners[3])));
        Point2f[] destination =
        {
            new(0, 0), new(width, 0), new(width, height), new(0, height)
        };
        using Mat sourceMat = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, source.Rgba);
        using Mat transform = Cv2.GetPerspectiveTransform(corners.ToArray(), destination);
        using var warped = new Mat();
        Cv2.WarpPerspective(sourceMat, warped, transform, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
        if (warped.Rows >= warped.Cols * 1.5)
        {
            using var transposed = new Mat();
            Cv2.Transpose(warped, transposed);
            Cv2.Flip(transposed, transposed, FlipMode.X);
            return MatToImagePixels(transposed);
        }

        return MatToImagePixels(warped);
    }

    private static ImagePixels MatToImagePixels(Mat mat)
    {
        byte[] bytes = new byte[mat.Rows * mat.Cols * 4];
        Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        return new ImagePixels(mat.Cols, mat.Rows, bytes);
    }

    private static float Distance(Point2f left, Point2f right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static ImagePixels Resize(ImagePixels source, int width, int height)
    {
        using Mat sourceMat = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, source.Rgba);
        using var resized = new Mat();
        Cv2.Resize(sourceMat, resized, new Size(width, height), 0, 0, InterpolationFlags.Linear);
        return MatToImagePixels(resized);
    }

    private void DisposeSessions()
    {
        lock (_gate)
        {
            _detector?.Dispose();
            _recognizer?.Dispose();
            _detector = null;
            _recognizer = null;
            _characterDictionary = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PackagedPpOcrV5Provider));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeSessions();
    }

    private sealed record ImagePixels(int Width, int Height, byte[] Rgba);
    private sealed record DetectionMap(float[] Values, int Width, int Height, string Shape);
    private sealed record DetectionBox(Point2f[] Corners, int Left, int Top, int Right, int Bottom);
}
