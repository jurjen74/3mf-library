using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Library.Web.Auth;

public enum UserChange
{
    Done,
    NotFound,
    LastAdmin,
}

/// <summary>A row on the Users page.</summary>
public sealed class UserSummary
{
    public long Id { get; init; }
    public string UserName { get; init; } = "";
    public long Admin { get; init; }
    public long CreatedAt { get; init; }
    public long? LastLoginAt { get; init; }
    public long MustChange { get; init; }

    public bool IsAdmin => Admin != 0;
    public bool MustChangePassword => MustChange != 0;
    public DateTimeOffset Created => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
    public DateTimeOffset? LastLogin => LastLoginAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
}

public sealed class AppUser
{
    public long Id { get; init; }
    public string UserName { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public long Admin { get; init; }
    public string SecurityStamp { get; init; } = "";
    public long MustChange { get; init; }

    public bool IsAdmin => Admin != 0;

    /// <summary>Set for a starting or reset password: the user must choose their own before doing anything else.</summary>
    public bool MustChangePassword => MustChange != 0;
}

/// <summary>
/// Accounts in <c>/data/users.db</c>: kept apart from the library index, which is a cache and gets rebuilt. Passwords
/// are stored as ASP.NET Core Identity hashes (PBKDF2). The security stamp changes with the password, which ends the
/// sessions on other devices. Delete the file to start over with the first-run setup.
/// </summary>
public sealed partial class UserStore
{
    public const int MinimumPasswordLength = 10;
    private const int MaximumPasswordLength = 256;

    private static readonly PasswordHasher<AppUser> Hasher = new();

    // Verifying against a throwaway hash for unknown users makes "no such user" as slow as "wrong password".
    private static readonly string DummyHash = Hasher.HashPassword(new AppUser(), "not-a-real-password");

    private readonly string _connectionString;

    public UserStore(LibraryOptions options) => _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataPath, "users.db"),
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    /// <summary>
    /// Brings users.db up to date step by step. Unlike the library index this file is never dropped: each step changes
    /// the schema in place and keeps every account.
    /// </summary>
    public void Migrate()
    {
        string[] steps =
        [
            """
            CREATE TABLE users (
                id             INTEGER PRIMARY KEY,
                user_name      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash  TEXT NOT NULL,
                is_admin       INTEGER NOT NULL DEFAULT 0,
                security_stamp TEXT NOT NULL,
                created_at     INTEGER NOT NULL,
                last_login_at  INTEGER
            );
            """,
            "ALTER TABLE users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0;",
        ];

        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        for (var step = (int)version; step < steps.Length; step++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(steps[step], transaction: transaction);
            connection.Execute($"PRAGMA user_version = {step + 1};", transaction: transaction);
            transaction.Commit();
        }
    }

