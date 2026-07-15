using System.Diagnostics;
using SaveEditor.Core;
using SaveEditor.Core.Formats;
using Xunit;

namespace SaveEditor.Tests;

/// <summary>
/// Regressions against real, non-synthetic save files. These are best-effort:
/// if the fixture (a locally dropped save, or a file that needs downloading)
/// isn't available, the test passes trivially instead of failing the build.
/// </summary>
public class RealFileRegressionTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void RenpySave_StructurallyMatchesCPythonPickle_AfterRoundTrip()
    {
        string? root = FindRepoRoot();
        string? savePath = root is null ? null : Path.Combine(root, "1-5-LT1.save");
        if (savePath is null || !File.Exists(savePath)) return; // fixture not present locally; skip

        byte[] data = File.ReadAllBytes(savePath);
        byte[] origLog = ExtractZipEntry(data, "log");

        var detector = new FormatDetector();
        var doc = detector.Detect(data, "1-5-LT1.save");
        Assert.Equal("renpy", doc.FormatId);
        byte[] output = detector.Encode(doc);
        byte[] newLog = ExtractZipEntry(output, "log");

        string tmp = Path.Combine(Path.GetTempPath(), "se-renpy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string origPath = Path.Combine(tmp, "orig.pkl");
            string newPath = Path.Combine(tmp, "new.pkl");
            File.WriteAllBytes(origPath, origLog);
            File.WriteAllBytes(newPath, newLog);

            string script = Path.Combine(AppContext.BaseDirectory, "Scripts", "pickle_compare.py");
            if (!File.Exists(script)) return; // script not deployed; skip

            var (exitCode, stdout) = RunPython(script, origPath, newPath);
            if (exitCode is null) return; // python not available locally; skip
            Assert.True(exitCode == 0, $"pickle_compare.py reported a mismatch:\n{stdout}");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/trumank/uesave-rs/HEAD/uesave/drg-save-test.sav")]
    [InlineData("https://raw.githubusercontent.com/trumank/uesave-rs/HEAD/uesave/examples/space-rig-decorator/PropPack.sav")]
    public async Task GvasFile_ByteIdentical_AfterRoundTrip(string url)
    {
        byte[]? data = await TryDownload(url);
        if (data is null) return; // no network access locally; skip

        var detector = new FormatDetector();
        var doc = detector.Detect(data, Path.GetFileName(url));
        Assert.Equal("gvas", doc.FormatId);
        byte[] output = detector.Encode(doc);
        Assert.Equal(data, output);
    }

    private static async Task<byte[]?> TryDownload(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            return await client.GetByteArrayAsync(url);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ExtractZipEntry(byte[] zipData, string entryName)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zipData, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"'{entryName}' girdisi yok.");
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static (int? exitCode, string stdout) RunPython(string script, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("python", "\"" + script + "\" \"" + string.Join("\" \"", args) + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (null, "");
        }
    }
}
