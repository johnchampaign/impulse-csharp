using System;
using System.IO;
using System.Text.Json;

namespace Impulse.Wpf;

// Per-user lobby preferences, persisted between runs. Stored under
// %LocalAppData%\Impulse\lobby.json. Failure to read or write is silent
// (preferences are a convenience, never critical).
public sealed class LobbyPrefs
{
    public int PlayerCount { get; set; } = 4;
    public string[] AiSelections { get; set; } = Array.Empty<string>();

    private static string PrefsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Impulse");
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
            return Path.Combine(dir, "lobby.json");
        }
    }

    public static LobbyPrefs Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return new LobbyPrefs();
            var json = File.ReadAllText(PrefsPath);
            return JsonSerializer.Deserialize<LobbyPrefs>(json) ?? new LobbyPrefs();
        }
        catch { return new LobbyPrefs(); }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this);
            File.WriteAllText(PrefsPath, json);
        }
        catch { /* best effort — prefs are non-critical */ }
    }
}
