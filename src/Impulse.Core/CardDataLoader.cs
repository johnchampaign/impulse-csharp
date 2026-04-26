using System.Reflection;
using System.Text;
using Impulse.Core.Cards;

namespace Impulse.Core;

/// Loads cards.tsv from embedded resources. Windows-1252 encoded.
public static class CardDataLoader
{
    private const string ResourceName = "Impulse.Core.Data.cards.tsv";

    public static void EnsureEncodingRegistered()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string LoadRawFromEmbeddedResource()
    {
        EnsureEncodingRegistered();
        var asm = typeof(CardDataLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252));
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<Card> LoadAll()
    {
        return Parse(LoadRawFromEmbeddedResource());
    }

    public static IReadOnlyList<Card> Parse(string tsv)
    {
        var lines = tsv.Split('\n');
        var cards = new List<Card>(110);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length != 7)
                throw new FormatException($"cards.tsv line {i + 1}: expected 7 fields, got {f.Length}");
            cards.Add(new Card(
                Id: int.Parse(f[0]),
                ActionType: Enum.Parse<CardActionType>(f[1]),
                Color: Enum.Parse<CardColor>(f[2]),
                Size: int.Parse(f[3]),
                BoostNumber: int.Parse(f[4]),
                EffectFamily: f[5],
                EffectText: f[6]));
        }
        return cards;
    }
}
