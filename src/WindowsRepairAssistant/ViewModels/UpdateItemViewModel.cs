using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.ViewModels;

/// <summary>One available Windows update with the user's tick. Nothing is ticked for them.</summary>
public sealed class UpdateItemViewModel(WindowsUpdateInfo info) : ObservableObject
{
    private bool _isSelected;

    public WindowsUpdateInfo Info => info;

    public string Title => info.Title;

    // No download size: Windows reports a worst case that can be wildly high (97 GB was seen for a
    // monthly cumulative update that really downloads far less), and Settings doesn't show one either.
    public string Details => !info.IsOptional ? "Important update"
        : info.IsDriver ? "Optional driver update"
        : "Optional update";

    /// <summary>Installing unattended could hang on a question nobody is there to answer.</summary>
    public bool CanSelect => !info.NeedsUserInput;

    public string? Note => info.NeedsUserInput
        ? "This update may ask you questions while it installs, so please install it from Settings > Windows Update."
        : null;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value && CanSelect);
    }
}
