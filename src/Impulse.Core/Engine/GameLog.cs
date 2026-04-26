namespace Impulse.Core.Engine;

public enum RevealOutcome { Kept, Discarded, Mined, Scored }

public sealed record RevealEvent(int CardId, RevealOutcome Outcome, string? Detail = null);

public sealed class GameLog : IDisposable
{
    private readonly List<string> _lines = new();
    private StreamWriter? _file;
    private readonly object _fileLock = new();

    public IReadOnlyList<string> Lines => _lines;

    public event Action<string>? OnLine;
    public event Action<RevealEvent>? OnReveal;
    public event Action<string>? OnAlert;

    public void EmitReveal(int cardId, RevealOutcome outcome, string? detail = null)
    {
        OnReveal?.Invoke(new RevealEvent(cardId, outcome, detail));
    }

    public void EmitAlert(string message)
    {
        Write($"!! {message}");
        OnAlert?.Invoke(message);
    }

    public bool Suppressed { get; set; }
    public string? FilePath { get; private set; }

    /// Default log file location. Previous game is rotated to *-prev-game.log
    /// so the last two games are always available.
    public static string DefaultPath =>
        Path.Combine(Path.GetTempPath(), "impulse-last-game.log");

    public static string PreviousPath =>
        Path.Combine(Path.GetTempPath(), "impulse-prev-game.log");

    public void OpenFile(string? path = null)
    {
        path ??= DefaultPath;
        FilePath = path;

        // Rotate: move existing -last-game to -prev-game.
        try
        {
            if (File.Exists(path))
            {
                if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
                File.Move(path, PreviousPath);
            }
        }
        catch { /* ignore rotation failures */ }

        // FileShare.ReadWrite tolerates a stale handle from a prior
        // MainWindow (during Load State) that hasn't been disposed yet.
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(fs) { AutoFlush = true };
        _file.WriteLine($"# impulse log opened {DateTime.Now:O}");
    }

    public void Write(string line)
    {
        if (Suppressed) return;
        _lines.Add(line);
        OnLine?.Invoke(line);
        if (_file is not null)
        {
            lock (_fileLock)
            {
                try { _file.WriteLine(line); } catch { /* drop */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
