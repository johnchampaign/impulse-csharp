using Impulse.Core.Cards;
using Impulse.Core.Engine;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

public enum BattleStage
{
    Start,
    AwaitingDefenderReinforce,
    AwaitingAttackerReinforce,
    Resolving,
    Done,
}

public sealed class BattleState
{
    public BattleStage Stage;
    public required PlayerId Attacker { get; init; }
    public required PlayerId Defender { get; init; }
    public required GateId BattleGate { get; init; }
    public required ShipLocation AttackerOrigin { get; init; }
    public required int AttackerCruiserCount { get; init; }
    public NodeId? PassageNode { get; init; }
    public List<int> AttackerReinforcements { get; init; } = new();
    public List<int> DefenderReinforcements { get; init; } = new();
    public List<int> AttackerCruiserDraws { get; init; } = new();
    public List<int> DefenderCruiserDraws { get; init; } = new();
}

// Battle resolution logic, shared between handlers that trigger battle.
// State machine driven through ctx.PendingChoice/PendingBattle. Handlers
// own a BattleState in their own state and call into BattleResolver.Step
// each Execute call until battle completes.
public static class BattleResolver
{
    // Returns the count of defender's cruisers at the battle gate.
    public static int CruiserCountAt(GameState g, PlayerId owner, GateId gate) =>
        g.ShipPlacements.Count(sp =>
            sp.Owner == owner &&
            sp.Location is ShipLocation.OnGate og && og.Gate == gate);

    // Reinforcement legality: card must match (size + color) of any card in
    // the owner's Impulse, Plan, or Researched-techs (not Minerals).
    public static bool IsValidReinforcement(GameState g, PlayerId owner, int handCardId)
    {
        var c = g.CardsById[handCardId];
        var p = g.Player(owner);
        bool MatchesAny(IEnumerable<int> ids) =>
            ids.Any(id =>
            {
                var x = g.CardsById[id];
                return x.Size == c.Size && x.Color == c.Color;
            });
        if (MatchesAny(g.Impulse)) return true;
        if (MatchesAny(p.Plan)) return true;
        if (p.Techs.Left is Tech.Researched lr && MatchesAny(new[] { lr.CardId })) return true;
        if (p.Techs.Right is Tech.Researched rr && MatchesAny(new[] { rr.CardId })) return true;
        // Basic techs have no card identity → cannot serve as a match anchor.
        return false;
    }

    public static IReadOnlyList<int> LegalReinforcements(GameState g, PlayerId owner) =>
        g.Player(owner).Hand.Where(id => IsValidReinforcement(g, owner, id)).ToList();

    // Drives one step of the battle state machine. Sets ctx.PendingChoice for
    // prompts. Returns true when battle is fully resolved (state.Stage = Done).
    public static bool Step(GameState g, EffectContext ctx, BattleState bs)
    {
        if (bs.Stage == BattleStage.Start)
        {
            g.Log.Write($"  ⚔ Battle at {bs.BattleGate}: {bs.Attacker} ({bs.AttackerCruiserCount} cruisers) attacks {bs.Defender}");
            bs.Stage = BattleStage.AwaitingDefenderReinforce;
            return PromptReinforce(g, ctx, bs, defender: true);
        }

        if (bs.Stage == BattleStage.AwaitingDefenderReinforce)
        {
            if (ctx.PendingChoice is SelectHandCardRequest answered)
            {
                ctx.PendingChoice = null;
                if (answered.ChosenCardId is { } picked)
                {
                    g.Player(bs.Defender).Hand.Remove(picked);
                    bs.DefenderReinforcements.Add(picked);
                    g.Log.Write($"  ⚔ {bs.Defender} reinforces with #{picked}");
                    return PromptReinforce(g, ctx, bs, defender: true);
                }
                bs.Stage = BattleStage.AwaitingAttackerReinforce;
                return PromptReinforce(g, ctx, bs, defender: false);
            }
            return PromptReinforce(g, ctx, bs, defender: true);
        }

        if (bs.Stage == BattleStage.AwaitingAttackerReinforce)
        {
            if (ctx.PendingChoice is SelectHandCardRequest answered)
            {
                ctx.PendingChoice = null;
                if (answered.ChosenCardId is { } picked)
                {
                    g.Player(bs.Attacker).Hand.Remove(picked);
                    bs.AttackerReinforcements.Add(picked);
                    g.Log.Write($"  ⚔ {bs.Attacker} reinforces with #{picked}");
                    return PromptReinforce(g, ctx, bs, defender: false);
                }
                bs.Stage = BattleStage.Resolving;
                return Resolve(g, ctx, bs);
            }
            return PromptReinforce(g, ctx, bs, defender: false);
        }

        if (bs.Stage == BattleStage.Resolving)
        {
            return Resolve(g, ctx, bs);
        }

        return true;
    }

