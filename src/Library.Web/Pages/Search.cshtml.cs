using Library.Web.Data;
using Library.Web.Indexing;
using Library.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>Search by file name, title, designer, folder and object names; matching folders are listed first.</summary>
public sealed class SearchModel(LibraryQueries queries) : PageModel
{
    private const int MaxResults = 200;

    public string Query { get; private set; } = "";
    public IReadOnlyList<FolderRow> Folders { get; private set; } = [];
    public ModelListView List { get; private set; } = null!;
    public IReadOnlyList<FileRow> OtherFiles { get; private set; } = [];
    public bool Truncated { get; private set; }

    public void OnGet(string? q)
    {
        Query = (q ?? "").Trim();
        ViewData["Title"] = Query.Length == 0 ? "Search" : $"Search: {Query}";
        ViewData["ShellClass"] = "shell--wide";
        ViewData["Query"] = Query;
        string[] sorts = [ModelListView.Relevance, ModelListView.Name, ModelListView.Recent, ModelListView.Size];
        if (Query.Length == 0)
        {
            List = ModelListView.Create(Request, [], sorts);
            return;
        }

        Folders = queries.SearchFolders(Query, 24);

        // A 3MF/STL filter is applied in the query, so matches beyond the first 200 of all kinds still show.
        var type = ModelListView.TypeFilter(Request);
        var files = queries.Search(Query, MaxResults + 1, type.Length == 0 ? null : type);
        Truncated = files.Count > MaxResults;
        List = ModelListView.Create(Request, files.Take(MaxResults).Where(f => FileKinds.IsModel(f.Kind)),
            queries.SearchModelCounts(Query), sorts);
        OtherFiles = List.Filtered ? [] : files.Take(MaxResults).Where(f => !FileKinds.IsModel(f.Kind)).ToList();
    }
}
