using System.Text.Json.Nodes;
using SaveEditor.Core.Marshal;

namespace SaveEditor.Core.Formats;

/// <summary>
/// RPG Maker XP/VX/VX Ace save files (.rxdata/.rvdata/.rvdata2): a Ruby
/// Marshal (format 4.8) dump of the game state. Ruby-specific values are
/// preserved with {"rb": ...} tags; anything that fails to parse falls back
/// to a read-only raw view so the file always round-trips.
/// </summary>
public sealed class RubyMarshalFormat : ISaveFormat
{
    public string Id => "marshal";
    public string Name => "Ruby Marshal (RPG Maker)";
    public int Priority => 35;

    private static readonly string[] Extensions = [".rvdata2", ".rvdata", ".rxdata"];

    public bool CanRead(byte[] data, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Extensions.Contains(ext) && data.Length >= 2 && data[0] == 4 && data[1] == 8;
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        try
        {
            var reader = new MarshalReader(data);
            object? graph = reader.Read();
            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = RbJson.ToJson(graph),
            };
            doc.Warnings.Add("Ruby'ye özgü değerler {\"rb\": ...} etiketiyle gösterilir; yapılarını bozmadan değerleri düzenleyin.");
            return doc;
        }
        catch (Exception ex)
        {
            // Never fail the upload: fall back to a read-only view that can
            // still reproduce the original file byte-for-byte.
            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = new JsonObject { ["__type"] = "bytes", ["b64"] = Convert.ToBase64String(data) },
                Editable = false,
            };
            doc.Warnings.Add($"Ruby Marshal verisi çözümlenemedi ({ex.Message}); ham veri gösteriliyor.");
            return doc;
        }
    }

    public byte[] Write(SaveDocument doc)
    {
        if (doc.Root is JsonObject o && o["__type"]?.GetValue<string>() == "bytes")
            return Convert.FromBase64String(o["b64"]!.GetValue<string>());
        object? graph = RbJson.FromJson(doc.Root);
        return new MarshalWriter().Write(graph);
    }
}
