using Dapper;

namespace Library.Web.Data;

/// <summary>Read-side queries for the web pages.</summary>
public sealed class LibraryQueries(LibraryDb db)
{
    private const string FileColumns = """
        id, path, folder, name, kind, size, mtime, cache_key, title, designer, license,
        plate_count, colours, cover, cover_small, error
        """;

    public IReadOnlyList<FolderRow> Folders()
    {
        using var connection = db.Open();
        return connection.Query<FolderRow>("""
            SELECT f.path, f.parent, f.name,
                   (SELECT COUNT(*) FROM files x WHERE x.folder = f.path AND x.kind IN ('3mf', 'stl')) AS model_count
            FROM folders f
            """).AsList();
    }

    public IReadOnlyList<FileRow> FilesIn(string folder)
    {
        using var connection = db.Open();
        return connection.Query<FileRow>($"SELECT {FileColumns} FROM files WHERE folder = @folder", new { folder }).AsList();
    }

    public FileRow? FindFile(string path)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<FileRow>($"SELECT {FileColumns} FROM files WHERE path = @path", new { path });
    }

    /// <summary>Up to <paramref name="limit"/> small covers from a folder and everything below it, for the folder tile.</summary>
    public IReadOnlyList<CoverImage> CoversUnder(string folder, int limit)
    {
        using var connection = db.Open();
        return connection.Query<CoverImage>("""
            SELECT cache_key, cover_small FROM files
            WHERE cover_small IS NOT NULL
              AND (folder = @folder OR substr(folder, 1, length(@folder) + 1) = @folder || '/')
            ORDER BY folder, name
            LIMIT @limit
            """, new { folder, limit }).AsList();
    }

    public LibraryStats Stats()
    {
        using var connection = db.Open();
        return connection.QuerySingle<LibraryStats>("""
            SELECT COALESCE(SUM(kind IN ('3mf', 'stl')), 0)     AS Models,
                   COALESCE(SUM(kind = '3mf'), 0)               AS ThreeMf,
                   COALESCE(SUM(kind = 'stl'), 0)               AS Stl,
                   COALESCE(SUM(kind NOT IN ('3mf', 'stl')), 0) AS Other,
                   COALESCE(SUM(error IS NOT NULL), 0)          AS Errors
            FROM files
            """);
    }
}
