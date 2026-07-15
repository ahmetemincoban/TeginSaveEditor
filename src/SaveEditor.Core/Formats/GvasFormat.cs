using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SaveEditor.Core.Formats;

/// <summary>
/// Unreal Engine 4/5 SaveGame files ("GVAS" magic). Common property types are
/// decoded into an editable tree; anything unrecognized is preserved as raw
/// base64 so the file always round-trips.
/// </summary>
public sealed class GvasFormat : ISaveFormat
{
    public string Id => "gvas";
    public string Name => "Unreal Engine SaveGame (GVAS)";
    public int Priority => 45;

    public bool CanRead(byte[] data, string fileName)
        => data.Length > 20 && data[0] == 'G' && data[1] == 'V' && data[2] == 'A' && data[3] == 'S';

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        try
        {
            var reader = new GvasReader(data);
            var root = reader.ReadFile();
            var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = root };
            if (reader.HadRawFallback)
                doc.Warnings.Add("Bazı özellikler tanınmadı ve ham (base64) olarak korundu; bunlar dışındaki her şey düzenlenebilir.");
            return doc;
        }
        catch (Exception ex) when (ex is not SaveFormatException)
        {
            // Unparseable variant: keep it openable read-only rather than failing.
            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = new JsonObject { ["__type"] = "bytes", ["b64"] = Convert.ToBase64String(data) },
                Editable = false,
            };
            doc.Warnings.Add($"GVAS dosyası çözümlenemedi ({ex.Message}); ham veri gösteriliyor.");
            return doc;
        }
    }

    public byte[] Write(SaveDocument doc)
    {
        if (doc.Root is JsonObject o && o["__type"]?.GetValue<string>() == "bytes")
            return Convert.FromBase64String(o["b64"]!.GetValue<string>());
        return new GvasWriter().WriteFile(doc.Root as JsonObject
            ?? throw new SaveFormatException("Geçersiz GVAS düzenleme verisi."));
    }
}

internal sealed class GvasReader(byte[] data)
{
    private readonly BinaryReader _r = new(new MemoryStream(data, writable: false));
    public bool HadRawFallback { get; private set; }

    public JsonObject ReadFile()
    {
        _r.BaseStream.Position = 4; // "GVAS"
        var header = new JsonObject();
        int saveGameVersion = _r.ReadInt32();
        header["saveGameVersion"] = saveGameVersion;
        header["packageVersionUE4"] = _r.ReadInt32();
        if (saveGameVersion >= 3) header["packageVersionUE5"] = _r.ReadInt32();
        header["engine"] = new JsonObject
        {
            ["major"] = _r.ReadUInt16(),
            ["minor"] = _r.ReadUInt16(),
            ["patch"] = _r.ReadUInt16(),
            ["changelist"] = _r.ReadUInt32(),
            ["branch"] = ReadFString(),
        };
        header["customVersionFormat"] = _r.ReadInt32();
        var customVersions = new JsonArray();
        int cvCount = _r.ReadInt32();
        if (cvCount is < 0 or > 10000) throw new SaveFormatException("Geçersiz GVAS başlığı.");
        for (int i = 0; i < cvCount; i++)
        {
            customVersions.Add(new JsonObject
            {
                ["guid"] = Convert.ToHexString(_r.ReadBytes(16)),
                ["version"] = _r.ReadInt32(),
            });
        }
        header["customVersions"] = customVersions;
        header["saveGameClassName"] = ReadFString();

        var properties = ReadPropertyList(_r.BaseStream.Length);
        long remaining = _r.BaseStream.Length - _r.BaseStream.Position;
        var root = new JsonObject { ["header"] = header, ["properties"] = properties };
        if (remaining > 0)
        {
            root["trailer"] = Convert.ToBase64String(_r.ReadBytes((int)remaining));
        }
        return root;
    }

    private JsonArray ReadPropertyList(long limit)
    {
        var list = new JsonArray();
        while (_r.BaseStream.Position < limit)
        {
            string name = ReadFString();
            if (name == "None") break;
            list.Add(ReadProperty(name));
        }
        return list;
    }

