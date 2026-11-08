using Library.Web.Data;

namespace Library.Web.Pages.Shared;

/// <summary>
/// A model card; <see cref="ShowFolder"/> adds the folder for lists that mix folders (search, recent). With
/// <see cref="FolderBase"/> the folder is shown relative to it, and not at all for files directly inside it.
/// </summary>
public sealed record ModelCard(FileRow File, bool ShowFolder = false, string? FolderBase = null)
{
    public string? FolderLabel =>
        !ShowFolder ? null
        : FolderBase is null ? (File.Folder.Length == 0 ? "Library" : File.Folder)
        : File.Folder == FolderBase ? null
        : FolderBase.Length == 0 ? File.Folder
        : File.Folder[(FolderBase.Length + 1)..];

    /// <summary>
    /// The file name adds something only when the title differs from it: "Count Dooku's Lightsaber" from
    /// "dooku_saber_v2.3mf" yes, "Lamp" from "Lamp.3mf" or "Moon_Lamp" from "Moon Lamp.3mf" no.
    /// </summary>
    public bool ShowFileName => !string.Equals(Simplify(Display.Title(File)), Simplify(Display.PrettyName(File.Name)), StringComparison.Ordinal);

    private static string Simplify(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    /// <summary>The end of a long folder path is the telling part: "…/ARMS/LEFT" (the full path goes in the tooltip).</summary>
    public string? FolderShort => FolderLabel?.Split('/') is { Length: > 2 } parts
        ? "…/" + string.Join('/', parts[^2..])
        : FolderLabel;
}
