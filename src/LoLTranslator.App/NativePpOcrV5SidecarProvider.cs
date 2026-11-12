using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LoLTranslator.App;

public sealed class NativePpOcrSidecarException : InvalidOperationException
{
    public NativePpOcrSidecarException(string diagnosticCode, Exception? innerException = null)
        : base(diagnosticCode, innerException) => DiagnosticCode = diagnosticCode;

    public string DiagnosticCode { get; }
}

/// <summary>
/// Distribution boundary for the packaged official PaddleOCR C++ sidecar. It
/// deliberately has no Windows OCR or DirectML fallback: a missing, unhealthy,
/// or malformed native result is unavailable/fail-closed rather than a lower
/// accuracy translation input.
/// </summary>
public sealed class NativePpOcrV5SidecarProvider : IHighAccuracyChatOcrProvider, IDisposable
{
    public const string LoadingMessage = "高精度识别模型正在加载…";
    public const string ReadyMessage = "高精度识别模型已就绪。";
    public const string UnavailableMessage = "此设备暂无法使用高精度识别模型。";
    public const string DownloadRequiredMessage = "需要安装高精度识别组件后才能开始翻译。";

    private readonly string _sidecarPath;
    private readonly string _detectorModelDirectory;
    private readonly string _recognizerModelDirectory;
    private readonly string _dictionaryPath;
    private readonly bool _enabled;
    private readonly bool _useGpu;
    private readonly HighAccuracyOcrCrop? _crop;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private Task? _stderrDrainTask;
    private bool _hasCompletedRecognition;
    private bool _disposed;