    private static bool PromptReinforce(GameState g, EffectContext ctx, BattleState bs, bool defender)
    {
        var who = defender ? bs.Defender : bs.Attacker;
        var hand = g.Player(who).Hand.ToList();
        if (hand.Count == 0)
        {
            // No cards to reinforce/bluff with → auto-skip.
            if (defender)
            {
                bs.Stage = BattleStage.AwaitingAttackerReinforce;
                return PromptReinforce(g, ctx, bs, defender: false);
            }
            bs.Stage = BattleStage.Resolving;
            return Resolve(g, ctx, bs);
        }
        // Rulebook p.30 line 759-760: players may commit any face-down card,
        // including 'bluff' cards that don't actually match. Bluffs reveal
        // and return to hand at resolution; only matching cards add icons.
        ctx.PendingChoice = new SelectHandCardRequest
        {
            Player = who,
            PromptPlayer = who,
            LegalCardIds = hand,
            AllowNone = true,
            Prompt = $"⚔ {(defender ? "DEFENDER" : "ATTACKER")} {who}: commit any card face-down (bluffs return to hand on reveal) or DONE.",
        };
        ctx.Paused = true;
        return false;
    }

    private static bool Resolve(GameState g, EffectContext ctx, BattleState bs)
    {
        // Reveal: split each side's reinforcements into matched (icons count)
        // and bluff (return to hand). Match-validity is checked at reveal
        // time against the current Impulse/Plan/Tech anchors.
        var defMatched = new List<int>();
        var defBluffs = new List<int>();
        foreach (var id in bs.DefenderReinforcements)
            (IsValidReinforcement(g, bs.Defender, id) ? defMatched : defBluffs).Add(id);
        var attMatched = new List<int>();
        var attBluffs = new List<int>();
        foreach (var id in bs.AttackerReinforcements)
            (IsValidReinforcement(g, bs.Attacker, id) ? attMatched : attBluffs).Add(id);
        // Bluffs are revealed then returned to hand.
        foreach (var id in defBluffs)
        {
            g.Player(bs.Defender).Hand.Add(id);
            g.Log.Write($"  ⚔ {bs.Defender} BLUFF revealed #{id} ({g.CardsById[id].Color}/{g.CardsById[id].Size}) — returned to hand");
        }
        foreach (var id in attBluffs)
        {
            g.Player(bs.Attacker).Hand.Add(id);
            g.Log.Write($"  ⚔ {bs.Attacker} BLUFF revealed #{id} ({g.CardsById[id].Color}/{g.CardsById[id].Size}) — returned to hand");
        }

        // Per-cruiser draws (no filter): each side draws 1 card per cruiser.
        int defenderCruisers = CruiserCountAt(g, bs.Defender, bs.BattleGate);
        for (int i = 0; i < defenderCruisers && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int id = g.Deck[0];
            g.Deck.RemoveAt(0);
            bs.DefenderCruiserDraws.Add(id);
            g.Log.Write($"  ⚔ {bs.Defender} draws cruiser-card #{id} ({g.CardsById[id].Color}/{g.CardsById[id].Size})");
        }
        for (int i = 0; i < bs.AttackerCruiserCount && Mechanics.EnsureDeckCanDraw(g, g.Log); i++)
        {
            int id = g.Deck[0];
            g.Deck.RemoveAt(0);
            bs.AttackerCruiserDraws.Add(id);
            g.Log.Write($"  ⚔ {bs.Attacker} draws cruiser-card #{id} ({g.CardsById[id].Color}/{g.CardsById[id].Size})");
        }

        int defenderTotal = defMatched.Concat(bs.DefenderCruiserDraws).Sum(id => g.CardsById[id].Size);
        int attackerTotal = attMatched.Concat(bs.AttackerCruiserDraws).Sum(id => g.CardsById[id].Size);
        g.Log.Write($"  ⚔ Totals: {bs.Attacker}={attackerTotal} vs {bs.Defender}={defenderTotal}");

        // Defender wins ties (rulebook p.34).
        bool attackerWon = attackerTotal > defenderTotal;
        var winner = attackerWon ? bs.Attacker : bs.Defender;
        var loser = attackerWon ? bs.Defender : bs.Attacker;
        g.Log.Write($"  ⚔ Winner: {winner}");

        // Build summary lines (used in alert). Cards listed by id/color/size.
        string Cards(IEnumerable<int> ids) => ids.Count() == 0
            ? "(none)"
            : string.Join(", ", ids.Select(id => {
                var c = g.CardsById[id];
                return $"#{id} ({c.Color}/{c.Size})";
            }));
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"BATTLE RESULT @ {bs.BattleGate}");
        summary.AppendLine();
        summary.AppendLine($"Attacker: {bs.Attacker} ({bs.AttackerCruiserCount} cruiser{(bs.AttackerCruiserCount == 1 ? "" : "s")})");
        summary.AppendLine($"  reinforcements: {Cards(attMatched)}");
        if (attBluffs.Count > 0) summary.AppendLine($"  bluffs (returned): {Cards(attBluffs)}");
        summary.AppendLine($"  cruiser draws:  {Cards(bs.AttackerCruiserDraws)}");
        summary.AppendLine($"  total icons:    {attackerTotal}");
        summary.AppendLine();
        summary.AppendLine($"Defender: {bs.Defender}");
        summary.AppendLine($"  reinforcements: {Cards(defMatched)}");
        if (defBluffs.Count > 0) summary.AppendLine($"  bluffs (returned): {Cards(defBluffs)}");
        summary.AppendLine($"  cruiser draws:  {Cards(bs.DefenderCruiserDraws)}");
        summary.AppendLine($"  total icons:    {defenderTotal}");
        summary.AppendLine();
        if (attackerTotal == defenderTotal)
            summary.AppendLine($"TIE — defender wins.");
        summary.AppendLine($"Winner: {winner} (+1 prestige for the win, +1 per ship destroyed).");

