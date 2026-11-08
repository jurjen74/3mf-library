using Dapper;
using Microsoft.Data.Sqlite;

namespace Library.Web.Data;

/// <summary>
/// The SQLite index in <c>/data/library.db</c>. It is a cache of what is on disk: deleting it only costs a rescan.
/// </summary>
public sealed class LibraryDb
{
    private const int SchemaVersion = 1;

    private const string SchemaV1 = """
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
            colours         TEXT,
            cover           TEXT,
            cover_small     TEXT,
            error           TEXT,
            indexed_at      INTEGER NOT NULL
        );

        CREATE INDEX ix_files_folder ON files (folder);

        CREATE TABLE plates (
            file_id     INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            idx         INTEGER NOT NULL,
            name        TEXT,
            image       TEXT,
            image_small TEXT,
            PRIMARY KEY (file_id, idx)
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
        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        connection.Execute(SchemaV1, transaction: transaction);
        connection.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: transaction);
        transaction.Commit();
    }
}
