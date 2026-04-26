namespace Impulse.Core.Players;

public abstract record Tech
{
    public sealed record BasicCommon : Tech
    {
        public static readonly BasicCommon Instance = new();
        public const string Slug = "tech_basic_common";
        // Impulserules p.20: "Discard a card in order to either: Command one fleet
        // for one move OR Build one ship at home."
        public const string Text =
            "Discard a card in order to either: Command one fleet for one move OR Build one ship at home.";
    }

    public sealed record BasicUnique(Race Race) : Tech;

    // Wraps a card whose effect was Researched into this slot.
    public sealed record Researched(int CardId) : Tech;
}

public enum TechSlot { Left, Right }

public sealed record TechSlots(Tech Left, Tech Right)
{
    public Tech this[TechSlot slot] => slot == TechSlot.Left ? Left : Right;
}
