namespace Impulse.Core.Players;

// Six races, color-coded to PlayerColor. Races differ only in their unique Basic Tech.
public sealed record Race(
    int Id,
    string Name,
    PlayerColor Color,
    string BasicUniqueTechSlug,
    string BasicUniqueTechText);

public static class Races
{
    // Names + tech text are placeholders pending a verified read of raza1-6.jpg
    // (Piscesish text confirmed in core-model doc; others TBD).
    public static readonly Race Piscesish = new(
        1, "Piscesish", PlayerColor.Blue,
        "tech_basic_unique_piscesish",
        "Draw one size one card from the deck.");

    public static readonly Race Ariek = new(
        2, "Ariek", PlayerColor.Green,
        "tech_basic_unique_ariek",
        "Command one fleet for one move. It must end the move occupying or patrolling the Sector Core.");

    public static readonly Race Herculese = new(
        3, "Herculese", PlayerColor.Purple,
        "tech_basic_unique_herculese",
        "Command one Cruiser for one move through an unexplored card.");

    public static readonly Race Draconians = new(
        4, "Draconians", PlayerColor.Red,
        "tech_basic_unique_draconians",
        "Research one card from your hand. It must match color and size with the last card on the Impulse.");

    public static readonly Race Triangulumnists = new(
        5, "Triangulumnists", PlayerColor.White,
        "tech_basic_unique_triangulumnists",
        "Build a Cruiser at home on an edge that touches an unexplored card.");

    public static readonly Race Caelumnites = new(
        6, "Caelumnites", PlayerColor.Yellow,
        "tech_basic_unique_caelumnites",
        "Mine one card from your hand. It must match color and size with the last card on the Impulse.");

    public static readonly IReadOnlyList<Race> All =
        new[] { Piscesish, Ariek, Herculese, Draconians, Triangulumnists, Caelumnites };
}
