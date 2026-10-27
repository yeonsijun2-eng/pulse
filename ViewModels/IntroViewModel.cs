using Pulse.Helpers;

namespace Pulse.ViewModels;

public sealed class IntroViewModel : ViewModelBase
{
    private bool _ready;

    public bool Ready
    {
        get => _ready;
        private set => SetProperty(ref _ready, value);
    }

    public bool AnimationsEnabled { get; private set; }

    public void Prepare(bool animationsEnabled)
    {
        AnimationsEnabled = animationsEnabled;
        Ready = false;
    }

    public async Task PlayAsync(bool animationsEnabled, CancellationToken cancellationToken)
    {
        Prepare(animationsEnabled);
        var hold = animationsEnabled ? 1600 : 250;
        await Task.Delay(hold, cancellationToken);
        Ready = true;
        await Task.Delay(animationsEnabled ? 500 : 80, cancellationToken);
    }
}
