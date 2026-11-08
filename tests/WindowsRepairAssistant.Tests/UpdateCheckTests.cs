using System.Net;
using System.Text;
using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

public class UpdateCheckerTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<UpdateCheckResult> Check(string current, string body) =>
        new GitHubUpdateChecker(new FakeHandler(_ => Json(body))).CheckAsync(current);

    private static string Release(string tag) => $$"""{"tag_name":{{System.Text.Json.JsonSerializer.Serialize(tag)}},"draft":false,"prerelease":false}""";

    [Theory]
    [InlineData("1.0.0", "v1.0.1", "1.0.1")]
    [InlineData("1.0.0", "v2.0.0", "2.0.0")]
    [InlineData("1.9.0", "v1.10.0", "1.10.0")] // Numeric, not alphabetical
    public async Task Newer_stable_release_is_reported(string current, string tag, string expected)
    {
        UpdateCheckResult result = await Check(current, Release(tag));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(expected, result.LatestVersion);
    }

    [Theory]
    [InlineData("1.0.0", "v1.0.0")]
    [InlineData("1.1.0", "v1.0.9")] // Never suggest a downgrade
    public async Task Same_or_older_release_is_up_to_date(string current, string tag)
    {
        UpdateCheckResult result = await Check(current, Release(tag));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.LatestVersion);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta.1")]
    [InlineData("v1.2.3\n")]
    [InlineData("v٣.0.0")] // Non-ASCII digit
    [InlineData("v9.9.9 Click here")]
    [InlineData("v99999999999.0.0")] // Overflows Version
    public async Task Tags_that_are_not_plain_stable_versions_are_rejected(string tag)
    {
        UpdateCheckResult result = await Check("1.0.0", Release(tag));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.LatestVersion);
    }

    [Theory]
    [InlineData("""{"tag_name":"v9.0.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"v9.0.0","draft":true}""")]
    [InlineData("""{"tag_name":9}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>captive portal</html>")]
    [InlineData("")]
    public async Task Unusable_responses_fail_quietly(string body)
    {
        UpdateCheckResult result = await Check("1.0.0", body);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task No_published_release_fails_quietly()
    {
        var checker = new GitHubUpdateChecker(new FakeHandler(_ => Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)));

        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync("1.0.0")).Status);
    }

    [Fact]
    public async Task Oversized_response_is_refused()
    {
        string huge = Release("v9.0.0").Insert(1, $"\"padding\":\"{new string('x', 2 * 1024 * 1024)}\",");

        Assert.Equal(UpdateCheckStatus.Failed, (await Check("1.0.0", huge)).Status);
    }

    [Fact]
    public async Task Network_failure_does_not_throw()
    {
        var checker = new GitHubUpdateChecker(new FakeHandler(_ => throw new HttpRequestException("offline")));

        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync("1.0.0")).Status);
    }

    [Fact]
    public async Task Timeout_does_not_throw()
    {
        var checker = new GitHubUpdateChecker(new FakeHandler(_ => throw new TaskCanceledException("timed out")));

        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync("1.0.0")).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a version")]
    public async Task Unreadable_current_version_never_contacts_the_network(string current)
    {
        var handler = new FakeHandler(_ => Json(Release("v9.0.0")));

        UpdateCheckResult result = await new GitHubUpdateChecker(handler).CheckAsync(current);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(handler.LastRequest);
    }

    [Fact] // The privacy policy promises exactly this: no data beyond the request itself
    public async Task Request_goes_only_to_github_and_identifies_nothing_about_the_user()
    {
        var handler = new FakeHandler(_ => Json(Release("v1.0.0")));

        await new GitHubUpdateChecker(handler).CheckAsync("1.0.0");

        HttpRequestMessage request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/ZuluBax/Windows-Repair-Assistant/releases/latest", request.RequestUri!.ToString());
        Assert.Null(request.Content);
        Assert.Equal("WindowsRepairAssistant", request.Headers.UserAgent.ToString());
        Assert.Equal(["User-Agent", "Accept"], request.Headers.Select(h => h.Key).OrderByDescending(k => k, StringComparer.Ordinal));
    }
}

public sealed class UpdatePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WRA-tests-" + Guid.NewGuid().ToString("N"));

    private LogStore Logs => new(_root, hardenPermissions: false);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void First_run_has_no_choice_so_the_user_gets_asked()
    {
        Assert.Null(new UpdatePreferenceStore(Logs).Load());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Choice_is_remembered_by_a_new_instance(bool choice)
    {
        Assert.True(new UpdatePreferenceStore(Logs).Save(choice));

        Assert.Equal(choice, new UpdatePreferenceStore(Logs).Load());
    }

    [Fact]
    public void Choice_can_be_changed_and_leaves_no_temp_files()
    {
        var store = new UpdatePreferenceStore(Logs);

        Assert.True(store.Save(true));
        Assert.True(store.Save(false));

        Assert.False(store.Load());
        Assert.Equal(["update-checks.txt"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("ENABLED")]
    public void Unrecognised_file_counts_as_no_choice(string content)
    {
        Logs.EnsureDirectory();
        File.WriteAllText(Logs.UpdatePreferencePath, content);

        Assert.Null(new UpdatePreferenceStore(Logs).Load());
    }

    [Fact]
    public void Oversized_file_counts_as_no_choice()
    {
        Logs.EnsureDirectory();
        File.WriteAllText(Logs.UpdatePreferencePath, "enabled" + new string(' ', 1000));

        Assert.Null(new UpdatePreferenceStore(Logs).Load());
    }

    [Fact]
    public void Save_failure_is_reported_not_thrown()
    {
        File.WriteAllText(_root, "a file where the folder should be");
        try
        {
            Assert.False(new UpdatePreferenceStore(Logs).Save(true));
        }
        finally
        {
            File.Delete(_root);
        }
    }
}
