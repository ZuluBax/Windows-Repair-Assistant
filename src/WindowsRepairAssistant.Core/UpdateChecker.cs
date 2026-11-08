using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsRepairAssistant.Core;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? LatestVersion = null);

public interface IUpdateChecker
{
    /// <summary>Never throws for network or response problems; those return <see cref="UpdateCheckStatus.Failed"/>.</summary>
    Task<UpdateCheckResult> CheckAsync(string currentVersion);
}

/// <summary>
/// Asks GitHub for the latest stable release. It only reports a version number: nothing is downloaded
/// or installed, and the page shown to the user is a fixed address rather than a link from the response.
/// </summary>
public sealed partial class GitHubUpdateChecker(HttpMessageHandler? handler = null) : IUpdateChecker
{
    public const string ReleasesPageUrl = "https://github.com/ZuluBax/Windows-Repair-Assistant/releases/latest";

    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ZuluBax/Windows-Repair-Assistant/releases/latest";

    // GitHub rejects requests without a User-Agent. Deliberately carries no version or system details.
    private const string UserAgent = "WindowsRepairAssistant";

    private const int MaxResponseBytes = 1024 * 1024;

    // This tool often runs on broken PCs, where a slow or absent network must not linger.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // [0-9] and \z rather than \d and $: \d accepts non-ASCII digits and $ allows a trailing newline.
    [GeneratedRegex(@"^v([0-9]+\.[0-9]+\.[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableTag();

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion)
    {
        if (!Version.TryParse(currentVersion, out Version? current))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }

        try
        {
            using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = RequestTimeout;
            client.MaxResponseContentBufferSize = MaxResponseBytes;

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed);
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Version? latest = ParseLatestVersion(json);
            if (latest is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed);
            }

            return latest > current
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latest.ToString(3))
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            // Being offline is normal here. An optional check must never get in the way of a repair.
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }

    private static Version? ParseLatestVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || IsTrue(root, "draft")
            || IsTrue(root, "prerelease")
            || !root.TryGetProperty("tag_name", out JsonElement tag)
            || tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        Match match = StableTag().Match(tag.GetString() ?? "");
        return match.Success && Version.TryParse(match.Groups[1].Value, out Version? version) ? version : null;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
