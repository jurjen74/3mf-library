using System.Globalization;

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
        };
    }
}
