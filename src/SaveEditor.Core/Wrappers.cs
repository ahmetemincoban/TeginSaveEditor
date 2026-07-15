using System.IO.Compression;
using System.Text;

namespace SaveEditor.Core;

public sealed class GZipWrapper : IWrapper
{
    public string Id => "gzip";

    public bool TryUnwrap(byte[] data, out byte[] inner)
    {
        inner = [];
        if (data.Length < 3 || data[0] != 0x1f || data[1] != 0x8b) return false;
        try
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            inner = output.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Wrap(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data);
        }
        return output.ToArray();
    }
}

public sealed class ZlibWrapper : IWrapper
{
    public string Id => "zlib";

    public bool TryUnwrap(byte[] data, out byte[] inner)
    {
        inner = [];
        // zlib header: 0x78 followed by a byte making the 16-bit value divisible by 31
        if (data.Length < 3 || data[0] != 0x78 || ((data[0] << 8) | data[1]) % 31 != 0) return false;
        try
        {
            using var input = new MemoryStream(data);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            inner = output.ToArray();
            return inner.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Wrap(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }
        return output.ToArray();
    }
}

/// <summary>lz-string compressToBase64 text (RPG Maker MV .rpgsave, many HTML5 games).</summary>
public sealed class LzStringBase64Wrapper : IWrapper
{
    public string Id => "lzstring-base64";

    public bool TryUnwrap(byte[] data, out byte[] inner)
    {
        inner = [];
        if (data.Length < 4 || data.Length > 64 * 1024 * 1024) return false;
        if (!Base64Wrapper.LooksLikeBase64(data, out string? text)) return false;
        try
        {
            string? decompressed = LzString.DecompressFromBase64(text!);
            if (string.IsNullOrEmpty(decompressed)) return false;
            // Require a plausible text payload; lz-string on random base64 yields garbage.
            string trimmed = decompressed.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[' or '"' or '<')) return false;
            inner = Encoding.UTF8.GetBytes(decompressed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Wrap(byte[] data)
    {
        return Encoding.ASCII.GetBytes(LzString.CompressToBase64(Encoding.UTF8.GetString(data)));
    }
}

/// <summary>Strict base64 text whose decoded payload is something we recognize.</summary>
public sealed class Base64Wrapper : IWrapper
{
    public string Id => "base64";

    public bool TryUnwrap(byte[] data, out byte[] inner)
    {
        inner = [];
        if (data.Length < 8) return false;
        if (!LooksLikeBase64(data, out string? text)) return false;

        Span<byte> buffer = new byte[(text!.Length / 4 + 1) * 3];
        if (!Convert.TryFromBase64String(text, buffer, out int written)) return false;
        byte[] decoded = buffer[..written].ToArray();

        // Only unwrap when the payload is recognizable, to avoid mangling
        // plain text that merely happens to be valid base64.
        if (!(HasKnownMagic(decoded) || LooksLikeStructuredText(decoded))) return false;
        inner = decoded;
        return true;
    }

    public byte[] Wrap(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(data));

    internal static bool LooksLikeBase64(byte[] data, out string? text)
    {
        text = null;
        // Strip embedded line breaks (classic 76-column wrapped base64, e.g.
        // RFC 2045) before validating; the rest must be pure base64 alphabet
        // plus optional trailing whitespace.
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data)
        {
            if (b is (byte)'\n' or (byte)'\r') continue;
            sb.Append((char)b);
        }
        int len = sb.Length;
        while (len > 0 && (sb[len - 1] is ' ' or '\t')) len--;
        if (len < 4) return false;
        for (int i = 0; i < len; i++)
        {
            char c = sb[i];
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                or (>= '0' and <= '9') or '+' or '/' or '=';
            if (!ok) return false;
        }
        text = sb.ToString(0, len);
        return true;
    }

    internal static bool HasKnownMagic(byte[] d)
    {
        if (d.Length < 4) return false;
        if (d[0] == 0x1f && d[1] == 0x8b) return true;                       // gzip
        if (d[0] == 0x78 && ((d[0] << 8) | d[1]) % 31 == 0) return true;     // zlib
        if (d[0] == (byte)'P' && d[1] == (byte)'K' && d[2] <= 7) return true; // zip
        if (d.Length >= 16 && Encoding.ASCII.GetString(d, 0, 15) == "SQLite format 3") return true;
        if (d[0] == (byte)'G' && d[1] == (byte)'V' && d[2] == (byte)'A' && d[3] == (byte)'S') return true;
        return false;
    }

    private static bool LooksLikeStructuredText(byte[] d)
    {
        if (d.Length == 0) return false;
        int probe = Math.Min(d.Length, 512);
        try
        {
            string s = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(d, 0, probe);
            string t = s.TrimStart();
            return t.Length > 0 && t[0] is '{' or '[' or '<' or '"';
        }
        catch
        {
            return false;
        }
    }
}
