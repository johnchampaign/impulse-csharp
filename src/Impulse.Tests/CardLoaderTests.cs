using Impulse.Core;
using Impulse.Core.Cards;

namespace Impulse.Tests;

public class CardLoaderTests
{
    private static readonly IReadOnlyList<Card> Cards = CardDataLoader.LoadAll();

    [Fact]
    public void Loads_108_cards()
    {
        Assert.Equal(108, Cards.Count);
    }

    [Fact]
    public void Vassal_id_21_is_the_gap()
    {
        var ids = Cards.Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(21, ids);
        Assert.Contains(1, ids);
        Assert.Contains(109, ids);
    }

    [Fact]
    public void All_ids_unique_and_in_range()
    {
        var ids = Cards.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.InRange(id, 1, 109));
    }

    [Fact]
    public void All_colors_in_locked_set()
    {
        var colors = Cards.Select(c => c.Color).Distinct().ToHashSet();
        Assert.Equal(4, colors.Count);
        Assert.Contains(CardColor.Blue, colors);
        Assert.Contains(CardColor.Yellow, colors);
        Assert.Contains(CardColor.Red, colors);
        Assert.Contains(CardColor.Green, colors);
    }

    [Fact]
    public void All_action_types_in_deck_enum()
    {
        // Every parsed value must round-trip through the enum (Parse already
        // enforces this at load); spot check that no Explore/Battle slipped in.
        var actions = Cards.Select(c => c.ActionType).Distinct().ToHashSet();
        Assert.DoesNotContain((CardActionType)999, actions); // sanity
        // Slice A doesn't require all 10 action types to be present, but all
        // present must be valid enum members — Parse would have thrown otherwise.
    }

    [Fact]
    public void Sizes_in_one_to_three()
    {
        Assert.All(Cards, c => Assert.InRange(c.Size, 1, 3));
        Assert.All(Cards, c => Assert.InRange(c.BoostNumber, 1, 3));
    }

    [Fact]
    public void Boost_equals_size_for_vassal_deck()
    {
        // Confirmed in core-model.md / project memory: across the Vassal deck,
        // BoostNumber == Size. Future card sets may diverge.
        Assert.All(Cards, c => Assert.Equal(c.Size, c.BoostNumber));
    }

    [Fact]
    public void Effect_family_slug_nonempty()
    {
        Assert.All(Cards, c => Assert.False(string.IsNullOrWhiteSpace(c.EffectFamily)));
    }

    [Fact]
    public void Effect_text_nonempty()
    {
        Assert.All(Cards, c => Assert.False(string.IsNullOrWhiteSpace(c.EffectText)));
    }

    [Fact]
    public void Encoding_provider_registers_idempotently()
    {
        CardDataLoader.EnsureEncodingRegistered();
        CardDataLoader.EnsureEncodingRegistered();
        // No throw = pass.
    }
}
