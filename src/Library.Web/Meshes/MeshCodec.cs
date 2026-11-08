using System.Buffers.Binary;
using System.IO.Compression;

namespace Library.Web.Meshes;

/// <summary>
/// Compact mesh format for the browser viewer (gzip-compressed on disk and on the wire). Little endian:
/// <code>
/// "M3M1"                                  magic
/// u32 vertexCount, u32 triangleCount, u32 groupCount
/// f32 min[3], f32 max[3]                  bounding box in mm
/// groupCount × (u32 firstTriangle, u32 triangleCount, u32 rgb)
/// vertexCount × u16[3]                    position quantised to the bounding box
/// padding to 4 bytes
/// triangleCount × u32[3]                  vertex indices, triangles sorted by colour group
/// </code>
/// </summary>
public static class MeshCodec
{
    public static void WriteCompressed(Mesh mesh, Stream output)
    {
        using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        using var buffered = new BufferedStream(gzip, 1 << 16);
        Write(mesh, buffered);
    }

    public static void Write(Mesh mesh, Stream output)
    {
        var (min, max) = mesh.Bounds();
        var size = max - min;

        // Group triangles by colour (counting sort keeps the order inside a colour).
        var counts = new int[mesh.Palette.Count];
        foreach (var c in mesh.TriangleColours)
        {
            counts[c]++;
        }

        var groups = Enumerable.Range(0, counts.Length).Where(c => counts[c] > 0).ToList();
        var starts = new int[counts.Length];
        var running = 0;
        foreach (var c in groups)
        {
            starts[c] = running;
            running += counts[c];
        }

        var order = new int[mesh.TriangleCount];
        var next = (int[])starts.Clone();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            order[next[mesh.TriangleColours[t]]++] = t;
        }

        var header = new byte[4 + 12 + 24 + groups.Count * 12];
        "M3M1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)mesh.Vertices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)mesh.TriangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)groups.Count);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16), min.X);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20), min.Y);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(24), min.Z);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(28), max.X);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(32), max.Y);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(36), max.Z);
        for (var g = 0; g < groups.Count; g++)
        {
            var colour = groups[g];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40 + g * 12), (uint)starts[colour]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44 + g * 12), (uint)counts[colour]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48 + g * 12), mesh.Palette[colour]);
        }

        output.Write(header);

        var vertexBytes = new byte[6];
        foreach (var v in mesh.Vertices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(vertexBytes, Quantise(v.X, min.X, size.X));
            BinaryPrimitives.WriteUInt16LittleEndian(vertexBytes.AsSpan(2), Quantise(v.Y, min.Y, size.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(vertexBytes.AsSpan(4), Quantise(v.Z, min.Z, size.Z));
            output.Write(vertexBytes);
        }

        if (mesh.Vertices.Count % 2 == 1)
        {
            output.Write([0, 0]);
        }

        var indexBytes = new byte[12];
        foreach (var t in order)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes, (uint)mesh.Indices[t * 3]);
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(4), (uint)mesh.Indices[t * 3 + 1]);
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(8), (uint)mesh.Indices[t * 3 + 2]);
            output.Write(indexBytes);
        }
    }

    private static ushort Quantise(float value, float min, float size) =>
        size <= 0 ? (ushort)0 : (ushort)Math.Clamp(MathF.Round((value - min) / size * 65535f), 0, 65535);
}
