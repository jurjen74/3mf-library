using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Library.Web;
using Library.Web.Auth;
using Library.Web.Data;
using Library.Web.Indexing;
using Library.Web.Meshes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Library.Web.Updates;

var builder = WebApplication.CreateBuilder(args);

var options = LibraryOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(options.DataPath);
Directory.CreateDirectory(options.CachePath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LibraryDb>();
builder.Services.AddSingleton<LibraryPaths>();
builder.Services.AddSingleton<LibraryQueries>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<IndexerService>();
builder.Services.AddSingleton<MeshWorkGate>();
builder.Services.AddSingleton<MeshService>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<SetupCode>();
builder.Services.AddSingleton<SlicerLinks>();
builder.Services.AddSingleton<FavouriteStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerService>());
builder.Services.AddHttpClient(UpdateChecker.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("3mf-library");   // GitHub's API requires a User-Agent
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")))
    .SetApplicationName("3mf-library");

// Accounts from users.db; the login cookie lasts 30 days (sliding) and dies when the password changes.
// Every endpoint needs a login unless marked anonymous.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.LogoutPath = "/logout";
        cookie.Cookie.Name = "library.auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = Session.ValidateAsync;
    });
builder.Services.AddAuthorization(authorization =>
    authorization.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Only addresses in KNOWN_PROXIES may set X-Forwarded-For/-Proto; anyone else's headers are ignored.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.ForwardLimit = 1;
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var proxy in options.KnownProxies)
    {
        forwarded.KnownProxies.Add(proxy);
    }
});

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("login", context => HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("read"));
    // Password changes count per account, not per address: people behind one connection (a household, a mobile
    // carrier) don't use up each other's logins, and guessing the current password stays limited per account.
    limiter.AddPolicy("account", context => HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(
            Session.UserId(context.User) is { } userId
                ? $"user:{userId}"
                : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("read"));
    limiter.OnRejected = async (rejected, cancellationToken) =>
    {
        rejected.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await rejected.HttpContext.Response.WriteAsync("Too many attempts. Try again in a few minutes.", cancellationToken);
    };
});

builder.Services.AddRazorPages();

var app = builder.Build();
app.Services.GetRequiredService<LibraryDb>().Migrate();
var users = app.Services.GetRequiredService<UserStore>();
users.Migrate();
if (!users.HasUsers() && SetupCode.IsRequired(options))
{
    // No trusted networks: the setup page is open to any direct visitor, so it asks for this code.
    app.Logger.LogWarning(
        "No account yet. Open /setup and use this setup code to create the first (admin) account: {SetupCode}",
        app.Services.GetRequiredService<SetupCode>().Display);
}

app.Logger.LogInformation(
    "Library: {Library}  Data: {Data}  Scan interval: {Interval}  Accounts: {Accounts}  Trusted networks: {Trusted}  Known proxies: {Proxies}",
    options.LibraryPath, options.DataPath, options.ScanInterval,
    users.HasUsers() ? "set up" : "none yet (first-run setup open to the home network)",
    options.TrustedNetworks.Count == 0 ? "none" : string.Join(", ", options.TrustedNetworks),
    options.KnownProxies.Count == 0 ? "none" : string.Join(", ", options.KnownProxies));

app.Use(async (context, next) =>
{
    TrustedNetwork.RecordConnection(context);
    await next();
});

// With an empty KnownProxies list the middleware would believe X-Forwarded-For from anyone, so it only runs when a
// proxy is configured.
if (options.KnownProxies.Count > 0)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Missing pages, folders and files get a proper page (nearest existing folder, search): /browse and /file always,
// any other address when a browser asks for a page (Accept: text/html, e.g. /foo). Files — previews, meshes,
// downloads, ZIPs, slicer links, static assets — keep their bare 404, so a slicer or <img> never gets HTML.
string[] fileRoutes = ["/img", "/mesh", "/download", "/dl", "/zip", "/slicer-link", "/health", "/manifest.webmanifest",
    "/css", "/js", "/lib", "/icons", "/fonts", "/backgrounds"];
app.UseWhen(
    context =>
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || fileRoutes.Any(route => request.Path.StartsWithSegments(route)))
        {
            return false;
        }

        return request.Path.StartsWithSegments("/browse") || request.Path.StartsWithSegments("/file") ||
               request.Headers.Accept.Any(accept => accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    },
    branch => branch.UseStatusCodePagesWithReExecute("/not-found"));

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    TrustedNetwork.SignInIfTrusted(context, options);
    await next();
});
// After authentication, so the "account" policy knows who is signed in.
app.UseRateLimiter();
app.Use(Session.RequirePasswordChange);
app.UseAuthorization();

// The login page needs its CSS before anyone is logged in.
app.MapStaticAssets().AllowAnonymous();

app.MapGet("/", () => Results.Redirect("/browse"));

