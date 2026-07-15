using SaveEditor.Core.Formats;

namespace SaveEditor.Core;

/// <summary>
/// Detects a save file's format by trying registered formats directly, then
/// peeling reversible encoding layers (gzip, zlib, base64, lz-string) and
/// retrying, so e.g. an .rmmzsave resolves to [zlib] + JSON.
/// </summary>
public sealed class FormatDetector
{
    private readonly List<ISaveFormat> _formats;
    private readonly List<IWrapper> _wrappers;
    private readonly Dictionary<string, IWrapper> _wrapperById;

    public FormatDetector()
    {
        _formats =
        [
            new RenpyFormat(),
            new Es3Format(),
            new SqliteFormat(),
            new GvasFormat(),
            new RubyMarshalFormat(),
            new NbtFormat(),
            new JsonFormat(),
            new XmlFormat(),
            new IniFormat(),
            new TextFormat(),
            new BinaryFormat(),
        ];
        _formats.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        _wrappers =
        [
            new GZipWrapper(),
            new ZlibWrapper(),
            new LzStringBase64Wrapper(),
            new Base64Wrapper(),
        ];
        _wrapperById = _wrappers.ToDictionary(w => w.Id);
    }

    public IReadOnlyList<ISaveFormat> Formats => _formats;

    public SaveDocument Detect(byte[] data, string fileName, ReadContext? ctx = null)
    {
        ctx ??= new ReadContext();
        var wrapperChain = new List<string>();
        byte[] current = data;

        for (int depth = 0; depth < 6; depth++)
        {
            // Fallback formats (text/binary) accept anything; only let them win
            // once no wrapper applies, i.e. on the final pass below.
            foreach (var format in _formats.Where(f => f.Priority < FallbackPriority))
            {
                if (format.CanRead(current, fileName))
                {
                    var doc = format.Read(current, fileName, ctx);
                    doc.Wrappers.AddRange(wrapperChain);
                    return doc;
                }
            }

            bool unwrapped = false;
            foreach (var wrapper in _wrappers)
            {
                if (wrapper.TryUnwrap(current, out byte[] inner))
                {
                    wrapperChain.Add(wrapper.Id);
                    current = inner;
                    unwrapped = true;
                    break;
                }
            }
            if (!unwrapped) break;
        }

        foreach (var format in _formats)
        {
            if (format.CanRead(current, fileName))
            {
                var doc = format.Read(current, fileName, ctx);
                doc.Wrappers.AddRange(wrapperChain);
                return doc;
            }
        }

        throw new SaveFormatException("Dosya formatı algılanamadı.");
    }

    public byte[] Encode(SaveDocument doc)
    {
        var format = _formats.FirstOrDefault(f => f.Id == doc.FormatId)
            ?? throw new SaveFormatException($"Bilinmeyen format: {doc.FormatId}");
        byte[] bytes = format.Write(doc);
        for (int i = doc.Wrappers.Count - 1; i >= 0; i--)
        {
            bytes = _wrapperById[doc.Wrappers[i]].Wrap(bytes);
        }
        return bytes;
    }

    /// <summary>Priority at or above which a format is a catch-all fallback.</summary>
    public const int FallbackPriority = 900;
}
