using Pulse.Helpers;
using Pulse.Services;

namespace Pulse.Models;

public enum StepState
{
    Pending,
    Active,
    Complete,
    NotFound,
    Error
}

public sealed class SetupStep : ViewModelBase
{
    private StepState _state = StepState.Pending;
    private string _detail = string.Empty;

    public SetupStep(int index, string titleKey)
    {
        Index = index;
        TitleKey = titleKey;
    }

    public int Index { get; }
    public string TitleKey { get; }
    public string Title => Localization.Get(TitleKey);
    public string Number => Index.ToString("00");

    public StepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                Raise(nameof(IsComplete));
                Raise(nameof(IsActive));
                Raise(nameof(IsError));
                Raise(nameof(IsNotFound));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsComplete => State == StepState.Complete;
    public bool IsActive => State == StepState.Active;
    public bool IsError => State == StepState.Error;
    public bool IsNotFound => State == StepState.NotFound;

    public void RefreshLanguage() => Raise(nameof(Title));
}
