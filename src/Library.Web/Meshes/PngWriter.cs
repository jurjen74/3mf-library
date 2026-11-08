using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Library.Web.Meshes;

/// <summary>Minimal PNG encoder for 8-bit RGBA images, plus a box-filter downscale for thumbnails.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(Stream output, byte[] rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(rgba.Length, width * height * 4);

        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            var previous = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                // "Up" filter: previews have large flat areas, so this compresses well.
                row[0] = 2;
                var current = rgba.AsSpan(y * width * 4, width * 4);
                for (var i = 0; i < current.Length; i++)
                {
                    row[i + 1] = (byte)(current[i] - previous[i]);
                }

                current.CopyTo(previous);
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    public static void Write(string path, byte[] rgba, int width, int height)
    {
        using var file = File.Create(path);
        Write(file, rgba, width, height);
    }

    /// <summary>Shrinks a square RGBA image by an integer factor, averaging colour weighted by alpha.</summary>
    public static byte[] Downscale(byte[] rgba, int size, int factor)
    {
        var target = size / factor;
        var result = new byte[target * target * 4];
        var samples = factor * factor;
        for (var y = 0; y < target; y++)
        {
            for (var x = 0; x < target; x++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                for (var sy = 0; sy < factor; sy++)
                {
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var o = ((y * factor + sy) * size + x * factor + sx) * 4;
                        var alpha = rgba[o + 3];
                        r += rgba[o] * alpha;
                        g += rgba[o + 1] * alpha;
                        b += rgba[o + 2] * alpha;
                        a += alpha;
                    }
                }

                var t = (y * target + x) * 4;
                if (a > 0)
                {
                    result[t] = (byte)(r / a);
                    result[t + 1] = (byte)(g / a);
                    result[t + 2] = (byte)(b / a);
                    result[t + 3] = (byte)(a / samples);
                }
            }
        }

        return result;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
