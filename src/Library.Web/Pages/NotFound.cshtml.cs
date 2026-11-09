using Library.Web.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>
/// Shown (status 404) for folders and files that aren't in the library: old links, or something moved or renamed in
/// the library folder. Points at the nearest folder that still exists and offers a search for the missing name.
/// </summary>
public sealed class NotFoundModel(LibraryQueries queries) : PageModel
{
    /// <summary>The missing folder or file name, when the request was for one.</summary>
    public string? Missing { get; private set; }

    public bool MissingFile { get; private set; }

    /// <summary>The closest existing folder above the missing path ("" is the library itself).</summary>
    public string Nearest { get; private set; } = "";

    public string SearchText { get; private set; } = "";

    public void OnGet()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        ViewData["Title"] = "Not found";
        ViewData["ShellClass"] = "shell--narrow";

        // Path values are already decoded ("/browse/Kits/Robot Mini").
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath ?? "";
        string? requested = null;
        if (original.StartsWith("/browse/", StringComparison.Ordinal))
        {
            requested = original["/browse/".Length..];
        }
        else if (original.StartsWith("/file/", StringComparison.Ordinal))
        {
            requested = original["/file/".Length..];
            MissingFile = true;
        }

        if (requested is null || !LibraryPaths.TryNormalize(requested, out var path) || path.Length == 0)
        {
            return;
        }

        var slash = path.LastIndexOf('/');
        Missing = path[(slash + 1)..];
        SearchText = Display.PrettyName(Missing);

        var folder = slash < 0 ? "" : path[..slash];
        while (folder.Length > 0 && !queries.FolderExists(folder))
        {
            slash = folder.LastIndexOf('/');
            folder = slash < 0 ? "" : folder[..slash];
        }

        Nearest = folder;
    }
}
