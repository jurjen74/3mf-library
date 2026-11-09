using Library.Web.Data;
using Library.Web.Indexing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Library.Web.Pages.Shared;

/// <summary>One choice in the sort or type bar: a plain link, so it works without JavaScript and can be bookmarked.</summary>
public sealed record ListOption(string Label, string Url, bool Selected, long? Count = null);

/// <summary>
/// Sorting (<c>?sort=</c>) and the 3MF/STL filter (<c>?type=</c>) for a list of models. Other query values (search
/// text, "include subfolders") are kept in the links; the default choices are left out of the URL.
/// </summary>
public sealed class ModelListView
{
    public const string Relevance = "relevance";
    public const string Folder = "folder";
    public const string Name = "name";
    public const string Recent = "recent";
    public const string Size = "size";

    private static readonly Dictionary<string, string> SortLabels = new(StringComparer.Ordinal)
    {
        [Relevance] = "Best match",
        [Folder] = "Folder",
        [Name] = "Name",
        [Recent] = "Recently changed",
        [Size] = "Size",
    };

    public required IReadOnlyList<FileRow> Models { get; init; }
    public required IReadOnlyList<ListOption> SortOptions { get; init; }
    public required IReadOnlyList<ListOption> TypeOptions { get; init; }

    /// <summary>A 3MF or STL filter is on: other (non-model) files are hidden too.</summary>
    public bool Filtered { get; init; }

    /// <param name="sorts">The sort orders this page offers; the first is the default.</param>
    public static ModelListView Create(HttpRequest request, IEnumerable<FileRow> models, params string[] sorts) =>
        Create(request, models, null, sorts);

    /// <param name="kindCounts">
    /// Models per kind for the type buttons, when <paramref name="models"/> is already filtered or cut short (search);
    /// null to count <paramref name="models"/> itself.
    /// </param>
    public static ModelListView Create(HttpRequest request, IEnumerable<FileRow> models,
        IReadOnlyDictionary<string, long>? kindCounts, string[] sorts)
    {
        var all = models.ToList();
        var sort = request.Query["sort"].ToString();
        if (!sorts.Contains(sort, StringComparer.Ordinal))
        {
            sort = sorts[0];
        }

        var kinds = kindCounts?.Where(k => k.Value > 0).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal)
                    ?? all.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var type = TypeFilter(request);
        var shown = type.Length == 0 ? all : all.Where(f => f.Kind == type).ToList();

        // Only offer the filter when the list mixes both kinds (or a filter is already on and must be undone).
        var typeOptions = kinds.Count > 1 || type.Length > 0
            ? new List<ListOption>
            {
                new("All", With(request, "type", null), type.Length == 0, kindCounts is null ? all.Count : kinds.Values.Sum()),
                new("3MF", With(request, "type", FileKinds.ThreeMf), type == FileKinds.ThreeMf, kinds.GetValueOrDefault(FileKinds.ThreeMf)),
                new("STL", With(request, "type", FileKinds.Stl), type == FileKinds.Stl, kinds.GetValueOrDefault(FileKinds.Stl)),
            }
            : [];

        return new ModelListView
        {
            Models = Sort(shown, sort),
            SortOptions = shown.Count > 1
                ? sorts.Select(s => new ListOption(SortLabels[s], With(request, "sort", s == sorts[0] ? null : s), s == sort)).ToList()
                : [],
            TypeOptions = typeOptions,
            Filtered = type.Length > 0,
        };
    }

    /// <summary>The <c>?type=</c> filter: <c>3mf</c>, <c>stl</c>, or "" for all models.</summary>
    public static string TypeFilter(HttpRequest request) =>
        request.Query["type"].ToString() is var type && type is FileKinds.ThreeMf or FileKinds.Stl ? type : "";

    private static List<FileRow> Sort(List<FileRow> files, string sort) => sort switch
    {
        Relevance => files,
        Folder => files.OrderBy(f => f.Folder, FolderOrder).ThenBy(f => f.Name, Display.NameOrder).ToList(),
        Recent => files.OrderByDescending(f => f.Mtime).ThenBy(f => f.Name, Display.NameOrder).ThenBy(f => f.Folder, FolderOrder).ToList(),
        Size => files.OrderByDescending(f => f.Size).ThenBy(f => f.Name, Display.NameOrder).ThenBy(f => f.Folder, FolderOrder).ToList(),
        _ => files.OrderBy(f => f.Name, Display.NameOrder).ThenBy(f => f.Folder, FolderOrder).ToList(),
    };

    /// <summary>Folder paths in tree order: "Robot", "Robot/Body", "Robot Mini" (per segment, not per character).</summary>
    private static readonly Comparer<string> FolderOrder = Comparer<string>.Create((a, b) =>
    {
        var left = a.Split('/');
        var right = b.Split('/');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = Display.NameOrder.Compare(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    });

    /// <summary>The current page's URL with one query value set (or removed when <paramref name="value"/> is null).</summary>
    public static string With(HttpRequest request, string key, string? value)
    {
        var query = new QueryBuilder();
        foreach (var (name, values) in request.Query)
        {
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in values)
                {
                    query.Add(name, item ?? "");
                }
            }
        }

        if (value is not null)
        {
            query.Add(key, value);
        }

        return request.PathBase.Add(request.Path).ToUriComponent() + query.ToQueryString().ToUriComponent();
    }
}
