namespace Library.Web.Indexing;

/// <param name="LastFinished">When the last successful scan ended.</param>
/// <param name="LastError">Why the most recent scan failed; null once a scan succeeds again.</param>
public sealed record IndexerStatus(
    bool Scanning,
    DateTimeOffset? LastFinished,
    ScanResult? LastResult,
    string? LastError = null,
    DateTimeOffset? LastFailed = null)
{
    public bool LastScanFailed => LastError is not null;
}

/// <summary>Scans at startup, then every <see cref="LibraryOptions.ScanInterval"/> or when asked to.</summary>
public sealed class IndexerService(LibraryScanner scanner, LibraryOptions options, ILogger<IndexerService> log) : BackgroundService
{
    private static readonly TimeSpan RetrySoon = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _trigger = new(0, 1);

    public IndexerStatus Status { get; private set; } = new(false, null, null);

    /// <summary>Starts a scan now (or right after the one in progress). Repeated requests collapse into one.</summary>
    public void RequestScan()
    {
        if (_trigger.CurrentCount == 0)
        {
            try
            {
                _trigger.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another request got there first.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Status = Status with { Scanning = true };
            try
            {
                var result = await Task.Run(() => scanner.Scan(stoppingToken), stoppingToken);
                Status = new IndexerStatus(false, DateTimeOffset.UtcNow, result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Library scan failed");
                Status = Status with { Scanning = false, LastError = ex.Message, LastFailed = DateTimeOffset.UtcNow };
            }

            // Files still being written get another look soon rather than at the next regular scan.
            var wait = Status.LastResult is { Deferred: > 0 } && options.ScanInterval > RetrySoon
                ? RetrySoon
                : options.ScanInterval;
            try
            {
                await _trigger.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
