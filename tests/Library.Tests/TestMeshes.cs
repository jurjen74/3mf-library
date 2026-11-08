using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Library.Tests;

/// <summary>Small synthetic geometry: a cube as binary/ASCII STL and as 3MF in core and Bambu (production) form.</summary>
internal static class TestMeshes
{
    private static readonly Vector3[] CubeCorners =
    [
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1),
    ];

    private static readonly int[] CubeTriangles =
    [
        0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
        1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7,
    ];

    public static byte[] BinaryStl(float size = 10, string header = "binary cube")
    {
        var bytes = new byte[84 + 12 * 50];
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 12);
        for (var t = 0; t < 12; t++)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = CubeCorners[CubeTriangles[t * 3 + c]] * size;
                var offset = 84 + t * 50 + 12 + c * 12;
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), v.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), v.Z);
            }
        }

        return bytes;
    }

    public static byte[] AsciiStl(float size = 10)
    {
        var text = new StringBuilder("solid cube\n");
        for (var t = 0; t < 12; t++)
        {
            text.Append(" facet normal 0 0 0\n  outer loop\n");
            for (var c = 0; c < 3; c++)
            {
                var v = CubeCorners[CubeTriangles[t * 3 + c]] * size;
                text.Append(CultureInfo.InvariantCulture, $"   vertex {v.X} {v.Y} {v.Z}\n");
            }

            text.Append("  endloop\n endfacet\n");
        }

        text.Append("endsolid cube\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static string MeshXml(float size, string? paint = null)
    {
        var xml = new StringBuilder("<mesh><vertices>");
        foreach (var v in CubeCorners.Select(c => c * size))
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<vertex x="{v.X}" y="{v.Y}" z="{v.Z}"/>""");
        }

        xml.Append("</vertices><triangles>");
        for (var t = 0; t < 12; t++)
        {
            var paintAttribute = paint is not null && t < 2 ? $" paint_color=\"{paint}\"" : "";
            xml.Append($"""<triangle v1="{CubeTriangles[t * 3]}" v2="{CubeTriangles[t * 3 + 1]}" v3="{CubeTriangles[t * 3 + 2]}"{paintAttribute}/>""");
        }

        return xml.Append("</triangles></mesh>").ToString();
    }

    /// <summary>A plain 3MF with one cube object placed twice by build items, in centimetres.</summary>
    public static byte[] CoreThreeMf() => TestThreeMf.Build(new Dictionary<string, object>
    {
        ["3D/3dmodel.model"] = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="centimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
             <resources><object id="1" type="model">{MeshXml(1)}</object></resources>
             <build>
              <item objectid="1"/>
              <item objectid="1" transform="1 0 0 0 1 0 0 0 1 3 0 0"/>
             </build>
            </model>
            """,
    });

    /// <summary>
    /// A Bambu-style project: two objects in separate model files, two plates, object extruders 2 and 1, and two
    /// triangles of object 4 painted with filament 3. Filament colours: red, green, blue.
    /// </summary>
    public static byte[] BambuThreeMf() => TestThreeMf.Build(new Dictionary<string, object>
    {
        ["3D/3dmodel.model"] = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02" xmlns:p="http://schemas.microsoft.com/3dmanufacturing/production/2015/06" requiredextensions="p">
             <resources>
              <object id="2" type="model"><components><component p:path="/3D/Objects/object_1.model" objectid="1" transform="1 0 0 0 1 0 0 0 1 0 0 0"/></components></object>
              <object id="4" type="model"><components><component p:path="/3D/Objects/object_2.model" objectid="3" transform="2 0 0 0 2 0 0 0 2 0 0 0"/></components></object>
             </resources>
             <build>
              <item objectid="2" transform="1 0 0 0 1 0 0 0 1 100 100 0" printable="1"/>
              <item objectid="4" transform="1 0 0 0 1 0 0 0 1 400 100 0" printable="1"/>
             </build>
            </model>
            """,
        ["3D/Objects/object_1.model"] = $"""<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"><resources><object id="1" type="model">{MeshXml(10)}</object></resources></model>""",
        ["3D/Objects/object_2.model"] = $"""<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"><resources><object id="3" type="model">{MeshXml(10, paint: "0C")}</object></resources></model>""",
        ["Metadata/model_settings.config"] = """
            <config>
              <object id="2"><metadata key="name" value="a.stl"/><metadata key="extruder" value="2"/><part id="1"><metadata key="name" value="a.stl"/></part></object>
              <object id="4"><metadata key="name" value="b.stl"/><metadata key="extruder" value="1"/><part id="3"><metadata key="name" value="b.stl"/></part></object>
              <plate><metadata key="plater_id" value="1"/><model_instance><metadata key="object_id" value="2"/><metadata key="instance_id" value="0"/></model_instance></plate>
              <plate><metadata key="plater_id" value="2"/><model_instance><metadata key="object_id" value="4"/><metadata key="instance_id" value="0"/></model_instance></plate>
            </config>
            """,
        ["Metadata/project_settings.config"] = """{ "filament_colour": ["#FF0000", "#00FF00", "#0000FF"] }""",
    });
}
