namespace Library.Web;

/// <summary>
/// Maps URL paths (<c>Stuff/Lamp.3mf</c>) to files under the library root and refuses anything that would escape it.
/// </summary>
public sealed class LibraryPaths(LibraryOptions options)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.LibraryPath));

    /// <summary>Normalises a relative path to <c>a/b/c</c> form; false for <c>.</c>, <c>..</c>, backslashes or NUL.</summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        if (path.Contains('\0') || path.Contains('\\'))
        {
            return false;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }

    public bool TryGetFullPath(string? relative, out string fullPath)
    {
        fullPath = "";
        if (!TryNormalize(relative, out var normalized))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (candidate != _root && !candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
