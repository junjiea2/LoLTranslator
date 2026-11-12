namespace LoLTranslator.App;

/// <summary>Capture time belongs to the image, never to the later OCR dequeue.</summary>
public sealed record RuntimeFrameTiming(long FrameId, DateTimeOffset CapturedAtUtc,
    DateTimeOffset OcrStartedAtUtc, DateTimeOffset OcrCompletedAtUtc);

internal sealed record CapturedOcrFrame(string Path, long Id, DateTimeOffset CapturedAtUtc);

public static class RuntimeBuildInfo
{
    public const string Label = "本地测试版 2026.09.04 · P2H";
    public const string Id = "2026.09.04-p2h";
}
