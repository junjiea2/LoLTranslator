using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;

namespace LoLTranslator.App;

public sealed record LocalModelStatus(
    string State,
    string Message,
    bool Ready,
    string? Diagnostic = null,
    bool StartedByApp = false,
    int? ProcessId = null);

/// <summary>Single-flight local Ollama lifecycle. It never downloads a model or
/// falls back to another provider; a hidden <c>serve</c> process is normal and
/// does not need a tray window.</summary>
public static class LocalModelManager
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromSeconds(2) };
    private static readonly HttpClient WarmupClient = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromSeconds(45) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<LocalModelStatus>? _inFlight;
    private static Task? _warmupTask;
    private static bool _warmedForProcess;

    public static Task<LocalModelStatus> EnsureQwen3Async(TimeSpan? maximumWait = null)
    {
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            _inFlight = EnsureCoreAsync(maximumWait ?? TimeSpan.FromSeconds(20));
            return _inFlight;
        }
    }

    private static async Task<LocalModelStatus> EnsureCoreAsync(TimeSpan wait)
    {
        if (await IsReadyAsync()) return await ReadyAfterWarmupAsync(false, null);
        string? executable = InstalledOllamaPath();
        if (executable is null)
            return new LocalModelStatus("not_installed", "需要安装本地 Ollama 运行组件；不会自动下载模型。", false, "ollama.exe_not_found");

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable, "serve")
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            });
        }
        catch (Win32Exception exception)
        {
            return new LocalModelStatus("start_failed", "无法启动本地 Ollama 后台服务。", false, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new LocalModelStatus("start_failed", "无法启动本地 Ollama 后台服务。", false, exception.Message);
        }

        int? pid = process?.Id;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(wait);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(750);
            if (process is { HasExited: true })
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                return new LocalModelStatus("start_failed", "本地 Ollama 后台服务已退出。", false,
                    $"exit_code={process.ExitCode}; {Summarize(stderr)}", true, pid);
            }
            if (await IsServerHealthyAsync())
                return await HasExpectedQwen3Async()
                    ? await ReadyAfterWarmupAsync(true, pid)
                    : new LocalModelStatus("model_missing", "Ollama API 已连接，但受控 qwen3:8b 模型未就绪；不会自动下载。", false, "model_or_digest_missing", true, pid);
        }
        return new LocalModelStatus("starting", "正在启动本地 Ollama 后台服务；会继续检查，无需打开窗口。", false, "health_check_timeout", true, pid);
    }

    public static string? InstalledOllamaPath() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe")
    }.FirstOrDefault(File.Exists);

    private static async Task<bool> IsReadyAsync() => await IsServerHealthyAsync() && await HasExpectedQwen3Async();
    private static async Task<bool> IsServerHealthyAsync()
    {
        try { using HttpResponseMessage response = await Client.GetAsync("api/tags"); return response.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
    private static async Task<bool> HasExpectedQwen3Async()
    {
        try
        {
            string tags = await Client.GetStringAsync("api/tags");
            return tags.Contains("qwen3:8b", StringComparison.OrdinalIgnoreCase) &&
                   tags.Contains(ControlledShadowQwen3Profile.ExpectedDigest, StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
    private static Task<LocalModelStatus> ReadyAfterWarmupAsync(bool startedByApp, int? pid)
    {
        BeginWarmupInBackground();
        return Task.FromResult(new LocalModelStatus(
            "ready",
            _warmedForProcess ? "本地翻译已就绪。" : "本地翻译已连接；首次翻译可能稍慢。",
            true,
            _warmedForProcess
                ? "api_200; qwen3:8b_digest_verified; model_warmed"
                : "api_200; qwen3:8b_digest_verified; warmup_background",
            startedByApp,
            pid));
    }

    private static void BeginWarmupInBackground()
    {
        lock (Gate)
        {
            if (_warmedForProcess || _warmupTask is { IsCompleted: false }) return;
            _warmupTask = WarmupCoreAsync();
        }
    }

    private static async Task WarmupCoreAsync()
    {
        try
        {
            using HttpResponseMessage response = await WarmupClient.PostAsJsonAsync("api/generate", new
            {
                model = "qwen3:8b",
                prompt = string.Empty,
                stream = false,
                think = false,
                keep_alive = "10m"
            });
            response.EnsureSuccessStatusCode();
            _warmedForProcess = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Warmup is an optimization only. A verified local model may take
            // longer than this request timeout on its first load; the actual
            // translation provider has its own longer timeout and must not be
            // blocked behind repeated warmup attempts.
        }
    }
    private static string Summarize(string value) => string.IsNullOrWhiteSpace(value) ? "no_stderr" : value.ReplaceLineEndings(" ").Trim()[..Math.Min(240, value.Trim().Length)];
}
