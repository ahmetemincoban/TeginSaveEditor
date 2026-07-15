namespace SaveEditor.Core;

/// <summary>
/// Best-effort cleanup for the "saveeditor-*" temp files SqliteFormat drops
/// in the OS temp directory while reading/writing a database. Those files
/// are deleted right after use, but a crash or killed process can leave one
/// behind; sweep stale ones on startup so they don't accumulate forever.
/// </summary>
public static class TempFileCleanup
{
    public static void CleanupOldTempFiles(string tempDir, TimeSpan maxAge)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(tempDir, "saveeditor-*");
        }
        catch
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - maxAge;
        foreach (string file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // Best effort: another process may hold the file, or it may
                // already be gone. Either way, startup must not fail on this.
            }
        }
    }
}
