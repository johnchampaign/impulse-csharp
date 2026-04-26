namespace Impulse.Core.Cards;

public sealed record Card(
    int Id,
    CardActionType ActionType,
    CardColor Color,
    int Size,
    int BoostNumber,
    string EffectFamily,
    string EffectText);