    private JsonObject ReadProperty(string name)
    {
        string type = ReadFString();
        int size = _r.ReadInt32();
        int arrayIndex = _r.ReadInt32();
        var prop = new JsonObject { ["name"] = name, ["type"] = type };
        if (arrayIndex != 0) prop["index"] = arrayIndex;

        switch (type)
        {
            case "BoolProperty":
                prop["value"] = _r.ReadByte() != 0;
                SkipGuidFlag(prop);
                return prop;

            case "IntProperty":
                SkipGuidFlag(prop);
                prop["value"] = _r.ReadInt32();
                return prop;
            case "Int8Property":
                SkipGuidFlag(prop);
                prop["value"] = _r.ReadSByte();
                return prop;
            case "Int16Property":
                SkipGuidFlag(prop);
                prop["value"] = _r.ReadInt16();
                return prop;
            case "Int64Property":
                SkipGuidFlag(prop);
                prop["value"] = GvasInt.ToNode(_r.ReadInt64());
                return prop;
            case "UInt16Property":
                SkipGuidFlag(prop);
                prop["value"] = _r.ReadUInt16();
                return prop;
            case "UInt32Property":
                SkipGuidFlag(prop);
                prop["value"] = _r.ReadUInt32();
                return prop;
            case "UInt64Property":
                SkipGuidFlag(prop);
                prop["value"] = GvasInt.ToNode(_r.ReadUInt64());
                return prop;
            case "FloatProperty":
                SkipGuidFlag(prop);
                prop["value"] = GvasFloat.ToNode(_r.ReadSingle());
                return prop;
            case "DoubleProperty":
                SkipGuidFlag(prop);
                prop["value"] = GvasFloat.ToNode(_r.ReadDouble());
                return prop;

            case "StrProperty":
            case "NameProperty":
            case "SoftObjectProperty":
            case "ObjectProperty":
                SkipGuidFlag(prop);
                prop["value"] = ReadFString();
                return prop;

            case "EnumProperty":
                prop["enumType"] = ReadFString();
                SkipGuidFlag(prop);
                prop["value"] = ReadFString();
                return prop;

            case "ByteProperty":
            {
                string enumName = ReadFString();
                prop["enumType"] = enumName;
                SkipGuidFlag(prop);
                if (enumName == "None") prop["value"] = _r.ReadByte();
                else prop["value"] = ReadFString();
                return prop;
            }

            case "StructProperty":
            {
                string structType = ReadFString();
                prop["structType"] = structType;
                prop["structGuid"] = Convert.ToHexString(_r.ReadBytes(16));
                SkipGuidFlag(prop);
                long bodyStart = _r.BaseStream.Position;
                JsonNode value = ReadStructBody(structType, size);
                if (_r.BaseStream.Position != bodyStart + size)
                {
                    // A struct parsed "successfully" but didn't consume exactly the
                    // declared byte span: our interpretation of the struct's shape is
                    // wrong, even though nothing threw. Trust the size field instead.
                    _r.BaseStream.Position = bodyStart;
                    value = new JsonObject { ["__raw"] = Convert.ToBase64String(_r.ReadBytes(size)) };
                    HadRawFallback = true;
                }
                prop["value"] = value;
                return prop;
            }

            case "ArrayProperty":
            {
                string innerType = ReadFString();
                prop["arrayType"] = innerType;
                SkipGuidFlag(prop);
                long start = _r.BaseStream.Position;
                try
                {
                    ReadArrayBody(prop, innerType, size);
                }
                catch (Exception ex) when (ex is EndOfStreamException or SaveFormatException)
                {
                    _r.BaseStream.Position = start;
                    prop.Remove("value");
                    prop.Remove("elementName");
                    prop.Remove("structType");
                    prop.Remove("structGuid");
                    prop["raw"] = Convert.ToBase64String(_r.ReadBytes(size));
                    HadRawFallback = true;
                }
                return prop;
            }

            default:
                // MapProperty, SetProperty, TextProperty, MulticastDelegate...
                // keep header extras + body verbatim.
                if (type == "MapProperty" || type == "SetProperty")
                {
                    prop["keyType"] = ReadFString();
                    if (type == "MapProperty") prop["valueType"] = ReadFString();
                }
                SkipGuidFlag(prop);
                prop["raw"] = Convert.ToBase64String(_r.ReadBytes(size));
                HadRawFallback = true;
                return prop;
        }
    }

