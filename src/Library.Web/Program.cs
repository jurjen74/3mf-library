using System.Text.RegularExpressions;
using Library.Web;
using Library.Web.Data;
using Library.Web.Indexing;
using Library.Web.Meshes;
using Microsoft.AspNetCore.DataProtection;

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
builder.Services.AddSingleton<MeshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerService>());
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")))
    .SetApplicationName("3mf-library");
builder.Services.AddRazorPages();

var app = builder.Build();
app.Services.GetRequiredService<LibraryDb>().Migrate();
app.Logger.LogInformation("Library: {Library}  Data: {Data}  Scan interval: {Interval}",
    options.LibraryPath, options.DataPath, options.ScanInterval);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.MapStaticAssets();

app.MapGet("/", () => Results.Redirect("/browse"));

app.MapGet("/health", (IndexerService indexer) => Results.Ok(new
{
    status = "ok",
    lastScan = indexer.Status.LastFinished,
    libraryOnline = indexer.Status.LastResult?.LibraryOnline,
}));

// Previews extracted into the cache. The key changes whenever the file does, so they can be cached forever.
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

    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
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
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(result.Path, "application/octet-stream");
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
