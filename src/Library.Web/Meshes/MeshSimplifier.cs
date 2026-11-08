using System.Numerics;

namespace Library.Web.Meshes;

/// <summary>
/// Reduces a mesh to a triangle budget by vertex clustering: vertices in the same grid cell merge into their average
/// and collapsed triangles disappear. Crude next to quadric decimation, but fast, colour-preserving, and plenty for
/// looking at a model in the browser.
/// </summary>
public static class MeshSimplifier
{
    public static Mesh Simplify(Mesh mesh, int maxTriangles)
    {
        if (mesh.TriangleCount <= maxTriangles)
        {
            return mesh;
        }

        var (min, max) = mesh.Bounds();
        var extent = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        if (extent <= 0)
        {
            return mesh;
        }

        // The triangle count after clustering grows roughly with the square of the grid resolution.
        var resolution = 1024;
        int[] clusterOf = [];
        Dictionary<long, int> clusters = [];
        for (var attempt = 0; attempt < 10; attempt++)
        {
            (clusterOf, clusters) = Cluster(mesh, min, extent / resolution);
            var count = CountTriangles(mesh, clusterOf);
            if (count <= maxTriangles || resolution <= 8)
            {
                break;
            }

            resolution = Math.Max(8, (int)(resolution * MathF.Sqrt((float)maxTriangles / count) * 0.95f));
        }

        var sums = new Vector3[clusters.Count];
        var counts = new int[clusters.Count];
        for (var v = 0; v < clusterOf.Length; v++)
        {
            if (clusterOf[v] >= 0)
            {
                sums[clusterOf[v]] += mesh.Vertices[v];
                counts[clusterOf[v]]++;
            }
        }

        var result = new Mesh(mesh.Palette);
        for (var c = 0; c < sums.Length; c++)
        {
            result.AddVertex(counts[c] > 0 ? sums[c] / counts[c] : Vector3.Zero);
        }

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            result.AddTriangle(
                clusterOf[mesh.Indices[t * 3]],
                clusterOf[mesh.Indices[t * 3 + 1]],
                clusterOf[mesh.Indices[t * 3 + 2]],
                mesh.TriangleColours[t]);
        }

        return result;
    }

    private static (int[] ClusterOf, Dictionary<long, int> Clusters) Cluster(Mesh mesh, Vector3 min, float cell)
    {
        var clusterOf = new int[mesh.Vertices.Count];
        Array.Fill(clusterOf, -1);
        var clusters = new Dictionary<long, int>();
        foreach (var v in mesh.Indices)
        {
            if (clusterOf[v] >= 0)
            {
                continue;
            }

            var p = (mesh.Vertices[v] - min) / cell;
            var key = ((long)Math.Clamp((int)p.X, 0, 0x1FFFFF) << 42) |
                      ((long)Math.Clamp((int)p.Y, 0, 0x1FFFFF) << 21) |
                      (long)Math.Clamp((int)p.Z, 0, 0x1FFFFF);
            if (!clusters.TryGetValue(key, out var cluster))
            {
                cluster = clusters.Count;
                clusters[key] = cluster;
            }

            clusterOf[v] = cluster;
        }

        return (clusterOf, clusters);
    }

    private static int CountTriangles(Mesh mesh, int[] clusterOf)
    {
        var count = 0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = clusterOf[mesh.Indices[t * 3]];
            var b = clusterOf[mesh.Indices[t * 3 + 1]];
            var c = clusterOf[mesh.Indices[t * 3 + 2]];
            if (a != b && b != c && a != c)
            {
                count++;
            }
        }

        return count;
    }
}
