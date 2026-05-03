using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Effects;

// Shared helper used by CommandHandler and tech-driven cruiser-attack handlers
// to honor rulebook p.29: "If multiple players patrol the same card, the
// player moving ships can choose who to fight."
//
// Pattern at the call site:
//   var defender = DefenderChoice.Resolve(g, ctx, candidates, "prompt");
//   if (defender is null) return true; // paused for choice
//   ... use defender.Value to set up the battle ...
//
// The candidate list is stored on EffectContext.PendingDefenderCandidates
// across the pause; on resume, the answer in ctx.PendingChoice is consumed
// and the chosen PlayerId is returned.
internal static class DefenderChoice
{
    public static PlayerId? Resolve(
        GameState g,
        EffectContext ctx,
        List<PlayerId> candidates,
        string promptText)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("DefenderChoice.Resolve called with no candidates");
        if (candidates.Count == 1) return candidates[0];

        // Resume path: previously prompted, now have an answer.
        if (ctx.PendingDefenderCandidates is { } pdc &&
            ctx.PendingChoice is SelectFromOptionsRequest req && req.Chosen.HasValue)
        {
            int idx = req.Chosen.Value;
            ctx.PendingChoice = null;
            ctx.PendingDefenderCandidates = null;
            return pdc[idx];
        }

        // First entry: prompt and pause.
        ctx.PendingDefenderCandidates = candidates;
        ctx.PendingChoice = new SelectFromOptionsRequest
        {
            Player = ctx.ActivatingPlayer,
            Options = candidates.Select(p => $"Fight {p}").ToList(),
            Prompt = promptText,
        };
        ctx.Paused = true;
        return null;
    }
}
