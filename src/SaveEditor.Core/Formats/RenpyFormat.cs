using System.IO.Compression;
using System.Text.Json.Nodes;
using SaveEditor.Core.Pickle;

namespace SaveEditor.Core.Formats;

/// <summary>
/// Ren'Py save files: a ZIP archive whose "log" entry is a Python pickle of
/// the game state. Other entries (screenshot, json metadata, signatures) are
/// carried through untouched.
/// </summary>
public sealed class RenpyFormat : ISaveFormat
{
    public string Id => "renpy";
    public string Name => "Ren'Py Kayıt Dosyası";
    public int Priority => 30;

    public bool CanRead(byte[] data, string fileName)
    {
        if (data.Length < 4 || data[0] != 'P' || data[1] != 'K' || data[2] > 7) return false;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            return archive.GetEntry("log") is not null;
        }
        catch
        {
            return false;
        }
    }

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        var otherEntries = new List<string[]>(); // [name, base64]
        byte[]? logBytes = null;

        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            if (entry.FullName == "log") logBytes = ms.ToArray();
            else otherEntries.Add([entry.FullName, Convert.ToBase64String(ms.ToArray())]);
        }

        if (logBytes is null) throw new SaveFormatException("Ren'Py kaydında 'log' girdisi yok.");

        try
        {
            var reader = new PickleReader(logBytes);
            object? graph = reader.Read();

            var doc = new SaveDocument
            {
                FormatId = Id,
                FormatName = Name,
                FileName = fileName,
                Root = PyJson.ToJson(graph),
            };
            doc.State["entries"] = otherEntries;
            doc.State["protocol"] = reader.Protocol;
            doc.Warnings.Add("Oyun değişkenleri genellikle store/ anahtarları altındadır. Python'a özgü değerler {\"py\": ...} etiketiyle gösterilir; yapılarını bozmadan değerleri düzenleyin.");
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
                Root = new JsonObject
                {
                    ["__type"] = "bytes",
                    ["not"] = "log girdisi çözümlenemedi; ham hali aşağıda",
                    ["b64"] = Convert.ToBase64String(logBytes),
                },
                Editable = false,
            };
            doc.State["entries"] = otherEntries;
            doc.State["rawLog"] = logBytes;
            doc.Warnings.Add($"Ren'Py 'log' pickle verisi çözümlenemedi: {ex.Message}");
            doc.Warnings.Add("Dosya salt okunur açıldı; indirme orijinalin aynısını verir. Bu hatayı dosyayla birlikte bildirirseniz destek eklenebilir.");
            return doc;
        }
    }

    public byte[] Write(SaveDocument doc)
    {
        byte[] logBytes;
        if (doc.State.TryGetValue("rawLog", out object? raw) && raw is byte[] rawBytes)
        {
            logBytes = rawBytes;
        }
        else
        {
            object? graph = PyJson.FromJson(doc.Root);
            int protocol = doc.State.TryGetValue("protocol", out object? p) && p is int pi ? pi : 2;
            logBytes = new PickleWriter().Write(graph, protocol);
        }

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var log = archive.CreateEntry("log", CompressionLevel.Optimal);
            using (var stream = log.Open()) stream.Write(logBytes);

            if (doc.State.TryGetValue("entries", out object? e) && e is List<string[]> entries)
            {
                foreach (var pair in entries)
                {
                    var entry = archive.CreateEntry(pair[0], CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(Convert.FromBase64String(pair[1]));
                }
            }
        }
        return ms.ToArray();
    }
}