    public bool HasUsers()
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>("SELECT EXISTS (SELECT 1 FROM users)") != 0;
    }

    public AppUser? FindById(long id)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<AppUser>(
            $"SELECT {Columns} FROM users WHERE id = @id", new { id });
    }

    public AppUser? Verify(string? userName, string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaximumPasswordLength)
        {
            return null;
        }

        using var connection = Open();
        var user = string.IsNullOrWhiteSpace(userName)
            ? null
            : connection.QuerySingleOrDefault<AppUser>($"SELECT {Columns} FROM users WHERE user_name = @name", new { name = userName.Trim() });

        var result = Hasher.VerifyHashedPassword(user ?? new AppUser(), user?.PasswordHash ?? DummyHash, password);
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            connection.Execute("UPDATE users SET password_hash = @hash WHERE id = @id",
                new { hash = Hasher.HashPassword(user, password), id = user.Id });
        }

        connection.Execute("UPDATE users SET last_login_at = @now WHERE id = @id",
            new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id = user.Id });
        return user;
    }

    /// <summary>Creates the first account (an admin), unless any account exists already; null in that case.</summary>
    public AppUser? CreateFirstAdmin(string userName, string password)
    {
        using var connection = Open();
        var id = connection.ExecuteScalar<long?>("""
            INSERT INTO users (user_name, password_hash, is_admin, security_stamp, created_at, last_login_at)
            SELECT @name, @hash, 1, @stamp, @now, @now
            WHERE NOT EXISTS (SELECT 1 FROM users)
            RETURNING id
            """, new { name = userName.Trim(), hash = Hasher.HashPassword(new AppUser(), password), stamp = NewStamp(), now = Now() });
        return id is null ? null : FindById(id.Value);
    }

    /// <summary>Adds an account; null when the user name is taken (names are case-insensitive).</summary>
    public AppUser? Create(string userName, string password, bool isAdmin, bool mustChangePassword = false)
    {
        using var connection = Open();
        var id = connection.ExecuteScalar<long?>("""
            INSERT INTO users (user_name, password_hash, is_admin, security_stamp, created_at, must_change_password)
            VALUES (@name, @hash, @admin, @stamp, @now, @mustChange)
            ON CONFLICT (user_name) DO NOTHING
            RETURNING id
            """, new
            {
                name = userName.Trim(), hash = Hasher.HashPassword(new AppUser(), password), admin = isAdmin ? 1 : 0,
                stamp = NewStamp(), now = Now(), mustChange = mustChangePassword ? 1 : 0,
            });
        return id is null ? null : FindById(id.Value);
    }

    public IReadOnlyList<UserSummary> List()
    {
        using var connection = Open();
        return connection.Query<UserSummary>(
            "SELECT id, user_name, is_admin AS admin, created_at, last_login_at, must_change_password AS must_change FROM users ORDER BY user_name COLLATE NOCASE")
            .AsList();
    }

    /// <summary>An admin sets someone's password; their sessions on every device end.</summary>
    public bool ResetPassword(long id, string newPassword, bool mustChangePassword = false)
    {
        using var connection = Open();
        return connection.Execute("""
            UPDATE users SET password_hash = @hash, security_stamp = @stamp, must_change_password = @mustChange
            WHERE id = @id
            """, new { hash = Hasher.HashPassword(new AppUser(), newPassword), stamp = NewStamp(), mustChange = mustChangePassword ? 1 : 0, id }) == 1;
    }

    /// <summary>Grants or removes admin rights; the last admin can't lose them.</summary>
    public UserChange SetAdmin(long id, bool admin)
    {
        using var connection = Open();
        var changed = connection.Execute($"""
            UPDATE users SET is_admin = @admin
            WHERE id = @id AND (@admin = 1 OR {NotLastAdmin})
            """, new { id, admin = admin ? 1 : 0 });
        return changed == 1 ? UserChange.Done : Explain(connection, id);
    }

    /// <summary>Removes an account (its sessions end on their next request); the last admin can't be removed.</summary>
    public UserChange Delete(long id)
    {
        using var connection = Open();
        var deleted = connection.Execute($"DELETE FROM users WHERE id = @id AND {NotLastAdmin}", new { id });
        return deleted == 1 ? UserChange.Done : Explain(connection, id);
    }

    // True for a non-admin, or an admin while another admin exists.
    private const string NotLastAdmin = "(is_admin = 0 OR (SELECT COUNT(*) FROM users WHERE is_admin = 1) > 1)";

    private static UserChange Explain(SqliteConnection connection, long id) =>
        connection.ExecuteScalar<long>("SELECT EXISTS (SELECT 1 FROM users WHERE id = @id)", new { id }) == 0
            ? UserChange.NotFound
            : UserChange.LastAdmin;

    /// <summary>Checks the current password, stores the new one and a new security stamp (ending other sessions).</summary>
    public AppUser? ChangePassword(long id, string currentPassword, string newPassword)
    {
        var user = FindById(id);
        if (user is null || Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            return null;
        }

        using var connection = Open();
        connection.Execute("""
            UPDATE users SET password_hash = @hash, security_stamp = @stamp, must_change_password = 0
            WHERE id = @id
            """, new { hash = Hasher.HashPassword(user, newPassword), stamp = NewStamp(), id });
        return FindById(id);
    }

    public static string? CheckUserName(string? userName) =>
        userName is not null && UserNamePattern().IsMatch(userName.Trim())
            ? null
            : "Use 1–64 letters, digits, dots, dashes, underscores or @.";

    public static string? CheckPassword(string? password, string? repeated) => password switch
    {
        null or { Length: < MinimumPasswordLength } => $"Use at least {MinimumPasswordLength} characters.",
        { Length: > MaximumPasswordLength } => $"Use at most {MaximumPasswordLength} characters.",
        _ when password != repeated => "The two passwords don't match.",
        _ => null,
    };

    private const string Columns = "id, user_name, password_hash, is_admin AS admin, security_stamp, must_change_password AS must_change";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
