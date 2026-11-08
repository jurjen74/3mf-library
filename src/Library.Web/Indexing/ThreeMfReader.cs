using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Library.Web.Indexing;

/// <summary>One build plate of a 3MF project and the preview images the slicer stored for it.</summary>
public sealed record PlateInfo(int Index, string? Name, string? ImageEntry, string? SmallImageEntry)
{
    /// <summary>Names of the objects placed on this plate, in plate order, without duplicates.</summary>
    public IReadOnlyList<string> Objects { get; init; } = [];

    /// <summary>Estimated print time; only present when the project was sliced before it was saved.</summary>
    public int? PrintSeconds { get; init; }

    /// <summary>Estimated filament weight in grams; only present when the project was sliced before it was saved.</summary>
    public double? WeightGrams { get; init; }
}

/// <summary>What the indexer needs from a 3MF file. Read from a handful of small entries; geometry is never unpacked.</summary>
public sealed class ThreeMfInfo
{
    public string? Title { get; set; }
    public string? Designer { get; set; }
    public string? License { get; set; }

    /// <summary>The description as HTML, unescaped but not yet sanitised.</summary>
    public string? Description { get; set; }

    public string? Application { get; set; }
    public string? CreationDate { get; set; }
    public List<PlateInfo> Plates { get; } = [];

    /// <summary>Project-level preview for files without plate images (PrusaSlicer, OrcaSlicer, generic 3MF).</summary>
    public string? FallbackCoverEntry { get; set; }

    public List<string> FilamentColours { get; } = [];

    /// <summary>Photos of the printed model that MakerWorld downloads embed, cover first.</summary>
    public List<string> PhotoEntries { get; } = [];
}

/// <summary>
/// Reads Bambu Studio / OrcaSlicer / PrusaSlicer 3MF projects. A 3MF is a ZIP, so only the central directory and the
/// few entries named below are read: an 87 MB project costs kilobytes.
/// </summary>
public static partial class ThreeMfReader
{
    private const int MaxEntries = 100_000;
    private const long MaxConfigBytes = 32L * 1024 * 1024;
    private const int MaxMetadataChars = 256 * 1024;
    private const int MaxColours = 16;
    private const int MaxPhotos = 40;
    private const int MaxObjectsPerPlate = 100;

    private static readonly string[] FallbackCovers =
    [
        "Metadata/thumbnail.png",
        "Auxiliaries/.thumbnails/thumbnail_middle.png",
        "Auxiliaries/.thumbnails/thumbnail_3mf.png",
    ];

    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public static ThreeMfInfo Read(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"Archive has more than {MaxEntries} entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith('/'))
            {
                entries.TryAdd(entry.FullName, entry);
            }
        }

        var info = new ThreeMfInfo();
        if (entries.TryGetValue("3D/3dmodel.model", out var model))
        {
            ReadModelMetadata(model, info);
        }

        // Plates come from the slicer's plate list (names, thumbnails, objects) plus any plate_N.png not listed there.
        var plates = new SortedDictionary<int, PlateSettings>();
        if (entries.TryGetValue("Metadata/model_settings.config", out var settings))
        {
            ReadPlateSettings(settings, plates);
        }

