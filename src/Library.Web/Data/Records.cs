using System.Text.Json;

namespace Library.Web.Data;

/// <summary>A file as the browse page needs it (no description: that can be large and is only for the detail page).</summary>
public sealed class FileRow
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Size { get; init; }
    public long Mtime { get; init; }
    public string? CacheKey { get; init; }
    public string? Title { get; init; }
    public string? Designer { get; init; }
    public string? License { get; init; }
    public long PlateCount { get; init; }
    public string? Colours { get; init; }
    public string? Cover { get; init; }
    public string? CoverSmall { get; init; }
    public string? Error { get; init; }

    public IReadOnlyList<string> ColourList => Colours is null
        ? []
        : JsonSerializer.Deserialize<List<string>>(Colours) ?? [];
}

public sealed class FolderRow
{
    public string Path { get; init; } = "";
    public string? Parent { get; init; }
    public string Name { get; init; } = "";
    public long ModelCount { get; init; }
}

public sealed class CoverImage
{
    public string CacheKey { get; init; } = "";
    public string CoverSmall { get; init; } = "";
}

public sealed record LibraryStats(long Models, long ThreeMf, long Stl, long Other, long Errors);
