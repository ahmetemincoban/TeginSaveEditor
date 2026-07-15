using SaveEditor.Core;
using Xunit;

namespace SaveEditor.Tests;

public class TempFileCleanupTests
{
    [Fact]
    public void CleanupOldTempFiles_DeletesOnlyStaleMatchingFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "se-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string stale = Path.Combine(dir, "saveeditor-old.sqlite");
            string fresh = Path.Combine(dir, "saveeditor-new.sqlite");
            string unrelated = Path.Combine(dir, "other-file.sqlite");
            File.WriteAllText(stale, "x");
            File.WriteAllText(fresh, "x");
            File.WriteAllText(unrelated, "x");

            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(3));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(3));

            TempFileCleanup.CleanupOldTempFiles(dir, TimeSpan.FromDays(1));

            Assert.False(File.Exists(stale), "stale saveeditor-* file should be deleted");
            Assert.True(File.Exists(fresh), "fresh saveeditor-* file should be kept");
            Assert.True(File.Exists(unrelated), "non-matching file should never be touched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanupOldTempFiles_MissingDirectory_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "se-cleanup-missing-" + Guid.NewGuid().ToString("N"));
        TempFileCleanup.CleanupOldTempFiles(dir, TimeSpan.FromDays(1));
    }
}
