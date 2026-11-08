using System.Text;

namespace WindowsRepairAssistant.Core;

/// <summary>Remembers whether the user agreed to update checks, next to the logs in the protected app folder.</summary>
public sealed class UpdatePreferenceStore(LogStore logStore)
{
    private const string Enabled = "enabled";
    private const string Disabled = "disabled";
    private const int MaxFileBytes = 64;

    /// <returns>The saved choice, or null if the user has not made a readable one, so they get asked.</returns>
    public bool? Load()
    {
        try
        {
            var file = new FileInfo(logStore.UpdatePreferencePath);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxFileBytes)
            {
                return null;
            }

            return File.ReadAllText(file.FullName).Trim() switch
            {
                Enabled => true,
                Disabled => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <returns>False if the choice could not be saved; it then only lasts for this session.</returns>
    public bool Save(bool enabled)
    {
        string? tempPath = null;
        try
        {
            logStore.EnsureDirectory();
            string path = logStore.UpdatePreferencePath;
            tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

            // We run elevated: writing through an existing path could follow a planted link. A new
            // file moved into place replaces a link rather than writing to wherever it points.
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.ASCII.GetBytes(enabled ? Enabled : Disabled));
            }

            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a stray temp file in our own folder; the failed save is already being reported.
        }
    }
}
