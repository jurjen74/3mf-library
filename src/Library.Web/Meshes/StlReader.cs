using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Library.Web.Meshes;

/// <summary>Reads binary and ASCII STL into an indexed <see cref="Mesh"/>, merging identical vertices.</summary>
public static class StlReader
{
    public const int MaxTriangles = 25_000_000;

    public static Mesh Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20,
            FileOptions.SequentialScan);
        return Read(stream);
    }

    public static Mesh Read(Stream stream)
    {
        var header = new byte[84];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 15)
        {
            throw new InvalidDataException("File is too short to be an STL.");
        }

        var count = read >= 84 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)) : 0;
        var binaryLength = 84 + (long)count * 50;
        var startsWithSolid = Encoding.ASCII.GetString(header, 0, 5).Equals("solid", StringComparison.OrdinalIgnoreCase);

        // Many binary STLs also start with "solid", so an exact size match wins; ASCII next; then binary with trailing bytes.
        if (count > 0 && stream.CanSeek && stream.Length == binaryLength)
        {
            return ReadBinary(stream, count);
        }

        if (startsWithSolid)
        {
            stream.Position = 0;
            return ReadAscii(stream);
        }

        if (count > 0 && stream.CanSeek && stream.Length > binaryLength)
        {
            return ReadBinary(stream, count);
        }

        throw new InvalidDataException("Not a recognisable STL file.");
    }

    private static Mesh ReadBinary(Stream stream, uint count)
    {
        if (count > MaxTriangles)
        {
            throw new InvalidDataException($"STL has {count} triangles; the limit is {MaxTriangles}.");
        }

        var mesh = new Mesh();
        var welder = new VertexWelder(mesh);
        var record = new byte[50];
        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(record);
            var a = welder.Add(ReadVector(record, 12));
            var b = welder.Add(ReadVector(record, 24));
            var c = welder.Add(ReadVector(record, 36));
            mesh.AddTriangle(a, b, c, 0);
        }

        return mesh;
    }

    private static Vector3 ReadVector(byte[] buffer, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset + 8)));

    private static Mesh ReadAscii(Stream stream)
    {
        var mesh = new Mesh();
        var welder = new VertexWelder(mesh);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        Span<int> corner = stackalloc int[3];
        var n = 0;
        while (reader.ReadLine() is { } line)
        {
            var text = line.AsSpan().Trim();
            if (!text.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = text[6..].Trim().ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                throw new InvalidDataException("Malformed vertex line in ASCII STL.");
            }

            corner[n++] = welder.Add(new Vector3(
                float.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture),
                float.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture),
                float.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture)));
            if (n == 3)
            {
                mesh.AddTriangle(corner[0], corner[1], corner[2], 0);
                n = 0;
                if (mesh.TriangleCount > MaxTriangles)
                {
                    throw new InvalidDataException($"STL has more than {MaxTriangles} triangles.");
                }
            }
        }

        return mesh;
    }

    /// <summary>STL repeats every corner per triangle; merging them makes the mesh indexed and about 6× smaller.</summary>
    private sealed class VertexWelder(Mesh mesh)
    {
        private readonly Dictionary<Vector3, int> _seen = [];

        public int Add(Vector3 vertex)
        {
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z))
            {
                throw new InvalidDataException("STL contains an invalid coordinate.");
            }

            if (!_seen.TryGetValue(vertex, out var index))
            {
                index = mesh.AddVertex(vertex);
                _seen[vertex] = index;
            }

            return index;
        }
    }
}
