using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LoLTranslator.App;

public sealed record PlayerFeedbackSubmission(
    string FeedbackId,
    string IssueType,
    string Description,
    string? Reproduction,
    string AppVersion,
    string TargetLocale,
    DateTimeOffset CreatedAtUtc);

public sealed record PlayerFeedbackSubmitResult(string Status, string? Detail = null)
{
    public bool SubmittedOnline => Status == "submitted_online";
}

public sealed class PlayerFeedbackService
{
    private sealed record FeedbackServiceConfig(string? Endpoint);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly Uri? _endpoint;
    private readonly HttpClient _client;
    private readonly string _pendingDirectory;

    public PlayerFeedbackService(Uri? endpoint, HttpClient? client = null, string? pendingDirectory = null)
    {
        if (endpoint is not null && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Player feedback endpoint must use HTTPS.", nameof(endpoint));

        _endpoint = endpoint;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _pendingDirectory = pendingDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoLTranslator",
            "pending-feedback-v0.1");
    }

    public bool OnlineEndpointConfigured => _endpoint is not null;

    public static PlayerFeedbackService LoadFromApplicationDirectory(string applicationDirectory)
    {
        string configPath = Path.Combine(applicationDirectory, "data", "runtime", "player-feedback-service.v0.1.json");
        try
        {
            FeedbackServiceConfig? config = JsonSerializer.Deserialize<FeedbackServiceConfig>(
                File.ReadAllText(configPath, Encoding.UTF8), JsonOptions);
            return Uri.TryCreate(config?.Endpoint, UriKind.Absolute, out Uri? endpoint) &&
                   endpoint.Scheme == Uri.UriSchemeHttps
                ? new PlayerFeedbackService(endpoint)
                : new PlayerFeedbackService(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PlayerFeedbackService(null);
        }
    }

    public static PlayerFeedbackSubmission CreateSubmission(
        string issueType,
        string description,
        string? reproduction)
    {
        string version = typeof(PlayerFeedbackService).Assembly.GetName().Version?.ToString(3) ?? "preview";
        return new PlayerFeedbackSubmission(
            Guid.NewGuid().ToString("N"),
            issueType.Trim(),
            description.Trim(),
            string.IsNullOrWhiteSpace(reproduction) ? null : reproduction.Trim(),
            version,
            CultureInfo.CurrentUICulture.Name,
            DateTimeOffset.UtcNow);
    }

    public async Task<PlayerFeedbackSubmitResult> SubmitOrQueueAsync(
        PlayerFeedbackSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (string.IsNullOrWhiteSpace(submission.Description))
            throw new ArgumentException("Feedback description is required.", nameof(submission));

        if (_endpoint is not null)
        {
            try
            {
                if (await PostAsync(submission, cancellationToken).ConfigureAwait(false))
                    return new PlayerFeedbackSubmitResult("submitted_online");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                await QueueAsync(submission, cancellationToken).ConfigureAwait(false);
                return new PlayerFeedbackSubmitResult("queued_for_retry", exception.GetType().Name);
            }
        }

        await QueueAsync(submission, cancellationToken).ConfigureAwait(false);
        return new PlayerFeedbackSubmitResult(
            "queued_for_retry",
            _endpoint is null ? "online_endpoint_not_configured" : "online_submission_rejected");
    }

    public async Task<int> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint is null || !Directory.Exists(_pendingDirectory)) return 0;

        int submitted = 0;
        foreach (string path in Directory.EnumerateFiles(_pendingDirectory, "*.json").OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PlayerFeedbackSubmission? feedback = JsonSerializer.Deserialize<PlayerFeedbackSubmission>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (feedback is null || !await PostAsync(feedback, cancellationToken).ConfigureAwait(false)) continue;
                File.Delete(path);
                submitted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException)
            {
                break;
            }
        }
        return submitted;
    }

    private async Task<bool> PostAsync(PlayerFeedbackSubmission submission, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(submission, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task QueueAsync(PlayerFeedbackSubmission submission, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pendingDirectory);
        string finalPath = Path.Combine(_pendingDirectory, submission.FeedbackId + ".json");
        string temporaryPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(submission, JsonOptions),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, finalPath, overwrite: true);
    }
}
