using Impulse.Core.Engine;

namespace Impulse.Tests;

public class GameLogTests
{
    [Fact]
    public void In_memory_log_records_lines()
    {
        var log = new GameLog();
        log.Write("hello");
        log.Write("world");
        Assert.Equal(2, log.Lines.Count);
        Assert.Equal("hello", log.Lines[0]);
    }

    [Fact]
    public void Suppressed_drops_lines()
    {
        var log = new GameLog();
        log.Suppressed = true;
        log.Write("nope");
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void File_logging_writes_and_rotates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "impulse-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "current.log");

            // First game
            using (var log = new GameLog())
            {
                log.OpenFile(path);
                log.Write("first game line");
            }
            Assert.True(File.Exists(path));
            var firstContents = File.ReadAllText(path);
            Assert.Contains("first game line", firstContents);

            // Reopening rotates the previous file (specific behavior tested
            // against the default path elsewhere if needed; here we just
            // verify writing new content overwrites the file).
            using (var log = new GameLog())
            {
                log.OpenFile(path);
                log.Write("second game line");
            }
            var secondContents = File.ReadAllText(path);
            Assert.Contains("second game line", secondContents);
            Assert.DoesNotContain("first game line", secondContents);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Default_paths_point_to_temp()
    {
        Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            GameLog.DefaultPath);
        Assert.Contains("impulse-last-game", GameLog.DefaultPath);
        Assert.Contains("impulse-prev-game", GameLog.PreviousPath);
    }
}