    private void ReadArrayBody(JsonObject prop, string innerType, int size)
    {
        int count = _r.ReadInt32();
        if (count < 0) throw new SaveFormatException("Geçersiz dizi uzunluğu.");
        var items = new JsonArray();

        switch (innerType)
        {
            // JsonValue.Create(x) explicitly wherever x isn't already a
            // JsonNode: JsonArray's generic Add<T> resolves through a
            // reflection-based JsonTypeInfo lookup that throws under
            // JsonSerializerIsReflectionEnabledByDefault=false (confirmed to
            // actually happen with .NET 10's file-based app runner).
            case "IntProperty": for (int i = 0; i < count; i++) items.Add(JsonValue.Create(_r.ReadInt32())); break;
            case "Int64Property": for (int i = 0; i < count; i++) items.Add(GvasInt.ToNode(_r.ReadInt64())); break;
            case "UInt32Property": for (int i = 0; i < count; i++) items.Add(JsonValue.Create(_r.ReadUInt32())); break;
            case "FloatProperty": for (int i = 0; i < count; i++) items.Add(GvasFloat.ToNode(_r.ReadSingle())); break;
            case "DoubleProperty": for (int i = 0; i < count; i++) items.Add(GvasFloat.ToNode(_r.ReadDouble())); break;
            case "ByteProperty": for (int i = 0; i < count; i++) items.Add(JsonValue.Create(_r.ReadByte())); break;
            case "BoolProperty": for (int i = 0; i < count; i++) items.Add(JsonValue.Create(_r.ReadByte() != 0)); break;
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            case "SoftObjectProperty":
            case "ObjectProperty":
                for (int i = 0; i < count; i++) items.Add(JsonValue.Create(ReadFString()));
                break;

            case "StructProperty":
            {
                prop["elementName"] = ReadFString();
                string innerTypeName = ReadFString(); // "StructProperty"
                if (innerTypeName != "StructProperty") throw new SaveFormatException("Beklenmeyen dizi yapısı.");
                long innerSize = _r.ReadInt32();
                _ = _r.ReadInt32(); // array index
                string structType = ReadFString();
                prop["structType"] = structType;
                prop["structGuid"] = Convert.ToHexString(_r.ReadBytes(16));
                _ = _r.ReadByte(); // guid flag
                long end = _r.BaseStream.Position + innerSize;
                int elementSize = count > 0 ? (int)(innerSize / count) : 0;
                for (int i = 0; i < count; i++)
                {
                    items.Add(ReadStructBody(structType, remainingHint: elementSize));
                }
                if (_r.BaseStream.Position != end) throw new SaveFormatException("Dizi yapı boyutu eşleşmedi.");
                break;
            }

            default:
                throw new SaveFormatException($"Desteklenmeyen dizi tipi: {innerType}");
        }

        prop["value"] = items;
    }

