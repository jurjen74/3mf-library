using Library.Web;
using Library.Web.Data;
using Library.Web.Indexing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Library.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryOptions _options;
    private readonly LibraryScanner _scanner;
    private readonly LibraryQueries _queries;

    public LibraryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "3mf-library-tests", Guid.NewGuid().ToString("N"));
        _options = new LibraryOptions { LibraryPath = Path.Combine(_root, "library"), DataPath = Path.Combine(_root, "data") };
        Directory.CreateDirectory(_options.LibraryPath);
        Directory.CreateDirectory(_options.DataPath);

        var db = new LibraryDb(_options);
        db.Migrate();
        _scanner = new LibraryScanner(_options, db, new LibraryPaths(_options), NullLogger<LibraryScanner>.Instance);
        _queries = new LibraryQueries(db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Indexes_folders_files_and_extracts_previews()
    {
        Write("Stuff/Lamp.3mf", TestThreeMf.Bambu("Lampshade 5.0"));
        Write("Stuff/Deep/Prusa.3mf", TestThreeMf.Prusa());
        Write("Stuff/part.stl", [1, 2, 3]);
        Write("Stuff/datapackage.json", "{}"u8.ToArray());
        Write("Stuff/@eaDir/Lamp.3mf/SYNOFILE_THUMB_M.jpg", [1]);

        var result = _scanner.Scan(CancellationToken.None);

        Assert.True(result is { LibraryOnline: true, Complete: true, Files: 3, Indexed: 3, Errors: 0 });
        Assert.Equal(["", "Stuff", "Stuff/Deep"], _queries.Folders().Select(f => f.Path).Order());

        var lamp = _queries.FindFile("Stuff/Lamp.3mf")!;
        Assert.Equal("Lampshade 5.0", lamp.Title);
        Assert.Equal(2, lamp.PlateCount);
        Assert.Equal("plate_1.png", lamp.Cover);
        Assert.Equal("plate_1_small.png", lamp.CoverSmall);
        Assert.Equal(["#161616", "#D4AF37", "#FFFFFF"], lamp.ColourList);
        Assert.True(File.Exists(Path.Combine(_options.CachePath, lamp.CacheKey!, "plate_2.png")));

        var prusa = _queries.FindFile("Stuff/Deep/Prusa.3mf")!;
        Assert.Equal("cover.png", prusa.Cover);

        Assert.Equal(FileKinds.Stl, _queries.FindFile("Stuff/part.stl")!.Kind);
        Assert.Null(_queries.FindFile("Stuff/datapackage.json"));
    }

    [Fact]
    public void Rescan_skips_unchanged_files_and_rereads_changed_ones()
    {
        Write("Lamp.3mf", TestThreeMf.Bambu("First"));
        _scanner.Scan(CancellationToken.None);
        var firstKey = _queries.FindFile("Lamp.3mf")!.CacheKey!;

        Assert.Equal(0, _scanner.Scan(CancellationToken.None).Indexed);

        Write("Lamp.3mf", TestThreeMf.Bambu("Second"));
        File.SetLastWriteTimeUtc(Path.Combine(_options.LibraryPath, "Lamp.3mf"), DateTime.UtcNow.AddMinutes(1));
        var result = _scanner.Scan(CancellationToken.None);

        var lamp = _queries.FindFile("Lamp.3mf")!;
        Assert.Equal(1, result.Indexed);
        Assert.Equal("Second", lamp.Title);
        Assert.NotEqual(firstKey, lamp.CacheKey);
        Assert.False(Directory.Exists(Path.Combine(_options.CachePath, firstKey)));
    }

    [Fact]
    public void Removes_deleted_files_and_their_previews()
    {
        Write("Old/Lamp.3mf", TestThreeMf.Bambu());
        _scanner.Scan(CancellationToken.None);
        var key = _queries.FindFile("Old/Lamp.3mf")!.CacheKey!;

        Directory.Delete(Path.Combine(_options.LibraryPath, "Old"), recursive: true);
        var result = _scanner.Scan(CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Null(_queries.FindFile("Old/Lamp.3mf"));
        Assert.DoesNotContain(_queries.Folders(), f => f.Path == "Old");
        Assert.False(Directory.Exists(Path.Combine(_options.CachePath, key)));
    }

    [Fact]
    public void Keeps_the_index_when_the_library_is_unavailable()
    {
        Write("Lamp.3mf", TestThreeMf.Bambu());
        _scanner.Scan(CancellationToken.None);

        Directory.Move(_options.LibraryPath, _options.LibraryPath + "-unmounted");
        var result = _scanner.Scan(CancellationToken.None);

        Assert.False(result.LibraryOnline);
        Assert.NotNull(_queries.FindFile("Lamp.3mf"));
    }

    [Fact]
    public void Records_an_error_for_a_damaged_3mf_and_keeps_going()
    {
        Write("Broken.3mf", "half-synced garbage"u8.ToArray());
        Write("Good.3mf", TestThreeMf.Bambu());

        var result = _scanner.Scan(CancellationToken.None);

        Assert.Equal(1, result.Errors);
        var broken = _queries.FindFile("Broken.3mf")!;
        Assert.NotNull(broken.Error);
        Assert.Null(broken.Cover);
        Assert.NotNull(_queries.FindFile("Good.3mf")!.Cover);
        Assert.Equal(1, _queries.Stats().Errors);
    }

    [Fact]
    public void Extracts_photos_plate_details_and_a_clean_description()
    {
        Write("Lamp.3mf", TestThreeMf.Bambu());
        _scanner.Scan(CancellationToken.None);

        var lamp = _queries.FindFile("Lamp.3mf")!;
        Assert.Equal(3, lamp.PhotoCount);
        var photos = _queries.Photos(lamp.Id);
        Assert.Equal(["photo_1.webp", "photo_2.jpg", "photo_3.webp"], photos.Select(p => p.Image));
        Assert.All(photos, p => Assert.True(File.Exists(Path.Combine(_options.CachePath, lamp.CacheKey!, p.Image))));

        var plate = _queries.Plates(lamp.Id)[0];
        Assert.Equal(["box.stl", "lid.stl"], plate.ObjectList);
        Assert.Equal(1843, plate.PrintSeconds);

        Assert.Equal("<p>Holds &amp; dispenses</p>", _queries.Description(lamp.Id));
    }

    [Theory]
    [InlineData("lamp", "Stuff/Lampshade.3mf")]
    [InlineData("LAMPSH", "Stuff/Lampshade.3mf")]
    [InlineData("razor", "Stuff/Lampshade.3mf")]
    [InlineData("thibault", "Stuff/Lampshade.3mf")]
    [InlineData("box", "Stuff/Lampshade.3mf")]
    [InlineData("stuff", "Stuff/Lampshade.3mf")]
    [InlineData("kintsu vase", "Stuff/Kintsu_Shizu_Vase.3mf")]
    [InlineData("dispenses", "Stuff/Lampshade.3mf")]
    public void Search_matches_names_titles_designers_objects_and_folders(string query, string expected)
    {
        Write("Stuff/Lampshade.3mf", TestThreeMf.Bambu("Razor Blade Dispenser"));
        Write("Stuff/Kintsu_Shizu_Vase.3mf", TestThreeMf.Prusa());
        _scanner.Scan(CancellationToken.None);

        Assert.Contains(expected, _queries.Search(query, 10).Select(f => f.Path));
    }

    [Fact]
    public void Search_forgets_deleted_files()
    {
        Write("Lampshade.3mf", TestThreeMf.Bambu());
        _scanner.Scan(CancellationToken.None);
        File.Delete(Path.Combine(_options.LibraryPath, "Lampshade.3mf"));
        _scanner.Scan(CancellationToken.None);

        Assert.Empty(_queries.Search("lampshade", 10));
    }

    [Fact]
    public async Task Defers_a_file_that_is_still_being_written()
    {
        var settling = new LibraryScanner(
            new LibraryOptions { LibraryPath = _options.LibraryPath, DataPath = _options.DataPath, SettleDelay = TimeSpan.FromMilliseconds(400) },
            new LibraryDb(_options), new LibraryPaths(_options), NullLogger<LibraryScanner>.Instance);
        Write("Done.3mf", TestThreeMf.Bambu());
        Write("Syncing.3mf", TestThreeMf.Bambu()[..100]);

        var writer = Task.Run(async () =>
        {
            await Task.Delay(100);
            await File.AppendAllTextAsync(Path.Combine(_options.LibraryPath, "Syncing.3mf"), "more bytes");
        });
        var result = settling.Scan(CancellationToken.None);
        await writer;

        Assert.Equal(1, result.Deferred);
        Assert.NotNull(_queries.FindFile("Done.3mf"));
        Assert.Null(_queries.FindFile("Syncing.3mf"));
    }

    [Fact]
    public void Removes_cache_folders_nothing_points_to()
    {
        var stray = Path.Combine(_options.CachePath, "0123456789abcdef");
        Directory.CreateDirectory(stray);
        Write("Lamp.3mf", TestThreeMf.Bambu());

        _scanner.Scan(CancellationToken.None);

        Assert.False(Directory.Exists(stray));
        Assert.True(Directory.Exists(Path.Combine(_options.CachePath, _queries.FindFile("Lamp.3mf")!.CacheKey!)));
    }

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_options.LibraryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
