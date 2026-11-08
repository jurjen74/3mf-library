using Library.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>The most recently modified models across the whole library.</summary>
public sealed class RecentModel(LibraryQueries queries) : PageModel
{
    public const int Count = 60;

    public IReadOnlyList<FileRow> Models { get; private set; } = [];

    public void OnGet()
    {
        Models = queries.Recent(Count);
        ViewData["Title"] = "Recent";
        ViewData["ShellClass"] = "shell--wide";
    }
}
