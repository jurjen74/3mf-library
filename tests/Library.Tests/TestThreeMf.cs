using System.IO.Compression;
using System.Text;

namespace Library.Tests;

/// <summary>Builds small synthetic 3MF archives shaped like the ones slicers write.</summary>
internal static class TestThreeMf
{
    public static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>A Bambu Studio project: metadata, two named plates with previews, filament colours.</summary>
    public static byte[] Bambu(string title = "Razor Blade Dispenser") => Build(new Dictionary<string, object>
    {
        ["3D/3dmodel.model"] = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="millimeter" xml:lang="en-US" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
             <metadata name="Application">BambuStudio-02.08.02.61</metadata>
             <metadata name="Title">{title}</metadata>
             <metadata name="Designer">Thibault01</metadata>
             <metadata name="License">MakerWorld Exclusive License</metadata>
             <metadata name="Description">&amp;lt;p&amp;gt;Holds &amp;amp;amp; dispenses&amp;lt;/p&amp;gt;</metadata>
             <metadata name="CreationDate">2026-08-31</metadata>
             <resources>
              <object id="1" type="model"><mesh><vertices/><triangles/></mesh></object>
             </resources>
             <metadata name="Title">Ignored: after resources</metadata>
            </model>
            """,
        ["Metadata/model_settings.config"] = """
            <?xml version="1.0" encoding="UTF-8"?>
            <config>
              <object id="2"><metadata key="name" value="lid.stl"/></object>
              <plate>
                <metadata key="plater_id" value="1"/>
                <metadata key="plater_name" value="Body"/>
                <metadata key="thumbnail_file" value="Metadata/plate_1.png"/>
                <model_instance><metadata key="object_id" value="2"/></model_instance>
              </plate>
              <plate>
                <metadata key="plater_id" value="2"/>
                <metadata key="plater_name" value=""/>
                <metadata key="thumbnail_file" value="Metadata/plate_2.png"/>
              </plate>
            </config>
            """,
        ["Metadata/project_settings.config"] = """
            { "filament_colour": ["#161616", "#d4af37", "#161616", "#FFFFFFFF", "not-a-colour"] }
            """,
        ["Metadata/plate_1.png"] = FakePng,
        ["Metadata/plate_1_small.png"] = FakePng,
        ["Metadata/plate_2.png"] = FakePng,
        ["Metadata/plate_no_light_1.png"] = FakePng,
        ["Metadata/top_1.png"] = FakePng,
    });

    /// <summary>A PrusaSlicer-style 3MF: one project thumbnail, no plates or settings.</summary>
    public static byte[] Prusa() => Build(new Dictionary<string, object>
    {
        ["3D/3dmodel.model"] = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
             <metadata name="Application">PrusaSlicer-2.9.0</metadata>
             <resources/>
            </model>
            """,
        ["Metadata/thumbnail.png"] = FakePng,
    });

    public static byte[] Build(IReadOnlyDictionary<string, object> entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var bytes = content switch
                {
                    string text => Encoding.UTF8.GetBytes(text),
                    byte[] raw => raw,
                    _ => throw new ArgumentException($"Unsupported content for {name}"),
                };
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    public static ZipArchive Open(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);
}
