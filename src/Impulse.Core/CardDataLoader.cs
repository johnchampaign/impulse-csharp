using System.Reflection;
using System.Text;

namespace Impulse.Core;

/// Loads cards.tsv from embedded resources. Windows-1252 encoded.
/// CodePagesEncodingProvider must be registered before first call.
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
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252));
        return reader.ReadToEnd();
    }
}
