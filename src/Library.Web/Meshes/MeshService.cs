using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Library.Web.Data;
using Library.Web.Indexing;

namespace Library.Web.Meshes;

public sealed record MeshResult(string? Path, string? Error);

/// <summary>
/// Builds viewer meshes on first request (<c>model</c> for a whole STL/3MF, <c>plate_N</c> for one Bambu plate),
/// simplified to <see cref="MaxViewerTriangles"/> and cached next to the previews. Builds run one at a time
/// (<see cref="MeshWorkGate"/>), shared with the indexer.
/// </summary>
public sealed partial class MeshService(
    LibraryOptions options,
    LibraryQueries queries,
    LibraryPaths paths,
    ILogger<MeshService> log,
    MeshWorkGate? workGate = null)
{
    private readonly MeshWorkGate _work = workGate ?? new MeshWorkGate();

    public const int MaxViewerTriangles = 750_000;

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public async Task<MeshResult> GetAsync(string cacheKey, string name, CancellationToken cancellationToken)
    {
        if (!IsValidName(name) || queries.FindByCacheKey(cacheKey) is not { } file || !Offers(file, name))
        {
            return new MeshResult(null, null);
        }

        var directory = Path.Combine(options.CachePath, cacheKey);
        var target = Path.Combine(directory, $"mesh_{name}.bin.gz");
        if (File.Exists(target))
        {
            return new MeshResult(target, null);
        }

        // One build at a time, viewer and indexer together; the second request for the same mesh finds the file.
        // Leaving the page while waiting gives up the turn; a build that has started finishes (it gets cached).
        using (await _work.EnterAsync(cancellationToken))
        {
            if (File.Exists(target))
            {
                return new MeshResult(target, null);
            }

            return await Task.Run(() => Build(file, name, directory, target), CancellationToken.None);
        }
    }

    /// <summary>
    /// The meshes a file page links to: <c>model</c> for every model, <c>plate_N</c> only for a 3MF plate the index
    /// knows. Anything else would build (and cache) the same STL again, or parse a 3MF for a plate that isn't there.
    /// </summary>
    private bool Offers(FileRow file, string name)
    {
        if (name == "model")
        {
            return FileKinds.IsModel(file.Kind);
        }

        var plate = int.Parse(name["plate_".Length..], CultureInfo.InvariantCulture);
        return file.Kind == FileKinds.ThreeMf && queries.Plates(file.Id).Any(p => p.Idx == plate);
    }

    private MeshResult Build(FileRow file, string name, string directory, string target)
    {
        if (!Directory.Exists(directory) || !paths.TryGetFullPath(file.Path, out var fullPath))
        {
            return new MeshResult(null, null);
        }

        var started = DateTime.UtcNow;
        string? temporary = null;
        try
        {
            Mesh mesh;
            if (file.Kind == FileKinds.Stl)
            {
                mesh = StlReader.Read(fullPath);
            }
            else
            {
                int? plate = name == "model" ? null : int.Parse(name["plate_".Length..], CultureInfo.InvariantCulture);
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    81920, FileOptions.RandomAccess);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                mesh = ThreeMfMeshReader.Read(zip, plate);
            }

            var simplified = MeshSimplifier.Simplify(mesh, MaxViewerTriangles);
            temporary = $"{target}.tmp-{Guid.NewGuid():N}";
            using (var output = File.Create(temporary))
            {
                MeshCodec.WriteCompressed(simplified, output);
            }

            File.Move(temporary, target, overwrite: true);
            log.LogInformation("Built viewer mesh {Name} for {Path}: {Triangles} → {Simplified} triangles in {Seconds:0.0}s",
                name, file.Path, mesh.TriangleCount, simplified.TriangleCount, (DateTime.UtcNow - started).TotalSeconds);
            return new MeshResult(target, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            log.LogWarning("Could not build viewer mesh {Name} for {Path}: {Message}", name, file.Path, ex.Message);
            return new MeshResult(null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unusual input the readers didn't anticipate: the viewer gets a message instead of an error page.
            log.LogError(ex, "Could not build viewer mesh {Name} for {Path}", name, file.Path);
            return new MeshResult(null, "Could not read the geometry of this file.");
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                TryDelete(temporary);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not remove {Path}", path);
        }
    }

    [GeneratedRegex(@"^(model|plate_\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
