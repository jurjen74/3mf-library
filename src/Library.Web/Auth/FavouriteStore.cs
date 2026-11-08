using Dapper;
using Microsoft.Data.Sqlite;

namespace Library.Web.Auth;

/// <summary>
/// Starred files, per account, in <c>users.db</c> (next to the accounts, not in the rebuildable index). Visitors on the
/// home network without a login share one list (owner 0). Favourites are kept by path: a file that disappears from the
/// library simply stops showing, and comes back if the file does.
/// </summary>
public sealed class FavouriteStore(LibraryOptions options)
{
    public const long HomeNetworkOwner = 0;
    private const string RequestKey = "library.favourites";

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataPath, "users.db"),
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    /// <summary>Whose list this request uses: the logged-in account, the shared home list, or nobody.</summary>
    public static long? Owner(HttpContext context) =>
        Session.CurrentUser(context)?.Id ?? (TrustedNetwork.IsHomeNetworkUser(context.User) ? HomeNetworkOwner : null);

    public HashSet<string> Paths(long owner)
    {
        using var connection = Open();
        return connection.Query<string>("SELECT path FROM favourites WHERE owner_id = @owner", new { owner })
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The favourites of this request's visitor, loaded once per request (cards ask for every file).</summary>
    public HashSet<string> ForRequest(HttpContext context)
    {
        if (context.Items[RequestKey] is HashSet<string> cached)
        {
            return cached;
        }

        var paths = Owner(context) is { } owner ? Paths(owner) : [];
        context.Items[RequestKey] = paths;
        return paths;
    }

    /// <summary>Stars or unstars a file; returns whether it's a favourite now.</summary>
    public bool Toggle(long owner, string path)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var removed = connection.Execute("DELETE FROM favourites WHERE owner_id = @owner AND path = @path", new { owner, path }, transaction);
        if (removed == 0)
        {
            connection.Execute("INSERT INTO favourites (owner_id, path, created_at) VALUES (@owner, @path, @now)",
                new { owner, path, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, transaction);
        }

        transaction.Commit();
        return removed == 0;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
