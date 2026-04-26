namespace Impulse.Core.Engine;

public interface IEffectHandler
{
    // Returns true if the effect made progress (or completed).
    // Pause/resume idiom: set ctx.PendingChoice + ctx.Paused = true; return false.
    bool Execute(GameState g, EffectContext ctx);
}

public sealed class EffectRegistry
{
    private readonly Dictionary<string, IEffectHandler> _byFamily = new();

    public void Register(string family, IEffectHandler handler) =>
        _byFamily[family] = handler;

    public IEffectHandler? Resolve(string family) =>
        _byFamily.TryGetValue(family, out var h) ? h : null;

    public bool IsRegistered(string family) => _byFamily.ContainsKey(family);
}