        foreach (var name in entries.Keys)
        {
            var match = PlateImagePattern().Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var index))
            {
                if (!plates.TryGetValue(index, out var plate))
                {
                    plates[index] = plate = new PlateSettings();
                }

                plate.Image ??= name;
            }
        }

        var sliced = new Dictionary<int, (int? Seconds, double? Grams)>();
        if (entries.TryGetValue("Metadata/slice_info.config", out var sliceInfo))
        {
            ReadSliceInfo(sliceInfo, sliced);
        }

        foreach (var (index, plate) in plates)
        {
            var image = plate.Image is not null && entries.TryGetValue(plate.Image, out var listed)
                ? listed.FullName
                : Find(entries, $"Metadata/plate_{index}.png");
            var small = Find(entries, $"Metadata/plate_{index}_small.png");
            var name = string.IsNullOrWhiteSpace(plate.Name) ? null : plate.Name.Trim();
            sliced.TryGetValue(index, out var estimate);
            info.Plates.Add(new PlateInfo(index, name, image, small)
            {
                Objects = plate.Objects,
                PrintSeconds = estimate.Seconds,
                WeightGrams = estimate.Grams,
            });
        }

        info.FallbackCoverEntry = FallbackCovers.Select(c => Find(entries, c)).FirstOrDefault(c => c is not null);

        if (entries.TryGetValue("Metadata/project_settings.config", out var project))
        {
            ReadFilamentColours(project, info.FilamentColours);
        }

        info.PhotoEntries.AddRange(entries.Values
            .Where(e => e.FullName.StartsWith("Auxiliaries/Model Pictures/", StringComparison.OrdinalIgnoreCase) &&
                        PhotoExtension(e.FullName) is not null)
            .Select(e => e.FullName)
            .OrderBy(n => Path.GetFileNameWithoutExtension(n).Equals("cover", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, NaturalOrder)
            .Take(MaxPhotos));

        return info;
    }

    /// <summary>The cache file extension for a photo entry, or null when it isn't a web image.</summary>
    public static string? PhotoExtension(string entryName) => Path.GetExtension(entryName).ToLowerInvariant() switch
    {
        ".webp" => "webp",
        ".jpg" or ".jpeg" => "jpg",
        ".png" => "png",
        _ => null,
    };

    /// <summary>Copies one entry to disk, refusing entries that claim or turn out to be larger than <paramref name="maxBytes"/>.</summary>
    public static void CopyEntry(ZipArchiveEntry entry, string destination, long maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            throw new InvalidDataException($"Entry '{entry.FullName}' is larger than {maxBytes} bytes.");
        }

        using var source = entry.Open();
        using var target = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Entry '{entry.FullName}' expands beyond {maxBytes} bytes.");
            }

            target.Write(buffer, 0, read);
        }
    }

    private static string? Find(Dictionary<string, ZipArchiveEntry> entries, string name) =>
        entries.TryGetValue(name, out var entry) ? entry.FullName : null;

    private static XmlReader CreateXmlReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>
    /// Reads the model-level &lt;metadata&gt; elements and stops at &lt;resources&gt;: in a non-Bambu 3MF the mesh follows
    /// in this same entry and can be hundreds of megabytes.
    /// </summary>
    private static void ReadModelMetadata(ZipArchiveEntry entry, ThreeMfInfo info)
    {
        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName is "resources" or "build")
                {
                    return;
                }

                if (reader.LocalName == "metadata" && reader.Depth == 1)
                {
                    var key = reader.GetAttribute("name");
                    var value = reader.ReadElementContentAsString();
                    Assign(info, key, value);
                    continue;
                }
            }

            reader.Read();
        }
    }

    private static void Assign(ThreeMfInfo info, string? key, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || key is null)
        {
            return;
        }

        if (value.Length > MaxMetadataChars)
        {
            value = value[..MaxMetadataChars];
        }

        switch (key.ToLowerInvariant())
        {
            case "title":
                info.Title ??= value;
                break;
            case "designer":
                info.Designer ??= value;
                break;
            case "license":
                info.License ??= value;
                break;
            case "description":
                info.Description ??= UnescapeDescription(value);
                break;
            case "application":
                info.Application ??= value;
                break;
            case "creationdate":
                info.CreationDate ??= value;
                break;
        }
    }

    /// <summary>
    /// Bambu Studio escapes the description HTML twice, and attribute quotes once more (<c>class=&amp;#34;x&amp;#34;</c>).
    /// XML parsing undoes one level; this undoes the rest.
    /// </summary>
    private static string UnescapeDescription(string value)
    {
        if (!value.Contains("&lt;", StringComparison.Ordinal))
        {
            return value;
        }

        return WebUtility.HtmlDecode(value)
            .Replace("&#34;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
    }

    private static void ReadPlateSettings(ZipArchiveEntry entry, SortedDictionary<int, PlateSettings> plates)
    {
        if (entry.Length > MaxConfigBytes)
        {
            return;
        }

        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);

        // <config><object id="8"><metadata key="name" value="tray.stl"/>…</object>…
        //         <plate><metadata key="plater_id" value="1"/>…<model_instance><metadata key="object_id" value="8"/>
        var objectNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var plateObjectIds = new Dictionary<int, List<string>>();
        string? objectId = null;
        PlateSettings? plate = null;
        int? plateId = null;
        var objectIds = new List<string>();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
            {
                objectId = reader.LocalName == "object" ? reader.GetAttribute("id") : null;
                if (reader.LocalName == "plate" && !reader.IsEmptyElement)
                {
                    plate = new PlateSettings();
                    plateId = null;
                    objectIds = [];
                }
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "metadata")
            {
                var key = reader.GetAttribute("key");
                var value = reader.GetAttribute("value");
                if (objectId is not null && reader.Depth == 2 && key == "name" && value is { Length: > 0 })
                {
                    objectNames.TryAdd(objectId, value);
                }
                else if (plate is not null && reader.Depth == 2)
                {
                    switch (key)
                    {
                        case "plater_id" when int.TryParse(value, CultureInfo.InvariantCulture, out var parsed):
                            plateId = parsed;
                            break;
                        case "plater_name":
                            plate.Name = value;
                            break;
                        case "thumbnail_file":
                            plate.Image = value;
                            break;
                    }
                }
                else if (plate is not null && reader.Depth == 3 && key == "object_id" && value is not null)
                {
                    objectIds.Add(value);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1 && reader.LocalName == "plate" && plate is not null)
            {
                if (plateId is > 0 and < 1000)
                {
                    plates[plateId.Value] = plate;
                    plateObjectIds[plateId.Value] = objectIds;
                }

                plate = null;
            }
        }

        foreach (var (id, ids) in plateObjectIds)
        {
            plates[id].Objects = ids
                .Select(i => objectNames.GetValueOrDefault(i))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(MaxObjectsPerPlate)
                .ToList();
        }
    }

    private static void ReadSliceInfo(ZipArchiveEntry entry, Dictionary<int, (int? Seconds, double? Grams)> plates)
    {
        if (entry.Length > MaxConfigBytes)
        {
            return;
        }

        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);

        int? index = null;
        int? seconds = null;
        double? grams = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "plate" && !reader.IsEmptyElement)
            {
                (index, seconds, grams) = (null, null, null);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == 2 && reader.LocalName == "metadata")
            {
                var value = reader.GetAttribute("value");
                switch (reader.GetAttribute("key"))
                {
                    case "index" when int.TryParse(value, CultureInfo.InvariantCulture, out var i):
                        index = i;
                        break;
                    case "prediction" when int.TryParse(value, CultureInfo.InvariantCulture, out var s) && s > 0:
                        seconds = s;
                        break;
                    case "weight" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0:
                        grams = g;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1 && reader.LocalName == "plate")
            {
                if (index is not null && (seconds is not null || grams is not null))
                {
                    plates[index.Value] = (seconds, grams);
                }
            }
        }
    }

    private static void ReadFilamentColours(ZipArchiveEntry entry, List<string> colours)
    {
        if (entry.Length > MaxConfigBytes)
        {
            return;
        }

        using var stream = entry.Open();
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("filament_colour", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } colour && ColourPattern().IsMatch(colour))
            {
                var normalised = colour[..7].ToUpperInvariant();
                if (!colours.Contains(normalised))
                {
                    colours.Add(normalised);
                }

                if (colours.Count == MaxColours)
                {
                    return;
                }
            }
        }
    }

    private sealed class PlateSettings
    {
        public string? Name { get; set; }
        public string? Image { get; set; }
        public IReadOnlyList<string> Objects { get; set; } = [];
    }

    [GeneratedRegex(@"^Metadata/plate_(\d{1,3})\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlateImagePattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ColourPattern();
}
