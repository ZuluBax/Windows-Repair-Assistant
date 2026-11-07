using System.Windows;

namespace WindowsRepairAssistant.Services;

public interface IDialogService
{
    /// <summary>Shows the save-your-work warning. Returns true only on an explicit "Yes".</summary>
    bool ConfirmRestart();

    void ShowError(string title, string message);

    void ShowInformation(string title, string message);
}

public sealed class MessageBoxDialogService : IDialogService
{
    public bool ConfirmRestart()
    {
        MessageBoxResult result = MessageBox.Show(
            Owner(),
            "Your computer will restart immediately.\n\n" +
            "Please save any open documents and close your other programs first. Unsaved work in other programs will be lost.\n\n" +
            "Do you want to restart now?",
            "Restart your computer?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static Window Owner() => Application.Current.MainWindow;
}
