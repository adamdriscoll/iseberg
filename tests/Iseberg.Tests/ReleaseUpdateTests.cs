using System.Net;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ReleaseUpdateTests
{
    [Theory]
    [InlineData("1.0.0", "v1.0.1", true)]
    [InlineData("1.9.0", "v1.10.0", true)]
    [InlineData("1.0.0+abc123", "1.0.0", false)]
    [InlineData("2.0.0", "v1.9.9", false)]
    [InlineData("1.0.0-preview.1+abc", "v1.0.0", true)]
    [InlineData("2.0.0-preview.1", "v1.0.0", false)]
    [InlineData("0.0.123-ci", "v1.0.0", true)]
    public async Task ComparesSemanticVersionsRatherThanTagText(string current, string tag, bool expected)
    {
        using var client = Client(Release(tag));
        var update = await new ReleaseUpdateChecker(client).CheckAsync(current, "win-x64");
        Assert.Equal(expected, update is not null);
    }

    [Theory]
    [InlineData("win-x64", "msi")]
    [InlineData("osx-x64", "dmg")]
    [InlineData("osx-arm64", "dmg")]
    [InlineData("linux-x64", "zip")]
    [InlineData("linux-arm64", "zip")]
    public async Task SelectsMatchingPackageAndNeverTrustsRemoteUrls(string runtime, string format)
    {
        var name = $"Iseberg-2.0.0-{runtime}.{format}";
        using var client = Client(Release("v2.0.0", [$"Iseberg-2.0.0-{runtime}.zip", name, "Iseberg-2.0.0-other.zip"]));
        var update = await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", runtime);
        Assert.NotNull(update);
        Assert.True(update.HasPackage);
        Assert.Equal($"https://github.com/adamdriscoll/iseberg/releases/download/v2.0.0/{name}", update.DownloadUri.AbsoluteUri);
        Assert.Equal("https://github.com/adamdriscoll/iseberg/releases/tag/v2.0.0", update.ReleaseUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("osx-arm64")]
    public async Task FallsBackToMatchingZipWhenInstallerIsAbsent(string runtime)
    {
        using var client = Client(Release("2.0.0", [$"Iseberg-2.0.0-{runtime}.zip"]));
        var update = await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", runtime);
        Assert.NotNull(update);
        Assert.True(update.HasPackage);
        Assert.EndsWith(".zip", update.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task MissingOrIncompletePackagesUseTheReleasePage()
    {
        using var client = Client("""{"tag_name":"v2.0.0","assets":[{"name":"Iseberg-2.0.0-win-x64.msi","state":"new"}]}""");
        var update = await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64");
        Assert.NotNull(update);
        Assert.False(update.HasPackage);
        Assert.Equal(update.ReleaseUri, update.DownloadUri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DoesNotOfferDraftsOrPrereleases(bool draft, bool prerelease)
    {
        using var client = Client(JsonSerializer.Serialize(new { tag_name = "v2.0.0", draft, prerelease }));
        Assert.Null(await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
    }

    [Fact]
    public async Task NoPublishedReleaseIsNotAnError()
    {
        using var client = Client("{}", HttpStatusCode.NotFound);
        Assert.Null(await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ApiFailuresAreExplicit(HttpStatusCode status)
    {
        using var client = Client("{}", status);
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"tag_name":"not-a-version"}""")]
    [InlineData("""{"tag_name":null}""")]
    [InlineData("""{"tag_name":"v2.0.0","assets":null}""")]
    [InlineData("""{"tag_name":"v2.0.0","assets":[null]}""")]
    public async Task InvalidReleaseDataIsNotReportedAsUpToDate(string json)
    {
        using var client = Client(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
    }

    [Fact]
    public async Task CancellationAndTimeoutStopPendingChecks()
    {
        using var client = new HttpClient(new UpdateHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        })) { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64", cancellation.Token));
    }

    [Fact]
    public async Task SendsAnUnauthenticatedGitHubRequestWithRequiredHeaders()
    {
        using var client = new HttpClient(new UpdateHttpHandler((request, _) =>
        {
            Assert.Equal(ReleaseUpdateChecker.LatestReleaseApi, request.RequestUri);
            Assert.Equal("Iseberg/1.0.0", request.Headers.UserAgent.ToString());
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/vnd.github+json");
            Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        Assert.Null(await new ReleaseUpdateChecker(client).CheckAsync("1.0.0", "win-x64"));
    }

    private static HttpClient Client(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new UpdateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) })));

    private static string Release(string tag, string[]? assets = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        html_url = "https://untrusted.example",
        assets = (assets ?? []).Select(name => new { name, state = "uploaded", browser_download_url = "https://untrusted.example" })
    });
}

internal sealed class UpdateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
