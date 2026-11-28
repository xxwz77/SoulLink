using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services;

internal static partial class BgiOnlineSources
{
    private const string GitHubRepo = "kaedelcb/better-genshin-impact";
    private const string GitHubApi = "https://api.github.com/repos/" + GitHubRepo;
    private const string ActionsArtifact = "BetterGI_7z";

    internal static async Task<IReadOnlyList<Package>> GetCatalogAsync(Source source, string coordinator, CancellationToken ct)
    {
        if (source != Source.GitHubReleases && source != Source.GitHubActions)
            return [await CheckAsync(source, coordinator, ct).ConfigureAwait(false)];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        if (source == Source.GitHubReleases)
        {
            var releases = await ReadGitHubAsync("/releases?per_page=15", timeout.Token).ConfigureAwait(false);
            return ParseReleases(releases);
        }

        var runsTask = ReadGitHubAsync("/actions/workflows/161175465/runs?status=success&per_page=10", timeout.Token);
        var artifactsTask = ReadGitHubAsync("/actions/artifacts?name=BetterGI_7z&per_page=30", timeout.Token);
        var results = await Task.WhenAll(runsTask, artifactsTask).ConfigureAwait(false);
        return ParseActions(results[0], results[1]);
    }

    private static async Task<JsonElement> ReadGitHubAsync(string path, CancellationToken ct)
    {
        using var response = await OpenAsync(new Uri(GitHubApi + path), ct).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.Clone();
    }

    internal static IReadOnlyList<Package> ParseReleases(JsonElement releases)
    {
        List<Package> packages = [];
        foreach (var release in releases.EnumerateArray())
        {
            if (!HasFalse(release, "draft") || !HasFalse(release, "prerelease")) continue;
            string tag = ReadString(release, "tag_name");
            string version = tag.TrimStart('v', 'V');
            if (!VersionPattern.IsMatch(version)) continue;

            string name = "BetterGI_v" + version + ".7z";
            foreach (var asset in ReadArray(release, "assets"))
            {
                if (ReadString(asset, "name") != name || ReadString(asset, "state") != "uploaded" ||
                    PositiveNumber(asset, "size") is not long size || GitHubDigest(asset) is not string digest ||
                    !Uri.TryCreate(ReadString(asset, "browser_download_url"), UriKind.Absolute, out var url) ||
                    !IsPublicHttpsHost(url, "github.com") || url.Query.Length != 0 ||
                    Uri.UnescapeDataString(url.AbsolutePath) != $"/{GitHubRepo}/releases/download/{tag}/{name}")
                    continue;
                packages.Add(new Package(version, name, url, size, digest));
                break;
            }
        }
        return packages.DistinctBy(p => p.Version, StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
    }

    internal static IReadOnlyList<Package> ParseActions(JsonElement runs, JsonElement artifacts)
    {
        var byRun = ReadArray(artifacts, "artifacts")
            .Where(a => ReadString(a, "name") == ActionsArtifact && HasFalse(a, "expired") &&
                a.TryGetProperty("workflow_run", out var run) && PositiveNumber(run, "id") != null)
            .ToLookup(a => PositiveNumber(a.GetProperty("workflow_run"), "id")!.Value);

        List<Package> packages = [];
        foreach (var run in ReadArray(runs, "workflow_runs"))
        {
            const string prefix = "BetterGI ";
            const string releaseMarker = "release:";
            string title = ReadString(run, "display_title");
            if (ReadString(run, "conclusion") != "success" || !title.StartsWith(prefix, StringComparison.Ordinal) ||
                PositiveNumber(run, "id") is not long runId)
                continue;

            string version = title[prefix.Length..];
            if (version.StartsWith(releaseMarker, StringComparison.Ordinal)) version = version[releaseMarker.Length..];
            if (!VersionPattern.IsMatch(version)) continue;

            var candidates = byRun[runId].ToArray();
            if (candidates.Length != 1 || PositiveNumber(candidates[0], "size_in_bytes") is not long size ||
                GitHubDigest(candidates[0]) is not string digest) continue;

            var url = new Uri($"https://nightly.link/{GitHubRepo}/actions/runs/{runId}/{ActionsArtifact}.zip");
            packages.Add(new Package(version, "BetterGI_v" + version + ".7z", url, size, digest, IsActionsArchive: true));
        }
        return packages.DistinctBy(p => p.Version, StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static bool HasFalse(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.False;

    private static long? PositiveNumber(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long number) && number > 0 ? number : null;

    private static string? GitHubDigest(JsonElement asset)
    {
        string digest = ReadString(asset, "digest");
        return digest.Length == 71 && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
            digest[7..].All(Uri.IsHexDigit) ? digest[7..] : null;
    }

    private static bool IsPublicHttpsHost(Uri url, string host) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.IdnHost == host && IsHttp(url);

    private static bool IsDownloadRedirect(Uri initial, Uri target)
    {
        if (SameOrigin(initial, target)) return true;
        if (target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort) return false;
        return initial.IdnHost switch
        {
            "cnb.cool" => target.IdnHost == "asset.cnb.cool",
            "github.com" => target.IdnHost is "release-assets.githubusercontent.com" or "objects.githubusercontent.com",
            "nightly.link" => target.IdnHost.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) ||
                              target.IdnHost == "results-receiver.actions.githubusercontent.com",
            _ => false
        };
    }
}