    public NativePpOcrV5SidecarProvider(
        string? sidecarPath = null,
        string? modelRoot = null,
        string? dictionaryPath = null,
        bool? enabled = null,
        string? detectorModelDirectory = null,
        string? recognizerModelDirectory = null,
        bool? useGpu = null,
        HighAccuracyOcrCrop? crop = null)
    {
        string? developmentRoot = FindDevelopmentRoot();
        _sidecarPath = sidecarPath
            ?? Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_SIDECAR_PATH")
            ?? FirstExistingPath(
                Path.Combine(AppContext.BaseDirectory, "ocr", "loltranslator-ppocr-sidecar.exe"),
                developmentRoot is null ? null : Path.Combine(developmentRoot, "tools", "ppocr-windows-sidecar", ".third-party", "build", "sidecar-gpu", "Release", "loltranslator-ppocr-sidecar.exe"));
        string resolvedModelRoot = modelRoot
            ?? Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_MODEL_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "models", "ppocrv5");
        string userModelRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".paddlex", "official_models");
        _detectorModelDirectory = detectorModelDirectory
            ?? Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_DET_MODEL_DIR")
            ?? FirstCompletePaddleModelDirectory(
                Path.Combine(resolvedModelRoot, "mobile-det"),
                Path.Combine(resolvedModelRoot, "PP-OCRv5_mobile_det"),
                Path.Combine(userModelRoot, "PP-OCRv5_mobile_det"));
        _recognizerModelDirectory = recognizerModelDirectory
            ?? Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_REC_MODEL_DIR")
            ?? FirstCompletePaddleModelDirectory(
                Path.Combine(resolvedModelRoot, "server-rec"),
                Path.Combine(resolvedModelRoot, "PP-OCRv5_server_rec"),
                Path.Combine(userModelRoot, "PP-OCRv5_server_rec"));
        _dictionaryPath = dictionaryPath
            ?? FirstExistingPath(
                Path.Combine(_recognizerModelDirectory, "character_dict.txt"),
                developmentRoot is null ? null : Path.Combine(developmentRoot, "tools", "ppocr-windows-sidecar", ".third-party", "models", "PP-OCRv5_server_rec", "character_dict.txt"));
        string? enabledSetting = Environment.GetEnvironmentVariable("LOLTRANSLATOR_ENABLE_PPOCRV5_SIDECAR");
        _enabled = enabled ?? !string.Equals(enabledSetting, "0", StringComparison.Ordinal);
        string? gpuSetting = Environment.GetEnvironmentVariable("LOLTRANSLATOR_PPOCRV5_USE_GPU");
        _useGpu = useGpu ?? !string.Equals(gpuSetting, "0", StringComparison.Ordinal);
        _crop = crop;
    }

    public HighAccuracyOcrCapability Capability { get; private set; } =
        new("checking", LoadingMessage, false);

    public async Task<HighAccuracyOcrCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!_enabled)
        {
            Capability = new("disabled", UnavailableMessage, false, "ppocrv5_sidecar_explicit_opt_in_required");
        }
        else if (!File.Exists(_sidecarPath))
        {
            Capability = new("component_missing", DownloadRequiredMessage, false, "ppocrv5_sidecar_binary_missing");
        }
        else if (!File.Exists(Path.Combine(_detectorModelDirectory, "inference.json")) ||
                 !File.Exists(Path.Combine(_detectorModelDirectory, "inference.pdiparams")) ||
                 !File.Exists(Path.Combine(_recognizerModelDirectory, "inference.json")) ||
                 !File.Exists(Path.Combine(_recognizerModelDirectory, "inference.pdiparams")) ||
                 !File.Exists(_dictionaryPath))
        {
            Capability = new("model_download_required", DownloadRequiredMessage, false, "ppocrv5_sidecar_models_or_dictionary_missing");
        }
        else
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureProcessStartedAsync(cancellationToken).ConfigureAwait(false);
                Capability = new("ready", ReadyMessage, true, _useGpu
                    ? "official_ppocrv5_cpp_sidecar_gpu_persistent"
                    : "official_ppocrv5_cpp_sidecar_cpu_persistent");
            }
            catch (OperationCanceledException)
            {
                StopProcess();
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StopProcess();
                Capability = new("native_runtime_unavailable", UnavailableMessage, false,
                    $"ppocrv5_sidecar_start_failed:{exception.GetType().Name}");
            }
            finally
            {
                _requestGate.Release();
            }
        }
        return Capability;
    }

    public async Task<IReadOnlyList<StructuredOcrLine>> RecognizeLinesAsync(
        string imagePath,
        string locale,
        long frameId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new ArgumentException("截图输入不可用。", nameof(imagePath));
        }
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? firstFailure = null;
            // The continuous runtime owns retry/failover policy. Retrying a
            // wedged GPU process here used to block the capture pipeline for
            // as long as 16 seconds (two eight-second timeouts), during which
            // fast chat rows could scroll out of view. Fail once and let the
            // caller switch to its CPU safety provider immediately.
            for (int attempt = 0; attempt < 1; attempt++)
            {
                try
                {
                    await EnsureProcessStartedAsync(cancellationToken).ConfigureAwait(false);
                    await _process!.StandardInput.WriteLineAsync(imagePath.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    // GPU scheduling becomes less predictable while a game is
                    // rendering. Three seconds was too aggressive and caused a
                    // healthy sidecar to be killed and cold-started repeatedly.
                    TimeSpan timeout = _hasCompletedRecognition ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(20);
                    string json = await ReadProtocolLineAsync(expectReady: false, timeout, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<StructuredOcrLine> lines = ParseJsonOnlyOutput(json, locale, frameId);
                    _hasCompletedRecognition = true;
                    return lines;
                }
                catch (OperationCanceledException)
                {
                    StopProcess();
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    firstFailure ??= exception;
                    StopProcess();
                }
            }
            string diagnosticCode = firstFailure switch
            {
                NativePpOcrSidecarException native => native.DiagnosticCode,
                TimeoutException => "response_timeout",
                JsonException => "malformed_json",
                _ => "native_failure"
            };
            Capability = new("native_runtime_unavailable", UnavailableMessage, false,
                $"ppocrv5_sidecar_recognition_failed:{diagnosticCode}");
            throw new NativePpOcrSidecarException(diagnosticCode, firstFailure);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private string BuildArguments()
    {
        var arguments = new List<string>
        {
            "--stdin_loop=true",
            Quote("--det_model_dir=" + _detectorModelDirectory),
            Quote("--rec_model_dir=" + _recognizerModelDirectory),
            Quote("--rec_char_dict_path=" + _dictionaryPath),
            _useGpu ? "--use_gpu=true" : "--use_gpu=false",
            "--use_angle_cls=false",
            "--visualize=false",
            "--det_db_score_mode=fast"
        };
        if (_useGpu)
        {
            arguments.Add("--gpu_id=0");
            // Live play and hardware-decoded streams share the same GPU. Keep
            // both the initial pool and recognition batch deliberately small;
            // the former 1 GB / batch-6 profile eventually stopped responding
            // under a long Twitch stress run.
            arguments.Add("--gpu_mem=256");
            arguments.Add("--rec_batch_num=2");
        }
        if (_crop is not null)
        {
            arguments.Add($"--crop_x={_crop.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            arguments.Add($"--crop_y={_crop.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            arguments.Add($"--crop_width={_crop.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            arguments.Add($"--crop_height={_crop.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(' ', arguments);
    }

    private async Task EnsureProcessStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        StopProcess();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _sidecarPath,
                Arguments = BuildArguments(),
                WorkingDirectory = Path.GetDirectoryName(_sidecarPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start()) throw new InvalidOperationException("ppocrv5_sidecar_start_rejected");
        _process = process;
        _stderrDrainTask = DrainDiagnosticsAsync(process.StandardError);
        await ReadProtocolLineAsync(expectReady: true, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadProtocolLineAsync(bool expectReady, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process is null) throw new InvalidOperationException("ppocrv5_sidecar_not_started");
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (true)
        {
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(expectReady
                    ? "ppocrv5_sidecar_ready_timeout"
                    : "ppocrv5_sidecar_response_timeout");
            }
            if (line is null) throw new InvalidOperationException("ppocrv5_sidecar_closed_protocol");
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("{\"status\":", StringComparison.Ordinal)) continue;
            bool isReady = trimmed.StartsWith("{\"status\":\"ready\"", StringComparison.Ordinal);
            if (isReady == expectReady) return trimmed;
        }
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { }
        }
        catch { }
    }

    private void StopProcess()
    {
        Process? process = _process;
        _process = null;
        _stderrDrainTask = null;
        _hasCompletedRecognition = false;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("__quit__");
                process.StandardInput.Close();
                if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        finally { process.Dispose(); }
    }

    private static IReadOnlyList<StructuredOcrLine> ParseJsonOnlyOutput(string output, string locale, long frameId)
    {
        string? json = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        if (string.IsNullOrWhiteSpace(json)) throw new NativePpOcrSidecarException("malformed_output");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("status", out JsonElement status) || status.GetString() != "ok")
        {
            string code = root.TryGetProperty("code", out JsonElement codeElement)
                ? SanitizeDiagnosticCode(codeElement.GetString())
                : "rejected_input";
            throw new NativePpOcrSidecarException(code);
        }

        var lines = new List<StructuredOcrLine>();
        foreach (JsonElement line in root.GetProperty("lines").EnumerateArray())
        {
            string text = OcrService.NormalizeRecognizedText(DecodeProtocolText(line));
            if (string.IsNullOrWhiteSpace(text)) continue;
            int[] xs = line.GetProperty("box").EnumerateArray().Select(point => point[0].GetInt32()).ToArray();
            int[] ys = line.GetProperty("box").EnumerateArray().Select(point => point[1].GetInt32()).ToArray();
            if (xs.Length < 3 || ys.Length < 3) continue;
            OcrBoundingBox bounds = new(xs.Min(), ys.Min(), Math.Max(1, xs.Max() - xs.Min()), Math.Max(1, ys.Max() - ys.Min()));
            lines.Add(new StructuredOcrLine(text, bounds, Array.Empty<StructuredOcrWord>(), "ppocrv5-cpp", frameId));
        }
        return lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToArray();
    }

    private static string DecodeProtocolText(JsonElement line)
    {
        if (line.TryGetProperty("text_base64", out JsonElement encoded))
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.GetString() ?? string.Empty));
            }
            catch (FormatException exception)
            {
                throw new NativePpOcrSidecarException("invalid_text_transport", exception);
            }
        }
        // Backward compatibility for an older packaged sidecar during a staged
        // application update.
        return line.TryGetProperty("text", out JsonElement plain) ? plain.GetString() ?? string.Empty : string.Empty;
    }

    private static string SanitizeDiagnosticCode(string? code) => code switch
    {
        "input_image_unreadable" => code,
        "invalid_normalized_crop" => code,
        "required_argument_missing" => code,
        "native_inference_exception" => code,
        "invalid_text_transport" => code,
        _ => "sidecar_error"
    };

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';

    private static string FirstExistingPath(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
        ?? candidates.First(candidate => !string.IsNullOrWhiteSpace(candidate))!;

    private static string FirstCompletePaddleModelDirectory(params string[] candidates) =>
        candidates.FirstOrDefault(candidate =>
            File.Exists(Path.Combine(candidate, "inference.json")) &&
            File.Exists(Path.Combine(candidate, "inference.pdiparams"))) ?? candidates[0];

    private static string? FindDevelopmentRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "ppocr-windows-sidecar")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "LoLTranslator.App"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativePpOcrV5SidecarProvider));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestGate.Wait();
        try { StopProcess(); }
        finally { _requestGate.Release(); _requestGate.Dispose(); }
    }
}
