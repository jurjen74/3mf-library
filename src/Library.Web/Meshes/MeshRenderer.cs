using System.Numerics;

namespace Library.Web.Meshes;

/// <summary>
/// Draws a shaded preview of a mesh on the CPU: orthographic view from front-right-above (like slicer plate
/// thumbnails), flat shading, z-buffer, 2× supersampling, transparent background. No GPU or native libraries needed.
/// </summary>
public static class MeshRenderer
{
    private const int Supersample = 2;
    private const float Margin = 0.07f;

    /// <summary>RGBA pixels, <paramref name="size"/>×<paramref name="size"/>, straight alpha.</summary>
    public static byte[] Render(Mesh mesh, int size, float azimuthDegrees = 35, float elevationDegrees = 28)
    {
        var n = size * Supersample;
        var depth = new float[n * n];
        Array.Fill(depth, float.PositiveInfinity);
        var colour = new uint[n * n];

        if (mesh.TriangleCount > 0)
        {
            Rasterize(mesh, n, depth, colour, azimuthDegrees, elevationDegrees);
        }

        return Downsample(colour, n, Supersample);
    }

    private static void Rasterize(Mesh mesh, int n, float[] depth, uint[] colour, float azimuthDegrees, float elevationDegrees)
    {
        var azimuth = azimuthDegrees * MathF.PI / 180;
        var elevation = elevationDegrees * MathF.PI / 180;
        var toCamera = new Vector3(MathF.Sin(azimuth) * MathF.Cos(elevation), -MathF.Cos(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation));
        var forward = -toCamera;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);
        var light = Vector3.Normalize(toCamera * 0.55f + up * 0.75f - right * 0.35f);

        // Project every vertex once; fit the used ones into the image.
        var vertices = mesh.Vertices;
        var indices = mesh.Indices;
        var screen = new Vector3[vertices.Count];
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            screen[i] = new Vector3(Vector3.Dot(v, right), Vector3.Dot(v, up), Vector3.Dot(v, forward));
        }

        foreach (var index in indices)
        {
            var s = screen[index];
            min = Vector2.Min(min, new Vector2(s.X, s.Y));
            max = Vector2.Max(max, new Vector2(s.X, s.Y));
        }

        var extent = MathF.Max(max.X - min.X, max.Y - min.Y);
        var scale = extent > 0 ? n * (1 - 2 * Margin) / extent : 1;
        var centre = (min + max) / 2;
        for (var i = 0; i < screen.Length; i++)
        {
            var s = screen[i];
            screen[i] = new Vector3((s.X - centre.X) * scale + n / 2f, n / 2f - (s.Y - centre.Y) * scale, s.Z);
        }

        var palette = mesh.Palette.Select(c => new Vector3((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF)).ToArray();
        var triangleColours = mesh.TriangleColours;

        for (var t = 0; t < triangleColours.Count; t++)
        {
            var ia = indices[t * 3];
            var ib = indices[t * 3 + 1];
            var ic = indices[t * 3 + 2];

            // Shade with the face normal in world space; light both sides (STL winding is often wrong).
            var normal = Vector3.Cross(vertices[ib] - vertices[ia], vertices[ic] - vertices[ia]);
            var length = normal.Length();
            if (length < 1e-12f)
            {
                continue;
            }

            normal /= length;
            if (Vector3.Dot(normal, toCamera) < 0)
            {
                normal = -normal;
            }

            var intensity = 0.36f + 0.62f * MathF.Max(0, Vector3.Dot(normal, light)) + 0.1f * MathF.Max(0, normal.Z);
            var rgb = Vector3.Clamp(palette[triangleColours[t]] * MathF.Min(intensity, 1.08f), Vector3.Zero, new Vector3(255));
            var packed = 0xFF000000u | ((uint)rgb.X << 16) | ((uint)rgb.Y << 8) | (uint)rgb.Z;

            FillTriangle(screen[ia], screen[ib], screen[ic], packed, n, depth, colour);
        }
    }

    private static void FillTriangle(Vector3 a, Vector3 b, Vector3 c, uint packed, int n, float[] depth, uint[] colour)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-9f)
        {
            return;
        }

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        var inverseArea = 1 / area;
        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f;
                var w0 = Edge(b, c, px, py) * inverseArea;
                var w1 = Edge(c, a, px, py) * inverseArea;
                var w2 = Edge(a, b, px, py) * inverseArea;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    continue;
                }

                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var i = y * n + x;
                if (z < depth[i])
                {
                    depth[i] = z;
                    colour[i] = packed;
                }
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private static byte[] Downsample(uint[] colour, int n, int factor)
    {
        var size = n / factor;
        var rgba = new byte[size * size * 4];
        var samples = factor * factor;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int r = 0, g = 0, b = 0, covered = 0;
                for (var sy = 0; sy < factor; sy++)
                {
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var c = colour[(y * factor + sy) * n + x * factor + sx];
                        if (c == 0)
                        {
                            continue;
                        }

                        r += (int)((c >> 16) & 0xFF);
                        g += (int)((c >> 8) & 0xFF);
                        b += (int)(c & 0xFF);
                        covered++;
                    }
                }

                if (covered == 0)
                {
                    continue;
                }

                var o = (y * size + x) * 4;
                rgba[o] = (byte)(r / covered);
                rgba[o + 1] = (byte)(g / covered);
                rgba[o + 2] = (byte)(b / covered);
                rgba[o + 3] = (byte)(255 * covered / samples);
            }
        }

        return rgba;
    }
}
