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
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        string json = doc.Root?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
        bool bom = doc.State.TryGetValue("bom", out object? b) && b is true;
        return TextUtil.EncodeUtf8(json, bom);
    }
}

public sealed class XmlFormat : ISaveFormat
{
    public string Id => "xml";
    public string Name => "XML";
    public int Priority => 110;

    public bool CanRead(byte[] data, string fileName)
    {
        if (!TextUtil.TryDecodeUtf8(data, out string? text, out _)) return false;
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
        TextUtil.TryDecodeUtf8(data, out string? text, out bool bom);
        var doc = new SaveDocument
        {
            FormatId = Id,
            FormatName = Name,
            FileName = fileName,
            Root = JsonValue.Create(text!),
        };
        doc.State["bom"] = bom;
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
        bool bom = doc.State.TryGetValue("bom", out object? b) && b is true;
        return TextUtil.EncodeUtf8(text, bom);
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
        return TextUtil.TryDecodeUtf8(data, out _, out _);
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeUtf8(data, out string? text, out bool bom);
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
        doc.State["bom"] = bom;
        doc.Warnings.AddRange(warnings);
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
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
        bool bom = doc.State.TryGetValue("bom", out object? b) && b is true;
        return TextUtil.EncodeUtf8(sb.ToString().TrimEnd('\n') + "\n", bom);
    }
}

/// <summary>Fallback: any valid UTF-8 text, edited as-is.</summary>
public sealed class TextFormat : ISaveFormat
{
    public string Id => "text";
    public string Name => "Düz Metin";
    public int Priority => 900;

    public bool CanRead(byte[] data, string fileName) => TextUtil.TryDecodeUtf8(data, out _, out _);

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        TextUtil.TryDecodeUtf8(data, out string? text, out bool bom);
        var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = JsonValue.Create(text!) };
        doc.State["bom"] = bom;
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        bool bom = doc.State.TryGetValue("bom", out object? b) && b is true;
        return TextUtil.EncodeUtf8(doc.Root?.GetValue<string>() ?? "", bom);
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
            // Reject text with embedded NULs or control garbage (likely binary).
            int probe = Math.Min(text.Length, 4096);
            for (int i = 0; i < probe; i++)
            {
                char c = text[i];
                if (c < 0x09 || (c > 0x0D && c < 0x20)) { text = null; return false; }
            }
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
}
