using System.Text;
using SaveEditor.Core;
using Xunit;

namespace SaveEditor.Tests;

/// <summary>
/// Throws malformed input at the detector and asserts it never leaks an
/// unhandled exception: every input either produces a SaveDocument or a
/// SaveFormatException, nothing else (in particular, no crash-class
/// exception from a format-specific library like Microsoft.Data.Sqlite).
/// </summary>
public class FuzzTests
{
    private static readonly byte[][] RealHeaders =
    [
        "GVAS"u8.ToArray(),
        [0x50, 0x4B, 0x03, 0x04], // PK.. (zip local file header, used by Ren'Py)
        Encoding.ASCII.GetBytes("SQLite format 3\0"),
        [0x1f, 0x8b, 0x08, 0x00], // gzip
        [0x78, 0x9c],             // zlib (default compression)
        [0x78, 0x01],             // zlib (no compression)
        [0x78, 0xda],             // zlib (best compression)
    ];

    private static void AssertNeverLeaksUnhandledException(byte[] data, string fileName)
    {
        try
        {
            new FormatDetector().Detect(data, fileName);
        }
        catch (SaveFormatException)
        {
            // expected outcome for garbage input
        }
    }

    [Fact]
    public void RandomBytes_NeverLeakUnhandledException()
    {
        var random = new Random(1234567);
        for (int i = 0; i < 1200; i++)
        {
            int length = random.Next(0, 2048);
            byte[] data = new byte[length];
            random.NextBytes(data);
            string fileName = (i % 5) switch
            {
                0 => "fuzz.save",
                1 => "fuzz.sav",
                2 => "fuzz.es3",
                3 => "fuzz.db",
                _ => "fuzz.dat",
            };
            AssertNeverLeaksUnhandledException(data, fileName);
        }
    }

    [Fact]
    public void TruncatedAndCorruptedRealHeaders_NeverLeakUnhandledException()
    {
        var random = new Random(7654321);
        foreach (byte[] header in RealHeaders)
        {
            // Truncated: the bare header, and every prefix of it.
            for (int len = 0; len <= header.Length; len++)
            {
                AssertNeverLeaksUnhandledException(header[..len], "fuzz.save");
                AssertNeverLeaksUnhandledException(header[..len], "fuzz.es3");
                AssertNeverLeaksUnhandledException(header[..len], "fuzz.db");
            }

            // Header followed by random garbage of varying length.
            for (int i = 0; i < 40; i++)
            {
                int tailLength = random.Next(0, 512);
                byte[] tail = new byte[tailLength];
                random.NextBytes(tail);
                byte[] data = [.. header, .. tail];
                AssertNeverLeaksUnhandledException(data, "fuzz.save");
                AssertNeverLeaksUnhandledException(data, "fuzz.es3");
                AssertNeverLeaksUnhandledException(data, "fuzz.db");
            }

            // Header with a single byte flipped somewhere inside it, plus garbage.
            for (int pos = 0; pos < header.Length; pos++)
            {
                byte[] mutated = (byte[])header.Clone();
                mutated[pos] ^= 0xFF;
                byte[] tail = new byte[random.Next(0, 256)];
                random.NextBytes(tail);
                byte[] data = [.. mutated, .. tail];
                AssertNeverLeaksUnhandledException(data, "fuzz.save");
                AssertNeverLeaksUnhandledException(data, "fuzz.db");
            }
        }
    }
}
