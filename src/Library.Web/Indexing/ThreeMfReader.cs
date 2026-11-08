using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Library.Web.Indexing;

/// <summary>One build plate of a 3MF project and the preview images the slicer stored for it.</summary>
public sealed record PlateInfo(int Index, string? Name, string? ImageEntry, string? SmallImageEntry);

/// <summary>What the indexer needs from a 3MF file. Read from a handful of small entries; geometry is never unpacked.</summary>
public sealed class ThreeMfInfo
{
    public string? Title { get; set; }
    public string? Designer { get; set; }
    public string? License { get; set; }
    public string? Description { get; set; }
    public string? Application { get; set; }
    public string? CreationDate { get; set; }
    public List<PlateInfo> Plates { get; } = [];

    /// <summary>Project-level preview for files without plate images (PrusaSlicer, OrcaSlicer, generic 3MF).</summary>
    public string? FallbackCoverEntry { get; set; }

    public List<string> FilamentColours { get; } = [];
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

    private static readonly string[] FallbackCovers =
    [
        "Metadata/thumbnail.png",
        "Auxiliaries/.thumbnails/thumbnail_middle.png",
        "Auxiliaries/.thumbnails/thumbnail_3mf.png",
    ];

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

        // Plates come from the slicer's plate list (names, thumbnail paths) plus any plate_N.png not listed there.
        var plates = new SortedDictionary<int, (string? Name, string? Image)>();
        if (entries.TryGetValue("Metadata/model_settings.config", out var settings))
        {
            ReadPlateSettings(settings, plates);
        }

        foreach (var name in entries.Keys)
        {
            var match = PlateImagePattern().Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var index))
            {
                if (!plates.TryGetValue(index, out var plate) || plate.Image is null)
                {
                    plates[index] = (plate.Name, name);
                }
            }
        }

        foreach (var (index, plate) in plates)
        {
            var image = plate.Image is not null && entries.TryGetValue(plate.Image, out var listed)
                ? listed.FullName
                : Find(entries, $"Metadata/plate_{index}.png");
            var small = Find(entries, $"Metadata/plate_{index}_small.png");
            var name = string.IsNullOrWhiteSpace(plate.Name) ? null : plate.Name.Trim();
            info.Plates.Add(new PlateInfo(index, name, image, small));
        }

        info.FallbackCoverEntry = FallbackCovers.Select(c => Find(entries, c)).FirstOrDefault(c => c is not null);

        if (entries.TryGetValue("Metadata/project_settings.config", out var project))
        {
            ReadFilamentColours(project, info.FilamentColours);
        }

        return info;
    }

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
                // Bambu Studio escapes the description HTML twice; XML parsing undoes one level.
                info.Description ??= value.Contains("&lt;", StringComparison.Ordinal) ? WebUtility.HtmlDecode(value) : value;
                break;
            case "application":
                info.Application ??= value;
                break;
            case "creationdate":
                info.CreationDate ??= value;
                break;
        }
    }

    private static void ReadPlateSettings(ZipArchiveEntry entry, SortedDictionary<int, (string? Name, string? Image)> plates)
    {
        if (entry.Length > MaxConfigBytes)
        {
            return;
        }

        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);

        int? id = null;
        string? name = null;
        string? image = null;
        var inPlate = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "plate" && reader.Depth == 1)
            {
                inPlate = !reader.IsEmptyElement;
                (id, name, image) = (null, null, null);
            }
            else if (inPlate && reader.NodeType == XmlNodeType.Element && reader.LocalName == "metadata" && reader.Depth == 2)
            {
                var value = reader.GetAttribute("value");
                switch (reader.GetAttribute("key"))
                {
                    case "plater_id" when int.TryParse(value, CultureInfo.InvariantCulture, out var parsed):
                        id = parsed;
                        break;
                    case "plater_name":
                        name = value;
                        break;
                    case "thumbnail_file":
                        image = value;
                        break;
                }
            }
            else if (inPlate && reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "plate" && reader.Depth == 1)
            {
                inPlate = false;
                if (id is > 0 and < 1000)
                {
                    plates[id.Value] = (name, image);
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

    [GeneratedRegex(@"^Metadata/plate_(\d{1,3})\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlateImagePattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ColourPattern();
}
