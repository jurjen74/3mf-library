using Dapper;
using Microsoft.Data.Sqlite;

namespace Library.Web.Data;

/// <summary>
/// The SQLite index in <c>/data/library.db</c>. It is a cache of what is on disk: deleting it only costs a rescan,
/// so schema changes simply drop and recreate everything.
/// </summary>
public sealed class LibraryDb
{
    private const int SchemaVersion = 4;

    private const string DropAll = """
        DROP TABLE IF EXISTS files_fts;
        DROP TABLE IF EXISTS photos;
        DROP TABLE IF EXISTS plates;
        DROP TABLE IF EXISTS files;
        DROP TABLE IF EXISTS folders;
        """;

    private const string Schema = """
        CREATE TABLE folders (
            path   TEXT PRIMARY KEY,
            parent TEXT,
            name   TEXT NOT NULL
        );

        CREATE TABLE files (
            id              INTEGER PRIMARY KEY,
            path            TEXT NOT NULL UNIQUE,
            folder          TEXT NOT NULL,
            name            TEXT NOT NULL,
            kind            TEXT NOT NULL,
            size            INTEGER NOT NULL,
            mtime           INTEGER NOT NULL,
            indexer_version INTEGER NOT NULL,
            cache_key       TEXT,
            title           TEXT,
            designer        TEXT,
            license         TEXT,
            description     TEXT,
            application     TEXT,
            created         TEXT,
            plate_count     INTEGER NOT NULL DEFAULT 0,
            photo_count     INTEGER NOT NULL DEFAULT 0,
            colours         TEXT,
            cover           TEXT,
            cover_small     TEXT,
            dim_x           REAL,
            dim_y           REAL,
            dim_z           REAL,
            triangles       INTEGER,
            error           TEXT,
            indexed_at      INTEGER NOT NULL
        );

        CREATE INDEX ix_files_folder ON files (folder);
        CREATE INDEX ix_files_mtime ON files (mtime);

        CREATE TABLE plates (
            file_id       INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            idx           INTEGER NOT NULL,
            name          TEXT,
            image         TEXT,
            image_small   TEXT,
            objects       TEXT,
            print_seconds INTEGER,
            weight_grams  REAL,
            PRIMARY KEY (file_id, idx)
        );

        CREATE TABLE photos (
            file_id INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            idx     INTEGER NOT NULL,
            image   TEXT NOT NULL,
            PRIMARY KEY (file_id, idx)
        );

        -- Full-text search; rowid = files.id. Kept in step by the scanner.
        CREATE VIRTUAL TABLE files_fts USING fts5 (
            name, title, designer, folder, objects, description,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    private readonly string _connectionString;

    static LibraryDb() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public LibraryDb(LibraryOptions options) => _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (version == SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        connection.Execute(DropAll, transaction: transaction);
        connection.Execute(Schema, transaction: transaction);
        connection.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: transaction);
        transaction.Commit();
    }
}
