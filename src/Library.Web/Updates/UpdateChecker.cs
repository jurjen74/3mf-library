using System.Text.Json;

namespace Library.Web.Updates;

/// <summary>A newer release than the one that's running.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// Once a day, asks GitHub for the latest release of 3MF Library and remembers it when it's newer than this build, so
/// the footer can say so. Only the release list is fetched; nothing about the site or the library is sent. Off with
/// <c>UPDATE_CHECK=false</c>, and for local builds (version "dev").
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory httpClients, LibraryOptions options, ILogger<UpdateChecker> log) : BackgroundService
{
    public const string HttpClientName = "updates";
    public const string Repository = "jurjen74/3mf-library";
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(1);

    /// <summary>The newer release, or null when this build is the latest (or nothing is known yet).</summary>
    public UpdateInfo? Available { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public static bool IsEnabled(LibraryOptions options, AppVersion version) => options.UpdateCheck && version.Release is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled(options, AppVersion.Current))
        {
            log.LogInformation("Version {Version}; update check is off", AppVersion.Current);
            return;
        }

        try
        {
            // Not during startup: the first scan matters more.
            await Task.Delay(FirstCheckAfter, stoppingToken);
            while (true)
            {
                await CheckAsync(AppVersion.Current, stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One check. A failure (offline, GitHub down, rate limit) keeps what was known and is tried tomorrow.</summary>
    public async Task CheckAsync(AppVersion current, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log.LogInformation("Update check: GitHub answered {Status}", (int)response.StatusCode);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            Available = Newer(current, tag, url);
            LastChecked = DateTimeOffset.UtcNow;
            if (Available is not null)
            {
                log.LogInformation("Version {Latest} is available (running {Current}): {Url}", Available.Version, current, Available.Url);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            log.LogInformation("Update check failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The latest release when it's newer than <paramref name="current"/>. A build from the main branch after 1.0.1
    /// ("1.0.1-2-g…") counts as 1.0.1: only a newer release is an update.
    /// </summary>
    public static UpdateInfo? Newer(AppVersion current, string? latestTag, string? latestUrl)
    {
        var running = current.Release;
        var latest = AppVersion.ParseRelease(latestTag);
        if (running is null || latest is null || latest <= running)
        {
            return null;
        }

        var url = Uri.TryCreate(latestUrl, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.ToString()
            : $"https://github.com/{Repository}/releases/latest";
        return new UpdateInfo(latest.ToString(3), url);
    }
}
