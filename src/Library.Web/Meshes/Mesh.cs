using System.Numerics;

namespace Library.Web.Meshes;

/// <summary>
/// An indexed triangle mesh in millimetres, Z up, with one palette colour per triangle. Shared by the preview
/// renderer and the 3D viewer export.
/// </summary>
public sealed class Mesh
{
    /// <summary>Colour for meshes that carry none (plain STL).</summary>
    public const uint DefaultColour = 0xA3B8CC;

    private readonly List<Vector3> _vertices = [];
    private readonly List<int> _indices = [];
    private readonly List<byte> _colours = [];

    public Mesh(IReadOnlyList<uint>? palette = null) => Palette = palette is { Count: > 0 } ? palette : [DefaultColour];

    /// <summary>RGB colours (0xRRGGBB); <see cref="TriangleColours"/> index into this list.</summary>
    public IReadOnlyList<uint> Palette { get; }

    public IReadOnlyList<Vector3> Vertices => _vertices;

    /// <summary>Three vertex indices per triangle.</summary>
    public IReadOnlyList<int> Indices => _indices;

    public IReadOnlyList<byte> TriangleColours => _colours;

    public int TriangleCount => _colours.Count;

    public int AddVertex(Vector3 vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    public void AddTriangle(int a, int b, int c, int colour)
    {
        if (a == b || b == c || a == c)
        {
            return;
        }

        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
        _colours.Add((byte)Math.Clamp(colour, 0, Math.Min(255, Palette.Count - 1)));
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (_vertices.Count == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var index in _indices)
        {
            var v = _vertices[index];
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        return _indices.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>Width × depth × height in millimetres.</summary>
    public Vector3 Size()
    {
        var (min, max) = Bounds();
        return max - min;
    }
}
