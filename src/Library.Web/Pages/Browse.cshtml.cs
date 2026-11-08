using Library.Web.Data;
using Library.Web.Indexing;
using Library.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

public sealed class FolderNode(string path, string name)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public List<FolderNode> Children { get; } = [];
    public long ModelCount { get; set; }
    public bool IsOpen { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed record Crumb(string Name, string Path);

public sealed record FolderTile(FolderNode Folder, IReadOnlyList<CoverImage> Covers);

/// <summary>One folder of the library: its sub-folders, its models as cards, and its other files as a list.</summary>
public sealed class BrowseModel(LibraryQueries queries, IndexerService indexer) : PageModel
{
    public string FolderPath { get; private set; } = "";
    public string FolderName { get; private set; } = "";
    public FolderNode Tree { get; private set; } = new("", "");
    public IReadOnlyList<Crumb> Crumbs { get; private set; } = [];
    public IReadOnlyList<FolderTile> Subfolders { get; private set; } = [];
    public ModelListView? List { get; private set; }
    public IReadOnlyList<FileRow> OtherFiles { get; private set; } = [];

    /// <summary><c>?all=1</c>: the models of every folder below this one in one grid, instead of folder tiles.</summary>
    public bool IncludeSubfolders { get; private set; }

    public bool HasSubfolders => Subfolders.Count > 0;

    /// <summary>Files and bytes in this folder and below, for the ZIP button.</summary>
    public (long Files, long Bytes) Totals { get; private set; }

    /// <summary>True while the very first scan hasn't produced anything to show yet.</summary>
    public bool StillIndexing { get; private set; }

    public IActionResult OnGet(string? path)
    {
        if (!LibraryPaths.TryNormalize(path, out var folder))
        {
            return NotFound();
        }

        var rows = queries.Folders();
        if (rows.Count == 0)
        {
            StillIndexing = indexer.Status.LastFinished is null;
            return folder.Length == 0 || StillIndexing ? Page() : NotFound();
        }

        var nodes = BuildTree(rows, out var root);
        if (!nodes.TryGetValue(folder, out var current))
        {
            return NotFound();
        }

        Tree = root;
        FolderPath = folder;
        FolderName = current.Name;
        MarkOpen(nodes, current);
        Crumbs = BuildCrumbs(folder);

        Subfolders = current.Children
            .Select(child => new FolderTile(child, queries.CoversUnder(child.Path, 4)))
            .ToList();

        IncludeSubfolders = HasSubfolders && Request.Query["all"] == "1";
        var files = IncludeSubfolders ? queries.FilesUnder(folder) : queries.FilesIn(folder);
        Totals = queries.FolderTotals(folder);
        List = ModelListView.Create(Request, files.Where(f => FileKinds.IsModel(f.Kind)),
            IncludeSubfolders ? [ModelListView.Folder, ModelListView.Name, ModelListView.Recent, ModelListView.Size]
                              : [ModelListView.Name, ModelListView.Recent, ModelListView.Size]);
        OtherFiles = List.Filtered
            ? []
            : files.Where(f => !FileKinds.IsModel(f.Kind))
                .OrderBy(f => f.Folder, Display.NameOrder)
                .ThenBy(f => f.Name, Display.NameOrder)
                .ToList();

        ViewData["Title"] = folder.Length == 0 ? "Library" : current.Name;
        ViewData["FolderPath"] = folder;
        return Page();
    }

    public IActionResult OnPostRescan(string? returnPath)
    {
        indexer.RequestScan();
        return LibraryPaths.TryNormalize(returnPath, out var folder)
            ? Redirect(Display.BrowseUrl(folder))
            : Redirect(Display.BrowseUrl(""));
    }

    private static Dictionary<string, FolderNode> BuildTree(IReadOnlyList<FolderRow> rows, out FolderNode root)
    {
        var nodes = rows.ToDictionary(r => r.Path, r => new FolderNode(r.Path, r.Name) { ModelCount = r.ModelCount }, StringComparer.Ordinal);
        if (!nodes.TryGetValue("", out root!))
        {
            root = new FolderNode("", "");
            nodes[""] = root;
        }

        foreach (var row in rows)
        {
            if (row.Path.Length > 0 && row.Parent is not null && nodes.TryGetValue(row.Parent, out var parent))
            {
                parent.Children.Add(nodes[row.Path]);
            }
        }

        SortAndCount(root);
        return nodes;
    }

    private static long SortAndCount(FolderNode node)
    {
        node.Children.Sort((a, b) => Display.NameOrder.Compare(a.Name, b.Name));
        foreach (var child in node.Children)
        {
            node.ModelCount += SortAndCount(child);
        }

        return node.ModelCount;
    }

    private static void MarkOpen(Dictionary<string, FolderNode> nodes, FolderNode current)
    {
        current.IsCurrent = true;
        var path = current.Path;
        while (true)
        {
            if (nodes.TryGetValue(path, out var node))
            {
                node.IsOpen = true;
            }

            if (path.Length == 0)
            {
                break;
            }

            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
    }

    private static List<Crumb> BuildCrumbs(string folder)
    {
        var crumbs = new List<Crumb> { new("Library", "") };
        if (folder.Length == 0)
        {
            return crumbs;
        }

        var parts = folder.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)])));
        }

        return crumbs;
    }
}
