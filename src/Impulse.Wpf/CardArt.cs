using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace Impulse.Wpf;

/// Optional card art from the player's own copy of the official Impulse VASSAL
/// module. Component art is copyrighted, so this app redistributes none of it:
/// the player points us at their `.vmod` (which is just a zip of images) and we
/// read the images out of it at runtime. The text/colour-band UI stays available
/// as an equal alternative — `Enabled` is a player preference, not a fallback.
///
/// Card ids map 1:1 onto `images/c{Id}.jpg` (verified: the 108 ids in
/// Data/cards.tsv match the module's 108 card entries exactly), plus
/// `images/back.jpg` for face-down sectors and `images/raza{1..6}.jpg` for the
/// race sheets.
public static class CardArt
{
    /// Where players get the module. Shown in the file picker and in the
    /// failure message so nobody has to go hunting for it.
    public const string ModuleUrl = "https://vassalengine.org/library/projects/Impulse";

    // Raw bytes held in memory (108 cards ≈ 1.9 MB); decoded lazily and cached
    // frozen, because Render() re-runs every panel on every prompt transition
    // and RenderMap also re-runs on window resize.
    private static readonly Dictionary<string, byte[]> Bytes = new();
    private static readonly Dictionary<string, BitmapImage> Decoded = new();

    /// A module has been read successfully.
    public static bool Loaded { get; private set; }

    /// The player wants art (independent of whether a module is loaded yet).
    public static bool Enabled { get; set; }

    /// Draw art right now.
    public static bool Active => Loaded && Enabled;

    /// Path of the loaded module, so it can be re-loaded on next launch.
    public static string? SourcePath { get; private set; }

    /// How many card images the module supplied (diagnostics for the UI).
    public static int CardCount { get; private set; }

    /// Read the needed images out of a .vmod. Returns false with a
    /// player-readable reason on any failure; leaves prior art untouched.
    public static bool TryLoad(string vmodPath, out string? error)
    {
        error = null;
        try
        {
            var found = new Dictionary<string, byte[]>();
            using (var zip = ZipFile.OpenRead(vmodPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!IsWanted(entry.FullName)) continue;
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    found[entry.FullName] = ms.ToArray();
                }
            }

            int cards = found.Keys.Count(k => k.StartsWith("images/c", StringComparison.Ordinal));
            if (cards == 0)
            {
                error = "That file has no Impulse card images in it — is it the Impulse VASSAL module?";
                return false;
            }

            Bytes.Clear();
            Decoded.Clear();
            foreach (var kv in found) Bytes[kv.Key] = kv.Value;
            CardCount = cards;
            SourcePath = vmodPath;
            Loaded = true;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Couldn't read that module: {ex.Message}";
            return false;
        }
    }

    public static void Unload()
    {
        Bytes.Clear();
        Decoded.Clear();
        Loaded = false;
        CardCount = 0;
        SourcePath = null;
    }

    public static BitmapImage? Card(int cardId) => Get($"images/c{cardId}.jpg");
    public static BitmapImage? Back() => Get("images/back.jpg");
    public static BitmapImage? Race(int raceId) => Get($"images/raza{raceId}.jpg");

    private static bool IsWanted(string entry)
    {
        if (!entry.StartsWith("images/", StringComparison.Ordinal)) return false;
        var name = entry["images/".Length..];
        if (name.Equals("back.jpg", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("raza", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return true;
        // images/c<digits>.jpg — the card faces.
        if (name.Length > 5 && (name[0] == 'c' || name[0] == 'C') &&
            name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            var digits = name[1..^4];
            return digits.Length > 0 && digits.All(char.IsAsciiDigit);
        }
        return false;
    }

    private static BitmapImage? Get(string entry)
    {
        if (!Loaded) return null;
        if (Decoded.TryGetValue(entry, out var cached)) return cached;
        if (!Bytes.TryGetValue(entry, out var raw)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(raw);
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now, release the stream
            bmp.EndInit();
            bmp.Freeze();                                  // shareable + cheap to re-render
            Decoded[entry] = bmp;
            return bmp;
        }
        catch
        {
            return null; // a corrupt entry falls back to the text card
        }
    }
}
