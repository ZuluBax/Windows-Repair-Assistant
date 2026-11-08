using System.Windows;

namespace WindowsRepairAssistant.Services;

public interface IDialogService
{
    /// <summary>Shows the save-your-work warning. Returns true only on an explicit "Yes".</summary>
    bool ConfirmRestart();

    /// <summary>Asks for consent to contact GitHub. Returns true only on an explicit "Yes".</summary>
    bool AskToCheckForUpdates();

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

    public bool AskToCheckForUpdates()
    {
        MessageBoxResult result = MessageBox.Show(
            Owner(),
            "Would you like Windows Repair Assistant to check for new versions each time it opens?\n\n" +
            "If you choose Yes, the app will ask GitHub for the latest version number. GitHub will see your IP address, " +
            "as any website you visit does. Nothing else about you or your computer is sent.\n\n" +
            "The app will only tell you when a new version is available. It will not download or install anything.\n\n" +
            "You can change this at any time with the \"Update checks\" link at the bottom of the window.",
            "Check for updates?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static Window Owner() => Application.Current.MainWindow;
}