    private JsonNode ReadStructBody(string structType, int remainingHint)
    {
        long start = _r.BaseStream.Position;
        switch (structType)
        {
            case "Guid":
                return Convert.ToHexString(_r.ReadBytes(16));
            case "DateTime":
            case "Timespan":
                return _r.ReadInt64();
            case "Vector" or "Rotator" when remainingHint == 12:
                return new JsonObject { ["__fmt"] = "f32", ["x"] = F32(), ["y"] = F32(), ["z"] = F32() };
            case "Vector" or "Rotator" when remainingHint == 24:
                return new JsonObject { ["__fmt"] = "f64", ["x"] = F64(), ["y"] = F64(), ["z"] = F64() };
            case "Vector2D" when remainingHint == 8:
                return new JsonObject { ["__fmt"] = "f32", ["x"] = F32(), ["y"] = F32() };
            case "Vector2D" when remainingHint == 16:
                return new JsonObject { ["__fmt"] = "f64", ["x"] = F64(), ["y"] = F64() };
            case "Quat" or "Vector4" when remainingHint == 16:
                return new JsonObject { ["__fmt"] = "f32", ["x"] = F32(), ["y"] = F32(), ["z"] = F32(), ["w"] = F32() };
            case "Quat" or "Vector4" when remainingHint == 32:
                return new JsonObject { ["__fmt"] = "f64", ["x"] = F64(), ["y"] = F64(), ["z"] = F64(), ["w"] = F64() };
            case "LinearColor" when remainingHint == 16:
                return new JsonObject { ["__fmt"] = "f32", ["r"] = F32(), ["g"] = F32(), ["b"] = F32(), ["a"] = F32() };
            case "Color" when remainingHint == 4:
                return new JsonObject { ["__fmt"] = "u8", ["b"] = _r.ReadByte(), ["g"] = _r.ReadByte(), ["r"] = _r.ReadByte(), ["a"] = _r.ReadByte() };
            case "IntPoint" when remainingHint == 8:
                return new JsonObject { ["__fmt"] = "i32", ["x"] = _r.ReadInt32(), ["y"] = _r.ReadInt32() };
        }

        // Generic struct: a nested property list terminated by "None".
        try
        {
            var list = ReadPropertyList(_r.BaseStream.Length);
            return new JsonObject { ["__struct"] = list };
        }
        catch (Exception ex) when (ex is EndOfStreamException or SaveFormatException)
        {
            _r.BaseStream.Position = start;
            HadRawFallback = true;
            return new JsonObject { ["__raw"] = Convert.ToBase64String(_r.ReadBytes(remainingHint)) };
        }
    }

    private JsonNode F32() => GvasFloat.ToNode(_r.ReadSingle());
    private JsonNode F64() => GvasFloat.ToNode(_r.ReadDouble());

    private void SkipGuidFlag(JsonObject prop)
    {
        byte flag = _r.ReadByte();
        if (flag == 1) prop["propertyGuid"] = Convert.ToHexString(_r.ReadBytes(16));
        else if (flag != 0) throw new SaveFormatException("Geçersiz özellik GUID bayrağı.");
    }

    private string ReadFString()
    {
        int len = _r.ReadInt32();
        if (len == 0) return "";
        if (len > 0)
        {
            if (len > 10_000_000) throw new SaveFormatException("Geçersiz dize uzunluğu.");
            byte[] bytes = _r.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            int effective = len > 0 && bytes[len - 1] == 0 ? len - 1 : len;
            return Encoding.UTF8.GetString(bytes, 0, effective);
        }
        int chars = -len;
        if (chars > 10_000_000) throw new SaveFormatException("Geçersiz dize uzunluğu.");
        byte[] wide = _r.ReadBytes(chars * 2);
        string s = Encoding.Unicode.GetString(wide);
        return s.EndsWith('\0') ? s[..^1] : s;
    }
}

/// <summary>
/// Floats that JSON cannot represent faithfully (NaN, ±Infinity, -0.0) are
/// carried as tagged strings so real saves survive the JSON round-trip.
/// </summary>
internal static class GvasFloat
{
    public static JsonNode ToNode(float v)
    {
        if (float.IsNaN(v)) return "NaN";
        if (float.IsPositiveInfinity(v)) return "Infinity";
        if (float.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0f && float.IsNegative(v)) return "-0.0";
        return v;
    }

