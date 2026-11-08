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

    /// <summary>The end of a long folder path is the telling part: "…/ARMS/LEFT" (the full path goes in the tooltip).</summary>
    public string? FolderShort => FolderLabel?.Split('/') is { Length: > 2 } parts
        ? "…/" + string.Join('/', parts[^2..])
        : FolderLabel;
}
