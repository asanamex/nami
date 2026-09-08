using System.Buffers.Binary;
using System.Text;

namespace Nami.Interop;

/// <summary>Header sanity checks failed — the file is not a plaintext IL2CPP metadata we can parse.</summary>
public sealed class MetadataFormatException(string message) : Exception(message);

/// <summary>A parsed il2cpp type definition (name, namespace, declaring type, member counts).</summary>
public sealed class Il2CppTypeInfo
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public int DeclaringTypeIndex { get; init; }
    public uint Flags { get; init; }
    public int MethodCount { get; init; }
    public int PropertyCount { get; init; }
    public int FieldCount { get; init; }
    public int MethodStart { get; init; }
    public int PropertyStart { get; init; }
    public int FieldStart { get; init; }

    /// <summary>Nested types have a non-negative declaring type index.</summary>
    public bool IsNested => DeclaringTypeIndex >= 0;

    /// <summary>TypeAttributes visibility bits (plausible member of a public API surface).</summary>
    public bool IsPublic =>
        (Flags & 0x00000007) is 0x00000001 or 0x00000002; // Public or NestedPublic

    public string FullName =>
        string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
}

/// <summary>A parsed il2cpp method definition.</summary>
public sealed class Il2CppMethodInfo
{
    public required string Name { get; init; }
    public required int ParameterCount { get; init; }
    public required bool IsStatic { get; init; }

    public override string ToString() => IsStatic ? $"static {Name}({ParameterCount})" : $"{Name}({ParameterCount})";
}

/// <summary>A parsed il2cpp field definition.</summary>
public sealed class Il2CppFieldInfo
{
    public required string Name { get; init; }
    public required bool IsStatic { get; init; }
}

/// <summary>
/// Reader for Unity IL2CPP's <c>global-metadata.dat</c> (plaintext metadata). Pure .NET, no
/// native dependencies, never touches the game. Dev-time only: powers <c>nami interop</c>,
/// never the runtime bridge (runtime access resolves through the game's own il2cpp_* API).
///
///  The metadata format is versioned, so the reader is defensive by construction:
///  1. the header is read as (offset,size) pairs in the canonical order (stable from v24.1
///     through v38; v38+ writes (offset,size,count) TRIPLETS — the count is skipped);
///     later versions append pairs rather than reorder;
///  2. every region is bounds-checked before use;
///  3. the Il2CppTypeDefinition / Il2CppMethodDefinition struct strides are CALIBRATED in-file by
///     sampling candidate strides and accepting the first whose decoded name strings are mostly
///     plausible (IL names like "&lt;Module&gt;" and ".ctor" count) — robust to Unity adding
///     fields, without hard-coded per-version tables;
///  4. the type-definition field shift is resolved per file too: v24.1 keeps byrefTypeIndex
///     (declaringType at +16) while v27+ drops it (declaringType at +12); v35 (Unity 6000.3)
///     drops elementTypeIndex (rows shrink 88 to 84, post fields shift by -4). Method/field
///     struct heads (nameIndex at +0; parameterCount in the final uint16) never moved.
///  Versions without documented struct changes (v32-v34, v36-v37) parse as their neighbor
///  layout; every file still has to pass the sentinel + stride + declaring-index validation,
///  so an unknown layout fails loud instead of returning shifted garbage.
/// </summary>
public sealed class Il2CppMetadata
{
    public const uint ExpectedMagic = 0xFAB11BAF;

    private enum MethodAttributes : uint
    {
        Static = 0x0010,
    }

    private byte[] _data;
    private (int Offset, int Size) _stringLiteral, _stringLiteralData, _string,
        _typeDefinitions, _methods, _fields, _images, _assemblies;

    private int _typeDefinitionStride;
    private int _methodStride;
    private int _fieldStride;

    public int MetadataVersion { get; }

