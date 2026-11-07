using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.ViewModels;

public sealed class StepViewModel(RepairStepDefinition definition, int number) : ObservableObject
{
    private StepStatus _status = StepStatus.Pending;
    private string _message = definition.Description;
    private string _durationText = "";

    public int Number { get; } = number;

    public string DisplayName => definition.DisplayName;

    public string Title => definition.Title;

    public StepStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public string StatusText => Status.ToDisplayText();

    public bool IsFinished => Status is not (StepStatus.Pending or StepStatus.Running);

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public void Reset()
    {
        Status = StepStatus.Pending;
        Message = definition.Description;
        DurationText = "";
    }

    public void Apply(StepResult result)
    {
        Status = result.Status;
        Message = result.Message;
        DurationText = result.Status == StepStatus.Skipped ? "" : FileSessionLog.FormatDuration(result.Duration);
    }
}
