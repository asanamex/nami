using System.Text;
using Nami.Interop;

namespace Nami.Cli.Tests;

/// <summary>Regression tests for offline global-metadata.dat parsing + projection.
/// Covers the three Il2CppTypeDefinition layouts (v27+ 88B, v24.1 92B with byref, v35+
/// 84B), v38 triplet headers, single-byte XOR de-obfuscation, IL-style names
/// ("&lt;Module&gt;", ".ctor") that strict checks reject, malformed inputs, and - when
/// present locally - the real Unity 6000 v31 file (fixtures-dev/, never committed).</summary>
public sealed class InteropTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public InteropTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private string TempPath()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    // Builds a minimal metadata blob: 22 header pairs (no fieldMarshaledSizes), one
    // image, caller-supplied type/method/field rows. typeStride 88 omits byrefTypeIndex,
    // 92 includes it, 84 additionally drops elementTypeIndex (v35+) - mirroring the real
    // layouts. triplets=true writes v38 (offset,size,count) header entries.
    private static byte[] BuildMetadata(
        int version,
        int typeStride,
        int methodStride,
        IReadOnlyList<(string Name, string Ns, int Flags, int Declaring, int MethodStart, int MethodCount, int FieldStart, int FieldCount)> types,
        IReadOnlyList<(string Name, bool IsStatic, int ParamCount)> methods,
        IReadOnlyList<string> fields,
        string imageName = "Assembly-CSharp.dll",
        bool triplets = false)
    {
        // First pass: collect names in a fixed order so "mscorlib" is the sentinel.
        var ordered = new List<string> { "mscorlib", string.Empty };
        foreach (var t in types)
        {
            ordered.Add(t.Name);
            ordered.Add(t.Ns);
        }

        foreach (var m in methods)
        {
            ordered.Add(m.Name);
        }

        foreach (var f in fields)
        {
            ordered.Add(f);
        }

        ordered.Add(imageName);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var heapList = new List<byte>();
        foreach (var s in ordered.Distinct(StringComparer.Ordinal))
        {
            map[s] = heapList.Count;
            heapList.AddRange(Encoding.UTF8.GetBytes(s));
            heapList.Add(0);
        }

        var methodRegion = new List<byte>();
        foreach (var (name, isStatic, paramCount) in methods)
        {
            var row = new byte[methodStride];
            BitConverter.GetBytes(map[name]).CopyTo(row, 0);
            BitConverter.GetBytes(0).CopyTo(row, 4); // declaringType
            BitConverter.GetBytes(0).CopyTo(row, 8); // returnType
            var tail = methodStride - 12;
            BitConverter.GetBytes(0).CopyTo(row, tail); // parameterStart
            BitConverter.GetBytes(-1).CopyTo(row, tail + 4); // genericContainerIndex
            BitConverter.GetBytes(0x06000001u).CopyTo(row, methodStride - 12); // token
            BitConverter.GetBytes((ushort)(isStatic ? 0x0010 : 0)).CopyTo(row, methodStride - 8); // flags
            BitConverter.GetBytes((ushort)0).CopyTo(row, methodStride - 6); // iflags
            BitConverter.GetBytes((ushort)0).CopyTo(row, methodStride - 4); // slot
            BitConverter.GetBytes((ushort)paramCount).CopyTo(row, methodStride - 2);
            methodRegion.AddRange(row);
        }

        var fieldRegion = new List<byte>();
        for (var i = 0; i < fields.Count; i++)
        {
            var row = new byte[12];
            BitConverter.GetBytes(map[fields[i]]).CopyTo(row, 0);
            BitConverter.GetBytes(0).CopyTo(row, 4); // typeIndex
            BitConverter.GetBytes(0x04000001 + i).CopyTo(row, 8); // token
            fieldRegion.AddRange(row);
        }

        var typeRegion = new List<byte>();
        for (var i = 0; i < types.Count; i++)
        {
            var (name, ns, flags, declaring, methodStart, methodCount, fieldStart, fieldCount) = types[i];
            var row = new byte[typeStride];
            BitConverter.GetBytes(map[name]).CopyTo(row, 0);
            BitConverter.GetBytes(map[ns]).CopyTo(row, 4);
            BitConverter.GetBytes(0).CopyTo(row, 8); // byvalTypeIndex
            var o = 12;
            if (typeStride == 92)
            {
                BitConverter.GetBytes(7).CopyTo(row, o); // byrefTypeIndex (small sane index)
                o += 4;
            }

            BitConverter.GetBytes(declaring).CopyTo(row, o); o += 4; // declaringTypeIndex
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // parentIndex
            if (typeStride != 84)
            {
                BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // elementTypeIndex (gone in v35+)
            }

            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // genericContainerIndex
            BitConverter.GetBytes((uint)flags).CopyTo(row, o); o += 4; // flags
            BitConverter.GetBytes(fieldStart).CopyTo(row, o); o += 4;
            BitConverter.GetBytes(methodStart).CopyTo(row, o); o += 4;
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // eventStart
            BitConverter.GetBytes(0).CopyTo(row, o); o += 4; // propertyStart
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // nestedTypesStart
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // interfacesStart
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // vtableStart
            BitConverter.GetBytes(-1).CopyTo(row, o); o += 4; // interfaceOffsetsStart
            BitConverter.GetBytes((ushort)methodCount).CopyTo(row, o); o += 2;
            BitConverter.GetBytes((ushort)0).CopyTo(row, o); o += 2; // property_count
            BitConverter.GetBytes((ushort)fieldCount).CopyTo(row, o); o += 2;
            // event/nested/vtable/interfaces/interfaceOffsets counts stay zero; bitfield + token
            BitConverter.GetBytes(0u).CopyTo(row, typeStride - 8); // bitfield
            BitConverter.GetBytes(0x02000001u + (uint)i).CopyTo(row, typeStride - 4); // token
            typeRegion.AddRange(row);
        }

        var imageRow = new byte[40];
        BitConverter.GetBytes(map[imageName]).CopyTo(imageRow, 0);
        BitConverter.GetBytes(0).CopyTo(imageRow, 4); // assemblyIndex
        BitConverter.GetBytes(0).CopyTo(imageRow, 8); // typeStart
        BitConverter.GetBytes(types.Count).CopyTo(imageRow, 12); // typeCount
        BitConverter.GetBytes(-1).CopyTo(imageRow, 24); // entryPointIndex

        // Header: magic + version + 22 (offset,size[,count]) entries, regions laid out in order.
        var empty = Array.Empty<byte>();
        var regions = new List<byte[]>
        {
            empty, empty, heapList.ToArray(), // stringLiteral, stringLiteralData, string
            empty, empty, // events, properties
            methodRegion.ToArray(), // methods
            empty, empty, empty, // parameterDefaultValues, fieldDefaultValues, fieldAndParameterData
            empty, // parameters
            fieldRegion.ToArray(), // fields
            empty, empty, empty, empty, empty, empty, empty, empty, // generics/nested/interfaces/vtable/interfaceOffsets
            typeRegion.ToArray(), // typeDefinitions
            imageRow, // images
            empty, // assemblies
        };
        Assert.Equal(22, regions.Count);

        var headerSize = 8 + regions.Count * (triplets ? 12 : 8);
        var offset = headerSize;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0xFAB11BAFu);
        writer.Write(version);
        foreach (var region in regions)
        {
            writer.Write(offset);
            writer.Write(region.Length);
            if (triplets)
            {
                writer.Write(region.Length); // count (ignored by the reader)
            }
            offset += region.Length;
        }

        foreach (var region in regions)
        {
            writer.Write(region);
        }

        return stream.ToArray();
    }

    [Fact]
    public void LoadsV27LayoutWithIlStyleNames()
    {
        var types = new[]
        {
            ("<Module>", string.Empty, 0, -1, 0, 0, 0, 0),
            ("Player", "Game", 1, -1, 0, 4, 0, 1),
            ("State", "Game", 2, 1, 0, 0, 0, 0), // nested in Player
        };
        var methods = new (string, bool, int)[]
        {
            (".ctor", false, 1), ("Jump", true, 0), ("Jump", false, 1), ("get_Score", false, 0),
        };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(30, 88, 32, types, methods, new[] { "health" }));

        var metadata = Il2CppMetadata.Load(path);
        Assert.Equal(30, metadata.MetadataVersion);
        Assert.Equal(22, Il2CppMetadata.DumpHeaderPairs(path).Count);
        Assert.Equal(30, Il2CppMetadata.ReadVersion(path));

        var images = metadata.Images();
        Assert.Single(images);
        Assert.Equal("Assembly-CSharp.dll", images[0].Name);

        Assert.Equal(3, metadata.AllTypes().Count);
        var inImage = metadata.TypesInImage("Assembly-CSharp.dll");
        Assert.Equal(2, inImage.Count); // <Module> + Player; nested State excluded

        var player = inImage.Single(t => t.Name == "Player");
        Assert.True(player.IsPublic);
        Assert.False(inImage.Single(t => t.Name == "<Module>").IsPublic);
        Assert.Equal(4, metadata.MethodsOf(player).Count);
        Assert.Equal("health", Assert.Single(metadata.FieldsOf(player)).Name);
    }

    [Fact]
    public void ProjectionCollapsesOverloadsAndStaysCompilable()
    {
        var types = new[]
        {
            ("Player", "Game", 1, -1, 0, 4, 0, 1),
            ("State", "Game", 2, 0, 0, 0, 0, 0),
        };
        var methods = new (string, bool, int)[]
        {
            (".ctor", false, 1), ("Jump", true, 0), ("Jump", false, 1), ("ToString", false, 0),
        };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(30, 88, 32, types, methods, new[] { "health" }));

        var metadata = Il2CppMetadata.Load(path);
        var source = ProjectionWriter.Generate(metadata, "Assembly-CSharp.dll");
        Assert.Contains("namespace GameInterop;", source, StringComparison.Ordinal);
        Assert.Contains("Player", source, StringComparison.Ordinal);

        // Overloads share one const: exactly one Jump definition, shape noted in the comment.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "const string Jump = "));
        // object-member names get `new` so the file compiles warning-free.
        Assert.Contains("new const string ToString", source, StringComparison.Ordinal);

        // No duplicate const inside any single class.
        string? current = null;
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("public static partial class ", StringComparison.Ordinal))
            {
                current = trimmed["public static partial class ".Length..];
                seen[current] = new HashSet<string>(StringComparer.Ordinal);
            }
            else if (trimmed.StartsWith("public ", StringComparison.Ordinal) && trimmed.Contains("const string ", StringComparison.Ordinal) && current is not null)
            {
                var name = trimmed.Split('=', StringSplitOptions.TrimEntries)[0].Split(' ')[^1];
                var firstSeen = seen[current].Add(name);
                Assert.True(firstSeen, $"duplicate const {name} in {current}");
            }
        }
    }

    [Fact]
    public void ProjectionMaterializesGameClassOncePerType()
    {
        var types = new[]
        {
            ("Player", string.Empty, 1, -1, 0, 1, 0, 1),
        };
        var methods = new (string, bool, int)[] { ("Jump", true, 0) };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(30, 88, 32, types, methods, new[] { "health" }));

        var metadata = Il2CppMetadata.Load(path);
        var source = ProjectionWriter.Generate(metadata, "Assembly-CSharp.dll");

        // Lazy materialization: exactly one Resolve per type, cached after first access.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            source, System.Text.RegularExpressions.Regex.Escape("GameClass.Resolve(")));
        Assert.Contains("private static GameClass _Player;", source, StringComparison.Ordinal);
        Assert.Contains("public static GameClass Player => _Player ??= GameClass.Resolve(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsV241LayoutWithByrefField()
    {
        var types = new[]
        {
            ("A", string.Empty, 1, -1, 0, 1, 0, 0),
            ("B", string.Empty, 1, -1, 1, 1, 0, 0),
        };
        var methods = new (string, bool, int)[] { ("RunA", true, 0), ("RunB", false, 2) };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(24, 92, 32, types, methods, Array.Empty<string>()));

        var metadata = Il2CppMetadata.Load(path);
        var inImage = metadata.TypesInImage("Assembly-CSharp.dll");
        Assert.Equal(2, inImage.Count);
        // With the v24.1 shift (+4) these resolve; with the v27 shift they would not.
        Assert.Equal("RunA", Assert.Single(metadata.MethodsOf(inImage[0])).Name);
        var runB = Assert.Single(metadata.MethodsOf(inImage[1]));
        Assert.Equal("RunB", runB.Name);
        Assert.Equal(2, runB.ParameterCount);
        Assert.False(runB.IsStatic);
    }

    [Fact]
    public void LoadsV32Layout()
    {
        // v32 carries no documented struct changes vs v31: same 88/36 rows, new version.
        var types = new[]
        {
            ("Player", "Game", 1, -1, 0, 2, 0, 1),
        };
        var methods = new (string, bool, int)[] { ("Jump", true, 0), ("Run", false, 1) };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(32, 88, 36, types, methods, new[] { "health" }));

        var metadata = Il2CppMetadata.Load(path);
        Assert.Equal(32, metadata.MetadataVersion);
        var player = Assert.Single(metadata.TypesInImage("Assembly-CSharp.dll"));
        Assert.Equal("Player", player.Name);
        Assert.Equal(2, metadata.MethodsOf(player).Count);
        Assert.Equal("health", Assert.Single(metadata.FieldsOf(player)).Name);
    }

    [Fact]
    public void LoadsV35LayoutWithShortRows()
    {
        // v35 drops elementTypeIndex: 84-byte type rows, post fields shifted by -4.
        // Non-zero method/field counts prove the shift (misaligned reads would fail).
        var types = new[]
        {
            ("Player", "Game", 1, -1, 0, 2, 0, 1),
        };
        var methods = new (string, bool, int)[] { ("Jump", true, 0), ("Run", false, 1) };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(35, 84, 36, types, methods, new[] { "health" }));

        var metadata = Il2CppMetadata.Load(path);
        Assert.Equal(35, metadata.MetadataVersion);
        var player = Assert.Single(metadata.TypesInImage("Assembly-CSharp.dll"));
        var playerMethods = metadata.MethodsOf(player);
        Assert.Equal(2, playerMethods.Count);
        Assert.Equal("Run", playerMethods[1].Name);
        Assert.Equal(1, playerMethods[1].ParameterCount);
        Assert.Equal("health", Assert.Single(metadata.FieldsOf(player)).Name);
    }

    [Fact]
    public void LoadsV38TripletLayout()
    {
        // v38 headers are (offset,size,count) triplets; structs match v35.
        var types = new[]
        {
            ("Player", "Game", 1, -1, 0, 1, 0, 1),
        };
        var methods = new (string, bool, int)[] { ("Jump", false, 0) };
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(38, 84, 36, types, methods, new[] { "health" },
            imageName: "Assembly-CSharp.dll", triplets: true));

        Assert.Equal(22, Il2CppMetadata.DumpHeaderPairs(path).Count);
        var metadata = Il2CppMetadata.Load(path);
        Assert.Equal(38, metadata.MetadataVersion);
        var player = Assert.Single(metadata.TypesInImage("Assembly-CSharp.dll"));
        Assert.Equal("Jump", Assert.Single(metadata.MethodsOf(player)).Name);
        Assert.Equal("health", Assert.Single(metadata.FieldsOf(player)).Name);
    }

    [Fact]
    public void LoadsSingleByteXorEncrypted()
    {
        var types = new[]
        {
            ("Player", "Game", 1, -1, 0, 1, 0, 0),
        };
        var methods = new (string, bool, int)[] { ("Jump", false, 0) };
        var plain = BuildMetadata(30, 88, 32, types, methods, Array.Empty<string>());
        var key = (byte)0x5A;
        var encrypted = plain.Select(b => (byte)(b ^ key)).ToArray();
        var path = TempPath();
        File.WriteAllBytes(path, encrypted);

        var metadata = Il2CppMetadata.Load(path);
        Assert.Equal(30, metadata.MetadataVersion);
        Assert.Equal("Player", Assert.Single(metadata.TypesInImage("Assembly-CSharp.dll")).Name);
    }

    [Fact]
    public void RejectsNewerThan38()
    {
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(39, 88, 32,
            new[] { ("A", string.Empty, 1, -1, 0, 0, 0, 0) },
            Array.Empty<(string, bool, int)>(), Array.Empty<string>()));

        var ex = Assert.Throws<MetadataFormatException>(() => Il2CppMetadata.Load(path));
        Assert.Contains("24-38", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad-magic")]
    [InlineData("bad-version")]
    [InlineData("truncated")]
    public void RejectsMalformedMetadata(string kind)
    {
        var path = TempPath();
        switch (kind)
        {
            case "bad-magic":
                File.WriteAllBytes(path, BuildMetadata(30, 88, 32,
                    new[] { ("A", string.Empty, 1, -1, 0, 0, 0, 0) },
                    Array.Empty<(string, bool, int)>(), Array.Empty<string>()));
                var bytes = File.ReadAllBytes(path);
                BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, 0);
                File.WriteAllBytes(path, bytes);
                break;
            case "bad-version":
                File.WriteAllBytes(path, BuildMetadata(17, 88, 32,
                    new[] { ("A", string.Empty, 1, -1, 0, 0, 0, 0) },
                    Array.Empty<(string, bool, int)>(), Array.Empty<string>()));
                break;
            default:
                File.WriteAllBytes(path, new byte[10]);
                break;
        }

        Assert.Throws<MetadataFormatException>(() => Il2CppMetadata.Load(path));
    }

    [Fact]
    public void UnknownImageExplainsItself()
    {
        var path = TempPath();
        File.WriteAllBytes(path, BuildMetadata(30, 88, 32,
            new[] { ("A", string.Empty, 1, -1, 0, 0, 0, 0) },
            Array.Empty<(string, bool, int)>(), Array.Empty<string>()));
        var metadata = Il2CppMetadata.Load(path);
        var ex = Assert.Throws<MetadataFormatException>(() => metadata.TypesInImage("Nope.dll"));
        Assert.Contains("Assembly-CSharp.dll", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealV31FileParsesWhenPresent()
    {
        var root = FindRepoRoot();
        var candidate = root is null ? null : Path.Combine(
            root, "fixtures-dev", "d1alogue", "D1AL-ogue_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        if (candidate is null || !File.Exists(candidate))
        {
            _output.WriteLine("real v31 fixture absent (fixtures-dev/ is gitignored) — skipping live assertions");
            return;
        }

        var metadata = Il2CppMetadata.Load(candidate);
        Assert.Equal(31, metadata.MetadataVersion);
        Assert.Contains(metadata.Images(), i => i.Name == "Assembly-CSharp.dll");

        var types = metadata.TypesInImage("Assembly-CSharp.dll");
        var enoughTypes = types.Count > 200;
        Assert.True(enoughTypes, $"expected hundreds of types, got {types.Count}");
        foreach (var type in types.Take(20))
        {
            _ = metadata.MethodsOf(type);
            _ = metadata.FieldsOf(type);
        }

        var source = ProjectionWriter.Generate(metadata, "Assembly-CSharp.dll");
        Assert.Contains("AudioManager", source, StringComparison.Ordinal);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nami.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
