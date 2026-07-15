using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SaveEditor.Core.Formats;

public sealed class JsonFormat : ISaveFormat
{
    public string Id => "json";
    public string Name => "JSON";
    public int Priority => 100;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 512,
    };

    public bool CanRead(byte[] data, string fileName)
    {
        if (!TextUtil.TryDecodeUtf8(data, out string? text, out _)) return false;
        string t = text!.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text, ParseOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeUtf8(data, out string? text, out bool bom);
        var root = JsonNode.Parse(text!, documentOptions: ParseOptions);
        var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = root };
        doc.State["bom"] = bom;
        doc.State["indented"] = LooksIndented(text!);
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        bool indented = doc.State.TryGetValue("indented", out object? ind) && ind is true;
        string json = doc.Root?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }) ?? "null";
        bool bom = doc.State.TryGetValue("bom", out object? b) && b is true;
        return TextUtil.EncodeUtf8(json, bom);
    }

    /// <summary>Heuristic: does the line right after the first newline start
    /// with whitespace? Good enough to tell a pretty-printed save from a
    /// compact one (e.g. lz-string-compressed RPG Maker saves, always
    /// single-line) without trying to reproduce the exact original style.</summary>
    private static bool LooksIndented(string text)
    {
        int nl = text.IndexOf('\n');
        if (nl < 0 || nl + 1 >= text.Length) return false;
        return text[nl + 1] is ' ' or '\t';
    }
}

public sealed class XmlFormat : ISaveFormat
{
    public string Id => "xml";
    public string Name => "XML";
    public int Priority => 110;

    public bool CanRead(byte[] data, string fileName)
    {
        if (!TextUtil.TryDecodeAny(data, out string? text, out _)) return false;
        string t = text!.TrimStart();
        if (t.Length == 0 || t[0] != '<') return false;
        try
        {
            XDocument.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeAny(data, out string? text, out var encoding);
        var doc = new SaveDocument
        {
            FormatId = Id,
            FormatName = Name,
            FileName = fileName,
            Root = JsonValue.Create(text!),
        };
        doc.State["encoding"] = encoding;
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        string text = doc.Root?.GetValue<string>() ?? "";
        try
        {
            XDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new SaveFormatException($"Geçersiz XML: {ex.Message}", ex);
        }
        var encoding = doc.State.TryGetValue("encoding", out object? e) && e is TextEncodingKind k ? k : TextEncodingKind.Utf8;
        return TextUtil.EncodeAny(text, encoding);
    }
}

/// <summary>INI / Godot ConfigFile style sections. Values are kept as raw strings.</summary>
public sealed class IniFormat : ISaveFormat
{
    public string Id => "ini";
    public string Name => "INI / ConfigFile";
    public int Priority => 200;

    private static readonly string[] Extensions = [".ini", ".cfg", ".config", ".properties"];

    public bool CanRead(byte[] data, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Extensions.Contains(ext)) return false;
        return TextUtil.TryDecodeAny(data, out _, out _);
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeAny(data, out string? text, out var encoding);

        if (HasDuplicates(text!))
        {
            // A duplicate key/section can't be represented in the section->key
            // JsonObject tree without silently dropping one of the values.
            // Fall back to plain-text editing instead of losing data.
            var fallback = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = JsonValue.Create(text!) };
            fallback.State["encoding"] = encoding;
            fallback.State["plainText"] = true;
            fallback.Warnings.Add("Yinelenen anahtar veya bölüm adı bulundu; veri kaybı olmaması için dosya düz metin olarak açıldı.");
            return fallback;
        }

        var root = new JsonObject();
        var current = new JsonObject();
        root[""] = current;
        var warnings = new List<string>();
        bool sawComment = false;

        foreach (string rawLine in text!.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] is ';' or '#')
            {
                sawComment = true;
                continue;
            }
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                current = new JsonObject();
                root[trimmed[1..^1]] = current;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                current[line] = "";
                continue;
            }
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        if (((JsonObject)root[""]!).Count == 0) root.Remove("");
        if (sawComment) warnings.Add("Yorum satırları kaydederken korunmaz.");

        var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = root };
        doc.State["encoding"] = encoding;
        doc.Warnings.AddRange(warnings);
        return doc;
    }

    /// <summary>Mirrors Read's parse loop just enough to detect a key repeated
    /// within a section, or a section name repeated, without building the tree.</summary>
    private static bool HasDuplicates(string text)
    {
        var seenSections = new HashSet<string> { "" };
        var seenKeys = new HashSet<string>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] is ';' or '#') continue;
            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                string section = trimmed[1..^1];
                if (!seenSections.Add(section)) return true;
                seenKeys = [];
                continue;
            }
            int eq = line.IndexOf('=');
            string key = eq < 0 ? line : line[..eq].Trim();
            if (!seenKeys.Add(key)) return true;
        }
        return false;
    }

    public byte[] Write(SaveDocument doc)
    {
        var encoding = doc.State.TryGetValue("encoding", out object? e) && e is TextEncodingKind k ? k : TextEncodingKind.Utf8;
        if (doc.State.TryGetValue("plainText", out object? pt) && pt is true)
        {
            return TextUtil.EncodeAny(doc.Root?.GetValue<string>() ?? "", encoding);
        }

        var sb = new StringBuilder();
        if (doc.Root is JsonObject sections)
        {
            foreach (var (sectionName, sectionNode) in sections)
            {
                if (sectionName.Length > 0) sb.Append('[').Append(sectionName).Append("]\n");
                if (sectionNode is JsonObject section)
                {
                    foreach (var (key, value) in section)
                    {
                        sb.Append(key).Append('=').Append(value?.GetValue<string>() ?? "").Append('\n');
                    }
                }
                sb.Append('\n');
            }
        }
        return TextUtil.EncodeAny(sb.ToString().TrimEnd('\n') + "\n", encoding);
    }
}

