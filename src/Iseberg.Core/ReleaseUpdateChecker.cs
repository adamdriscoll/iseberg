using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Iseberg.Core;

public sealed record ReleaseUpdate(string Version, Uri ReleaseUri, Uri DownloadUri, bool HasPackage);

public sealed class ReleaseUpdateChecker(HttpClient client)
{
    public const string RepositoryUrl = "https://github.com/adamdriscoll/iseberg";
    public static Uri LatestReleaseApi { get; } = new("https://api.github.com/repos/adamdriscoll/iseberg/releases/latest");

    public async Task<ReleaseUpdate?> CheckAsync(string currentVersion, string runtime, CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("Iseberg/" + current.Numeric);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (release.Draft || release.Prerelease) return null;
        if (release.Assets is null || release.Assets.Any(asset => asset is null))
            throw new InvalidDataException("GitHub returned invalid release assets.");
        var latest = ParseVersion(release.Tag);
        if (latest.IsPrerelease) return null;
        if (latest.Numeric < current.Numeric || (latest.Numeric == current.Numeric && !current.IsPrerelease)) return null;

        var releaseUri = new Uri($"{RepositoryUrl}/releases/tag/{Uri.EscapeDataString(release.Tag)}");
        var formats = runtime.StartsWith("win-", StringComparison.Ordinal) ? new[] { "msi", "zip" }
            : runtime.StartsWith("osx-", StringComparison.Ordinal) ? ["dmg", "zip"] : ["zip"];
        foreach (var format in formats)
        {
            var name = $"Iseberg-{latest.Numeric}-{runtime}.{format}";
            if (release.Assets.Any(asset => asset.Name == name && asset.State == "uploaded"))
                return new(latest.Numeric.ToString(), releaseUri,
                    new Uri($"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(release.Tag)}/{Uri.EscapeDataString(name)}"), true);
        }
        return new(latest.Numeric.ToString(), releaseUri, releaseUri, false);
    }

    private static (Version Numeric, bool IsPrerelease) ParseVersion(string? value)
    {
        var match = Regex.Match(value ?? "",
            @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version))
            throw new InvalidDataException($"Invalid release version: '{value}'. Expected a semantic version such as 1.2.3.");
        return (version, match.Groups[4].Success);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string Tag { get; init; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("state")] public string State { get; init; } = "";
    }
}
