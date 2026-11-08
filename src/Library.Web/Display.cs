using System.Globalization;
using Library.Web.Data;

namespace Library.Web;

/// <summary>Formatting and link helpers shared by the pages.</summary>
public static class Display
{
    /// <summary>Natural, case-insensitive order: "Plate 2" before "Plate 10".</summary>
    public static readonly StringComparer NameOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public static string BrowseUrl(string path) => path.Length == 0 ? "/browse" : "/browse/" + EscapePath(path);

    public static string DownloadUrl(string path) => "/download/" + EscapePath(path);

    public static string FileUrl(string path) => "/file/" + EscapePath(path);

    public static string SlicerLinkUrl(string path) => "/slicer-link/" + EscapePath(path);

    public static string ZipUrl(string folder) => folder.Length == 0 ? "/zip" : "/zip/" + EscapePath(folder);

    /// <summary>"35 min", "2 h 05 min".</summary>
    public static string Duration(long seconds)
    {
        var minutes = (int)Math.Round(seconds / 60.0);
        return minutes < 60
            ? $"{Math.Max(minutes, 1)} min"
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min");
    }

    public static string Grams(double grams) =>
        grams.ToString(grams < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " g";

    /// <summary>"105 × 105 × 177 mm".</summary>
    public static string Dimensions(double x, double y, double z) =>
        string.Create(CultureInfo.InvariantCulture, $"{Millimetres(x)} × {Millimetres(y)} × {Millimetres(z)} mm");

    private static string Millimetres(double value) =>
        value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string ImageUrl(string cacheKey, string file) => $"/img/{cacheKey}/{file}";

    /// <summary>The model's own title (MakerWorld/Bambu metadata) when it has one, otherwise the tidied file name.</summary>
    public static string Title(FileRow file) => file.Title ?? PrettyName(file.Name);

    public static string PrettyName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    public static string Ago(DateTimeOffset? moment)
    {
        if (moment is null)
        {
            return "never";
        }

        var elapsed = DateTimeOffset.UtcNow - moment.Value;
        return elapsed.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)elapsed.TotalMinutes} min ago",
            < 86400 => $"{(int)elapsed.TotalHours} h ago",
            _ => $"{(int)elapsed.TotalDays} d ago",
        };
    }

    public static string Plural(long count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
