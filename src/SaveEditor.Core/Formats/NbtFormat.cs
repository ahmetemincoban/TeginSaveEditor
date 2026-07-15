using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SaveEditor.Core.Formats;

/// <summary>
/// Minecraft NBT (Named Binary Tag) files, most commonly seen gzip-wrapped
/// as "level.dat"/player data. Unlike GVAS/pickle/Marshal, NBT is a strict
/// tree (no shared references or cycles) so no separate object-graph model
/// is needed: the reader/writer convert directly to/from the JSON tree.
/// Only TAG_Int and TAG_String/TAG_Compound have a natural, untagged JSON
/// shape; every other tag type is tagged with {"nbt": ...} so the exact
/// type round-trips (reusing GvasInt/GvasFloat's JS-safety conventions for
/// Long/Float/Double, since NBT needs the exact same handling).
/// </summary>
public sealed class NbtFormat : ISaveFormat
{
    public string Id => "nbt";
    public string Name => "NBT (Minecraft)";
    public int Priority => 42;

    public bool CanRead(byte[] data, string fileName)
    {
        if (!Path.GetExtension(fileName).Equals(".dat", StringComparison.OrdinalIgnoreCase)) return false;
        if (data.Length < 3 || data[0] != 10) return false; // TAG_Compound
        int nameLen = (data[1] << 8) | data[2];
        return nameLen >= 0 && nameLen <= 1024 && data.Length >= 3 + nameLen;
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        try
        {
            var reader = new NbtReader(data);
            JsonNode? root = reader.ReadFile();
            var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = root };
            doc.State["rootName"] = reader.RootName;
            return doc;
        }
        catch (Exception ex)
        {
            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = new JsonObject { ["__type"] = "bytes", ["b64"] = Convert.ToBase64String(data) },
                Editable = false,
            };
            doc.Warnings.Add($"NBT verisi çözümlenemedi ({ex.Message}); ham veri gösteriliyor.");
            return doc;
        }
    }

    public byte[] Write(SaveDocument doc)
    {
        if (doc.Root is JsonObject o && o["__type"]?.GetValue<string>() == "bytes")
            return Convert.FromBase64String(o["b64"]!.GetValue<string>());
        string rootName = doc.State.TryGetValue("rootName", out object? rn) && rn is string s ? s : "";
        return new NbtWriter().WriteFile(rootName, doc.Root);
    }
}

internal sealed class NbtReader(byte[] data)
{
    private const int MaxDepth = 500;

    private int _pos;
    private int _depth;

    public string RootName { get; private set; } = "";

    public JsonNode? ReadFile()
    {
        byte type = NextByte();
        if (type != 10) throw Bad("Kök etiket TAG_Compound değil.");
        RootName = ReadName();
        return ReadCompoundBody();
    }

