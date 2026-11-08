using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Library.Web.Meshes;

namespace Library.Tests;

public class MeshTests
{
    [Fact]
    public void Reads_binary_stl_and_merges_shared_corners()
    {
        var mesh = StlReader.Read(new MemoryStream(TestMeshes.BinaryStl()));

        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(8, mesh.Vertices.Count);
        Assert.Equal(new Vector3(10), mesh.Size());
    }

    [Fact]
    public void Reads_binary_stl_whose_header_starts_with_solid()
    {
        var mesh = StlReader.Read(new MemoryStream(TestMeshes.BinaryStl(header: "solid but actually binary")));

        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void Reads_ascii_stl()
    {
        var mesh = StlReader.Read(new MemoryStream(TestMeshes.AsciiStl(5)));

        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(new Vector3(5), mesh.Size());
    }

    [Fact]
    public void Rejects_garbage_as_stl()
    {
        Assert.Throws<InvalidDataException>(() => StlReader.Read(new MemoryStream("definitely not a mesh file"u8.ToArray())));
    }

    [Theory]
    [InlineData("4", 1)]
    [InlineData("8", 2)]
    [InlineData("0C", 3)]
    [InlineData("1C", 4)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    // Split into 4 (last char "3"); children read right to left: state 2, state 2, state 1, state 0 → 2 wins.
    [InlineData("0488" + "3", 2)]
    public void Decodes_the_dominant_paint_state(string code, int expected)
    {
        Assert.Equal(expected, ThreeMfMeshReader.DominantPaintState(code));
    }

    [Fact]
    public void Reads_core_3mf_with_units_and_build_items()
    {
        using var zip = TestThreeMf.Open(TestMeshes.CoreThreeMf());

        var mesh = ThreeMfMeshReader.Read(zip, plate: null);

        Assert.Equal(24, mesh.TriangleCount);
        // Two 1 cm cubes, the second moved 3 cm along X: 40 × 10 × 10 mm overall.
        Assert.Equal(new Vector3(40, 10, 10), mesh.Size());
    }

    [Fact]
    public void Reads_one_bambu_plate_with_component_transforms()
    {
        using var zip = TestThreeMf.Open(TestMeshes.BambuThreeMf());

        var plate1 = ThreeMfMeshReader.Read(zip, plate: 1);
        var plate2 = ThreeMfMeshReader.Read(zip, plate: 2);

        Assert.Equal(12, plate1.TriangleCount);
        Assert.Equal(new Vector3(10), plate1.Size());
        Assert.Equal(new Vector3(20), plate2.Size()); // component scaled 2×
        Assert.Equal(new Vector3(400, 100, 0), plate2.Bounds().Min);
        Assert.Empty(ThreeMfMeshReader.Read(zip, plate: 3).Vertices);
    }

    [Fact]
    public void Colours_triangles_by_object_filament_and_paint()
    {
        using var zip = TestThreeMf.Open(TestMeshes.BambuThreeMf());

        var plate1 = ThreeMfMeshReader.Read(zip, plate: 1);
        var plate2 = ThreeMfMeshReader.Read(zip, plate: 2);

        Assert.Equal([0xFF0000u, 0x00FF00u, 0x0000FFu], plate1.Palette);
        Assert.All(plate1.TriangleColours, c => Assert.Equal(1, c)); // object extruder 2 → green
        Assert.Equal(2, plate2.TriangleColours.Count(c => c == 2)); // painted "0C" → filament 3, blue
        Assert.Equal(10, plate2.TriangleColours.Count(c => c == 0)); // the rest: extruder 1, red
    }

    [Fact]
    public void Renders_a_preview_with_the_model_in_the_middle_and_a_transparent_border()
    {
        var mesh = StlReader.Read(new MemoryStream(TestMeshes.BinaryStl()));

        var rgba = MeshRenderer.Render(mesh, 64);

        Assert.Equal(64 * 64 * 4, rgba.Length);
        Assert.Equal(255, rgba[(32 * 64 + 32) * 4 + 3]); // centre is opaque
        Assert.Equal(0, rgba[3]);                         // corner is transparent
    }

    [Fact]
    public void Writes_a_valid_png()
    {
        var rgba = new byte[4 * 4 * 4];
        rgba[0] = 255;
        rgba[3] = 255;
        using var png = new MemoryStream();

        PngWriter.Write(png, rgba, 4, 4);

        var bytes = png.ToArray();
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
    }

    [Fact]
    public void Simplifies_to_the_triangle_budget()
    {
        var mesh = new Mesh();
        const int n = 200;
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                mesh.AddVertex(new Vector3(x, y, MathF.Sin(x * 0.1f) * 5));
            }
        }

        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = y * (n + 1) + x;
                mesh.AddTriangle(i, i + 1, i + n + 2, 0);
                mesh.AddTriangle(i, i + n + 2, i + n + 1, 0);
            }
        }

        var simplified = MeshSimplifier.Simplify(mesh, 10_000);

        Assert.Equal(80_000, mesh.TriangleCount);
        Assert.InRange(simplified.TriangleCount, 1_000, 10_000);
        Assert.Equal(mesh.Size().X, simplified.Size().X, 1f);
    }

    [Fact]
    public void Encodes_the_viewer_format_with_colour_groups()
    {
        using var zip = TestThreeMf.Open(TestMeshes.BambuThreeMf());
        var mesh = ThreeMfMeshReader.Read(zip, plate: 2);
        using var compressed = new MemoryStream();

        MeshCodec.WriteCompressed(mesh, compressed);

        compressed.Position = 0;
        using var plain = new MemoryStream();
        new GZipStream(compressed, CompressionMode.Decompress).CopyTo(plain);
        var data = plain.ToArray();
        Assert.Equal("M3M1"u8.ToArray(), data[..4]);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        // Groups: 10 red triangles first, then 2 blue.
        Assert.Equal((0u, 10u, 0xFF0000u), Group(data, 0));
        Assert.Equal((10u, 2u, 0x0000FFu), Group(data, 1));
        Assert.Equal(40 + 2 * 12 + 8 * 6 + 12 * 12, data.Length);
    }

    private static (uint, uint, uint) Group(byte[] data, int index) => (
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40 + index * 12)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44 + index * 12)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48 + index * 12)));
}
