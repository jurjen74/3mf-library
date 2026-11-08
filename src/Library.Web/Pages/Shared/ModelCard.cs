using Library.Web.Data;

namespace Library.Web.Pages.Shared;

/// <summary>A model card; <see cref="ShowFolder"/> adds the folder for lists that mix folders (search, recent).</summary>
public sealed record ModelCard(FileRow File, bool ShowFolder = false);