    private JsonNode? ReadValue(byte type)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw Bad("Veri çok derin iç içe geçmiş.");
        }
        try
        {
            return ReadValueCore(type);
        }
        finally
        {
            _depth--;
        }
    }

    private JsonNode? ReadValueCore(byte type) => type switch
    {
        1 => new JsonObject { ["nbt"] = "byte", ["v"] = (int)ReadSByte() },
        2 => new JsonObject { ["nbt"] = "short", ["v"] = (int)ReadInt16() },
        3 => ReadInt32(),
        4 => new JsonObject { ["nbt"] = "long", ["v"] = GvasInt.ToNode(ReadInt64()) },
        5 => new JsonObject { ["nbt"] = "float", ["v"] = GvasFloat.ToNode(ReadSingle()) },
        6 => new JsonObject { ["nbt"] = "double", ["v"] = GvasFloat.ToNode(ReadDouble()) },
        7 => ReadByteArray(),
        8 => ReadName(),
        9 => ReadList(),
        10 => ReadCompoundBody(),
        11 => ReadIntArray(),
        12 => ReadLongArray(),
        _ => throw Bad($"Bilinmeyen NBT etiket tipi: {type}"),
    };

    private JsonObject ReadCompoundBody()
    {
        var obj = new JsonObject();
        while (true)
        {
            byte type = NextByte();
            if (type == 0) break; // TAG_End
            string name = ReadName();
            obj[name] = ReadValue(type);
        }
        return obj;
    }

    private JsonNode ReadList()
    {
        byte elemType = NextByte();
        int count = ReadInt32Raw();
        if (count < 0) throw Bad("Geçersiz liste uzunluğu.");
        var items = new JsonArray();
        for (int i = 0; i < count; i++) items.Add(ReadValue(elemType));
        return new JsonObject { ["nbt"] = "list", ["type"] = TagName(elemType), ["items"] = items };
    }

    private JsonNode ReadByteArray()
    {
        int count = ReadInt32Raw();
        if (count < 0 || count > 256 * 1024 * 1024) throw Bad("Geçersiz bayt dizisi uzunluğu.");
        byte[] bytes = Next(count).ToArray();
        return new JsonObject { ["nbt"] = "bytearray", ["b64"] = Convert.ToBase64String(bytes) };
    }

    private JsonNode ReadIntArray()
    {
        int count = ReadInt32Raw();
        if (count < 0 || count > 64 * 1024 * 1024) throw Bad("Geçersiz int dizisi uzunluğu.");
        var items = new JsonArray();
        for (int i = 0; i < count; i++) items.Add(ReadInt32Raw());
        return new JsonObject { ["nbt"] = "intarray", ["items"] = items };
    }

    private JsonNode ReadLongArray()
    {
        int count = ReadInt32Raw();
        if (count < 0 || count > 32 * 1024 * 1024) throw Bad("Geçersiz long dizisi uzunluğu.");
        var items = new JsonArray();
        for (int i = 0; i < count; i++) items.Add(GvasInt.ToNode(ReadInt64()));
        return new JsonObject { ["nbt"] = "longarray", ["items"] = items };
    }

    private int ReadInt32() => ReadInt32Raw();

    internal static string TagName(byte type) => type switch
    {
        0 => "end", // conventional "no element type" marker for an empty list
        1 => "byte",
        2 => "short",
        3 => "int",
        4 => "long",
        5 => "float",
        6 => "double",
        7 => "bytearray",
        8 => "string",
        9 => "list",
        10 => "compound",
        11 => "intarray",
        12 => "longarray",
        _ => throw new SaveFormatException($"Bilinmeyen NBT etiket tipi: {type}"),
    };

    private string ReadName()
    {
        int len = ReadUInt16();
        if (len == 0) return "";
        return Encoding.UTF8.GetString(Next(len));
    }

    private byte NextByte()
    {
        if (_pos >= data.Length) throw Bad("Beklenmeyen dosya sonu.");
        return data[_pos++];
    }

    private sbyte ReadSByte() => unchecked((sbyte)NextByte());
    private short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Next(2));
    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Next(2));
    private int ReadInt32Raw() => BinaryPrimitives.ReadInt32BigEndian(Next(4));
    private long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Next(8));
    private float ReadSingle() => BinaryPrimitives.ReadSingleBigEndian(Next(4));
    private double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Next(8));

    private ReadOnlySpan<byte> Next(int count)
    {
        if (count < 0 || _pos + count > data.Length) throw Bad("Beklenmeyen dosya sonu.");
        var span = data.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    private static SaveFormatException Bad(string message) => new($"NBT çözümleme hatası: {message}");
}

internal sealed class NbtWriter
{
    private const int MaxDepth = 500;

    private readonly MemoryStream _out = new();
    private int _depth;

    public byte[] WriteFile(string rootName, JsonNode? root)
    {
        _out.WriteByte(10); // TAG_Compound
        WriteName(rootName);
        WriteCompoundBody(root);
        return _out.ToArray();
    }