/// <summary>Fallback: any valid UTF-8/UTF-16 text, edited as-is.</summary>
public sealed class TextFormat : ISaveFormat
{
    public string Id => "text";
    public string Name => "Düz Metin";
    public int Priority => 900;

    public bool CanRead(byte[] data, string fileName) => TextUtil.TryDecodeAny(data, out _, out _);

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeAny(data, out string? text, out var encoding);
        var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = JsonValue.Create(text!) };
        doc.State["encoding"] = encoding;
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        var encoding = doc.State.TryGetValue("encoding", out object? e) && e is TextEncodingKind k ? k : TextEncodingKind.Utf8;
        return TextUtil.EncodeAny(doc.Root?.GetValue<string>() ?? "", encoding);
    }
}

/// <summary>Last resort: opaque binary exposed as base64.</summary>
public sealed class BinaryFormat : ISaveFormat
{
    public string Id => "binary";
    public string Name => "İkili (tanınmadı)";
    public int Priority => 999;

    public bool CanRead(byte[] data, string fileName) => true;

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        var doc = new SaveDocument
        {
            FormatId = Id,
            FormatName = Name,
            FileName = fileName,
            Root = new JsonObject { ["__type"] = "bytes", ["b64"] = Convert.ToBase64String(data) },
        };
        doc.Warnings.Add("Bu ikili format tanınamadı; veri base64 olarak gösteriliyor.");
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        string b64 = doc.Root?["b64"]?.GetValue<string>()
            ?? throw new SaveFormatException("İkili veri bulunamadı.");
        return Convert.FromBase64String(b64);
    }
}

internal enum TextEncodingKind { Utf8, Utf8Bom, Utf16LE, Utf16BE }

internal static class TextUtil
{
    public static bool TryDecodeUtf8(byte[] data, out string? text, out bool bom)
    {
        text = null;
        bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        try
        {
            var encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
            text = bom ? encoding.GetString(data, 3, data.Length - 3) : encoding.GetString(data);
            if (!IsPlausibleText(text)) { text = null; return false; }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static byte[] EncodeUtf8(string text, bool bom)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        if (!bom) return body;
        byte[] result = new byte[body.Length + 3];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    /// <summary>Like <see cref="TryDecodeUtf8"/>, but also recognizes UTF-16
    /// LE/BE by BOM (FF FE / FE FF) so text/XML/INI saves exported by engines
    /// that default to UTF-16 (many .NET/Unity tools) can be opened.</summary>
    public static bool TryDecodeAny(byte[] data, out string? text, out TextEncodingKind encoding)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return TryDecodeUtf16(data, bigEndian: false, TextEncodingKind.Utf16LE, out text, out encoding);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return TryDecodeUtf16(data, bigEndian: true, TextEncodingKind.Utf16BE, out text, out encoding);
        }
        if (TryDecodeUtf8(data, out text, out bool bom))
        {
            encoding = bom ? TextEncodingKind.Utf8Bom : TextEncodingKind.Utf8;
            return true;
        }
        encoding = TextEncodingKind.Utf8;
        return false;
    }

    public static byte[] EncodeAny(string text, TextEncodingKind encoding) => encoding switch
    {
        TextEncodingKind.Utf16LE => Prepend(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(text)),
        TextEncodingKind.Utf16BE => Prepend(Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode.GetBytes(text)),
        TextEncodingKind.Utf8Bom => EncodeUtf8(text, bom: true),
        _ => EncodeUtf8(text, bom: false),
    };

    private static bool TryDecodeUtf16(byte[] data, bool bigEndian, TextEncodingKind kind, out string? text, out TextEncodingKind encoding)
    {
        text = null;
        encoding = kind;
        try
        {
            var dec = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
            string decoded = dec.GetString(data, 2, data.Length - 2);
            if (!IsPlausibleText(decoded)) return false;
            text = decoded;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Odd trailing byte: not valid UTF-16.
            return false;
        }
    }

    private static byte[] Prepend(byte[] preamble, byte[] body)
    {
        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    // Reject text with embedded NULs or control garbage (likely binary).
    private static bool IsPlausibleText(string text)
    {
        int probe = Math.Min(text.Length, 4096);
        for (int i = 0; i < probe; i++)
        {
            char c = text[i];
            if (c < 0x09 || (c > 0x0D && c < 0x20)) return false;
        }
        return true;
    }
}
