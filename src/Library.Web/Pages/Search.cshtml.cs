using Library.Web.Data;
using Library.Web.Indexing;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>Search by file name, title, designer, folder and object names; matching folders are listed first.</summary>
public sealed class SearchModel(LibraryQueries queries) : PageModel
{
    private const int MaxResults = 200;

    public string Query { get; private set; } = "";
    public IReadOnlyList<FolderRow> Folders { get; private set; } = [];
    public IReadOnlyList<FileRow> Models { get; private set; } = [];
    public IReadOnlyList<FileRow> OtherFiles { get; private set; } = [];
    public bool Truncated { get; private set; }

    public void OnGet(string? q)
    {
        Query = (q ?? "").Trim();
        ViewData["Title"] = Query.Length == 0 ? "Search" : $"Search: {Query}";
        ViewData["ShellClass"] = "shell--wide";
        ViewData["Query"] = Query;
        if (Query.Length == 0)
        {
            return;
        }

        Folders = queries.SearchFolders(Query, 24);
        var files = queries.Search(Query, MaxResults + 1);
        Truncated = files.Count > MaxResults;
        Models = files.Take(MaxResults).Where(f => FileKinds.IsModel(f.Kind)).ToList();
        OtherFiles = files.Take(MaxResults).Where(f => !FileKinds.IsModel(f.Kind)).ToList();
    }
}
