using System.IO.Compression;
using Library.Web.Data;

namespace Library.Web;

/// <summary>
/// Streams a folder (with its sub-folders) as a ZIP straight from the library: nothing is staged on disk. 3MFs, images,
/// PDFs and archives are already compressed and are stored as-is; STLs and text are compressed quickly.
/// </summary>
public static class FolderZip
{
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3mf", ".zip", ".7z", ".rar", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".pdf", ".gz", ".mp4",
    };

    /// <summary>File name for the download: the folder's own name, or "Library" for the root.</summary>
    public static string ArchiveName(string folder) =>
        (folder.Length == 0 ? "Library" : folder[(folder.LastIndexOf('/') + 1)..]) + ".zip";

    /// <summary>Writes the archive. Entries keep their path below the folder, inside a top-level folder of the same name.</summary>
    public static void Write(Stream output, string folder, IEnumerable<FileRow> files, LibraryPaths paths, CancellationToken cancellationToken)
    {
        var top = Path.GetFileNameWithoutExtension(ArchiveName(folder));
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.TryGetFullPath(file.Path, out var fullPath) || !File.Exists(fullPath))
            {
                continue;
            }

            var relative = folder.Length == 0 ? file.Path : file.Path[(folder.Length + 1)..];
            var level = AlreadyCompressed.Contains(Path.GetExtension(file.Name)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
            var entry = zip.CreateEntry($"{top}/{relative}", level);
            entry.LastWriteTime = file.Modified;

            using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.SequentialScan);
            using var target = entry.Open();
            source.CopyTo(target, 1 << 16);
        }
    }
}
