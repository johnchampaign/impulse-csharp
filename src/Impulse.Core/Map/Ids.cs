namespace Impulse.Core.Map;

public readonly record struct NodeId(int Value)
{
    public override string ToString() => $"N{Value}";
}

public readonly record struct GateId(int Value)
{
    public override string ToString() => $"G{Value}";
}