    private Il2CppMetadata(byte[] data)
    {
        _data = data;

        if (data.Length < 8 + 32 * 8)
        {
            throw new MetadataFormatException("file too small for an il2cpp metadata header");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != ExpectedMagic)
        {
            throw new MetadataFormatException(
                $"bad magic 0x{magic:X8} (expected 0x{ExpectedMagic:X8}) — not plaintext il2cpp metadata " +
                "(single-byte XOR probe found no key: custom encryption/obfuscation needs per-game reversing)");
        }

        MetadataVersion = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (MetadataVersion is < 24 or > 38)
        {
            throw new MetadataFormatException(
                MetadataVersion < 24
                    ? $"metadata version {MetadataVersion} is older than the supported range (24-38)"
                    : $"metadata version {MetadataVersion} is newer than the tested range (24-38) — run `nami interop header` to diagnose the layout");
        }

        // (offset, size) pairs follow {sanity, version}. The canonical order is stable, but v29+
        // REMOVED the (deprecated) fieldMarshaledSizes pair, shifting everything after it, and
        // v38 writes (offset, size, count) TRIPLETS. We parse every variant and keep the one
        // that validates (sentinel string + stride calibration) — self-correcting against
        // version drift without hard-coded tables.
        Exception? lastError = null;
        foreach (var variant in new (bool Triplets, bool Marshaled)[] { (false, false), (false, true), (true, false), (true, true) })
        {
            try
            {
                ParseHeader(data, variant.Marshaled, variant.Triplets);
            }
            catch (MetadataFormatException ex)
            {
                lastError = ex;
                continue;
            }

            break; // validated
        }

        if (_typeDefinitionStride == 0)
        {
            throw new MetadataFormatException(
                $"header layout not recognized (last error: {lastError?.Message}) — unsupported metadata version {MetadataVersion}");
        }
    }