    public static JsonNode ToNode(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0d && double.IsNegative(v)) return "-0.0";
        return v;
    }

    public static float GetSingle(JsonNode node) => (float)GetDouble(node);

    public static double GetDouble(JsonNode node)
    {
        var v = node.AsValue();
        if (v.TryGetValue(out string? s))
        {
            return s switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => double.Parse(s!, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        return v.GetValue<double>();
    }
}

/// <summary>
/// Int64/UInt64 values whose magnitude exceeds JavaScript's safe integer
/// range (2^53-1) are carried as tagged strings so browser round-trips
/// don't silently lose precision, mirroring <see cref="GvasFloat"/>.
/// </summary>
internal static class GvasInt
{
    private const long SafeMax = 9_007_199_254_740_991L; // 2^53 - 1

    public static JsonNode ToNode(long v)
    {
        if (v > SafeMax || v < -SafeMax) return v.ToString(CultureInfo.InvariantCulture);
        return v;
    }

    public static JsonNode ToNode(ulong v)
    {
        if (v > (ulong)SafeMax) return v.ToString(CultureInfo.InvariantCulture);
        return v;
    }

    public static long GetInt64(JsonNode node)
    {
        var v = node.AsValue();
        if (v.TryGetValue(out string? s)) return long.Parse(s!, CultureInfo.InvariantCulture);
        return v.GetValue<long>();
    }

    public static ulong GetUInt64(JsonNode node)
    {
        var v = node.AsValue();
        if (v.TryGetValue(out string? s)) return ulong.Parse(s!, CultureInfo.InvariantCulture);
        return v.GetValue<ulong>();
    }
}

internal sealed class GvasWriter
{
    private BinaryWriter _w = null!;

    public byte[] WriteFile(JsonObject root)
    {
        using var ms = new MemoryStream();
        _w = new BinaryWriter(ms);
        var header = root["header"] as JsonObject ?? throw new SaveFormatException("GVAS başlığı eksik.");

        _w.Write("GVAS"u8);
        int saveGameVersion = header["saveGameVersion"]!.GetValue<int>();
        _w.Write(saveGameVersion);
        _w.Write(header["packageVersionUE4"]!.GetValue<int>());
        if (saveGameVersion >= 3) _w.Write(header["packageVersionUE5"]!.GetValue<int>());
        var engine = header["engine"] as JsonObject ?? throw new SaveFormatException("GVAS motor sürümü eksik.");
        _w.Write(engine["major"]!.GetValue<ushort>());
        _w.Write(engine["minor"]!.GetValue<ushort>());
        _w.Write(engine["patch"]!.GetValue<ushort>());
        _w.Write(engine["changelist"]!.GetValue<uint>());
        WriteFString(engine["branch"]!.GetValue<string>());
        _w.Write(header["customVersionFormat"]!.GetValue<int>());
        var customVersions = header["customVersions"] as JsonArray ?? [];
        _w.Write(customVersions.Count);
        foreach (var cv in customVersions)
        {
            _w.Write(Convert.FromHexString(cv!["guid"]!.GetValue<string>()));
            _w.Write(cv["version"]!.GetValue<int>());
        }
        WriteFString(header["saveGameClassName"]!.GetValue<string>());

        WritePropertyList(root["properties"] as JsonArray ?? []);

        if (root["trailer"] is JsonNode trailer)
            _w.Write(Convert.FromBase64String(trailer.GetValue<string>()));

        _w.Flush();
        return ms.ToArray();
    }

    private void WritePropertyList(JsonArray properties)
    {
        foreach (var node in properties)
        {
            WriteProperty(node as JsonObject ?? throw new SaveFormatException("Geçersiz özellik düğümü."));
        }
        WriteFString("None");
    }

    private void WriteProperty(JsonObject prop)
    {
        string name = prop["name"]!.GetValue<string>();
        string type = prop["type"]!.GetValue<string>();
        int arrayIndex = prop["index"]?.GetValue<int>() ?? 0;
        WriteFString(name);
        WriteFString(type);

        // Body is built separately so the size field is always correct.
        using var bodyStream = new MemoryStream();
        var outer = _w;
        _w = new BinaryWriter(bodyStream);
        byte[] headerExtra;
        try
        {
            headerExtra = WritePropertyBody(prop, type);
        }
        finally
        {
            _w.Flush();
            _w = outer;
        }
        byte[] body = bodyStream.ToArray();

        _w.Write(type == "BoolProperty" ? 0 : body.Length);
        _w.Write(arrayIndex);
        _w.Write(headerExtra);
        WriteGuidFlag(prop);
        _w.Write(body);
    }

    /// <summary>Writes the property body into the temp stream and returns bytes that belong to the header (before the guid flag).</summary>
    private byte[] WritePropertyBody(JsonObject prop, string type)
    {
        switch (type)
        {
            case "BoolProperty":
                // Value lives in the header area (after size+index, before guid flag).
                return [(byte)(prop["value"]!.GetValue<bool>() ? 1 : 0)];

            case "IntProperty": _w.Write(prop["value"]!.GetValue<int>()); return [];
            case "Int8Property": _w.Write(prop["value"]!.GetValue<sbyte>()); return [];
            case "Int16Property": _w.Write(prop["value"]!.GetValue<short>()); return [];
            case "Int64Property": _w.Write(GvasInt.GetInt64(prop["value"]!)); return [];
            case "UInt16Property": _w.Write(prop["value"]!.GetValue<ushort>()); return [];
            case "UInt32Property": _w.Write(prop["value"]!.GetValue<uint>()); return [];
            case "UInt64Property": _w.Write(GvasInt.GetUInt64(prop["value"]!)); return [];
            case "FloatProperty": _w.Write(GvasFloat.GetSingle(prop["value"]!)); return [];
            case "DoubleProperty": _w.Write(GvasFloat.GetDouble(prop["value"]!)); return [];

            case "StrProperty":
            case "NameProperty":
            case "SoftObjectProperty":
            case "ObjectProperty":
                WriteFString(prop["value"]!.GetValue<string>());
                return [];

            case "EnumProperty":
            {
                WriteFString(prop["value"]!.GetValue<string>());
                return FStringBytes(prop["enumType"]!.GetValue<string>());
            }

            case "ByteProperty":
            {
                string enumName = prop["enumType"]?.GetValue<string>() ?? "None";
                if (enumName == "None") _w.Write(prop["value"]!.GetValue<byte>());
                else WriteFString(prop["value"]!.GetValue<string>());
                return FStringBytes(enumName);
            }

            case "StructProperty":
            {
                WriteStructBody(prop["value"]!);
                return [.. FStringBytes(prop["structType"]!.GetValue<string>()),
                        .. Convert.FromHexString(prop["structGuid"]?.GetValue<string>() ?? new string('0', 32))];
            }

            case "ArrayProperty":
            {
                string innerType = prop["arrayType"]!.GetValue<string>();
                if (prop["raw"] is JsonNode raw)
                {
                    _w.Write(Convert.FromBase64String(raw.GetValue<string>()));
                }
                else
                {
                    WriteArrayBody(prop, innerType);
                }
                return FStringBytes(innerType);
            }

            default:
            {
                _w.Write(Convert.FromBase64String(prop["raw"]!.GetValue<string>()));
                if (type == "MapProperty")
                    return [.. FStringBytes(prop["keyType"]!.GetValue<string>()),
                            .. FStringBytes(prop["valueType"]!.GetValue<string>())];
                if (type == "SetProperty")
                    return FStringBytes(prop["keyType"]!.GetValue<string>());
                return [];
            }
        }
    }

    private void WriteArrayBody(JsonObject prop, string innerType)
    {
        var items = prop["value"] as JsonArray ?? throw new SaveFormatException("Dizi değeri eksik.");
        _w.Write(items.Count);

        switch (innerType)
        {
            case "IntProperty": foreach (var i in items) _w.Write(i!.GetValue<int>()); break;
            case "Int64Property": foreach (var i in items) _w.Write(GvasInt.GetInt64(i!)); break;
            case "UInt32Property": foreach (var i in items) _w.Write(i!.GetValue<uint>()); break;
            case "FloatProperty": foreach (var i in items) _w.Write(GvasFloat.GetSingle(i!)); break;
            case "DoubleProperty": foreach (var i in items) _w.Write(GvasFloat.GetDouble(i!)); break;
            case "ByteProperty": foreach (var i in items) _w.Write(i!.GetValue<byte>()); break;
            case "BoolProperty": foreach (var i in items) _w.Write((byte)(i!.GetValue<bool>() ? 1 : 0)); break;
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            case "SoftObjectProperty":
            case "ObjectProperty":
                foreach (var i in items) WriteFString(i!.GetValue<string>());
                break;

            case "StructProperty":
            {
                WriteFString(prop["elementName"]!.GetValue<string>());
                WriteFString("StructProperty");
                using var inner = new MemoryStream();
                var outer = _w;
                _w = new BinaryWriter(inner);
                try
                {
                    foreach (var item in items) WriteStructBody(item!);
                }
                finally
                {
                    _w.Flush();
                    _w = outer;
                }
                _w.Write((int)inner.Length);
                _w.Write(0); // array index
                WriteFString(prop["structType"]!.GetValue<string>());
                _w.Write(Convert.FromHexString(prop["structGuid"]?.GetValue<string>() ?? new string('0', 32)));
                _w.Write((byte)0); // guid flag
                _w.Write(inner.ToArray());
                break;
            }

            default:
                throw new SaveFormatException($"Desteklenmeyen dizi tipi: {innerType}");
        }
    }

    private void WriteStructBody(JsonNode value)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue(out string? hex):
                _w.Write(Convert.FromHexString(hex!)); // Guid
                return;
            case JsonValue v when v.TryGetValue(out long ticks):
                _w.Write(ticks); // DateTime / Timespan
                return;
            case JsonObject obj when obj["__raw"] is JsonNode raw:
                _w.Write(Convert.FromBase64String(raw.GetValue<string>()));
                return;
            case JsonObject obj when obj["__struct"] is JsonArray list:
                WritePropertyList(list);
                return;
            case JsonObject obj when obj["__fmt"] is JsonNode fmtNode:
            {
                string fmt = fmtNode.GetValue<string>();
                foreach (var (key, node) in obj)
                {
                    if (key == "__fmt") continue;
                    switch (fmt)
                    {
                        case "f32": _w.Write(GvasFloat.GetSingle(node!)); break;
                        case "f64": _w.Write(GvasFloat.GetDouble(node!)); break;
                        case "i32": _w.Write((int)node!.GetValue<long>()); break;
                        case "u8": _w.Write((byte)node!.GetValue<long>()); break;
                        default: throw new SaveFormatException($"Bilinmeyen yapı biçimi: {fmt}");
                    }
                }
                return;
            }
            default:
                throw new SaveFormatException("Geçersiz yapı değeri.");
        }
    }

    private void WriteGuidFlag(JsonObject prop)
    {
        if (prop["propertyGuid"] is JsonNode guid)
        {
            _w.Write((byte)1);
            _w.Write(Convert.FromHexString(guid.GetValue<string>()));
        }
        else
        {
            _w.Write((byte)0);
        }
    }

    private byte[] FStringBytes(string s)
    {
        using var ms = new MemoryStream();
        var outer = _w;
        _w = new BinaryWriter(ms);
        try
        {
            WriteFString(s);
        }
        finally
        {
            _w.Flush();
            _w = outer;
        }
        return ms.ToArray();
    }

    private void WriteFString(string s)
    {
        if (s.Length == 0)
        {
            _w.Write(0);
            return;
        }
        bool ascii = s.All(c => c < 128);
        if (ascii)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            _w.Write(bytes.Length + 1);
            _w.Write(bytes);
            _w.Write((byte)0);
        }
        else
        {
            _w.Write(-(s.Length + 1));
            _w.Write(Encoding.Unicode.GetBytes(s));
            _w.Write((ushort)0);
        }
    }
}
