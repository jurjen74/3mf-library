using Library.Web;
using Library.Web.Data;
using Library.Web.Indexing;
using Library.Web.Meshes;
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
        Write("Stuff/part.stl", TestMeshes.BinaryStl());
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
    public void Renders_previews_and_measures_stls_and_bare_geometry_3mfs()
    {
        Write("Parts/cube.stl", TestMeshes.BinaryStl(size: 12.5f));
        Write("Parts/geometry.3mf", TestMeshes.CoreThreeMf());
        Write("Parts/broken.stl", "not an stl at all"u8.ToArray());

        var result = _scanner.Scan(CancellationToken.None);

        var cube = _queries.FindFile("Parts/cube.stl")!;
        Assert.Equal(("render.png", "render_small.png"), (cube.Cover, cube.CoverSmall));
        Assert.Equal((12.5, 12.5, 12.5, 12L), (cube.DimX, cube.DimY, cube.DimZ, cube.Triangles));
        Assert.True(new FileInfo(Path.Combine(_options.CachePath, cube.CacheKey!, "render.png")).Length > 100);

        var geometry = _queries.FindFile("Parts/geometry.3mf")!;
        Assert.Equal("render.png", geometry.Cover);
        Assert.Equal(40, geometry.DimX);

        Assert.NotNull(_queries.FindFile("Parts/broken.stl")!.Error);
        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task Builds_and_caches_viewer_meshes_on_request()
    {
        Write("Lamp.3mf", TestMeshes.BambuThreeMf());
        Write("cube.stl", TestMeshes.BinaryStl());
        _scanner.Scan(CancellationToken.None);
        var meshes = new MeshService(_options, _queries, new LibraryPaths(_options), NullLogger<MeshService>.Instance);
        var lampKey = _queries.FindFile("Lamp.3mf")!.CacheKey!;
        var cubeKey = _queries.FindFile("cube.stl")!.CacheKey!;

        var plate = await meshes.GetAsync(lampKey, "plate_2", CancellationToken.None);
        var model = await meshes.GetAsync(cubeKey, "model", CancellationToken.None);
        var again = await meshes.GetAsync(lampKey, "plate_2", CancellationToken.None);

        Assert.True(File.Exists(plate.Path));
        Assert.True(File.Exists(model.Path));
        Assert.Equal(plate.Path, again.Path);
        Assert.Null((await meshes.GetAsync(lampKey, "../../etc", CancellationToken.None)).Path);

        // Only the meshes a file page links to: no plates for an STL, no plate the 3MF doesn't have.
        Assert.Null((await meshes.GetAsync(cubeKey, "plate_1", CancellationToken.None)).Path);
        Assert.Null((await meshes.GetAsync(lampKey, "plate_9", CancellationToken.None)).Path);
        Assert.False(File.Exists(Path.Combine(_options.CachePath, cubeKey, "mesh_plate_1.bin.gz")));
        Assert.Null((await meshes.GetAsync("0000000000000000", "model", CancellationToken.None)).Path);
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

    [Fact]
    public void Retries_a_file_that_failed_to_read_after_an_hour_even_if_unchanged()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // the read error is simulated with file permissions
        }

        Write("Lamp.3mf", TestThreeMf.Bambu("Lamp"));
        var path = Path.Combine(_options.LibraryPath, "Lamp.3mf");
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var scanner = new LibraryScanner(_options, new LibraryDb(_options), new LibraryPaths(_options),
            NullLogger<LibraryScanner>.Instance, clock: clock);

        // A read error that goes away by itself (like a timeout on the NFS mount); size and mtime stay the same.
        File.SetUnixFileMode(path, UnixFileMode.None);
        Assert.Equal(1, scanner.Scan(CancellationToken.None).Errors);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(LibraryScanner.ReadFailedError, _queries.FindFile("Lamp.3mf")!.Error); // no server path

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, scanner.Scan(CancellationToken.None).Indexed);
        Assert.NotNull(_queries.FindFile("Lamp.3mf")!.Error);

        clock.Advance(LibraryScanner.RetryFailedAfter);
        var retry = scanner.Scan(CancellationToken.None);
        var lamp = _queries.FindFile("Lamp.3mf")!;
        Assert.Equal(1, retry.Indexed);
        Assert.Null(lamp.Error);
        Assert.Equal("Lamp", lamp.Title);

        // A file that reads fine isn't read again.
        clock.Advance(LibraryScanner.RetryFailedAfter * 2);
        Assert.Equal(0, scanner.Scan(CancellationToken.None).Indexed);
    }

    [Fact]
    public void A_file_that_is_really_broken_waits_until_it_changes()
    {
        Write("Broken.3mf", "this is not a ZIP"u8.ToArray());
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var scanner = new LibraryScanner(_options, new LibraryDb(_options), new LibraryPaths(_options),
            NullLogger<LibraryScanner>.Instance, clock: clock);

        Assert.Equal(1, scanner.Scan(CancellationToken.None).Errors);
        var error = _queries.FindFile("Broken.3mf")!.Error;
        Assert.NotNull(error);
        Assert.NotEqual(LibraryScanner.ReadFailedError, error);

        clock.Advance(LibraryScanner.RetryFailedAfter * 3);
        Assert.Equal(0, scanner.Scan(CancellationToken.None).Indexed);
    }

    [Fact]
    public void Search_filters_by_type_in_the_query_and_counts_every_match()
    {
        for (var i = 1; i <= 201; i++)
        {
            Write($"arm {i}.3mf", TestThreeMf.Prusa());
        }

        Write("arm/part.stl", TestMeshes.BinaryStl()); // matches on its folder only, so it ranks below the 3MFs
        _scanner.Scan(CancellationToken.None);

        Assert.DoesNotContain(_queries.Search("arm", 201), f => f.Kind == FileKinds.Stl);
        Assert.Equal(["arm/part.stl"], _queries.Search("arm", 201, FileKinds.Stl).Select(f => f.Path));
        Assert.Equal(201, _queries.Search("arm", 500, FileKinds.ThreeMf).Count);
        Assert.Equal(new Dictionary<string, long> { [FileKinds.ThreeMf] = 201, [FileKinds.Stl] = 1 }, _queries.SearchModelCounts("arm"));
    }

    [Theory]
    [InlineData("uber")]
    [InlineData("ÜBER")]
    [InlineData("lamp")]
    public void Folder_search_ignores_case_and_accents_beyond_ascii(string term)
    {
        Write("Über Lamp/x.stl", TestMeshes.BinaryStl());
        Write("Other/y.stl", TestMeshes.BinaryStl());
        _scanner.Scan(CancellationToken.None);

        var folder = Assert.Single(_queries.SearchFolders(term, 24));
        Assert.Equal("Über Lamp", folder.Path);
        Assert.Equal(1, folder.ModelCount);
    }

    [Fact]
    public async Task A_failing_scan_is_reported_until_one_succeeds()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "");
        var options = new LibraryOptions { LibraryPath = _options.LibraryPath, DataPath = blocker }; // cache can't be created
        var scanner = new LibraryScanner(options, new LibraryDb(options), new LibraryPaths(options), NullLogger<LibraryScanner>.Instance);
        using var indexer = new IndexerService(scanner, options, NullLogger<IndexerService>.Instance);

        await indexer.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!indexer.Status.LastScanFailed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await indexer.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(indexer.Status.LastScanFailed);
        Assert.NotNull(indexer.Status.LastFailed);
        Assert.Null(indexer.Status.LastFinished);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_options.LibraryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
