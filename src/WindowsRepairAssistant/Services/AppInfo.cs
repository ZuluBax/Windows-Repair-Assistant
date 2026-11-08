using System.Reflection;

namespace WindowsRepairAssistant.Services;

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public const string PrivacyPolicyUrl = "https://github.com/ZuluBax/Windows-Repair-Assistant/blob/main/PRIVACY.md";

    /// <summary>In-app summary of PRIVACY.md. Keep the two in sync when the policy changes.</summary>
    public const string PrivacySummary =
        "Windows Repair Assistant does not collect, share or send any information about you or your computer. " +
        "It has no telemetry, analytics, advertising or accounts.\n\n" +
        "Update checks are optional and only happen if you agree. The app then asks GitHub for the latest version " +
        "number when it opens. GitHub sees your IP address, as any website you visit does, and nothing else about you " +
        "or your computer is sent. The app makes no other network connections, and it never downloads or installs updates itself.\n\n" +
        "During a repair, Microsoft's DISM tool may download replacement Windows files from Windows Update.\n\n" +
        "Logs are saved only on this computer, in C:\\ProgramData\\WindowsRepairAssistant\\Logs. " +
        "Only the 5 most recent repair logs and 5 most recent crash logs are kept, and they are never sent anywhere. " +
        "You can delete them at any time.\n\n" +
        "Full privacy policy:\n" + PrivacyPolicyUrl;
}
