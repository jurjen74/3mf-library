using System.Net;
using System.Text.RegularExpressions;
using Library.Web;
using Library.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Library.Tests;

public sealed partial class AuthTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Lan = "192.168.2.10";
    private const string Proxy = "192.168.2.50";
    private const string Outside = "203.0.113.7";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "3mf-library-auth", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _factories = [];

    public AuthTests() => Directory.CreateDirectory(Path.Combine(_root, "library"));

    public void Dispose()
    {
        _factories.ForEach(f => f.Dispose());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("/browse")]
    [InlineData("/search?q=lamp")]
    [InlineData("/recent")]
    [InlineData("/account")]
    [InlineData("/file/Lamp.3mf")]
    [InlineData("/download/Lamp.3mf")]
    [InlineData("/img/0123456789abcdef/plate_1.png")]
    [InlineData("/mesh/0123456789abcdef/model.bin")]
    public async Task Everything_needs_a_login_from_outside(string url)
    {
        var (client, _) = Site();

        var response = await Send(client, url, peer: Outside);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", PathOf(response.Headers.Location!));
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/health")]
    [InlineData("/css/site.css")]
    [InlineData("/favicon.svg")]
    public async Task Login_page_its_assets_and_health_are_open(string url)
    {
        var (client, _) = Site();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, url, peer: Outside)).StatusCode);
    }

    [Fact]
    public async Task Direct_visitors_from_the_trusted_network_need_no_login()
    {
        var (client, _) = Site();

        var response = await Send(client, "/browse", peer: Lan);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Home network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ipv4_mapped_addresses_count_as_the_trusted_network()
    {
        var (client, _) = Site();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/browse", peer: "::ffff:" + Lan)).StatusCode);
    }

    [Theory]
    [InlineData(Lan, "X-Forwarded-For", Lan)]        // a LAN client adding proxy headers
    [InlineData(Lan, "X-Real-IP", Lan)]
    [InlineData(Proxy, "X-Forwarded-For", Lan)]      // NPM forwarding a visitor that claims to be on the LAN
    [InlineData(Proxy, null, null)]                  // NPM itself
    public async Task Requests_through_a_proxy_always_need_a_login(string peer, string? header, string? value)
    {
        var (client, _) = Site();

        var response = await Send(client, "/browse", peer, header is null ? [] : [(header, value!)]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Without_trusted_networks_the_lan_needs_a_login_too()
    {
        var (client, _) = Site(trustedNetworks: "");

        Assert.Equal(HttpStatusCode.Redirect, (await Send(client, "/browse", peer: Lan)).StatusCode);
    }

    [Fact]
    public async Task Logging_in_gives_access_and_logging_out_takes_it_away()
    {
        var (client, _) = Site();

        var login = await LogIn(client, "Jurjen", Password, returnUrl: "/recent");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/recent", login.Headers.Location?.OriginalString);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("library.auth=", StringComparison.Ordinal));

        var page = await Send(client, "/browse", peer: Outside);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Log out", html);

        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/logout", Outside, [new("__RequestVerificationToken", Token(html))])).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Send(client, "/browse", peer: Outside)).StatusCode);
    }

    [Fact]
    public async Task Logging_in_through_the_proxy_works_and_keeps_the_cookie_secure()
    {
        var (client, _) = Site();

        var login = await LogIn(client, "jurjen", Password, peer: Proxy, headers: [("X-Forwarded-For", Outside), ("X-Forwarded-Proto", "https")]);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("library.auth=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("jurjen", "wrong password")]
    [InlineData("someone", Password)]
    [InlineData("", "")]
    public async Task Wrong_credentials_are_refused(string user, string password)
    {
        var (client, _) = Site();

        var response = await LogIn(client, user, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("library.auth=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Redirect, (await Send(client, "/browse", peer: Outside)).StatusCode);
    }

    [Fact]
    public async Task Return_url_cannot_send_you_to_another_site()
    {
        var (client, _) = Site();

        var response = await LogIn(client, "jurjen", Password, returnUrl: "https://evil.example/");

        Assert.Equal("/browse", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Repeated_login_attempts_are_limited()
    {
        var (client, _) = Site();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 9; i++)
        {
            statuses.Add((await LogIn(client, "jurjen", "nope", peer: "198.51.100.9")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/login", peer: "198.51.100.9")).StatusCode);
    }

    [Theory]
    [InlineData("")]           // no proxy configured: nobody's X-Forwarded-For counts
    [InlineData(Proxy)]        // a proxy configured, but this visitor isn't it
    public async Task Invented_forwarding_addresses_do_not_escape_the_login_rate_limit(string knownProxies)
    {
        var (client, _) = Site(knownProxies: knownProxies);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 9; i++)
        {
            statuses.Add((await LogIn(client, "jurjen", "nope", peer: "198.51.100.9", headers: [("X-Forwarded-For", $"10.0.0.{i}")])).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task First_run_setup_from_the_home_network_creates_an_admin_and_logs_in()
    {
        var (client, users) = Site(withAccount: false);

        Assert.Equal("/setup", PathOf((await Send(client, "/login", peer: Lan)).Headers.Location!));
        Assert.Contains("No account yet", await (await Send(client, "/browse", peer: Lan)).Content.ReadAsStringAsync());

        var created = await SetUp(client, "jurjen", Password, Password, peer: Lan);
        Assert.Equal("/browse", PathOf(created.Headers.Location!));
        Assert.True(users.Verify("jurjen", Password)?.IsAdmin);

        // The cookie works from outside too, and the setup page is closed now.
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/browse", peer: Outside)).StatusCode);
        Assert.Equal("/login", PathOf((await Send(client, "/setup", peer: Lan)).Headers.Location!));
    }

    [Theory]
    [InlineData(Proxy, true)]     // through Nginx Proxy Manager
    [InlineData(Outside, false)]  // directly, but not from the trusted network
    public async Task First_run_setup_is_refused_from_outside_the_home_network(string peer, bool proxied)
    {
        var (client, users) = Site(withAccount: false);
        (string, string)[] headers = proxied ? [("X-Forwarded-For", Outside)] : [];

        var page = await (await Send(client, "/setup", peer, headers)).Content.ReadAsStringAsync();
        var post = await SetUp(client, "intruder", Password, Password, peer, headers, tokenPage: "/login");

        Assert.Contains("only from the home network", page);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.False(users.HasUsers());
    }

    [Fact]
    public async Task Without_trusted_networks_setup_needs_the_code_from_the_log()
    {
        var (client, users) = Site(trustedNetworks: "", withAccount: false);
        var code = LastFactory.Services.GetRequiredService<SetupCode>();

        Assert.Contains("Setup code", await (await Send(client, "/setup", peer: Outside)).Content.ReadAsStringAsync());

        var missing = await SetUp(client, "jurjen", Password, Password, peer: Outside);
        var wrong = await SetUp(client, "jurjen", Password, Password, peer: Outside, code: "AAAA-AAAA");
        Assert.Contains("setup code isn't right", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.False(users.HasUsers());

        // Typed by hand: lower case, a space instead of the dash.
        var created = await SetUp(client, "jurjen", Password, Password, peer: Outside, code: code.Display.ToLowerInvariant().Replace('-', ' '));
        Assert.Equal("/browse", PathOf(created.Headers.Location!));
        Assert.True(users.Verify("jurjen", Password)?.IsAdmin);
    }

    [Fact]
    public async Task With_trusted_networks_setup_from_home_needs_no_code()
    {
        var (client, _) = Site(withAccount: false);

        Assert.DoesNotContain("Setup code", await (await Send(client, "/setup", peer: Lan)).Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("jurjen", "short", "short", "at least 10")]
    [InlineData("jurjen", Password, Password + "x", "don't match")]
    [InlineData("not a name!", Password, Password, "letters, digits")]
    public async Task First_run_setup_checks_its_input(string user, string password, string repeat, string message)
    {
        var (client, users) = Site(withAccount: false);

        var response = await SetUp(client, user, password, repeat, peer: Lan);

        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.False(users.HasUsers());
    }

    [Fact]
    public async Task Changing_the_password_logs_out_other_devices()
    {
        var (phone, users) = Site();
        var laptop = NewClient(LastFactory); // same site, own cookies
        await LogIn(phone, "jurjen", Password);
        await LogIn(laptop, "jurjen", Password);

        var account = await (await Send(laptop, "/account", peer: Outside)).Content.ReadAsStringAsync();
        var change = await Post(laptop, "/account", Outside,
        [
            new("CurrentPassword", Password), new("NewPassword", "a brand new password"), new("RepeatPassword", "a brand new password"),
            new("__RequestVerificationToken", Token(account)),
        ]);

        Assert.Equal("/account?changed=true", change.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await Send(laptop, "/browse", peer: Outside)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Send(phone, "/browse", peer: Outside)).StatusCode);
        Assert.Null(users.Verify("jurjen", Password));
        Assert.NotNull(users.Verify("jurjen", "a brand new password"));
    }

    [Fact]
    public async Task Password_change_attempts_are_limited_per_account_not_per_address()
    {
        var (jurjen, users) = Site();
        users.Create("guest", "guest password 123", isAdmin: false);
        var guest = NewClient(LastFactory);
        await LogIn(jurjen, "jurjen", Password);
        await LogIn(guest, "guest", "guest password 123");

        async Task<HttpStatusCode> WrongCurrentPassword(HttpClient client)
        {
            var account = await (await Send(client, "/account", peer: Outside)).Content.ReadAsStringAsync();
            var response = await Post(client, "/account", Outside,
            [
                new("CurrentPassword", "guessing"), new("NewPassword", "a brand new password"), new("RepeatPassword", "a brand new password"),
                new("__RequestVerificationToken", Token(account)),
            ]);
            return response.StatusCode;
        }

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 9; i++)
        {
            statuses.Add(await WrongCurrentPassword(jurjen));
        }

        // Same address: the other account can still change its password, and logins aren't affected.
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.OK, await WrongCurrentPassword(guest));
        Assert.Equal(HttpStatusCode.Redirect, (await LogIn(NewClient(LastFactory), "guest", "guest password 123")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one()
    {
        var (client, users) = Site();
        await LogIn(client, "jurjen", Password);
        var account = await (await Send(client, "/account", peer: Outside)).Content.ReadAsStringAsync();

        var response = await Post(client, "/account", Outside,
        [
            new("CurrentPassword", "guessing"), new("NewPassword", "a brand new password"), new("RepeatPassword", "a brand new password"),
            new("__RequestVerificationToken", Token(account)),
        ]);

        Assert.Contains("current password isn't right", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.NotNull(users.Verify("jurjen", Password));
    }

    [Fact]
    public async Task Only_admins_can_open_the_users_page()
    {
        var (admin, users) = Site();
        users.Create("guest", "guest password 123", isAdmin: false);
        var guest = NewClient(LastFactory);
        var home = NewClient(LastFactory);
        await LogIn(admin, "jurjen", Password);
        await LogIn(guest, "guest", "guest password 123");

        var adminPage = await Send(admin, "/users", Outside);
        var browse = await (await Send(admin, "/browse", Outside)).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, adminPage.StatusCode);
        Assert.Contains("href=\"/users\"", browse);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(guest, "/users", Outside)).StatusCode);
        Assert.DoesNotContain("href=\"/users\"", await (await Send(guest, "/browse", Outside)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(home, "/users", Lan)).StatusCode);
    }

    [Fact]
    public async Task An_admin_adds_a_user_who_can_then_log_in()
    {
        var (admin, users) = Site();
        await LogIn(admin, "jurjen", Password);

        await UsersAction(admin, "Add", [new("userName", "Guest"), new("password", "guest password 123"), new("repeatPassword", "guest password 123")]);
        var duplicate = await UsersForm(admin, "Add", [new("userName", "GUEST"), new("password", "another password"), new("repeatPassword", "another password")]);

        var guest = NewClient(LastFactory);
        Assert.Equal("/browse", (await LogIn(guest, "guest", "guest password 123")).Headers.Location?.OriginalString);
        Assert.False(users.Verify("guest", "guest password 123")!.IsAdmin);
        Assert.Contains("already a user called GUEST", WebUtility.HtmlDecode(duplicate));
        Assert.Single(users.List(), u => u.UserName == "Guest");
    }

    [Fact]
    public async Task A_problem_in_the_add_user_form_keeps_what_was_entered_except_the_passwords()
    {
        var (admin, users) = Site();
        await LogIn(admin, "jurjen", Password);

        var page = WebUtility.HtmlDecode(await UsersForm(admin, "Add",
            [new("userName", "robin"), new("password", "first password 1"), new("repeatPassword", "other password 2"), new("isAdmin", "true")]));

        Assert.Contains("don't match", page);
        Assert.Contains("name=\"userName\" value=\"robin\"", page);
        Assert.Matches("name=\"isAdmin\" value=\"true\" checked", page);
        Assert.DoesNotMatch("name=\"mustChangePassword\" value=\"true\" checked", page);   // unticked stays unticked
        Assert.DoesNotContain("first password 1", page);
        Assert.DoesNotContain("other password 2", page);
        Assert.DoesNotContain(users.List(), u => u.UserName == "robin");
    }

    [Fact]
    public async Task An_admin_can_make_an_account_browse_only_and_back()
    {
        var (admin, users) = Site();
        await LogIn(admin, "jurjen", Password);

        await UsersAction(admin, "Add", [new("userName", "viewer"), new("password", "viewer password 1"), new("repeatPassword", "viewer password 1"), new("browseOnly", "true")]);
        var viewer = users.List().Single(u => u.UserName == "viewer");
        var listed = await (await Send(admin, "/users", Outside)).Content.ReadAsStringAsync();
        var allowed = await UsersAction(admin, "Downloads", [new("id", viewer.Id.ToString()), new("allow", "true")]);
        var canAfter = users.List().Single(u => u.UserName == "viewer").CanDownload;
        await UsersAction(admin, "Downloads", [new("id", viewer.Id.ToString()), new("allow", "false")]);

        Assert.False(viewer.CanDownload);
        Assert.Contains(">browse only</span>", listed);
        Assert.Contains("viewer can download again", allowed);
        Assert.True(canAfter);
        Assert.False(users.Verify("viewer", "viewer password 1")!.CanDownload);
        Assert.True(users.Verify("jurjen", Password)!.CanDownload);
    }

    [Fact]
    public void Admins_can_always_download()
    {
        var users = new UserStore(new LibraryOptions { LibraryPath = _root, DataPath = _root });
        users.Migrate();
        var admin = users.CreateFirstAdmin("jurjen", Password)!;

        users.SetCanDownload(admin.Id, false);

        Assert.True(users.FindById(admin.Id)!.CanDownload);
        Assert.False(users.List().Single().CanDownload);  // stored, so it applies if admin rights go
    }

    [Fact]
    public async Task Resetting_or_deleting_a_user_ends_their_sessions()
    {
        var (admin, users) = Site();
        var guestId = users.Create("guest", "guest password 123", isAdmin: false)!.Id;
        users.Create("other", "other password 123", isAdmin: false);
        var guest = NewClient(LastFactory);
        var other = NewClient(LastFactory);
        await LogIn(admin, "jurjen", Password);
        await LogIn(guest, "guest", "guest password 123");
        await LogIn(other, "other", "other password 123");

        var reset = await UsersAction(admin, "Reset", [new("id", guestId.ToString()), new("password", "a new guest password"), new("repeatPassword", "a new guest password")]);
        await UsersAction(admin, "Delete", [new("id", users.List().Single(u => u.UserName == "other").Id.ToString())]);

        Assert.Contains("New password set for guest", reset);
        Assert.Equal(HttpStatusCode.Redirect, (await Send(guest, "/browse", Outside)).StatusCode);
        Assert.NotNull(users.Verify("guest", "a new guest password"));
        Assert.Equal(HttpStatusCode.Redirect, (await Send(other, "/browse", Outside)).StatusCode);
        Assert.Null(users.Verify("other", "other password 123"));
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, "/browse", Outside)).StatusCode);
    }

    [Fact]
    public async Task Admins_cannot_delete_themselves_or_remove_the_last_admin()
    {
        var (admin, users) = Site();
        var myId = users.List().Single().Id.ToString();
        await LogIn(admin, "jurjen", Password);

        var deleteSelf = await UsersAction(admin, "Delete", [new("id", myId)]);
        var demoteSelf = await UsersAction(admin, "Admin", [new("id", myId), new("admin", "false")]);

        Assert.Contains("can't delete your own account", WebUtility.HtmlDecode(deleteSelf));
        Assert.Contains("at least one admin", demoteSelf);
        Assert.True(users.List().Single().IsAdmin);
    }

    [Fact]
    public async Task Admin_rights_can_be_handed_over()
    {
        var (admin, users) = Site();
        var guestId = users.Create("guest", "guest password 123", isAdmin: false)!.Id.ToString();
        var myId = users.List().Single(u => u.UserName == "jurjen").Id.ToString();
        await LogIn(admin, "jurjen", Password);

        await UsersAction(admin, "Admin", [new("id", guestId), new("admin", "true")]);
        var demote = await Post(admin, "/users?handler=Admin", Outside,
            [new("id", myId), new("admin", "false"), new("__RequestVerificationToken", Token(await (await Send(admin, "/users", Outside)).Content.ReadAsStringAsync()))]);

        Assert.Equal("/browse", demote.Headers.Location?.OriginalString);
        Assert.True(users.Verify("guest", "guest password 123")!.IsAdmin);
        Assert.False(users.Verify("jurjen", Password)!.IsAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(admin, "/users", Outside)).StatusCode);
    }

    [Fact]
    public async Task A_new_user_with_a_starting_password_must_choose_their_own_first()
    {
        var (admin, users) = Site();
        await LogIn(admin, "jurjen", Password);
        await UsersAction(admin, "Add",
            [new("userName", "guest"), new("password", "starting password"), new("repeatPassword", "starting password"), new("mustChangePassword", "true")]);
        var guest = NewClient(LastFactory);

        await LogIn(guest, "guest", "starting password");

        foreach (var url in new[] { "/browse", "/search?q=x", "/download/Lamp.3mf", "/img/0123456789abcdef/plate_1.png", "/users" })
        {
            var blocked = await Send(guest, url, Outside);
            Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
            Assert.Equal("/account", blocked.Headers.Location?.OriginalString);
        }

        var page = await (await Send(guest, "/account", Outside)).Content.ReadAsStringAsync();
        Assert.Contains("Choose your own password", page);
        Assert.DoesNotContain("Search models", page);

        var same = await ChangePassword(guest, "starting password", "starting password");
        Assert.Contains("different from the one you have now", WebUtility.HtmlDecode(await same.Content.ReadAsStringAsync()));

        var changed = await ChangePassword(guest, "starting password", "my own password 42");
        Assert.Equal("/browse", changed.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await Send(guest, "/browse", Outside)).StatusCode);
        Assert.False(users.Verify("guest", "my own password 42")!.MustChangePassword);
    }

    [Fact]
    public async Task Without_the_checkbox_a_new_user_can_start_straight_away()
    {
        var (admin, users) = Site();
        await LogIn(admin, "jurjen", Password);
        await UsersAction(admin, "Add", [new("userName", "guest"), new("password", "starting password"), new("repeatPassword", "starting password")]);
        var guest = NewClient(LastFactory);

        await LogIn(guest, "guest", "starting password");

        Assert.Equal(HttpStatusCode.OK, (await Send(guest, "/browse", Outside)).StatusCode);
        Assert.False(users.List().Single(u => u.UserName == "guest").MustChangePassword);
    }

    [Fact]
    public async Task A_reset_password_can_require_a_new_choice_and_logging_out_still_works()
    {
        var (admin, users) = Site();
        var guestId = users.Create("guest", "guest password 123", isAdmin: false)!.Id;
        await LogIn(admin, "jurjen", Password);

        var list = await UsersAction(admin, "Reset",
            [new("id", guestId.ToString()), new("password", "temporary password"), new("repeatPassword", "temporary password"), new("mustChangePassword", "true")]);
        var guest = NewClient(LastFactory);
        await LogIn(guest, "guest", "temporary password");

        Assert.Contains("must change password", list);
        Assert.Equal("/account", (await Send(guest, "/browse", Outside)).Headers.Location?.OriginalString);
        var page = await (await Send(guest, "/account", Outside)).Content.ReadAsStringAsync();
        var logout = await Post(guest, "/logout", Outside, [new("__RequestVerificationToken", Token(page))]);
        Assert.Equal("/login", logout.Headers.Location?.OriginalString);
    }

    [Fact]
    public void Upgrading_an_existing_users_db_keeps_accounts_and_forces_nothing()
    {
        var dataPath = Path.Combine(_root, "upgrade");
        Directory.CreateDirectory(dataPath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataPath, "users.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE users (id INTEGER PRIMARY KEY, user_name TEXT NOT NULL UNIQUE COLLATE NOCASE, password_hash TEXT NOT NULL,
                    is_admin INTEGER NOT NULL DEFAULT 0, security_stamp TEXT NOT NULL, created_at INTEGER NOT NULL, last_login_at INTEGER);
                INSERT INTO users (user_name, password_hash, is_admin, security_stamp, created_at)
                VALUES ('jurjen', '{new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(new AppUser(), Password)}', 1, 'stamp', 0);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var users = new UserStore(new LibraryOptions { LibraryPath = _root, DataPath = dataPath });
        users.Migrate();
        users.Migrate();

        var user = users.Verify("jurjen", Password);
        Assert.NotNull(user);
        Assert.True(user.IsAdmin);
        Assert.False(user.MustChangePassword);
        Assert.True(user.CanDownload);
    }

    [Fact]
    public void User_store_keeps_names_unique_and_the_first_admin_single()
    {
        var users = new UserStore(new LibraryOptions { LibraryPath = _root, DataPath = _root });
        users.Migrate();

        Assert.NotNull(users.CreateFirstAdmin("jurjen", Password));
        Assert.Null(users.CreateFirstAdmin("someone", Password));
        Assert.Null(users.Create("JURJEN", Password, isAdmin: false));

        var second = users.Create("guest", "another long password", isAdmin: false)!;
        Assert.False(second.IsAdmin);
        Assert.Equal(second.Id, users.Verify("GUEST", "another long password")?.Id);
        Assert.DoesNotContain(Password, second.PasswordHash);
    }

    [Theory]
    [InlineData("TRUSTED_NETWORKS", "192.168.2.0/33")]
    [InlineData("KNOWN_PROXIES", "npm.local")]
    public void Invalid_network_settings_stop_the_app(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build();

        Assert.Throws<InvalidOperationException>(() => LibraryOptions.FromConfiguration(config, _root));
    }

    private (HttpClient Client, UserStore Users) Site(string trustedNetworks = "192.168.2.0/24", bool withAccount = true,
        string knownProxies = Proxy)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("LIBRARY_PATH", Path.Combine(_root, "library"));
            host.UseSetting("DATA_PATH", Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N")));
            host.UseSetting("TRUSTED_NETWORKS", trustedNetworks);
            host.UseSetting("KNOWN_PROXIES", knownProxies);
            host.UseSetting("SETTLE_DELAY", "00:00:00");
            host.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestPeerFilter>());
        });
        _factories.Add(factory);
        var client = NewClient(factory);
        var users = factory.Services.GetRequiredService<UserStore>();
        if (withAccount)
        {
            users.CreateFirstAdmin("jurjen", Password);
        }

        return (client, users);
    }

    private WebApplicationFactory<Program> LastFactory => _factories.OfType<WebApplicationFactory<Program>>().Last();

    /// <summary>Runs a Users page action as the logged-in client and returns the page it lands on.</summary>
    private static async Task<string> UsersAction(HttpClient client, string handler, KeyValuePair<string, string>[] form)
    {
        var token = Token(await (await Send(client, "/users", Outside)).Content.ReadAsStringAsync());
        var response = await Post(client, $"/users?handler={handler}", Outside, [.. form, new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return await (await Send(client, "/users", Outside)).Content.ReadAsStringAsync();
    }

    /// <summary>Posts a Users page form that has a problem: the page comes back directly (no redirect), form kept.</summary>
    private static async Task<string> UsersForm(HttpClient client, string handler, KeyValuePair<string, string>[] form)
    {
        var token = Token(await (await Send(client, "/users", Outside)).Content.ReadAsStringAsync());
        var response = await Post(client, $"/users?handler={handler}", Outside, [.. form, new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> ChangePassword(HttpClient client, string current, string next)
    {
        var token = Token(await (await Send(client, "/account", Outside)).Content.ReadAsStringAsync());
        return await Post(client, "/account", Outside,
            [new("CurrentPassword", current), new("NewPassword", next), new("RepeatPassword", next), new("__RequestVerificationToken", token)]);
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string PathOf(Uri location) => location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];

    private static async Task<HttpResponseMessage> Send(HttpClient client, string url, string peer, (string Name, string Value)[]? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, peer, headers);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string peer,
        KeyValuePair<string, string>[] form, (string Name, string Value)[]? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        AddHeaders(request, peer, headers);
        return await client.SendAsync(request);
    }

    private static void AddHeaders(HttpRequestMessage request, string peer, (string Name, string Value)[]? headers)
    {
        request.Headers.Add("X-Test-Peer", peer);
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.Add(name, value);
        }
    }

    private static async Task<HttpResponseMessage> LogIn(HttpClient client, string user, string password, string? returnUrl = null,
        string peer = Outside, (string Name, string Value)[]? headers = null)
    {
        var token = Token(await (await Send(client, "/login", peer, headers)).Content.ReadAsStringAsync());
        var url = returnUrl is null ? "/login" : $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
        return await Post(client, url, peer,
            [new("UserName", user), new("Password", password), new("Remember", "true"), new("__RequestVerificationToken", token)], headers);
    }

    /// <summary>
    /// Posts the setup form. Visitors who get no form (outside the home network) take a valid antiforgery token from
    /// another page, e.g. the login page, as an attacker could.
    /// </summary>
    private static async Task<HttpResponseMessage> SetUp(HttpClient client, string user, string password, string repeat, string peer,
        (string Name, string Value)[]? headers = null, string tokenPage = "/setup", string? code = null)
    {
        var page = await Send(client, tokenPage, peer, headers);
        var token = Token(await page.Content.ReadAsStringAsync());
        KeyValuePair<string, string>[] form =
            [new("UserName", user), new("Password", password), new("RepeatPassword", repeat), new("__RequestVerificationToken", token)];
        return await Post(client, "/setup", peer, code is null ? form : [.. form, new("Code", code)], headers);
    }

    private static string Token(string html) => TokenPattern().Match(html) is { Success: true } match
        ? match.Groups[1].Value
        : throw new InvalidOperationException("No antiforgery token on the page.");

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    /// <summary>The in-memory test server has no client address; this sets one from a test header.</summary>
    private sealed class TestPeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                }

                return nextMiddleware();
            });
            next(app);
        };
    }
}
