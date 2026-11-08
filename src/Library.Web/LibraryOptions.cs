using System.Globalization;
using System.Net;

namespace Library.Web;

/// <summary>Settings from environment variables (<c>LIBRARY_PATH</c>, <c>DATA_PATH</c>, …); see docker-compose.yml.</summary>
public sealed class LibraryOptions
{
    public static readonly IReadOnlyList<string> DefaultIgnore =
    [
        "datapackage.json", "._*", "@eaDir", "#recycle", "#snapshot", "Thumbs.db", "desktop.ini", "*.tmp",
    ];

    /// <summary>Root of the 3D print folder. Mounted read-only; never written to.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>Writable folder for the SQLite index, the preview cache and data-protection keys.</summary>
    public required string DataPath { get; init; }

    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a new or changed file must stay unchanged before it is read (Cloud Sync writes in parts).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.Zero;

    public IReadOnlyList<string> Ignore { get; init; } = DefaultIgnore;

    public string SiteTitle { get; init; } = "3D Print library";

    /// <summary>Networks whose direct (non-proxied) visitors need no login (<c>TRUSTED_NETWORKS</c>, e.g. 192.168.2.0/24).</summary>
    public IReadOnlyList<IPNetwork> TrustedNetworks { get; init; } = [];

    /// <summary>Reverse proxies whose X-Forwarded-For/-Proto are believed (<c>KNOWN_PROXIES</c>); never trusted as LAN.</summary>
    public IReadOnlyList<IPAddress> KnownProxies { get; init; } = [];

    public string CachePath => Path.Combine(DataPath, "cache");

    public string DatabasePath => Path.Combine(DataPath, "library.db");

    public static LibraryOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        string Resolve(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, contentRoot));

        var interval = TimeSpan.TryParse(config["SCAN_INTERVAL"], CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero
            ? parsed
            : TimeSpan.FromMinutes(3);

        var settle = TimeSpan.TryParse(config["SETTLE_DELAY"], CultureInfo.InvariantCulture, out var parsedSettle) && parsedSettle >= TimeSpan.Zero
            ? parsedSettle
            : TimeSpan.FromSeconds(10);

        var ignore = config["IGNORE"] is { Length: > 0 } list
            ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultIgnore;

        return new LibraryOptions
        {
            LibraryPath = Resolve(config["LIBRARY_PATH"] ?? "/library"),
            DataPath = Resolve(config["DATA_PATH"] ?? "/data"),
            ScanInterval = interval,
            SettleDelay = settle,
            Ignore = ignore,
            SiteTitle = config["SITE_TITLE"] is { Length: > 0 } title ? title : "3D Print library",
            TrustedNetworks = SplitList(config["TRUSTED_NETWORKS"]).Select(ParseNetwork).ToList(),
            KnownProxies = SplitList(config["KNOWN_PROXIES"]).Select(ParseAddress).ToList(),
        };
    }

    private static string[] SplitList(string? value) =>
        (value ?? "").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Bad network settings stop the app at startup instead of silently opening or closing access.
    private static IPNetwork ParseNetwork(string text) => IPNetwork.TryParse(text, out var network)
        ? network
        : throw new InvalidOperationException($"TRUSTED_NETWORKS: '{text}' is not a network like 192.168.2.0/24.");

    private static IPAddress ParseAddress(string text) => IPAddress.TryParse(text, out var address)
        ? address
        : throw new InvalidOperationException($"KNOWN_PROXIES: '{text}' is not an IP address.");
}