// Web app manifest: lets phones and desktops install the site as an app (home screen icon, own window).
app.MapGet("/manifest.webmanifest", () => Results.Json(new
{
    name = options.SiteTitle,
    short_name = options.SiteTitle.Length <= 12 ? options.SiteTitle : "3D Prints",
    description = "Browse, preview and download the 3D print library.",
    start_url = "/browse",
    scope = "/",
    display = "standalone",
    background_color = "#131416",
    theme_color = "#2f6fde",
    icons = new object[]
    {
        new { src = "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
        new { src = "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
        new { src = "/icons/maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
    },
}, contentType: "application/manifest+json")).AllowAnonymous();

// 503 while scans fail, for a monitor. Anonymous, so no error text: that's in the log and on the site.
app.MapGet("/health", (IndexerService indexer) =>
{
    var status = indexer.Status;
    return Results.Json(new
    {
        status = status.LastScanFailed ? "scan-failed" : "ok",
        lastScan = status.LastFinished,
        lastFailure = status.LastFailed,
        libraryOnline = status.LastResult?.LibraryOnline,
    }, statusCode: status.LastScanFailed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
}).AllowAnonymous();

// Previews extracted into the cache. The key changes whenever the file does, so they can be cached forever —
// privately: they sit behind the login and must not end up in a shared (proxy) cache.
var cacheKeyPattern = new Regex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
var cacheFilePattern = new Regex(@"^[a-z0-9_]{1,64}\.(png|jpg|webp)$", RegexOptions.CultureInvariant);
app.MapGet("/img/{key}/{file}", (string key, string file, HttpContext context) =>
{
    if (!cacheKeyPattern.IsMatch(key) || !cacheFilePattern.IsMatch(file))
    {
        return Results.NotFound();
    }

    var path = Path.Combine(options.CachePath, key, file);
    if (!File.Exists(path))
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(path, FileKinds.ContentType(file));
});

// Viewer meshes, built on first request and cached gzip-compressed; sent as-is with Content-Encoding.
app.MapGet("/mesh/{key}/{name}.bin", async (string key, string name, MeshService meshes, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!cacheKeyPattern.IsMatch(key) || !MeshService.IsValidName(name))
    {
        return Results.NotFound();
    }

    var result = await meshes.GetAsync(key, name, cancellationToken);
    if (result.Error is not null)
    {
        return Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    if (result.Path is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.ContentEncoding = "gzip";
    context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(result.Path, "application/octet-stream");
});

// "Open in Bambu Studio": a logged-in visitor gets a short-lived signed link for one model...
app.MapGet("/slicer-link/{**path}", (string? path, HttpContext context, LibraryQueries queries, SlicerLinks links) =>
{
    if (!LibraryPaths.TryNormalize(path, out var relative) || queries.FindFile(relative) is not { } file || !FileKinds.IsModel(file.Kind))
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { url = links.Create(context.Request, file.Path, file.Name) });
});

// ...which the slicer downloads without a cookie. Only a valid, unexpired token opens it, and only for that file.
app.MapGet("/dl/{token}/{name}", (string token, string name, LibraryQueries queries, LibraryPaths paths, SlicerLinks links) =>
{
    if (links.Resolve(token) is not { } relative ||
        queries.FindFile(relative) is not { } file ||
        !paths.TryGetFullPath(relative, out var fullPath) ||
        !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, FileKinds.ContentType(file.Name), file.Name, enableRangeProcessing: true);
}).AllowAnonymous();

// A whole folder as one ZIP, streamed while it's built (no size known up front, so no Content-Length).
app.MapGet("/zip/{**path}", async (string? path, HttpContext context, LibraryQueries queries, LibraryPaths paths, ILogger<Program> log) =>
{
    if (!LibraryPaths.TryNormalize(path, out var folder) || !queries.FolderExists(folder) ||
        queries.FilesUnder(folder) is not { Count: > 0 } files)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var name = FolderZip.ArchiveName(folder);
    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
    {
        FileNameStar = name,
        FileName = new string(name.Select(c => c < 128 && c != '"' ? c : '_').ToArray()),
    }.ToString();
    context.Response.Headers.CacheControl = "private, no-store";

    // ZipArchive writes synchronously; allow it for this response only, on a worker thread.
    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
    try
    {
        await Task.Run(() => FolderZip.Write(context.Response.Body, folder, files, paths, context.RequestAborted), context.RequestAborted);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
        log.LogInformation("Folder download of '{Folder}' stopped: {Message}", folder, ex.Message);
    }
});

// Only files the index knows about can be downloaded, so ignored files and anything outside the library stay out.
app.MapGet("/download/{**path}", (string? path, LibraryQueries queries, LibraryPaths paths) =>
{
    if (!LibraryPaths.TryNormalize(path, out var relative) ||
        queries.FindFile(relative) is not { } file ||
        !paths.TryGetFullPath(relative, out var fullPath) ||
        !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, FileKinds.ContentType(file.Name), file.Name, enableRangeProcessing: true);
});

app.MapRazorPages();

app.Run();

/// <summary>Exposed for the end-to-end tests (WebApplicationFactory).</summary>
public partial class Program;
