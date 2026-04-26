using System.Threading.Tasks;
using Impulse.Core;
using Impulse.Core.Engine;
using Impulse.Core.Players;

namespace Impulse.Wpf;

// HumanController: synchronous from the engine's view; under the hood it
// posts a prompt event for the UI thread and blocks on a TCS until the UI
// completes it. Engine must run off the UI thread.
public sealed class HumanController : IPlayerController
{
    public PlayerId Seat { get; }

    public event Action<GameState, IReadOnlyList<PlayerAction>>? ActionNeeded;
    public event Action<GameState, ChoiceRequest>? ChoiceNeeded;

    private TaskCompletionSource<PlayerAction>? _actionTcs;
    private TaskCompletionSource<bool>? _choiceTcs;

    public HumanController(PlayerId seat) { Seat = seat; }

    public PlayerAction PickAction(GameState g, IReadOnlyList<PlayerAction> legal)
    {
        _actionTcs = new TaskCompletionSource<PlayerAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionNeeded?.Invoke(g, legal);
        return _actionTcs.Task.GetAwaiter().GetResult();
    }

    public void SubmitAction(PlayerAction action)
    {
        var tcs = _actionTcs;
        _actionTcs = null;
        tcs?.TrySetResult(action);
    }

    public void AnswerChoice(GameState g, ChoiceRequest request)
    {
        _choiceTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChoiceNeeded?.Invoke(g, request);
        _choiceTcs.Task.GetAwaiter().GetResult();
    }

    public void CompleteChoice()
    {
        var tcs = _choiceTcs;
        _choiceTcs = null;
        tcs?.TrySetResult(true);
    }

    private ChoiceRequest? _currentChoice;
    public ChoiceRequest? CurrentChoice => _currentChoice;
    public void TrackChoice(ChoiceRequest? req) => _currentChoice = req;

    public void CancelChoice()
    {
        if (_currentChoice is null) return;
        _currentChoice.Cancelled = true;
        _currentChoice = null;
        CompleteChoice();
    }
}
