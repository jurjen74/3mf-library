using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Library.Web.Data;
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
    TimeSpan Duration);

/// <summary>
/// Walks the library, re-reads files whose size or modification time changed, and keeps the index and the preview
/// cache in step. Never writes to the library itself.
/// </summary>
public sealed class LibraryScanner(LibraryOptions options, LibraryDb db, LibraryPaths paths, ILogger<LibraryScanner> log)
{
    /// <summary>Bump when extraction changes, so every file is re-read once.</summary>
    public const int IndexerVersion = 1;

    private const long MaxImageBytes = 16L * 1024 * 1024;

    private readonly IgnoreMatcher _ignore = new(options.Ignore);

    public ScanResult Scan(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // An unmounted or unreachable library must not empty the index.
        if (!Directory.Exists(options.LibraryPath))
        {
            log.LogWarning("Library folder {Path} is not available; keeping the existing index", options.LibraryPath);
            return new ScanResult(false, false, 0, 0, 0, 0, 0, stopwatch.Elapsed);
        }

        Directory.CreateDirectory(options.CachePath);
        var walk = Walk(cancellationToken);

        using var connection = db.Open();
        var existing = connection.Query<ExistingFile>(
            "SELECT id, path, size, mtime, indexer_version, cache_key FROM files")
            .ToDictionary(f => f.Path, StringComparer.Ordinal);

        int indexed = 0, errors = 0;
        foreach (var file in walk.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            existing.TryGetValue(file.Path, out var previous);
            if (previous is not null &&
                previous.Size == file.Size &&
                previous.Mtime == file.Mtime &&
                previous.IndexerVersion == IndexerVersion)
            {
                continue;
            }

            indexed++;
            if (!IndexFile(connection, file, previous))
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
                connection.Execute("DELETE FROM files WHERE id = @Id", new { gone.Id });
                DeleteCacheDirectory(gone.CacheKey);
                removed++;
            }
        }

        SyncFolders(connection, walk);

        var result = new ScanResult(true, walk.Complete, walk.Folders.Count, walk.Files.Count, indexed, removed, errors, stopwatch.Elapsed);
        log.LogInformation(
            "Scan finished in {Duration:0.0}s: {Files} files in {Folders} folders, {Indexed} (re)indexed, {Removed} removed, {Errors} errors{Partial}",
            result.Duration.TotalSeconds, result.Files, result.Folders, result.Indexed, result.Removed, result.Errors,
            result.Complete ? "" : " (partial walk: nothing removed)");
        return result;
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
                            files.Add(new WalkedFile(
                                relative,
                                directory.Relative,
                                entry.Name,
                                info.Length,
                                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
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

    private bool IndexFile(SqliteConnection connection, WalkedFile file, ExistingFile? previous)
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
            IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var plates = new List<PlateRecord>();

        if (record.Kind == FileKinds.ThreeMf)
        {
            try
            {
                ReadThreeMf(file, record, plates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One damaged or half-synced file must not stop the scan; it gets another try when it changes.
                log.LogWarning("Could not read {Path}: {Message}", file.Path, ex.Message);
                record.Error = ex.Message;
                record.CacheKey = null;
                record.Cover = record.CoverSmall = null;
                plates.Clear();
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            var id = connection.ExecuteScalar<long>("""
                INSERT INTO files (path, folder, name, kind, size, mtime, indexer_version, cache_key, title, designer,
                                   license, description, application, created, plate_count, colours, cover, cover_small,
                                   error, indexed_at)
                VALUES (@Path, @Folder, @Name, @Kind, @Size, @Mtime, @IndexerVersion, @CacheKey, @Title, @Designer,
                        @License, @Description, @Application, @Created, @PlateCount, @Colours, @Cover, @CoverSmall,
                        @Error, @IndexedAt)
                ON CONFLICT (path) DO UPDATE SET
                    folder = excluded.folder, name = excluded.name, kind = excluded.kind, size = excluded.size,
                    mtime = excluded.mtime, indexer_version = excluded.indexer_version, cache_key = excluded.cache_key,
                    title = excluded.title, designer = excluded.designer, license = excluded.license,
                    description = excluded.description, application = excluded.application, created = excluded.created,
                    plate_count = excluded.plate_count, colours = excluded.colours, cover = excluded.cover,
                    cover_small = excluded.cover_small, error = excluded.error, indexed_at = excluded.indexed_at
                RETURNING id
                """, record, transaction);

            connection.Execute("DELETE FROM plates WHERE file_id = @id", new { id }, transaction);
            foreach (var plate in plates)
            {
                connection.Execute(
                    "INSERT INTO plates (file_id, idx, name, image, image_small) VALUES (@FileId, @Index, @Name, @Image, @ImageSmall)",
                    plate with { FileId = id },
                    transaction);
            }

            transaction.Commit();
        }

        if (previous?.CacheKey is { } oldKey && oldKey != record.CacheKey)
        {
            DeleteCacheDirectory(oldKey);
        }

        return record.Error is null;
    }

    private void ReadThreeMf(WalkedFile file, FileRecord record, List<PlateRecord> plates)
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
        record.Description = info.Description;
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

                plates.Add(new PlateRecord(0, plate.Index, plate.Name, image, small));
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
        public string? Colours { get; set; }
        public string? Cover { get; set; }
        public string? CoverSmall { get; set; }
        public string? Error { get; set; }
    }

    private sealed record PlateRecord(long FileId, int Index, string? Name, string? Image, string? ImageSmall);
}
