using System.IO.Compression;
using Library.Web.Indexing;

namespace Library.Tests;

public class ThreeMfReaderTests
{
    [Fact]
    public void Reads_model_metadata_and_stops_at_resources()
    {
        using var zip = TestThreeMf.Open(TestThreeMf.Bambu());

        var info = ThreeMfReader.Read(zip);

        Assert.Equal("Razor Blade Dispenser", info.Title);
        Assert.Equal("Thibault01", info.Designer);
        Assert.Equal("MakerWorld Exclusive License", info.License);
        Assert.Equal("BambuStudio-02.08.02.61", info.Application);
        Assert.Equal("2026-08-31", info.CreationDate);
    }

    [Fact]
    public void Undoes_bambu_double_escaped_description()
    {
        using var zip = TestThreeMf.Open(TestThreeMf.Bambu());

        var info = ThreeMfReader.Read(zip);

        Assert.Equal("<p>Holds &amp; dispenses</p>", info.Description);
    }

    [Fact]
    public void Reads_plates_with_names_and_preview_entries()
    {
        using var zip = TestThreeMf.Open(TestThreeMf.Bambu());

        var info = ThreeMfReader.Read(zip);

        Assert.Collection(info.Plates,
            p => Assert.Equal(new PlateInfo(1, "Body", "Metadata/plate_1.png", "Metadata/plate_1_small.png"), p),
            p => Assert.Equal(new PlateInfo(2, null, "Metadata/plate_2.png", null), p));
    }

    [Fact]
    public void Reads_distinct_valid_filament_colours()
    {
        using var zip = TestThreeMf.Open(TestThreeMf.Bambu());

        var info = ThreeMfReader.Read(zip);

        Assert.Equal(["#161616", "#D4AF37", "#FFFFFF"], info.FilamentColours);
    }

    [Fact]
    public void Finds_plate_images_without_model_settings()
    {
        var bytes = TestThreeMf.Build(new Dictionary<string, object>
        {
            ["Metadata/plate_3.png"] = TestThreeMf.FakePng,
            ["Metadata/plate_10.png"] = TestThreeMf.FakePng,
        });
        using var zip = TestThreeMf.Open(bytes);

        var info = ThreeMfReader.Read(zip);

        Assert.Equal([3, 10], info.Plates.Select(p => p.Index));
    }

    [Fact]
    public void Falls_back_to_project_thumbnail_for_other_slicers()
    {
        using var zip = TestThreeMf.Open(TestThreeMf.Prusa());

        var info = ThreeMfReader.Read(zip);

        Assert.Empty(info.Plates);
        Assert.Equal("Metadata/thumbnail.png", info.FallbackCoverEntry);
        Assert.Equal("PrusaSlicer-2.9.0", info.Application);
        Assert.Null(info.Title);
    }

    [Fact]
    public void Copy_entry_refuses_oversized_entries()
    {
        var bytes = TestThreeMf.Build(new Dictionary<string, object> { ["big.bin"] = new byte[4096] });
        using var zip = TestThreeMf.Open(bytes);
        var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidDataException>(() => ThreeMfReader.CopyEntry(zip.GetEntry("big.bin")!, target, maxBytes: 1024));

        File.Delete(target);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_zip()
    {
        Assert.Throws<InvalidDataException>(() => new ZipArchive(new MemoryStream("not a zip"u8.ToArray()), ZipArchiveMode.Read));
    }
}
