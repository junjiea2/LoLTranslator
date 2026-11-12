using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLTranslator.App;

/// <summary>
/// Explicitly opt-in local diagnostic containing OCR/chat text. Unlike the
/// always-on privacy trace, this file may contain player text and must never be
/// treated as Pack evidence or uploaded automatically. Screenshots are omitted.
/// </summary>
public sealed class OcrTextDiagnosticTrace
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _gate = new();

    public OcrTextDiagnosticTrace(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLTranslator", "ocr-diagnostics",
        $"ocr-text-diagnostic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");

    public string FilePath { get; }

    public void Append(IReadOnlyList<StructuredOcrLine> nativeLines, VisibleMessageDiffResult diff)
    {
        var entry = new DiagnosticEntry(
            DateTimeOffset.UtcNow,
            nativeLines.Select(line => new NativeLine(line.Text, line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height)).ToArray(),
            diff.Decisions ?? Array.Empty<VisibleMessageDecision>(),
            diff.Inserted.Count,
            diff.PendingConfirmationCount);
        lock (_gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
            }
            catch { }
        }
    }

    public void AppendTranslation(IncrementalGeneralChatShadowResult result)
    {
        lock (_gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var entry = new TranslationEntry(DateTimeOffset.UtcNow, result.Lines.Select(line =>
                    new TranslationLine(line.LineNumber, line.MessageBody, line.Route, line.FinalOutput, line.FailureReason)).ToArray());
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
            }
            catch { }
        }
    }

    private sealed record DiagnosticEntry(
        [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("ocr_lines")] IReadOnlyList<NativeLine> OcrLines,
        [property: JsonPropertyName("message_decisions")] IReadOnlyList<VisibleMessageDecision> MessageDecisions,
        [property: JsonPropertyName("inserted_messages")] int InsertedMessages,
        [property: JsonPropertyName("pending_confirmation")] int PendingConfirmation);

    private sealed record NativeLine(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("width")] double Width,
        [property: JsonPropertyName("height")] double Height);

    private sealed record TranslationEntry(
        [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
        [property: JsonPropertyName("translation_lines")] IReadOnlyList<TranslationLine> TranslationLines);

    private sealed record TranslationLine(
        [property: JsonPropertyName("line_number")] int LineNumber,
        [property: JsonPropertyName("message_body")] string MessageBody,
        [property: JsonPropertyName("route")] string Route,
        [property: JsonPropertyName("final_output")] string? FinalOutput,
        [property: JsonPropertyName("failure_reason")] string? FailureReason);
}
