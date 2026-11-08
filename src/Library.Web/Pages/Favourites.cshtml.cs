using Library.Web.Auth;
using Library.Web.Data;
using Library.Web.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>Your starred files, and the endpoint the star buttons post to.</summary>
public sealed class FavouritesModel(FavouriteStore favourites, LibraryQueries queries) : PageModel
{
    public IReadOnlyList<FileRow> Models { get; private set; } = [];
    public IReadOnlyList<FileRow> OtherFiles { get; private set; } = [];
    public bool Shared { get; private set; }

    public IActionResult OnGet()
    {
        if (FavouriteStore.Owner(HttpContext) is not { } owner)
        {
            return Forbid();
        }

        var files = queries.FilesByPath(favourites.Paths(owner)).OrderBy(f => f.Name, Display.NameOrder).ToList();
        Models = files.Where(f => FileKinds.IsModel(f.Kind)).ToList();
        OtherFiles = files.Where(f => !FileKinds.IsModel(f.Kind)).ToList();
        Shared = owner == FavouriteStore.HomeNetworkOwner;
        ViewData["Title"] = "Favourites";
        ViewData["ShellClass"] = "shell--wide";
        return Page();
    }

    /// <summary>Star or unstar; JSON for the buttons, a redirect back for plain form posts.</summary>
    public IActionResult OnPostToggle(string? path)
    {
        if (FavouriteStore.Owner(HttpContext) is not { } owner)
        {
            return Forbid();
        }

        if (!LibraryPaths.TryNormalize(path, out var relative) || queries.FindFile(relative) is null)
        {
            return NotFound();
        }

        var favourite = favourites.Toggle(owner, relative);
        if (Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { favourite });
        }

        return Redirect(Display.FileUrl(relative));
    }
}
