using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaveEditor.Core.Formats;

/// <summary>
/// Unity "Easy Save 3" (.es3) files: JSON, optionally gzip-compressed and/or
/// AES-128-CBC encrypted (key = PBKDF2-SHA1(password, iv, 100 iterations),
/// IV prepended to the ciphertext — matching ES3's EncryptionAlgorithm).
/// </summary>
public sealed class Es3Format : ISaveFormat
{
    public string Id => "es3";
    public string Name => "Unity Easy Save 3";
    public int Priority => 50;

    public bool CanRead(byte[] data, string fileName)
        => Path.GetExtension(fileName).Equals(".es3", StringComparison.OrdinalIgnoreCase) && data.Length > 0;

    public SaveDocument Read(byte[] data, string fileName, ReadContext ctx)
    {
        bool encrypted = false, gzipped = false;
        string? password = null;
        byte[] payload = data;

        if (!TryGetJsonText(payload, ref gzipped, out string? json))
        {
            // Assume encryption; try candidate passwords (ES3's default is "password").
            string[] candidates = ctx.Password is { Length: > 0 } p
                ? [p, "password", ""]
                : ["password", ""];
            foreach (string candidate in candidates)
            {
                if (TryDecrypt(data, candidate, out byte[] decrypted) &&
                    TryGetJsonText(decrypted, ref gzipped, out json))
                {
                    encrypted = true;
                    password = candidate;
                    break;
                }
            }
        }

        if (json is null)
        {
            throw new SaveFormatException(
                "Bu .es3 dosyası JSON olarak çözümlenemedi. Şifreli olabilir (doğru şifreyi girip tekrar deneyin) " +
                "veya ES3'ün desteklenmeyen bir ikili (binary) serileştirme formatını kullanıyor olabilir.");
        }

        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 512,
        });

        var doc = new SaveDocument { FormatId = Id, FormatName = Name, FileName = fileName, Root = root };
        doc.State["encrypted"] = encrypted;
        doc.State["gzip"] = gzipped;
        doc.State["password"] = password;
        if (encrypted) doc.Warnings.Add($"Dosya AES ile şifreliydi (şifre: \"{password}\"); kaydederken aynı şifreyle yeniden şifrelenecek.");
        return doc;
    }

    public byte[] Write(SaveDocument doc)
    {
        string json = doc.Root?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        if (doc.State.TryGetValue("gzip", out object? g) && g is true)
        {
            payload = new GZipWrapper().Wrap(payload);
        }
        if (doc.State.TryGetValue("encrypted", out object? e) && e is true)
        {
            string password = doc.State.TryGetValue("password", out object? p) && p is string s ? s : "password";
            payload = Encrypt(payload, password);
        }
        return payload;
    }

    private static bool TryGetJsonText(byte[] data, ref bool gzipped, out string? json)
    {
        json = null;
        byte[] payload = data;
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            var gz = new GZipWrapper();
            if (!gz.TryUnwrap(data, out payload!)) return false;
            gzipped = true;
        }
        if (!TextUtil.TryDecodeUtf8(payload, out string? text, out _)) return false;
        string t = text!.TrimStart();
        if (t.Length == 0 || t[0] != '{') return false;
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 512 });
            json = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecrypt(byte[] data, string password, out byte[] plaintext)
    {
        plaintext = [];
        if (data.Length < 32) return false;
        try
        {
            byte[] iv = data[..16];
            using var aes = Aes.Create();
            aes.Key = Rfc2898DeriveBytes.Pbkdf2(password, iv, 100, HashAlgorithmName.SHA1, 16);
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(data, 16, data.Length - 16);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Encrypt(byte[] plaintext, string password)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(password, iv, 100, HashAlgorithmName.SHA1, 16);
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        byte[] cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        byte[] result = new byte[16 + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, 16);
        return result;
    }
}
