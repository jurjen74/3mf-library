using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Xml;

namespace Library.Web.Meshes;

/// <summary>
/// Reads 3MF geometry: core objects and components, the Production extension's per-object model files (Bambu Studio,
/// OrcaSlicer), build-item transforms, Bambu plate assignments, per-object/part filaments and painted colours.
/// </summary>
public static class ThreeMfMeshReader
{
    public const int MaxTriangles = 30_000_000;
    private const int MaxDepth = 8;
    private const string MainModel = "3D/3dmodel.model";

    /// <summary>Geometry on one plate, or everything in the build when <paramref name="plate"/> is null.</summary>
    /// <param name="maxTriangles">Refuse more than this, both while reading the model files and in the result.</param>
    public static Mesh Read(ZipArchive zip, int? plate, int maxTriangles = MaxTriangles)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            entries.TryAdd(entry.FullName.TrimStart('/'), entry);
        }

        var settings = ReadSettings(entries);
        var mesh = new Mesh(ReadPalette(entries));
        var models = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
        var budget = new ParseBudget(maxTriangles);

        ModelFile Load(string path)
        {
            path = path.TrimStart('/');
            if (!models.TryGetValue(path, out var model))
            {
                if (!entries.TryGetValue(path, out var entry))
                {
                    throw new InvalidDataException($"3MF refers to missing part '{path}'.");
                }

                model = ParseModel(entry, budget);
                models[path] = model;
            }

            return model;
        }

        var root = Load(MainModel);

        // Transforms are in the main model's unit; convert to millimetres after placing everything.
        var toMillimetres = Matrix4x4.CreateScale(root.UnitScale);
        IEnumerable<BuildItem> items = root.Items;
        if (plate is not null)
        {
            if (!settings.Plates.TryGetValue(plate.Value, out var instances))
            {
                return mesh;
            }

            // Bambu lists (object, instance) per plate; instance n is the n-th build item of that object.
            var byObject = root.Items.GroupBy(i => i.ObjectId).ToDictionary(g => g.Key, g => g.ToList());
            items = instances
                .Select(i => byObject.TryGetValue(i.ObjectId, out var list) && i.InstanceId >= 0 && i.InstanceId < list.Count ? list[i.InstanceId] : null)
                .OfType<BuildItem>()
                .ToList();
        }

        foreach (var item in items)
        {
            Emit(item.Path ?? MainModel, item.ObjectId, item.Transform, item.ObjectId, null, 0);
        }

        return mesh;

        void Emit(string path, string objectId, Matrix4x4 transform, string topObject, string? part, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("3MF components are nested too deeply.");
            }

            var model = Load(path);
            if (!model.Objects.TryGetValue(objectId, out var definition))
            {
                throw new InvalidDataException($"3MF refers to missing object {objectId} in '{path}'.");
            }

            if (definition.Mesh is { } source)
            {
                var extruder = (part is not null && settings.PartExtruders.TryGetValue((topObject, part), out var p) ? p : (int?)null)
                               ?? (settings.ObjectExtruders.TryGetValue(topObject, out var o) ? o : 1);
                var scaled = transform * toMillimetres;
                var offset = mesh.Vertices.Count;
                foreach (var vertex in source.Vertices)
                {
                    mesh.AddVertex(Vector3.Transform(vertex, scaled));
                }

                for (var t = 0; t < source.Triangles.Count / 3; t++)
                {
                    var state = source.PaintStates[t];
                    var colour = (state > 0 ? state : extruder) - 1;
                    mesh.AddTriangle(
                        offset + source.Triangles[t * 3],
                        offset + source.Triangles[t * 3 + 1],
                        offset + source.Triangles[t * 3 + 2],
                        ((colour % mesh.Palette.Count) + mesh.Palette.Count) % mesh.Palette.Count);
                }

                if (mesh.TriangleCount > maxTriangles)
                {
                    throw new InvalidDataException($"3MF has more than {maxTriangles} triangles.");
                }
            }

            foreach (var component in definition.Components)
            {
                Emit(component.Path ?? path, component.ObjectId, component.Transform * transform, topObject,
                    part ?? component.ObjectId, depth + 1);
            }
        }
    }

    /// <summary>
    /// The colour a painted triangle mostly has. PrusaSlicer/Bambu store painting as a hex string encoding a
    /// subdivision tree, read from the last character: per node one nibble, low 2 bits = number of split sides
    /// (0 = leaf), high 2 bits = state (3 means "read the next nibble and add 3"). State 0 = the object's own filament.
    /// </summary>
    public static int DominantPaintState(ReadOnlySpan<char> code)
    {
        Span<double> weights = stackalloc double[32];
        var position = code.Length - 1;
        Visit(code, ref position, 1.0, weights, 0);

        var best = 0;
        for (var i = 1; i < weights.Length; i++)
        {
            if (weights[i] > weights[best])
            {
                best = i;
            }
        }

        return best;

        static void Visit(ReadOnlySpan<char> code, ref int position, double weight, Span<double> weights, int depth)
        {
            if (position < 0 || depth > 32)
            {
                return;
            }

            var nibble = HexValue(code[position--]);
            var splitSides = nibble & 0b11;
            if (splitSides == 0)
            {
                var state = nibble >> 2;
                if (state == 3)
                {
                    if (position < 0)
                    {
                        return;
                    }

                    state = HexValue(code[position--]) + 3;
                }

                if (state < weights.Length)
                {
                    weights[state] += weight;
                }

                return;
            }

            var children = splitSides + 1;
            for (var i = 0; i < children; i++)
            {
                Visit(code, ref position, weight / children, weights, depth + 1);
            }
        }

        static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => 0,
        };
    }

    private static ModelFile ParseModel(ZipArchiveEntry entry, ParseBudget budget)
    {
        var model = new ModelFile();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        ObjectDefinition? current = null;
        var inBuild = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "object":
                        current = null;
                        break;
                    case "build":
                        inBuild = false;
                        break;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "model":
                    model.UnitScale = UnitScale(reader.GetAttribute("unit"));
                    break;
                case "object":
                    current = new ObjectDefinition();
                    if (reader.GetAttribute("id") is { } id)
                    {
                        model.Objects[id] = current;
                    }

                    if (reader.IsEmptyElement)
                    {
                        current = null;
                    }

                    break;
                case "mesh" when current is not null:
                    current.Mesh = new SourceMesh();
                    break;
                case "vertex" when current?.Mesh is { } mesh:
                    budget.CountVertex();
                    mesh.Vertices.Add(new Vector3(Float(reader, "x"), Float(reader, "y"), Float(reader, "z")));
                    break;
                case "triangle" when current?.Mesh is { } mesh:
                    budget.CountTriangle();
                    mesh.Triangles.Add(Int(reader, "v1"));
                    mesh.Triangles.Add(Int(reader, "v2"));
                    mesh.Triangles.Add(Int(reader, "v3"));
                    mesh.PaintStates.Add(PaintState(reader));
                    break;
                case "component" when current is not null && reader.GetAttribute("objectid") is { } objectId:
                    current.Components.Add(new Component(objectId, PathAttribute(reader), Transform(reader.GetAttribute("transform"))));
                    break;
                case "build":
                    inBuild = !reader.IsEmptyElement;
                    break;
                case "item" when inBuild && reader.GetAttribute("objectid") is { } itemObject:
                    model.Items.Add(new BuildItem(itemObject, PathAttribute(reader), Transform(reader.GetAttribute("transform"))));
                    break;
            }
        }

        foreach (var definition in model.Objects.Values)
        {
            if (definition.Mesh is { } mesh && mesh.Triangles.Any(i => i < 0 || i >= mesh.Vertices.Count))
            {
                throw new InvalidDataException("3MF triangle refers to a vertex that does not exist.");
            }
        }

        return model;
    }

    private static byte PaintState(XmlReader reader)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return 0;
        }

        byte state = 0;
        do
        {
            if (reader.LocalName is "paint_color" or "mmu_segmentation" && reader.Value.Length > 0)
            {
                state = (byte)Math.Min(DominantPaintState(reader.Value), 255);
                break;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return state;
    }

    /// <summary>The Production extension's <c>p:path</c>, whatever prefix the file uses.</summary>
    private static string? PathAttribute(XmlReader reader)
    {
        string? path = null;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.LocalName == "path")
                {
                    path = reader.Value;
                    break;
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return path;
    }

    private static float Float(XmlReader reader, string name) =>
        float.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : throw new InvalidDataException($"3MF vertex has an invalid '{name}'.");

    private static int Int(XmlReader reader, string name) =>
        int.TryParse(reader.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"3MF triangle has an invalid '{name}'.");

    /// <summary>3MF matrices are 4×3, row-major, for row vectors: p' = p · M.</summary>
    private static Matrix4x4 Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Matrix4x4.Identity;
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12)
        {
            throw new InvalidDataException("3MF transform must have 12 numbers.");
        }

        var m = new float[12];
        for (var i = 0; i < 12; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]) || !float.IsFinite(m[i]))
            {
                throw new InvalidDataException("3MF transform has a value that isn't a number.");
            }
        }

        return new Matrix4x4(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    private static float UnitScale(string? unit) => unit?.ToLowerInvariant() switch
    {
        "micron" => 0.001f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => 1f,
    };

    private static Settings ReadSettings(Dictionary<string, ZipArchiveEntry> entries)
    {
        var settings = new Settings();
        if (!entries.TryGetValue("Metadata/model_settings.config", out var entry))
        {
            return settings;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        string? objectId = null, partId = null;
        int? plateId = null, instanceId = null;
        string? instanceObjectId = null;
        List<(string ObjectId, int InstanceId)>? plateItems = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "object" when reader.Depth == 1:
                        objectId = reader.GetAttribute("id");
                        partId = null;
                        break;
                    case "part" when objectId is not null && !reader.IsEmptyElement:
                        partId = reader.GetAttribute("id");
                        break;
                    case "plate" when reader.Depth == 1:
                        objectId = null;
                        plateId = null;
                        plateItems = [];
                        break;
                    case "model_instance":
                        (instanceObjectId, instanceId) = (null, 0);
                        break;
                    case "metadata":
                        var key = reader.GetAttribute("key");
                        var value = reader.GetAttribute("value");
                        if (key == "extruder" && int.TryParse(value, CultureInfo.InvariantCulture, out var extruder) && extruder > 0)
                        {
                            if (partId is not null && objectId is not null)
                            {
                                settings.PartExtruders[(objectId, partId)] = extruder;
                            }
                            else if (objectId is not null)
                            {
                                settings.ObjectExtruders[objectId] = extruder;
                            }
                        }
                        else if (plateItems is not null && reader.Depth == 2 && key == "plater_id" &&
                                 int.TryParse(value, CultureInfo.InvariantCulture, out var plate))
                        {
                            plateId = plate;
                        }
                        else if (plateItems is not null && reader.Depth == 3 && key == "object_id")
                        {
                            instanceObjectId = value;
                        }
                        else if (plateItems is not null && reader.Depth == 3 && key == "instance_id" &&
                                 int.TryParse(value, CultureInfo.InvariantCulture, out var instance))
                        {
                            instanceId = instance;
                        }

                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "part":
                        partId = null;
                        break;
                    case "object" when reader.Depth == 1:
                        objectId = null;
                        break;
                    case "model_instance" when plateItems is not null && instanceObjectId is not null:
                        plateItems.Add((instanceObjectId, instanceId ?? 0));
                        break;
                    case "plate" when reader.Depth == 1 && plateItems is not null:
                        if (plateId is not null)
                        {
                            settings.Plates[plateId.Value] = plateItems;
                        }

                        plateItems = null;
                        break;
                }
            }
        }

        return settings;
    }

    private static List<uint> ReadPalette(Dictionary<string, ZipArchiveEntry> entries)
    {
        var palette = new List<uint>();
        if (entries.TryGetValue("Metadata/project_settings.config", out var entry) && entry.Length < 32 * 1024 * 1024)
        {
            try
            {
                using var stream = entry.Open();
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("filament_colour", out var colours) &&
                    colours.ValueKind == JsonValueKind.Array)
                {
                    foreach (var colour in colours.EnumerateArray())
                    {
                        var text = colour.ValueKind == JsonValueKind.String ? colour.GetString() : null;
                        if (text is { Length: >= 7 } && text[0] == '#' &&
                            uint.TryParse(text.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                        {
                            palette.Add(rgb);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A broken settings file only costs the colours.
            }
        }

        return palette;
    }

    /// <summary>
    /// Stops reading model files as soon as they hold more geometry than a mesh may have, instead of loading all of
    /// it into memory first and refusing it afterwards.
    /// </summary>
    private sealed class ParseBudget(int maxTriangles)
    {
        private long _vertices;
        private long _triangles;

        public void CountVertex()
        {
            // A closed mesh has about half as many vertices as triangles; this only catches the absurd.
            if (++_vertices > maxTriangles)
            {
                throw new InvalidDataException($"3MF model files hold more than {maxTriangles} vertices.");
            }
        }

        public void CountTriangle()
        {
            if (++_triangles > maxTriangles)
            {
                throw new InvalidDataException($"3MF model files hold more than {maxTriangles} triangles.");
            }
        }
    }

    private sealed class ModelFile
    {
        public float UnitScale { get; set; } = 1f;
        public Dictionary<string, ObjectDefinition> Objects { get; } = new(StringComparer.Ordinal);
        public List<BuildItem> Items { get; } = [];
    }

    private sealed class ObjectDefinition
    {
        public SourceMesh? Mesh { get; set; }
        public List<Component> Components { get; } = [];
    }

    private sealed class SourceMesh
    {
        public List<Vector3> Vertices { get; } = [];
        public List<int> Triangles { get; } = [];
        public List<byte> PaintStates { get; } = [];
    }

    private sealed record Component(string ObjectId, string? Path, Matrix4x4 Transform);

    private sealed record BuildItem(string ObjectId, string? Path, Matrix4x4 Transform);

    private sealed class Settings
    {
        public Dictionary<string, int> ObjectExtruders { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Object, string Part), int> PartExtruders { get; } = [];
        public Dictionary<int, List<(string ObjectId, int InstanceId)>> Plates { get; } = [];
    }
}
