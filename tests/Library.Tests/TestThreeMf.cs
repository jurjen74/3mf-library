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
             <metadata name="Description">&amp;lt;commercialme&amp;gt;&amp;lt;commercialtitle&amp;gt;Membership&amp;lt;/commercialtitle&amp;gt;&amp;lt;/commercialme&amp;gt;&amp;lt;p&amp;gt;Holds &amp;amp;amp; dispenses&amp;lt;/p&amp;gt;&amp;lt;figure class=&amp;#34;image&amp;#34;&amp;gt;&amp;lt;img src=&amp;#34;https://makerworld.example/a.jpg&amp;#34;&amp;gt;&amp;lt;/figure&amp;gt;</metadata>
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
              <object id="2">
                <metadata key="name" value="lid.stl"/>
                <part id="1"><metadata key="name" value="part name, not object name"/></part>
              </object>
              <object id="4"><metadata key="name" value="box.stl"/></object>
              <plate>
                <metadata key="plater_id" value="1"/>
                <metadata key="plater_name" value="Body"/>
                <metadata key="thumbnail_file" value="Metadata/plate_1.png"/>
                <model_instance><metadata key="object_id" value="4"/><metadata key="instance_id" value="0"/></model_instance>
                <model_instance><metadata key="object_id" value="2"/><metadata key="instance_id" value="0"/></model_instance>
                <model_instance><metadata key="object_id" value="2"/><metadata key="instance_id" value="1"/></model_instance>
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
        ["Metadata/slice_info.config"] = """
            <?xml version="1.0" encoding="UTF-8"?>
            <config>
              <header><header_item key="X-BBL-Client-Type" value="slicer"/></header>
              <plate>
                <metadata key="index" value="1"/>
                <metadata key="prediction" value="1843"/>
                <metadata key="weight" value="5.94"/>
                <object identify_id="66" name="lid.stl" skipped="false" />
              </plate>
            </config>
            """,
        ["Metadata/plate_1.png"] = FakePng,
        ["Metadata/plate_1_small.png"] = FakePng,
        ["Metadata/plate_2.png"] = FakePng,
        ["Metadata/plate_no_light_1.png"] = FakePng,
        ["Metadata/top_1.png"] = FakePng,
        ["Auxiliaries/Model Pictures/10.webp"] = FakePng,
        ["Auxiliaries/Model Pictures/2.jpeg"] = FakePng,
        ["Auxiliaries/Model Pictures/Cover.webp"] = FakePng,
        ["Auxiliaries/Model Pictures/notes.txt"] = "not a photo"u8.ToArray(),
        ["Auxiliaries/Profile Pictures/Cover.webp"] = FakePng,
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

    /// <summary>A copy of a 3MF with the text of one entry changed (for malformed-input tests).</summary>
    public static byte[] Rewrite(byte[] bytes, string entryName, Func<string, string> change)
    {
        using var zip = Open(bytes);
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            entries[entry.FullName] = entry.FullName == entryName ? change(Encoding.UTF8.GetString(copy.ToArray())) : copy.ToArray();
        }

        return Build(entries);
    }
}