    private void ParseHeader(byte[] data, bool includeMarshaledSizes, bool triplets)
    {
        // Reset calibration state: ParseHeader is tried for every header variant and a
        // failed attempt must not leave a stale stride behind that looks like success.
        _typeDefinitionStride = _methodStride = _fieldStride = 0;

        var idx = 2;
        (int, int) Pair()
        {
            var o = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(idx * 4));
            var s = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((idx + 1) * 4));
            idx += triplets ? 3 : 2; // v38 triplets carry a count we don't need
            return (o, s);
        }

        _stringLiteral = Pair();               // 1  stringLiterals
        _stringLiteralData = Pair();           // 2  stringLiteralData
        _string = Pair();                      // 3  string (names)
        _ = Pair();                            // 4  events
        _ = Pair();                            // 5  properties
        _methods = Pair();                     // 6  methods
        _ = Pair();                            // 7  parameterDefaultValues
        _ = Pair();                            // 8  fieldDefaultValues
        _ = Pair();                            // 9  fieldAndParameterDefaultValueData
        if (includeMarshaledSizes)
        {
            _ = Pair();                        // (v24.1-v27) fieldMarshaledSizes
        }

        _ = Pair();                            //    parameters
        _fields = Pair();                      //    fields
        _ = Pair();                            //    genericContainers
        _ = Pair();                            //    genericParameters
        _ = Pair();                            //    genericParameterConstraints
        _ = Pair();                            //    genericInstances
        _ = Pair();                            //    nestedTypes
        _ = Pair();                            //    interfaces
        _ = Pair();                            //    vtableMethods
        _ = Pair();                            //    interfaceOffsets
        _typeDefinitions = Pair();             //    typeDefinitions
        _images = Pair();                      //    images
        _assemblies = Pair();                  //    assemblies

        ValidateRegion(_string);
        ValidateRegion(_typeDefinitions);
        ValidateRegion(_methods);
        ValidateRegion(_images);

        // Header-shift sentinel: the first metadata string must be plausible ("mscorlib" etc.).
        var sentinel = StringAt(_string.Offset);
        if (sentinel.Length == 0 || !LooksLikeIdentifier(sentinel))
        {
            throw new MetadataFormatException(
                $"header looks shifted (first metadata string is {Describe(sentinel)}) — unsupported metadata layout");
        }

        _typeDefinitionStride = CalibrateStride(_typeDefinitions, minStride: 84, maxStride: 92, "typeDefinitions");
        _methodStride = CalibrateStride(_methods, minStride: 32, maxStride: 40, "methods");
        _fieldStride = CalibrateStride(_fields, minStride: 8, maxStride: 16, "fields");
        _typeShift = ResolveTypeLayout();
    }

    /// <summary>
    /// Resolves the Il2CppTypeDefinition field shift from the calibrated stride: v24.1 rows
    /// are 92 bytes (byrefTypeIndex present, declaringType at +16) while v27+ rows are 88
    /// bytes (declaringType at +12); v35 drops elementTypeIndex (84-byte rows — declaring
    /// stays at +12 since the removed field sits after it, everything after shifts by -4).
    /// The winning layout is validated by checking that declaring indices decode sanely
    /// (top-level types store -1); anything else fails loud instead of returning shifted
    /// garbage.
    /// </summary>
    private int ResolveTypeLayout()
    {
        var rows = _typeDefinitions.Size / _typeDefinitionStride;
        var checkedRows = Math.Min(rows, 64);
        var step = Math.Max(1, rows / Math.Max(checkedRows, 1));

        int Score(int declaringOffset)
        {
            var good = 0;
            var seen = 0;
            for (var row = 0; row < rows && seen < checkedRows; row += step, seen++)
            {
                var declaring = Int32At(_typeDefinitions.Offset + (long)row * _typeDefinitionStride + declaringOffset);
                if (declaring == -1 || (declaring >= 0 && declaring < rows))
                {
                    good++;
                }
            }

            return seen == 0 ? 0 : good * 100 / seen;
        }

        // The stride already disambiguates the three known layouts; the score only confirms
        // the chosen declaring offset decodes sanely (a future layout that breaks the
        // assumption fails loud here instead of returning shifted garbage).
        var declaringOffset = _typeDefinitionStride == 92 ? 16 : 12;
        var score = Score(declaringOffset);
        if (score >= 50)
        {
            // Post-elementTypeIndex fields shift with the stride vs the v27 88-byte base.
            return _typeDefinitionStride - 88;
        }

        throw new MetadataFormatException(
            $"could not resolve the type-definition layout (stride {_typeDefinitionStride}: +{declaringOffset} scored {score}%)" +
            " — run `nami interop header` to diagnose the layout");
    }

    /// <summary>Loads and parses a <c>global-metadata.dat</c> file.</summary>
    public static Il2CppMetadata Load(string path)
    {
        var data = ReadFileBytes(path);

        try
        {
            return new Il2CppMetadata(data);
        }
        catch (MetadataFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MetadataFormatException($"unreadable metadata '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a metadata file, transparently undoing single-byte XOR obfuscation: the key is
    /// derived from the first byte against the magic and accepted only if it decodes all
    /// four magic bytes (2^-24 false-positive rate). Catches the weakest real-world scheme;
    /// multi-byte/rolling/custom encryption still needs per-game reversing.
    /// </summary>
    private static byte[] ReadFileBytes(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new MetadataFormatException($"unreadable metadata '{path}': {ex.Message}");
        }

        if (data.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(data) != ExpectedMagic)
        {
            var key = (byte)(data[0] ^ (ExpectedMagic & 0xFF));
            var hit = true;
            for (var i = 0; i < 4; i++)
            {
                if ((data[i] ^ key) != ((ExpectedMagic >> (8 * i)) & 0xFF))
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] ^= key;
                }
            }
        }

        return data;
    }

    /// <summary>Reads the metadata version without parsing the header.</summary>
    public static int ReadVersion(string path)
    {
        var data = ReadFileBytes(path);
        if (data.Length < 8)
        {
            throw new MetadataFormatException("file too small");
        }

        Span<byte> header = stackalloc byte[8];
        data.AsSpan(0, 8).CopyTo(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != ExpectedMagic)
        {
            throw new MetadataFormatException($"bad magic 0x{magic:X8}");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
    }

    /// <summary>
    /// Diagnostic: reads the header's (offset,size) pairs without interpreting them. Used by
    /// `nami interop header` to make layout problems visible on unsupported metadata versions.
    /// The pair count is derived from the first region's offset (which equals the header size),
    /// so string-heap bytes are never misread as header pairs. v38+ triplet headers report
    /// (offset,size), skipping the per-section count.
    /// </summary>
    public static IReadOnlyList<(int Offset, int Size)> DumpHeaderPairs(string path)
    {
        var data = ReadFileBytes(path);
        if (data.Length < 8 + 2 * 4)
        {
            throw new MetadataFormatException("file too small");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != ExpectedMagic)
        {
            throw new MetadataFormatException($"bad magic 0x{magic:X8}");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        if (headerSize < 8 || headerSize > data.Length)
        {
            throw new MetadataFormatException($"implausible header size {headerSize}");
        }

        // Pairs vs v38+ triplets is ambiguous from size alone (a 22-triplet header is
        // also a whole number of pairs), so validate candidates by bounds and break ties
        // by version (triplets exist only at v38+). Pairs first (legacy default).
        var version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var strides = new List<int>(2);
        if ((headerSize - 8) % 8 == 0)
        {
            strides.Add(8);
        }

        if ((headerSize - 8) % 12 == 0)
        {
            strides.Add(12); // v38+ (offset,size,count) triplets
        }

        if (strides.Count == 0)
        {
            throw new MetadataFormatException($"implausible header size {headerSize}");
        }

        List<(int, int)>? pairsPreferred = null;
        foreach (var stride in strides)
        {
            var pairCount = (headerSize - 8) / stride;
            if (pairCount is <= 0 or > 256)
            {
                continue;
            }

            var pairs = new List<(int, int)>(pairCount);
            var valid = true;
            for (var i = 0; i < pairCount; i++)
            {
                var o = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8 + i * stride));
                var s = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8 + i * stride + 4));
                if (o < 0 || s < 0 || o + (long)s > data.Length)
                {
                    valid = false;
                    break;
                }

                pairs.Add((o, s));
            }

            if (!valid)
            {
                continue;
            }

            // Triplets at v38+, pairs below — matches the format's introduction version.
            if ((stride == 12) == (version >= 38))
            {
                return pairs;
            }

            pairsPreferred ??= pairs;
        }

        if (pairsPreferred is not null)
        {
            return pairsPreferred;
        }

        throw new MetadataFormatException("header pairs exceed the file — encrypted or unsupported metadata?");
    }

    /// <summary>
    /// Diagnostic: for each header region, finds struct strides where the +0 int32 decodes (via
    /// the metadata string heap) as a plausible identifier for most sampled rows. This is how
    /// the name-bearing regions (string-indexed struct arrays) are identified empirically.
    /// </summary>
    public static IReadOnlyList<(int Offset, int Size, List<int> PlausibleStrides)> AnalyzeRegions(string path)
    {
        var data = ReadFileBytes(path);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != ExpectedMagic)
        {
            throw new MetadataFormatException($"bad magic 0x{magic:X8}");
        }

        var pairs = DumpHeaderPairs(path);

        // Locate the string heap: the first region whose start decodes as a long printable run.
        var stringOffset = -1;
        foreach (var (o, s) in pairs)
        {
            if (s > 16 && LooksLikeIdentifier(DecodeString(data, o, s)))
            {
                stringOffset = o;
                break;
            }
        }

        string DecodeString(byte[] d, long start, int regionSize)
        {
            if (start < 0 || start >= d.Length)
            {
                return string.Empty;
            }

            var end = start;
            var limit = Math.Min(d.Length, start + 256);
            while (end < limit && d[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(d, (int)start, (int)(end - start));
        }

        var result = new List<(int, int, List<int>)>(pairs.Count);
        foreach (var (offset, size) in pairs)
        {
            var plausible = new List<int>();
            if (stringOffset >= 0 && size > 0)
            {
                foreach (var stride in Enumerable.Range(4, 137)) // 4..140
                {
                    if (size % stride != 0)
                    {
                        continue;
                    }

                    var rows = size / stride;
                    if (rows < 4)
                    {
                        continue;
                    }

                    var checkedRows = Math.Min(rows, 24);
                    var good = 0;
                    for (var row = 0; row < checkedRows; row++)
                    {
                        var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(
                            data.AsSpan(offset + row * stride, 4));
                        var s = DecodeString(data, stringOffset + nameIndex, size);
                        if (s.Length > 0 && s.All(c => c is >= ' ' and <= '~') && (char.IsLetter(s[0]) || s[0] == '_'))
                        {
                            good++;
                        }
                    }

                    if (good >= checkedRows * 3 / 4)
                    {
                        plausible.Add(stride);
                    }
                }
            }

            result.Add((offset, size, plausible));
        }

        return result;
    }

    // -------------------------------------------------------------- primitives

    private static string Describe(string s) =>
        s.Length == 0 ? "an empty string" : $"'{s[..Math.Min(s.Length, 24)]}'";

    // IL names are not C# identifiers: types include "<Module>" and "<>c", methods
    // include ".ctor"/".cctor" and accessors like "get_Item", generic arities use '`'.
    // Calibration accepts those; it only rejects empty/overlong/non-printable strings.
    // ponytail: 75% threshold (not 100%) because real heaps contain a few odd entries.
    private static bool LooksLikeIdentifier(string s) =>
        s.Length is > 0 and <= 128 &&
        s.All(c => c is >= ' ' and <= '~') &&
        (char.IsLetter(s[0]) || s[0] is '_' or '<' or '.' or '$');

    private void ValidateRegion((int Offset, int Size) region)
    {
        if (region.Offset < 0 || region.Size < 0 || region.Offset + (long)region.Size > _data.Length)
        {
            throw new MetadataFormatException($"region [{region.Offset}, {region.Size}) exceeds the file");
        }
    }

    private int Int32At(long byteOffset)
    {
        if (byteOffset < 0 || byteOffset + 4 > _data.Length)
        {
            throw new MetadataFormatException($"read at {byteOffset} exceeds the file");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan((int)byteOffset, 4));
    }

    private uint UInt16At(long byteOffset)
    {
        if (byteOffset < 0 || byteOffset + 2 > _data.Length)
        {
            throw new MetadataFormatException($"read at {byteOffset} exceeds the file");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)byteOffset, 2));
    }

    private string StringAt(long byteOffset)
    {
        if (byteOffset < 0 || byteOffset >= _data.Length)
        {
            return string.Empty;
        }

        var end = byteOffset;
        while (end < _data.Length && _data[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(_data, (int)byteOffset, (int)(end - byteOffset));
    }

    private string StringByIndex(int index)
    {
        if (index < 0 || index >= _string.Size)
        {
            return string.Empty;
        }

        var offset = _string.Offset + index;
        return offset >= 0 && offset < _data.Length ? StringAt(offset) : string.Empty;
    }

    private int CalibrateStride((int Offset, int Size) region, int minStride, int maxStride, string regionName)
    {
        ValidateRegion(region);

        if (region.Size == 0)
        {
            return minStride; // empty table: no rows to calibrate from, stride is irrelevant
        }

        Exception? lastMismatch = null;
        foreach (var stride in Enumerable.Range(minStride, maxStride - minStride + 1))
        {
            if (region.Size % stride != 0)
            {
                continue; // a real struct array divides the region exactly
            }

            var rows = region.Size / stride;
            if (rows == 0)
            {
                continue;
            }

            var checkedRows = Math.Min(rows, 32);
            var good = 0;
            for (var row = 0; row < checkedRows; row++)
            {
                int nameIndex;
                try
                {
                    nameIndex = Int32At(region.Offset + (long)row * stride);
                }
                catch (MetadataFormatException ex)
                {
                    lastMismatch = ex;
                    break;
                }

                if (LooksLikeIdentifier(StringByIndex(nameIndex)))
                {
                    good++;
                }
            }

            if (good * 4 >= checkedRows * 3)
            {
                return stride;
            }
        }

        throw new MetadataFormatException(
            $"could not calibrate a struct stride for {regionName} region [{region.Offset}, {region.Size})" +
            $" (tried strides {minStride}-{maxStride}; last read error: {lastMismatch?.Message ?? "none — names did not decode"})" +
            " — run `nami interop header` to diagnose the layout");
    }

    // ----------------------------------------------------------------- strings

    /// <summary>Reads a metadata name string (class/field/method/image/namespace) by index.</summary>
    public string GetString(int index) => StringByIndex(index);

    /// <summary>
    /// Reads a string literal (managed string data embedded in the game's code) by index.
    /// Returns null when the index is out of range or the bytes are not valid UTF-8.
    /// </summary>
    public string? GetStringLiteral(int index)
    {
        const int entrySize = 8; // { int32 length; int32 dataIndex; }
        if (index < 0 || (index + 1) * entrySize > _stringLiteral.Size)
        {
            return null;
        }

        var length = Int32At(_stringLiteral.Offset + index * entrySize);
        var dataIndex = Int32At(_stringLiteral.Offset + index * entrySize + 4);
        if (length < 0 || dataIndex < 0 || dataIndex + length > _stringLiteralData.Size)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(_data, _stringLiteralData.Offset + dataIndex, length);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Number of string literals in the metadata.</summary>
    public int StringLiteralCount => _stringLiteral.Size / 8;

    // ------------------------------------------------------------------ images

    private const int ImageDefinitionSize = 40; // v24.1+: nameIndex +0, typeStart +8, typeCount +12

    /// <summary>All image (assembly) definitions, in metadata order.</summary>
    public IReadOnlyList<(string Name, int Index)> Images()
    {
        if (_images.Size % ImageDefinitionSize != 0)
        {
            throw new MetadataFormatException(
                $"images region size {_images.Size} is not a multiple of {ImageDefinitionSize} — unsupported metadata layout");
        }

        var rows = _images.Size / ImageDefinitionSize;
        var result = new List<(string, int)>(rows);
        for (var i = 0; i < rows; i++)
        {
            var nameIndex = Int32At(_images.Offset + (long)i * ImageDefinitionSize);
            result.Add((StringByIndex(nameIndex), i));
        }

        return result;
    }

    // ------------------------------------------------------------------- types

    // Il2CppTypeDefinition, v24.1 (92 bytes, byrefTypeIndex present) vs v27+ (88 bytes,
    // byrefTypeIndex removed) vs v35+ (84 bytes, elementTypeIndex removed): every field
    // at/after the removal point shifts by exactly 4 per step. _typeShift is +4 on the
    // v24.1 layout, 0 on v27+, -4 on v35+; resolved empirically per file
    // (see ResolveTypeLayout) so minor-version drift needs no hard-coded table.
    // v27+ bases: name +0, namespace +4, declaring +12, flags +28, fieldStart +32,
    // methodStart +36, propertyStart +44, method_count +64, property_count +66,
    // field_count +68. Method/field struct heads (nameIndex +0) never moved.
    private int _typeShift;

    private int TypeDeclaringOffset => _typeDefinitionStride == 92 ? 16 : 12;
    private int TypeFlagsOffset => 28 + _typeShift;
    private int TypeFieldStartOffset => 32 + _typeShift;
    private int TypeMethodStartOffset => 36 + _typeShift;
    private int TypePropertyStartOffset => 44 + _typeShift;
    private int TypeMethodCountOffset => 64 + _typeShift;
    private int TypePropertyCountOffset => 66 + _typeShift;
    private int TypeFieldCountOffset => 68 + _typeShift;

    private Il2CppTypeInfo ReadType(int index)
    {
        var row = _typeDefinitions.Offset + (long)index * _typeDefinitionStride;
        return new Il2CppTypeInfo
        {
            Name = StringByIndex(Int32At(row)),
            Namespace = StringByIndex(Int32At(row + 4)),
            DeclaringTypeIndex = Int32At(row + TypeDeclaringOffset),
            Flags = (uint)Int32At(row + TypeFlagsOffset),
            FieldStart = Int32At(row + TypeFieldStartOffset),
            MethodStart = Int32At(row + TypeMethodStartOffset),
            PropertyStart = Int32At(row + TypePropertyStartOffset),
            MethodCount = (int)UInt16At(row + TypeMethodCountOffset),
            PropertyCount = (int)UInt16At(row + TypePropertyCountOffset),
            FieldCount = (int)UInt16At(row + TypeFieldCountOffset)
        };
    }

    /// <summary>Enumerates every type definition in the metadata.</summary>
    public IReadOnlyList<Il2CppTypeInfo> AllTypes()
    {
        var rows = _typeDefinitions.Size / _typeDefinitionStride;
        var result = new List<Il2CppTypeInfo>(rows);
        for (var i = 0; i < rows; i++)
        {
            result.Add(ReadType(i));
        }

        return result;
    }

    /// <summary>Top-level (non-nested) types of one image, e.g. "Assembly-CSharp.dll".</summary>
    public IReadOnlyList<Il2CppTypeInfo> TypesInImage(string imageName)
    {
        foreach (var image in Images())
        {
            if (!string.Equals(image.Name, imageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = _images.Offset + (long)image.Index * ImageDefinitionSize;
            var typeStart = Int32At(row + 8);
            var typeCount = Int32At(row + 12);
            var total = _typeDefinitions.Size / _typeDefinitionStride;
            if (typeStart < 0 || typeCount < 0 || typeStart + (long)typeCount > total)
            {
                throw new MetadataFormatException($"image '{imageName}' type range [{typeStart}, {typeCount}) is out of bounds");
            }

            var result = new List<Il2CppTypeInfo>(typeCount);
            for (var t = 0; t < typeCount; t++)
            {
                var info = ReadType(typeStart + t);
                if (!info.IsNested)
                {
                    result.Add(info);
                }
            }

            return result;
        }

        throw new MetadataFormatException($"image '{imageName}' not found (images: {string.Join(", ", Images().Select(i => i.Name))})");
    }

    // ----------------------------------------------------------------- methods

    // Il2CppMethodDefinition, v24.1+: nameIndex +0; flags(u32) at stride-8; parameterCount(u16)
    // is the struct's final uint16 (stride-2) — invariant across v24.1..v38 (v31 only
    // inserts returnParameterToken mid-struct, which the relative tail layout absorbs).
    private const MethodAttributes MethodStatic = MethodAttributes.Static;

    /// <summary>Enumerates the methods of a type (from <see cref="Il2CppTypeInfo.MethodStart"/>).</summary>
    public IReadOnlyList<Il2CppMethodInfo> MethodsOf(Il2CppTypeInfo type)
    {
        if (type.MethodCount > 0)
        {
            var total = _methods.Size / _methodStride;
            if (type.MethodStart < 0 || type.MethodStart + (long)type.MethodCount > total)
            {
                throw new MetadataFormatException(
                    $"type '{type.FullName}' method range [{type.MethodStart}, {type.MethodCount}) is out of bounds ({total} methods)");
            }
        }

        var result = new List<Il2CppMethodInfo>(type.MethodCount);
        for (var i = 0; i < type.MethodCount; i++)
        {
            var row = _methods.Offset + (long)(type.MethodStart + i) * _methodStride;
            var flags = (uint)Int32At(row + _methodStride - 8);
            result.Add(new Il2CppMethodInfo
            {
                Name = StringByIndex(Int32At(row)),
                ParameterCount = (int)UInt16At(row + _methodStride - 2),
                IsStatic = (flags & (uint)MethodStatic) != 0
            });
        }

        return result;
    }

    // ------------------------------------------------------------------ fields

    // Il2CppFieldDefinition: nameIndex +0, typeIndex +4 (v24.1+; stride calibrated ~12).
    // Static-ness needs the FieldInfo default-value flag which is v24-only; convention:
    // treat fields as instance unless the type's static data says otherwise. We expose the
    // name list; static detection happens through the runtime bridge anyway.
    private const int FieldNameOffset = 0;

    /// <summary>Enumerates the fields of a type (from <see cref="Il2CppTypeInfo.FieldStart"/>).</summary>
    public IReadOnlyList<Il2CppFieldInfo> FieldsOf(Il2CppTypeInfo type)
    {
        if (type.FieldCount > 0)
        {
            var total = _fields.Size / _fieldStride;
            if (type.FieldStart < 0 || type.FieldStart + (long)type.FieldCount > total)
            {
                throw new MetadataFormatException(
                    $"type '{type.FullName}' field range [{type.FieldStart}, {type.FieldCount}) is out of bounds ({total} fields)");
            }
        }

        var result = new List<Il2CppFieldInfo>(type.FieldCount);
        for (var i = 0; i < type.FieldCount; i++)
        {
            var row = _fields.Offset + (long)(type.FieldStart + i) * _fieldStride;
            result.Add(new Il2CppFieldInfo
            {
                Name = StringByIndex(Int32At(row + FieldNameOffset)),
                IsStatic = false // static-ness is resolved by the runtime bridge; not encoded here
            });
        }

        return result;
    }
}
