using Impulse.Core.Players;

namespace Impulse.Core.Engine;

public interface IPlayerController
{
    PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal);
    void AnswerChoice(GameState g, ChoiceRequest request);
    PlayerId Seat { get; }
}
