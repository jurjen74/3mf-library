using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Library.Web;
using Library.Web.Auth;
using Library.Web.Indexing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Library.Tests;

/// <summary>Folder ZIP, "Open in Bambu Studio" links, favourites and the app manifest, end to end.</summary>
public sealed partial class FeatureTests : IDisposable
{
    private const string Lan = "192.168.2.10";
    private const string Outside = "203.0.113.7";
    private const string Password = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "3mf-library-features", Guid.NewGuid().ToString("N"));

    // Built once: a ZIP records entry times (2-second precision), so two builds can differ by a timestamp byte.
    private readonly byte[] _torso = TestThreeMf.Bambu();
    private readonly WebApplicationFactory<Program> _factory;

    public FeatureTests()
    {
        var library = Path.Combine(_root, "library");
        Write(library, "Kits/Robot/arm.stl", TestMeshes.BinaryStl());
        Write(library, "Kits/Robot/Body/torso.3mf", _torso);
        Write(library, "Kits/Robot/readme.txt", "Glue the arms last."u8.ToArray());
        Write(library, "Kits/Robot/datapackage.json", "{}"u8.ToArray());
        Write(library, "Kits/Robot Mini/arm.stl", TestMeshes.BinaryStl());
        Write(library, "Lamp.3mf", TestThreeMf.Bambu());

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("LIBRARY_PATH", library);
            host.UseSetting("DATA_PATH", Path.Combine(_root, "data"));
            host.UseSetting("TRUSTED_NETWORKS", "192.168.2.0/24");
            host.UseSetting("SETTLE_DELAY", "00:00:00");
            host.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestPeerFilter>());
        });
        // Let the app's own startup scan finish (a second scan alongside it would only queue behind it).
        var indexer = _factory.Services.GetRequiredService<IndexerService>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (indexer.Status.LastFinished is null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.NotNull(indexer.Status.LastFinished);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_folder_downloads_as_one_zip_with_its_sub_folders()
    {
        var response = await Send(Client(), "/zip/Kits/Robot", Lan);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Robot.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Robot/arm.stl", "Robot/Body/torso.3mf", "Robot/readme.txt"], zip.Entries.Select(e => e.FullName).Order());
        Assert.Equal(TestMeshes.BinaryStl(), Read(zip.GetEntry("Robot/arm.stl")!));
        Assert.Equal(_torso, Read(zip.GetEntry("Robot/Body/torso.3mf")!));
    }

    [Theory]
    [InlineData("/zip/Kits/Nope", HttpStatusCode.NotFound)]
    [InlineData("/zip/Kits%2F..%2F..", HttpStatusCode.NotFound)]  // HttpClient resolves plain "/../" itself
    [InlineData("/zip/Kits/Robot%20Min", HttpStatusCode.NotFound)]  // a prefix of "Robot Mini" is not a folder
    public async Task Zip_refuses_unknown_folders(string url, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Send(Client(), url, Lan)).StatusCode);
    }

    [Fact]
    public async Task Zip_needs_a_login_from_outside()
    {
        Assert.Equal(HttpStatusCode.Redirect, (await Send(Client(), "/zip/Kits", Outside)).StatusCode);
    }

    [Fact]
    public async Task Slicer_links_open_one_file_without_a_login_and_nothing_else()
    {
        var link = await SlicerLink(Client(), "/slicer-link/Kits/Robot/Body/torso.3mf");
        var url = new Uri(link);
        var slicer = Client();

        Assert.StartsWith("/dl/", url.AbsolutePath);
        Assert.EndsWith("/torso.3mf", url.AbsolutePath);
        var download = await Send(slicer, url.PathAndQuery, Outside);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(_torso, await download.Content.ReadAsByteArrayAsync());

        var token = url.Segments[2].TrimEnd('/');
        Assert.Equal(HttpStatusCode.NotFound, (await Send(slicer, $"/dl/{token[..^4]}AAAA/torso.3mf", Outside)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Send(slicer, "/download/Lamp.3mf", Outside)).StatusCode);
    }

    [Fact]
    public async Task Expired_slicer_links_stop_working()
    {
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(SlicerLinks.Purpose).ToTimeLimitedDataProtector();
        var expired = protector.Protect("Lamp.3mf", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.NotFound, (await Send(Client(), $"/dl/{expired}/Lamp.3mf", Outside)).StatusCode);
    }

    [Theory]
    [InlineData("/slicer-link/Kits/Robot/readme.txt")]
    [InlineData("/slicer-link/Nope.3mf")]
    public async Task Slicer_links_are_only_for_models(string url)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Client(), url, Lan)).StatusCode);
    }

    [Theory]
    [InlineData("Robot Mini (v2).3mf", "Robot_Mini__v2_.3mf")]
    [InlineData("水草球.3mf", "___.3mf")]
    [InlineData("a./b.stl", "a__b.stl")]
    public void Slicer_file_names_are_kept_plain(string name, string expected)
    {
        Assert.Equal(expected, SlicerLinks.SafeName(name));
    }

    [Fact]
    public async Task Favourites_are_per_account_and_shared_on_the_home_network()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.CreateFirstAdmin("jurjen", Password);
        users.Create("guest", "guest password 123", isAdmin: false);
        var jurjen = Client();
        var guest = Client();
        var home = Client();
        await LogIn(jurjen, "jurjen", Password);
        await LogIn(guest, "guest", "guest password 123");

        Assert.True(await Toggle(jurjen, "Lamp.3mf"));
        Assert.True(await Toggle(home, "Kits/Robot/arm.stl", peer: Lan));

        Assert.Contains("Lamp.3mf", await Page(jurjen, "/favourites"));
        Assert.DoesNotContain("Lamp.3mf", await Page(guest, "/favourites"));
        Assert.Contains("arm.stl", await Page(home, "/favourites", Lan));
        Assert.DoesNotContain("arm.stl", await Page(jurjen, "/favourites"));
        Assert.Contains("aria-pressed=\"true\"", await Page(jurjen, "/browse"));

        Assert.False(await Toggle(jurjen, "Lamp.3mf"));
        Assert.DoesNotContain("Lamp.3mf", await Page(jurjen, "/favourites"));
    }

    [Fact]
    public async Task Favourites_need_a_known_file_and_a_login()
    {
        var home = Client();
        var page = await Page(home, "/browse", Lan);
        var unknown = await Post(home, "/favourites?handler=Toggle", Lan, [new("path", "Nope.3mf"), new("__RequestVerificationToken", Token(page))]);
        var outside = await Post(Client(), "/favourites?handler=Toggle", Outside, [new("path", "Lamp.3mf")]);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, outside.StatusCode);
    }

    [Fact]
    public async Task The_app_manifest_is_public_and_describes_the_site()
    {
        var response = await Send(Client(), "/manifest.webmanifest", Outside);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("standalone", json.RootElement.GetProperty("display").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("icons").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await Send(Client(), "/icons/maskable-512.png", Outside)).StatusCode);
    }

    [Fact]
    public async Task Include_subfolders_shows_every_model_below_a_folder_with_its_subfolder()
    {
        var client = Client();

        Assert.Empty(Cards(await Page(client, "/browse/Kits", Lan)));
        var page = await Page(client, "/browse/Kits?all=1", Lan);

        // Default order in this view: by folder, then name.
        Assert.Equal(["/file/Kits/Robot/arm.stl", "/file/Kits/Robot/Body/torso.3mf", "/file/Kits/Robot%20Mini/arm.stl"], Cards(page));
        Assert.Contains(">Robot/Body</a>", page);
        Assert.Contains("aria-checked=\"true\"", page);
        Assert.DoesNotContain("class=\"folder-tile\"", page);
    }

    [Fact]
    public async Task Model_lists_sort_and_filter_by_type()
    {
        var client = Client();
        var library = Path.Combine(_root, "library");
        long Size(string path) => new FileInfo(Path.Combine(library, path)).Length;
        var bySize = new[] { "Kits/Robot/arm.stl", "Kits/Robot/Body/torso.3mf", "Kits/Robot Mini/arm.stl" }
            .OrderByDescending(Size).ThenBy(p => Path.GetFileName(p), Display.NameOrder).ThenBy(p => Path.GetDirectoryName(p), Display.NameOrder)
            .Select(p => Display.FileUrl(p));

        Assert.Equal(bySize, Cards(await Page(client, "/browse/Kits?all=1&sort=size", Lan)));
        Assert.Equal(["/file/Kits/Robot/arm.stl", "/file/Kits/Robot%20Mini/arm.stl"], Cards(await Page(client, "/browse/Kits?all=1&type=stl", Lan)));
        Assert.Equal(["/file/Kits/Robot/Body/torso.3mf"], Cards(await Page(client, "/browse/Kits?all=1&type=3mf", Lan)));
        Assert.Empty(Cards(await Page(client, "/search?q=arm&type=3mf", Lan)));
        Assert.Equal(2, Cards(await Page(client, "/search?q=arm&type=stl", Lan)).Count);

        // Sort and filter links keep the other choices; unknown values fall back to the defaults.
        var filtered = await Page(client, "/browse/Kits?all=1&type=stl", Lan);
        Assert.Contains("href=\"/browse/Kits?all=1&amp;type=stl&amp;sort=recent\"", filtered);
        Assert.Equal(3, Cards(await Page(client, "/browse/Kits?all=1&type=nope&sort=nope", Lan)).Count);
    }

    [Theory]
    [InlineData("/browse/Kits/Nope/Deeper", "/browse/Kits", "Deeper")]
    [InlineData("/file/Kits/Robot/gone_arm.3mf", "/browse/Kits/Robot", "gone arm")]
    [InlineData("/file/Elsewhere/x.stl", "/browse", "x")]
    public async Task Missing_folders_and_files_get_a_page_pointing_at_the_nearest_folder(string url, string nearest, string search)
    {
        var response = await Send(Client(), url, Lan);
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("<h1>Not found</h1>", page);
        Assert.Contains($"class=\"button primary\" href=\"{nearest}\"", page);
        Assert.Contains($"name=\"q\" value=\"{search}\"", page);
    }

    [Fact]
    public async Task Any_unknown_page_a_browser_opens_gets_the_not_found_page()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/foo");
        request.Headers.Add("X-Test-Peer", Lan);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Not found</h1>", page);
        Assert.Contains("This page doesn't exist.", page);
    }

    [Theory]
    [InlineData("/img/0123456789abcdef/plate_1.png", Lan)]
    [InlineData("/dl/not-a-token/x.3mf", Outside)]
    [InlineData("/css/nope.css", Lan)]
    public async Task Previews_downloads_and_slicer_links_keep_a_bare_404(string url, string peer)
    {
        // Even when the client claims to want HTML, as a browser tab opening the link would.
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Test-Peer", peer);
        request.Headers.Add("Accept", "text/html,*/*");
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static List<string> Cards(string html) => CardPattern().Matches(html).Select(m => m.Groups[1].Value).ToList();

    [GeneratedRegex("class=\"card-thumb\" href=\"([^\"]+)\"")]
    private static partial Regex CardPattern();

    private HttpClient Client() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> Send(HttpClient client, string url, string peer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Test-Peer", peer);
        return await client.SendAsync(request);
    }

    private static async Task<string> Page(HttpClient client, string url, string peer = Outside) =>
        await (await Send(client, url, peer)).Content.ReadAsStringAsync();

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string peer, KeyValuePair<string, string>[] form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Test-Peer", peer);
        request.Headers.Add("Accept", "application/json");
        return await client.SendAsync(request);
    }

    private static async Task LogIn(HttpClient client, string user, string password)
    {
        var token = Token(await Page(client, "/login"));
        var response = await Post(client, "/login", Outside,
            [new("UserName", user), new("Password", password), new("Remember", "true"), new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<bool> Toggle(HttpClient client, string path, string peer = Outside)
    {
        var token = Token(await Page(client, "/browse", peer));
        var response = await Post(client, "/favourites?handler=Toggle", peer, [new("path", path), new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("favourite").GetBoolean();
    }

    private static async Task<string> SlicerLink(HttpClient client, string url)
    {
        var response = await Send(client, url, Lan);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("url").GetString()!;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void Write(string root, string relativePath, byte[] content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static string Token(string html) => TokenPattern().Match(html) is { Success: true } match
        ? match.Groups[1].Value
        : throw new InvalidOperationException("No antiforgery token on the page.");

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

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