    private void WriteValue(JsonNode? node, byte type)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new SaveFormatException("Veri çok derin iç içe geçmiş.");
        }
        try
        {
            WriteValueCore(node, type);
        }
        finally
        {
            _depth--;
        }
    }

    private void WriteValueCore(JsonNode? node, byte type)
    {
        switch (type)
        {
            case 1: WriteSByte((sbyte)GetIntegral(node)); return;
            case 2: WriteInt16((short)GetIntegral(node)); return;
            case 3: WriteInt32Raw((int)GetIntegral(node)); return;
            case 4: WriteInt64(GvasInt.GetInt64(RequireTagged(node))); return;
            case 5: WriteSingle(GvasFloat.GetSingle(RequireTagged(node))); return;
            case 6: WriteDouble(GvasFloat.GetDouble(RequireTagged(node))); return;
            case 7: WriteByteArrayBody(node); return;
            case 8: WriteName(RequireString(node)); return;
            case 9: WriteListBody(node); return;
            case 10: WriteCompoundBody(node); return;
            case 11: WriteIntArrayBody(node); return;
            case 12: WriteLongArrayBody(node); return;
            default: throw new SaveFormatException($"Bilinmeyen NBT etiket tipi: {type}");
        }
    }

    private void WriteCompoundBody(JsonNode? node)
    {
        var obj = node as JsonObject ?? throw new SaveFormatException("Geçersiz NBT compound değeri.");
        foreach (var (name, value) in obj)
        {
            byte type = DetectType(value);
            _out.WriteByte(type);
            WriteName(name);
            WriteValue(value, type);
        }
        _out.WriteByte(0); // TAG_End
    }

    private void WriteListBody(JsonNode? node)
    {
        var obj = node as JsonObject ?? throw new SaveFormatException("Geçersiz NBT liste değeri.");
        string typeName = obj["type"]?.GetValue<string>() ?? throw new SaveFormatException("NBT listesinde 'type' eksik.");
        byte elemType = TypeId(typeName);
        var items = obj["items"] as JsonArray ?? throw new SaveFormatException("NBT listesinde 'items' eksik.");
        _out.WriteByte(elemType);
        WriteInt32Raw(items.Count);
        foreach (var item in items) WriteValue(item, elemType);
    }

    private void WriteByteArrayBody(JsonNode? node)
    {
        var obj = node as JsonObject ?? throw new SaveFormatException("Geçersiz NBT bayt dizisi.");
        byte[] bytes = Convert.FromBase64String(obj["b64"]?.GetValue<string>() ?? throw new SaveFormatException("NBT bayt dizisinde 'b64' eksik."));
        WriteInt32Raw(bytes.Length);
        _out.Write(bytes);
    }

    private void WriteIntArrayBody(JsonNode? node)
    {
        var obj = node as JsonObject ?? throw new SaveFormatException("Geçersiz NBT int dizisi.");
        var items = obj["items"] as JsonArray ?? throw new SaveFormatException("NBT int dizisinde 'items' eksik.");
        WriteInt32Raw(items.Count);
        foreach (var item in items) WriteInt32Raw((int)GetIntegral(item));
    }

    private void WriteLongArrayBody(JsonNode? node)
    {
        var obj = node as JsonObject ?? throw new SaveFormatException("Geçersiz NBT long dizisi.");
        var items = obj["items"] as JsonArray ?? throw new SaveFormatException("NBT long dizisinde 'items' eksik.");
        WriteInt32Raw(items.Count);
        foreach (var item in items) WriteInt64(GvasInt.GetInt64(item!));
    }

    private static byte TypeId(string name) => name switch
    {
        "end" => 0,
        "byte" => 1,
        "short" => 2,
        "int" => 3,
        "long" => 4,
        "float" => 5,
        "double" => 6,
        "bytearray" => 7,
        "string" => 8,
        "list" => 9,
        "compound" => 10,
        "intarray" => 11,
        "longarray" => 12,
        _ => throw new SaveFormatException($"Bilinmeyen NBT etiket adı: {name}"),
    };

    /// <summary>Determines what NBT tag a JSON value should be written as.
    /// Only TAG_Int (bare number), TAG_String (bare string) and TAG_Compound
    /// (bare object) are untagged; everything else carries an "nbt" tag.</summary>
    private static byte DetectType(JsonNode? node) => node switch
    {
        null => throw new SaveFormatException("NBT boş (null) değer içeremez."),
        JsonObject obj when obj["nbt"] is JsonValue tag && tag.TryGetValue(out string? t) => TypeId(t!),
        JsonObject => 10,
        JsonArray => throw new SaveFormatException("Ham NBT listesi {\"nbt\":\"list\",\"type\":...,\"items\":[...]} biçiminde olmalı."),
        JsonValue v when v.TryGetValue(out bool _) => 1, // lenient: treat a hand-edited true/false as a boolean byte
        JsonValue v when v.TryGetValue(out string? _) => 8,
        JsonValue => 3,
        _ => throw new SaveFormatException("Geçersiz NBT düğümü."),
    };

    private static JsonNode RequireTagged(JsonNode? node)
        => (node as JsonObject)?["v"] ?? throw new SaveFormatException("Geçersiz NBT etiketli değer.");

    private static string RequireString(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s!,
        _ => throw new SaveFormatException("Geçersiz NBT dize değeri."),
    };

    /// <summary>Robustly extracts an integral value from either a tagged
    /// {"nbt":"byte"/"short",...,"v":N} node or a bare number/bool (the
    /// latter for TAG_Int and the lenient hand-edited-boolean case),
    /// regardless of which concrete numeric CLR type System.Text.Json
    /// boxed it as.</summary>
    private static long GetIntegral(JsonNode? node)
    {
        if (node is null) throw new SaveFormatException("NBT boş (null) tamsayı değeri içeremez.");
        if (node is JsonObject obj) node = obj["v"] ?? throw new SaveFormatException("Geçersiz NBT etiketli değer.");
        var v = node!.AsValue();
        if (v.TryGetValue(out bool b)) return b ? 1 : 0;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out short sh)) return sh;
        if (v.TryGetValue(out sbyte sb)) return sb;
        if (v.TryGetValue(out string? s)) return long.Parse(s!, CultureInfo.InvariantCulture);
        return v.GetValue<long>();
    }

    private void WriteName(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        WriteUInt16((ushort)bytes.Length);
        _out.Write(bytes);
    }

    private void WriteSByte(sbyte v) => _out.WriteByte(unchecked((byte)v));

    private void WriteInt16(short v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, v);
        _out.Write(buf);
    }

    private void WriteUInt16(ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        _out.Write(buf);
    }

    private void WriteInt32Raw(int v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, v);
        _out.Write(buf);
    }

    private void WriteInt64(long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, v);
        _out.Write(buf);
    }

    private void WriteSingle(float v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, v);
        _out.Write(buf);
    }

    private void WriteDouble(double v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buf, v);
        _out.Write(buf);
    }
}
