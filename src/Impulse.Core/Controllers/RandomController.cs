using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Core.Controllers;

public sealed class RandomController : IPlayerController
{
    private readonly Random _rng;

    public PlayerId Seat { get; }

    public RandomController(PlayerId seat, int seed)
    {
        Seat = seat;
        _rng = new Random(seed);
    }

    public PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal) =>
        legal[_rng.Next(legal.Count)];

    public void AnswerChoice(GameState g, ChoiceRequest request)
    {
        switch (request)
        {
            case SelectFleetRequest f:
                f.Chosen = f.LegalLocations[_rng.Next(f.LegalLocations.Count)];
                break;
            case DeclareMoveRequest m:
                m.ChosenPath = m.LegalPaths[_rng.Next(m.LegalPaths.Count)];
                break;
            case SelectHandCardRequest h:
                h.ChosenCardId = h.LegalCardIds[_rng.Next(h.LegalCardIds.Count)];
                break;
            case SelectShipPlacementRequest sp:
                sp.Chosen = sp.LegalLocations[_rng.Next(sp.LegalLocations.Count)];
                break;
            case SelectMineralCardRequest m:
                m.ChosenCardId = m.LegalCardIds[_rng.Next(m.LegalCardIds.Count)];
                break;
            case SelectFleetSizeRequest fs:
                fs.Chosen = fs.Min + _rng.Next(fs.Max - fs.Min + 1);
                break;
            case SelectTechSlotRequest ts:
                ts.Chosen = _rng.Next(2) == 0 ? TechSlot.Left : TechSlot.Right;
                break;
            case SelectFromOptionsRequest opt:
                opt.Chosen = _rng.Next(opt.Options.Count);
                break;
            case SelectSabotageTargetRequest sab:
                sab.Chosen = sab.LegalTargets[_rng.Next(sab.LegalTargets.Count)];
                break;
            default:
                throw new NotSupportedException($"unknown choice {request.GetType().Name}");
        }
    }
}
