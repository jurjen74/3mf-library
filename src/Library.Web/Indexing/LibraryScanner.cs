using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Library.Web.Data;
using Library.Web.Meshes;
using Microsoft.Data.Sqlite;

namespace Library.Web.Indexing;

public sealed record ScanResult(
    bool LibraryOnline,
    bool Complete,
    int Folders,
    int Files,
    int Indexed,
    int Removed,
    int Errors,
    int Deferred,
    TimeSpan Duration)
{
    public static ScanResult Offline(TimeSpan duration) => new(false, false, 0, 0, 0, 0, 0, 0, duration);
}

/// <summary>
/// Walks the library, re-reads files whose size or modification time changed, and keeps the index, the search index
/// and the preview cache in step. Never writes to the library itself.
/// </summary>
public sealed class LibraryScanner(
    LibraryOptions options,
    LibraryDb db,
    LibraryPaths paths,
    ILogger<LibraryScanner> log,
    MeshWorkGate? workGate = null,
    TimeProvider? clock = null)
{
    /// <summary>Bump when extraction changes, so every file is re-read once.</summary>
    public const int IndexerVersion = 4;

    private const long MaxImageBytes = 16L * 1024 * 1024;
    private const long MaxStlBytes = 1024L * 1024 * 1024;
    private const int PreviewSize = 512;

    /// <summary>
    /// A file that couldn't be read is tried again after this long even when it hasn't changed: the NFS mount is
    /// "soft" (a timeout is an I/O error) and Cloud Sync can pause half-way through a file for longer than the settle
    /// delay. A file that is really broken costs one read per interval.
    /// </summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Stored (and shown) instead of the exception text when the library folder didn't deliver a file: an I/O or
    /// permission error, which says nothing about the file itself. Only these are retried after
    /// <see cref="RetryFailedAfter"/>; a file that is really broken (not a ZIP, over a limit) waits until it changes.
    /// The details, server paths included, go to the log only.
    /// </summary>
    public const string ReadFailedError =
        "The library folder didn't deliver the file (a network or permission problem). It's tried again within an hour.";

    private static string ErrorText(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ? ReadFailedError : ex.Message;

    private readonly IgnoreMatcher _ignore = new(options.Ignore);
    private readonly DescriptionSanitizer _sanitizer = new();
    private readonly Lock _gate = new();
    private readonly MeshWorkGate _work = workGate ?? new MeshWorkGate();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>One scan at a time: two at once would replace each other's preview folders.</summary>
    public ScanResult Scan(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ScanOnce(cancellationToken);
        }
    }

    private ScanResult ScanOnce(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // An unmounted or unreachable library must not empty the index.
        if (!Directory.Exists(options.LibraryPath))
        {
            log.LogWarning("Library folder {Path} is not available; keeping the existing index", options.LibraryPath);
            return ScanResult.Offline(stopwatch.Elapsed);
        }

        Directory.CreateDirectory(options.CachePath);
        var walk = Walk(cancellationToken);

        using var connection = db.Open();
        var existing = connection.Query<ExistingFile>(
            "SELECT id, path, size, mtime, indexer_version, cache_key, error = @ReadFailedError AS read_failed_flag, indexed_at FROM files",
            new { ReadFailedError })
            .ToDictionary(f => f.Path, StringComparer.Ordinal);

        var retryBefore = _clock.GetUtcNow().Subtract(RetryFailedAfter).ToUnixTimeMilliseconds();
        var changed = walk.Files.Where(file =>
                !existing.TryGetValue(file.Path, out var previous) ||
                previous.Size != file.Size ||
                previous.Mtime != file.Mtime ||
                previous.IndexerVersion != IndexerVersion ||
                (previous.ReadFailed && previous.IndexedAt <= retryBefore))
            .ToList();

        var (settled, deferred) = Settle(changed, cancellationToken);

        int indexed = 0, errors = 0;
        foreach (var file in settled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            indexed++;
            if (!IndexFile(connection, file, existing.GetValueOrDefault(file.Path), cancellationToken))
            {
                errors++;
            }
        }

        var removed = 0;
        if (walk.Complete)
        {
            var seen = walk.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in existing.Values.Where(f => !seen.Contains(f.Path)))
            {
                using var transaction = connection.BeginTransaction();
                connection.Execute("DELETE FROM files_fts WHERE rowid = @Id", new { gone.Id }, transaction);
                connection.Execute("DELETE FROM files WHERE id = @Id", new { gone.Id }, transaction);
                transaction.Commit();
                DeleteCacheDirectory(gone.CacheKey);
                removed++;
            }
        }

        SyncFolders(connection, walk);
        if (walk.Complete)
        {
            RemoveOrphanedCache(connection);
        }

        var result = new ScanResult(true, walk.Complete, walk.Folders.Count, walk.Files.Count, indexed, removed, errors,
            deferred, stopwatch.Elapsed);
        log.LogInformation(
            "Scan finished in {Duration:0.0}s: {Files} files in {Folders} folders, {Indexed} (re)indexed, {Removed} removed, {Errors} errors, {Deferred} still being written{Partial}",
            result.Duration.TotalSeconds, result.Files, result.Folders, result.Indexed, result.Removed, result.Errors,
            result.Deferred, result.Complete ? "" : " (partial walk: nothing removed)");
        return result;
    }

    /// <summary>
    /// Cloud Sync writes files in parts and may keep the original modification time, so a new or changed file is only
    /// read once its size and time stay the same over <see cref="LibraryOptions.SettleDelay"/>. The rest waits for the
    /// next scan.
    /// </summary>
    private (List<WalkedFile> Settled, int Deferred) Settle(List<WalkedFile> changed, CancellationToken cancellationToken)
    {
        if (changed.Count == 0 || options.SettleDelay <= TimeSpan.Zero)
        {
            return (changed, 0);
        }

        cancellationToken.WaitHandle.WaitOne(options.SettleDelay);
        cancellationToken.ThrowIfCancellationRequested();

        var settled = new List<WalkedFile>(changed.Count);
        foreach (var file in changed)
        {
            if (paths.TryGetFullPath(file.Path, out var fullPath))
            {
                var info = new FileInfo(fullPath);
                if (info.Exists && info.Length == file.Size && ToUnixMs(info.LastWriteTimeUtc) == file.Mtime)
                {
                    settled.Add(file);
                }
            }
        }

        return (settled, changed.Count - settled.Count);
    }

    private WalkResult Walk(CancellationToken cancellationToken)
    {
        var folders = new List<WalkedFolder> { new("", null, "") };
        var files = new List<WalkedFile>();
        var complete = true;
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((options.LibraryPath, ""));

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory.Full).EnumerateFileSystemInfos("*", enumeration))
                {
                    if (_ignore.IsIgnored(entry.Name))
                    {
                        continue;
                    }

                    var relative = directory.Relative.Length == 0 ? entry.Name : $"{directory.Relative}/{entry.Name}";
                    switch (entry)
                    {
                        case DirectoryInfo:
                            folders.Add(new WalkedFolder(relative, directory.Relative, entry.Name));
                            pending.Push((entry.FullName, relative));
                            break;
                        case FileInfo info:
                            files.Add(new WalkedFile(relative, directory.Relative, entry.Name, info.Length, ToUnixMs(info.LastWriteTimeUtc)));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                log.LogWarning(ex, "Could not list {Folder}", directory.Full);
            }
        }

        return new WalkResult(folders, files, complete);
    }

    private bool IndexFile(SqliteConnection connection, WalkedFile file, ExistingFile? previous, CancellationToken cancellationToken)
    {
        var record = new FileRecord
        {
            Path = file.Path,
            Folder = file.Folder,
            Name = file.Name,
            Kind = FileKinds.FromName(file.Name),
            Size = file.Size,
            Mtime = file.Mtime,
            IndexerVersion = IndexerVersion,
            IndexedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        var plates = new List<PlateRecord>();
        var photos = new List<string>();

        if (record.Kind == FileKinds.ThreeMf)
        {
            try
            {
                ReadThreeMf(file, record, plates, photos, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One damaged or half-synced file must not stop the scan; it gets another try when it changes, or
                // after RetryFailedAfter when the folder didn't deliver it.
                log.LogWarning("Could not read {Path}: {Message}", file.Path, ex.Message);
                record.Error = ErrorText(ex);
                record.CacheKey = null;
                record.Cover = record.CoverSmall = null;
                record.PlateCount = record.PhotoCount = 0;
                plates.Clear();
                photos.Clear();
            }
        }
        else if (record.Kind == FileKinds.Stl)
        {
            try
            {
                ReadStl(file, record, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not read {Path}: {Message}", file.Path, ex.Message);
                record.Error = ErrorText(ex);
                record.CacheKey = null;
                record.Cover = record.CoverSmall = null;
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            var id = connection.ExecuteScalar<long>("""
                INSERT INTO files (path, folder, name, kind, size, mtime, indexer_version, cache_key, title, designer,
                                   license, description, application, created, plate_count, photo_count, colours, cover,
                                   cover_small, dim_x, dim_y, dim_z, triangles, error, indexed_at)
                VALUES (@Path, @Folder, @Name, @Kind, @Size, @Mtime, @IndexerVersion, @CacheKey, @Title, @Designer,
                        @License, @Description, @Application, @Created, @PlateCount, @PhotoCount, @Colours, @Cover,
                        @CoverSmall, @DimX, @DimY, @DimZ, @Triangles, @Error, @IndexedAt)
                ON CONFLICT (path) DO UPDATE SET
                    folder = excluded.folder, name = excluded.name, kind = excluded.kind, size = excluded.size,
                    mtime = excluded.mtime, indexer_version = excluded.indexer_version, cache_key = excluded.cache_key,
                    title = excluded.title, designer = excluded.designer, license = excluded.license,
                    description = excluded.description, application = excluded.application, created = excluded.created,
                    plate_count = excluded.plate_count, photo_count = excluded.photo_count, colours = excluded.colours,
                    cover = excluded.cover, cover_small = excluded.cover_small, dim_x = excluded.dim_x,
                    dim_y = excluded.dim_y, dim_z = excluded.dim_z, triangles = excluded.triangles, error = excluded.error,
                    indexed_at = excluded.indexed_at
                RETURNING id
                """, record, transaction);

            connection.Execute("DELETE FROM plates WHERE file_id = @id", new { id }, transaction);
            foreach (var plate in plates)
            {
                connection.Execute("""
                    INSERT INTO plates (file_id, idx, name, image, image_small, objects, print_seconds, weight_grams)
                    VALUES (@FileId, @Index, @Name, @Image, @ImageSmall, @Objects, @PrintSeconds, @WeightGrams)
                    """, plate with { FileId = id }, transaction);
            }

            connection.Execute("DELETE FROM photos WHERE file_id = @id", new { id }, transaction);
            for (var i = 0; i < photos.Count; i++)
            {
                connection.Execute("INSERT INTO photos (file_id, idx, image) VALUES (@id, @idx, @image)",
                    new { id, idx = i + 1, image = photos[i] }, transaction);
            }

            connection.Execute("DELETE FROM files_fts WHERE rowid = @id", new { id }, transaction);
            connection.Execute(
                "INSERT INTO files_fts (rowid, name, title, designer, folder, objects, description) VALUES (@id, @name, @title, @designer, @folder, @objects, @description)",
                new
                {
                    id,
                    name = Path.GetFileNameWithoutExtension(record.Name),
                    title = record.Title,
                    designer = record.Designer,
                    folder = record.Folder,
                    objects = string.Join(' ', plates.SelectMany(p => p.ObjectList).Distinct(StringComparer.Ordinal)),
                    description = DescriptionSanitizer.ToPlainText(record.Description),
                },
                transaction);

            transaction.Commit();
        }

        if (previous?.CacheKey is { } oldKey && oldKey != record.CacheKey)
        {
            DeleteCacheDirectory(oldKey);
        }

        return record.Error is null;
    }

    private void ReadThreeMf(WalkedFile file, FileRecord record, List<PlateRecord> plates, List<string> photos, CancellationToken cancellationToken)
    {
        if (!paths.TryGetFullPath(file.Path, out var fullPath))
        {
            throw new IOException("Path is outside the library.");
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, FileOptions.RandomAccess);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var info = ThreeMfReader.Read(zip);

        record.Title = info.Title;
        record.Designer = info.Designer;
        record.License = info.License;
        record.Description = _sanitizer.Sanitize(info.Description);
        record.Application = info.Application;
        record.Created = info.CreationDate;
        record.PlateCount = info.Plates.Count;
        record.Colours = info.FilamentColours.Count > 0 ? JsonSerializer.Serialize(info.FilamentColours) : null;

        var key = CacheKey(file);
        var staging = Path.Combine(options.CachePath, $"{key}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var plate in info.Plates)
            {
                string? image = null, small = null;
                if (plate.ImageEntry is not null)
                {
                    image = $"plate_{plate.Index}.png";
                    ThreeMfReader.CopyEntry(zip.GetEntry(plate.ImageEntry)!, Path.Combine(staging, image), MaxImageBytes);
                }

                if (plate.SmallImageEntry is not null)
                {
                    small = $"plate_{plate.Index}_small.png";
                    ThreeMfReader.CopyEntry(zip.GetEntry(plate.SmallImageEntry)!, Path.Combine(staging, small), MaxImageBytes);
                }

                plates.Add(new PlateRecord(0, plate.Index, plate.Name, image, small,
                    plate.Objects.Count > 0 ? JsonSerializer.Serialize(plate.Objects) : null,
                    plate.PrintSeconds, plate.WeightGrams)
                {
                    ObjectList = plate.Objects,
                });

                if (record.Cover is null && image is not null)
                {
                    record.Cover = image;
                    record.CoverSmall = small ?? image;
                }
            }

            if (record.Cover is null && info.FallbackCoverEntry is not null)
            {
                record.Cover = record.CoverSmall = "cover.png";
                ThreeMfReader.CopyEntry(zip.GetEntry(info.FallbackCoverEntry)!, Path.Combine(staging, "cover.png"), MaxImageBytes);
            }

            foreach (var entryName in info.PhotoEntries)
            {
                var photo = $"photo_{photos.Count + 1}.{ThreeMfReader.PhotoExtension(entryName)}";
                ThreeMfReader.CopyEntry(zip.GetEntry(entryName)!, Path.Combine(staging, photo), MaxImageBytes);
                photos.Add(photo);
            }

            record.PhotoCount = photos.Count;

            // No image anywhere in the file (a bare geometry export): draw one from the meshes.
            if (record.Cover is null)
            {
                TryRenderGeometry(zip, staging, file, record, cancellationToken);
            }

            var target = Path.Combine(options.CachePath, key);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            record.CacheKey = key;
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private void ReadStl(WalkedFile file, FileRecord record, CancellationToken cancellationToken)
    {
        if (!paths.TryGetFullPath(file.Path, out var fullPath))
        {
            throw new IOException("Path is outside the library.");
        }

        if (file.Size > MaxStlBytes)
        {
            throw new InvalidDataException($"STL is larger than {MaxStlBytes / (1024 * 1024)} MB.");
        }

        var key = CacheKey(file);
        var staging = Path.Combine(options.CachePath, $"{key}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using (_work.Enter(cancellationToken))
            {
                WritePreview(StlReader.Read(fullPath), staging, record);
            }

            var target = Path.Combine(options.CachePath, key);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            record.CacheKey = key;
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private void TryRenderGeometry(ZipArchive zip, string staging, WalkedFile file, FileRecord record, CancellationToken cancellationToken)
    {
        try
        {
            using (_work.Enter(cancellationToken))
            {
                var mesh = ThreeMfMeshReader.Read(zip, plate: null);
                if (mesh.TriangleCount > 0)
                {
                    WritePreview(mesh, staging, record);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not render {Path}: {Message}", file.Path, ex.Message);
        }
    }

    /// <summary>Renders <c>render.png</c> and <c>render_small.png</c> and records the model's size.</summary>
    private static void WritePreview(Mesh mesh, string directory, FileRecord record)
    {
        var rgba = MeshRenderer.Render(mesh, PreviewSize);
        PngWriter.Write(Path.Combine(directory, "render.png"), rgba, PreviewSize, PreviewSize);
        PngWriter.Write(Path.Combine(directory, "render_small.png"), PngWriter.Downscale(rgba, PreviewSize, 4), PreviewSize / 4, PreviewSize / 4);

        var size = mesh.Size();
        record.Cover = "render.png";
        record.CoverSmall = "render_small.png";
        record.DimX = Math.Round(size.X, 2);
        record.DimY = Math.Round(size.Y, 2);
        record.DimZ = Math.Round(size.Z, 2);
        record.Triangles = mesh.TriangleCount;
    }

    private void SyncFolders(SqliteConnection connection, WalkResult walk)
    {
        using var transaction = connection.BeginTransaction();
        if (walk.Complete)
        {
            connection.Execute("DELETE FROM folders", transaction: transaction);
        }

        foreach (var folder in walk.Folders)
        {
            connection.Execute(
                "INSERT OR REPLACE INTO folders (path, parent, name) VALUES (@Path, @Parent, @Name)",
                folder,
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>Removes cache folders no file points to any more (after an index reset or an interrupted scan).</summary>
    private void RemoveOrphanedCache(SqliteConnection connection)
    {
        var used = connection.Query<string>("SELECT cache_key FROM files WHERE cache_key IS NOT NULL")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(options.CachePath))
        {
            if (!used.Contains(Path.GetFileName(directory)))
            {
                DeleteCacheDirectory(Path.GetFileName(directory));
            }
        }
    }

    private void DeleteCacheDirectory(string? key)
    {
        if (key is null)
        {
            return;
        }

        var directory = Path.Combine(options.CachePath, key);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not remove cache folder {Folder}", directory);
        }
    }

    /// <summary>Changes whenever the file or the extraction changes, so cached previews can be served as immutable.</summary>
    private static string CacheKey(WalkedFile file)
    {
        var input = Encoding.UTF8.GetBytes($"{file.Path}\n{file.Size}\n{file.Mtime}\n{IndexerVersion}");
        return Convert.ToHexStringLower(SHA256.HashData(input))[..16];
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private sealed record WalkedFolder(string Path, string? Parent, string Name);

    private sealed record WalkedFile(string Path, string Folder, string Name, long Size, long Mtime);

    private sealed record WalkResult(List<WalkedFolder> Folders, List<WalkedFile> Files, bool Complete);

    private sealed class ExistingFile
    {
        public long Id { get; init; }
        public string Path { get; init; } = "";
        public long Size { get; init; }
        public long Mtime { get; init; }
        public long IndexerVersion { get; init; }
        public string? CacheKey { get; init; }
        /// <summary>1 when the last read failed with <see cref="ReadFailedError"/> (SQLite: NULL when it didn't fail).</summary>
        public long? ReadFailedFlag { get; init; }
        public long IndexedAt { get; init; }

        public bool ReadFailed => ReadFailedFlag == 1;
    }

    private sealed class FileRecord
    {
        public required string Path { get; init; }
        public required string Folder { get; init; }
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public long Size { get; init; }
        public long Mtime { get; init; }
        public int IndexerVersion { get; init; }
        public long IndexedAt { get; init; }
        public string? CacheKey { get; set; }
        public string? Title { get; set; }
        public string? Designer { get; set; }
        public string? License { get; set; }
        public string? Description { get; set; }
        public string? Application { get; set; }
        public string? Created { get; set; }
        public int PlateCount { get; set; }
        public int PhotoCount { get; set; }
        public string? Colours { get; set; }
        public string? Cover { get; set; }
        public string? CoverSmall { get; set; }
        public double? DimX { get; set; }
        public double? DimY { get; set; }
        public double? DimZ { get; set; }
        public long? Triangles { get; set; }
        public string? Error { get; set; }
    }

    private sealed record PlateRecord(
        long FileId,
        int Index,
        string? Name,
        string? Image,
        string? ImageSmall,
        string? Objects,
        int? PrintSeconds,
        double? WeightGrams)
    {
        public IReadOnlyList<string> ObjectList { get; init; } = [];
    }
}
