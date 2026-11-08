using Library.Web.Data;
using Library.Web.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Library.Web.Pages;

/// <summary>One file: plate previews, photos, facts and description, with download and previous/next in its folder.</summary>
public sealed class FileModel(LibraryQueries queries) : PageModel
{
    public FileRow Entry { get; private set; } = new();
    public IReadOnlyList<Crumb> Crumbs { get; private set; } = [];
    public IReadOnlyList<PlateRow> Plates { get; private set; } = [];
    public IReadOnlyList<PhotoRow> Photos { get; private set; } = [];
    public PlateRow? SelectedPlate { get; private set; }
    public string? DescriptionHtml { get; private set; }
    public FileRow? Previous { get; private set; }
    public FileRow? Next { get; private set; }
    public int PositionInFolder { get; private set; }
    public int ModelsInFolder { get; private set; }

    public long? TotalPrintSeconds => Plates.Any(p => p.PrintSeconds is not null) ? Plates.Sum(p => p.PrintSeconds ?? 0) : null;

    public double? TotalWeightGrams => Plates.Any(p => p.WeightGrams is not null) ? Plates.Sum(p => p.WeightGrams ?? 0) : null;

    public IActionResult OnGet(string? path, int? plate)
    {
        if (!LibraryPaths.TryNormalize(path, out var relative) || relative.Length == 0 ||
            queries.FindFile(relative) is not { } file)
        {
            return NotFound();
        }

        Entry = file;
        Crumbs = BuildCrumbs(file);

        if (file.Kind == FileKinds.ThreeMf)
        {
            Plates = queries.Plates(file.Id);
            Photos = queries.Photos(file.Id);
            DescriptionHtml = queries.Description(file.Id);
            SelectedPlate = Plates.FirstOrDefault(p => p.Idx == plate && p.Image is not null)
                            ?? Plates.FirstOrDefault(p => p.Image is not null);
        }

        if (FileKinds.IsModel(file.Kind))
        {
            var models = queries.FilesIn(file.Folder)
                .Where(f => FileKinds.IsModel(f.Kind))
                .OrderBy(f => f.Name, Display.NameOrder)
                .ToList();
            var index = models.FindIndex(f => f.Id == file.Id);
            if (index >= 0)
            {
                Previous = index > 0 ? models[index - 1] : null;
                Next = index < models.Count - 1 ? models[index + 1] : null;
                PositionInFolder = index + 1;
                ModelsInFolder = models.Count;
            }
        }

        ViewData["Title"] = Display.Title(file);
        ViewData["FolderPath"] = file.Folder;
        ViewData["ShellClass"] = "shell--wide";
        return Page();
    }

    public static string PlateLabel(PlateRow plate) =>
        plate.Name is { Length: > 0 } name ? $"Plate {plate.Idx} · {name}" : $"Plate {plate.Idx}";

    public static string PlateDetails(PlateRow plate)
    {
        var parts = new List<string>();
        if (plate.PrintSeconds is { } seconds)
        {
            parts.Add(Display.Duration(seconds));
        }

        if (plate.WeightGrams is { } grams)
        {
            parts.Add(Display.Grams(grams));
        }

        if (plate.ObjectList.Count > 0)
        {
            parts.Add(Display.Plural(plate.ObjectList.Count, "object", "objects"));
        }

        return string.Join(" · ", parts);
    }

    private static List<Crumb> BuildCrumbs(FileRow file)
    {
        var crumbs = new List<Crumb> { new("Library", "") };
        if (file.Folder.Length > 0)
        {
            var parts = file.Folder.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)])));
            }
        }

        return crumbs;
    }
}