        // Destroy losing fleet.
        int destroyedCount = 0;
        if (attackerWon)
        {
            // Defender's cruisers at battle gate destroyed.
            var victims = g.ShipPlacements
                .Where(sp => sp.Owner == bs.Defender &&
                             sp.Location is ShipLocation.OnGate og && og.Gate == bs.BattleGate)
                .ToList();
            foreach (var v in victims)
            {
                Mechanics.DestroyShipAt(g, v.Owner, v.Location, attackerForPrestige: null, g.Log);
                destroyedCount++;
            }
            // Patrol-through: transports on passage node also destroyed
            // (rulebook p.28: "A Cruiser fleet that moves through a card
            // containing enemy Transports destroys them all"). ALL non-
            // attacker transports are destroyed, not just the defender's —
            // a battle resolved while passing through a card still counts
            // as moving through that card for the destruction rule.
            if (bs.PassageNode is { } passageNode)
            {
                var transports = g.ShipPlacements
                    .Where(sp => sp.Owner != bs.Attacker &&
                                 sp.Location is ShipLocation.OnNode n && n.Node == passageNode)
                    .ToList();
                foreach (var v in transports)
                {
                    // attackerForPrestige=null: prestige is awarded once
                    // below via destroyedCount + Scoring.AddPrestige to
                    // avoid double-counting.
                    Mechanics.DestroyShipAt(g, v.Owner, v.Location, attackerForPrestige: null, g.Log);
                    destroyedCount++;
                }
            }
            // Move attacker's cruisers from origin to battle gate.
            for (int i = 0; i < bs.AttackerCruiserCount; i++)
                Mechanics.MoveShip(g, bs.Attacker, bs.AttackerOrigin, new ShipLocation.OnGate(bs.BattleGate), g.Log);
        }
        else
        {
            // Attacker's moving cruisers (still at origin) destroyed.
            for (int i = 0; i < bs.AttackerCruiserCount; i++)
            {
                var loc = bs.AttackerOrigin;
                Mechanics.DestroyShipAt(g, bs.Attacker, loc, attackerForPrestige: null, g.Log);
                destroyedCount++;
            }
        }

        // Score winner: +1 prestige + 1 per ship destroyed.
        Scoring.AddPrestige(g, winner, 1, PrestigeSource.BattleWon, g.Log);
        if (destroyedCount > 0)
            Scoring.AddPrestige(g, winner, destroyedCount, PrestigeSource.ShipsDestroyed, g.Log);

        summary.AppendLine($"Ships destroyed: {destroyedCount}.");
        summary.AppendLine($"{winner} prestige now {g.Player(winner).Prestige}.");

        // Discard all cards used in battle. Bluffs were already returned to
        // hand — only matched reinforcements + cruiser draws end up discarded.
        var allBattleCards = attMatched
            .Concat(defMatched)
            .Concat(bs.AttackerCruiserDraws)
            .Concat(bs.DefenderCruiserDraws);
        foreach (var id in allBattleCards)
            g.Discard.Add(id);

        // Surface the summary as a UI alert (MessageBox in WPF).
        g.Log.EmitAlert(summary.ToString().TrimEnd());

        bs.Stage = BattleStage.Done;
        return true;
    }
}
