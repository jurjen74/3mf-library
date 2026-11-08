using System.Text;
using Dapper;

namespace Library.Web.Data;

/// <summary>Read-side queries for the web pages.</summary>
public sealed class LibraryQueries(LibraryDb db)
{
    private const string FileColumns = """
        f.id, f.path, f.folder, f.name, f.kind, f.size, f.mtime, f.cache_key, f.title, f.designer, f.license,
        f.application, f.created, f.plate_count, f.photo_count, f.colours, f.cover, f.cover_small, f.error,
        f.dim_x, f.dim_y, f.dim_z, f.triangles
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
        return connection.Query<FileRow>($"SELECT {FileColumns} FROM files f WHERE f.folder = @folder", new { folder }).AsList();
    }

    public FileRow? FindFile(string path)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<FileRow>($"SELECT {FileColumns} FROM files f WHERE f.path = @path", new { path });
    }

    public FileRow? FindByCacheKey(string cacheKey)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<FileRow>($"SELECT {FileColumns} FROM files f WHERE f.cache_key = @cacheKey", new { cacheKey });
    }

    /// <summary>The sanitised description HTML, produced by the indexer.</summary>
    public string? Description(long fileId)
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<string?>("SELECT description FROM files WHERE id = @fileId", new { fileId });
    }

    public IReadOnlyList<PlateRow> Plates(long fileId)
    {
        using var connection = db.Open();
        return connection.Query<PlateRow>(
            "SELECT idx, name, image, image_small, objects, print_seconds, weight_grams FROM plates WHERE file_id = @fileId ORDER BY idx",
            new { fileId }).AsList();
    }

    public IReadOnlyList<PhotoRow> Photos(long fileId)
    {
        using var connection = db.Open();
        return connection.Query<PhotoRow>("SELECT idx, image FROM photos WHERE file_id = @fileId ORDER BY idx", new { fileId }).AsList();
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

    /// <summary>Models whose name, title, designer, folder or object names match every word of <paramref name="text"/> (prefix match).</summary>
    public IReadOnlyList<FileRow> Search(string text, int limit)
    {
        var match = ToFtsQuery(text);
        if (match is null)
        {
            return [];
        }

        using var connection = db.Open();
        return connection.Query<FileRow>($"""
            SELECT {FileColumns}
            FROM files_fts
            JOIN files f ON f.id = files_fts.rowid
            WHERE files_fts MATCH @match
            ORDER BY CASE WHEN f.kind IN ('3mf', 'stl') THEN 0 ELSE 1 END, bm25(files_fts, 10.0, 8.0, 4.0, 2.0, 1.0, 0.3)
            LIMIT @limit
            """, new { match, limit }).AsList();
    }

    /// <summary>Folders whose own name contains <paramref name="text"/>.</summary>
    public IReadOnlyList<FolderRow> SearchFolders(string text, int limit)
    {
        var term = text.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        using var connection = db.Open();
        return connection.Query<FolderRow>("""
            SELECT f.path, f.parent, f.name,
                   (SELECT COUNT(*) FROM files x
                    WHERE (x.folder = f.path OR substr(x.folder, 1, length(f.path) + 1) = f.path || '/')
                      AND x.kind IN ('3mf', 'stl')) AS model_count
            FROM folders f
            WHERE f.path <> '' AND instr(lower(f.name), lower(@term)) > 0
            ORDER BY f.path
            LIMIT @limit
            """, new { term, limit }).AsList();
    }

    public IReadOnlyList<FileRow> Recent(int limit)
    {
        using var connection = db.Open();
        return connection.Query<FileRow>($"""
            SELECT {FileColumns} FROM files f
            WHERE f.kind IN ('3mf', 'stl')
            ORDER BY f.mtime DESC, f.path
            LIMIT @limit
            """, new { limit }).AsList();
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

    /// <summary>
    /// Turns what someone typed into an FTS5 query: every word must match as a prefix. Words are quoted, so FTS
    /// operators and punctuation in the input can't break the query.
    /// </summary>
    public static string? ToFtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var query = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12))
        {
            // Words without any letter or digit (e.g. "-") match nothing in FTS; skip them.
            if (!word.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            if (query.Length > 0)
            {
                query.Append(' ');
            }

            query.Append('"').Append(word.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"*");
        }

        return query.Length == 0 ? null : query.ToString();
    }
}
