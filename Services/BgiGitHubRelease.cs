using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services;

internal static class BgiGitHubRelease
{
    private const string Repository = "kaedelcb/better-genshin-impact";
    private const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ReleasePath = $"/{Repository}/releases/download/";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal sealed record Package(string Version, string Name, Uri DownloadUrl, long Size, string Sha256);

    internal static async Task<Package> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.ParseAdd("SoulLink-BGI-Updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub 暂时拒绝查询 LCB 发行版。请稍后重试，或从该仓库的 Releases 手动下载便携包。");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("LCB 尚无可查询的正式发行版。");
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        return ParseRelease(json.RootElement);
    }

    internal static Package ParseRelease(JsonElement release)
    {
        string? tag = ReadString(release, "tag_name");
        if (string.IsNullOrEmpty(tag) || tag.Length > 100 ||
            !tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_') ||
            release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub 返回的 LCB 发行版信息无效。");

        string version = tag.TrimStart('v', 'V');
        string expectedName = "BetterGI_v" + version;
        Package? zip = null;
        Package? sevenZip = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name = ReadString(asset, "name");
            if (name == null || ReadString(asset, "state") != "uploaded" ||
                !asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out long size) || size <= 0)
                continue;

            bool isSevenZip = name.Equals(expectedName + ".7z", StringComparison.OrdinalIgnoreCase);
            if (!isSevenZip && !name.Equals(expectedName + ".zip", StringComparison.OrdinalIgnoreCase)) continue;

            string? digest = ReadString(asset, "digest");
            if (digest == null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !digest.AsSpan(7).ContainsOnlyHexDigits() ||
                !Uri.TryCreate(ReadString(asset, "browser_download_url"), UriKind.Absolute, out var url) ||
                !IsGitHubReleaseAsset(url, tag, name))
                continue;

            var package = new Package(tag, name, url, size, digest[7..]);
            if (isSevenZip)
            {
                if (sevenZip != null) throw new InvalidDataException("LCB 发布中出现多个相同的便携包。");
                sevenZip = package;
            }
            else
            {
                if (zip != null) throw new InvalidDataException("LCB 发布中出现多个相同的便携包。");
                zip = package;
            }
        }

        return sevenZip ?? zip ??
            throw new InvalidDataException("此 LCB 发行版没有可验证的 BetterGI 便携包（7z 或 zip）。");
    }

    internal static async Task<string> DownloadAsync(Package package, string directory, IProgress<int>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await GetDownloadResponseAsync(package, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length != package.Size)
            throw new InvalidDataException("下载大小与 GitHub 公布的大小不一致。");

        string path = Path.Combine(directory, package.Name);
        using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var progressClock = Stopwatch.StartNew();
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (received > package.Size) throw new InvalidDataException("下载大小超过 GitHub 公布的大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            if (progressClock.ElapsedMilliseconds >= 200)
            {
                progress?.Report((int)(100.0 * received / package.Size));
                progressClock.Restart();
            }
        }

        if (received != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载包校验失败。请重新下载。");
        progress?.Report(100);
        return path;
    }

    private static async Task<HttpResponseMessage> GetDownloadResponseAsync(Package package, CancellationToken ct)
    {
        var url = package.DownloadUrl;
        if (!IsGitHubReleaseAsset(url, package.Version, package.Name))
            throw new InvalidDataException("无效的 LCB 发布附件地址。");
        for (int hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("SoulLink-BGI-Updater");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400)
                    response.EnsureSuccessStatusCode();
                Uri? target = response.Headers.Location;
                if (target == null || !target.IsAbsoluteUri || !IsGitHubAssetHost(target))
                    throw new InvalidDataException("GitHub 返回了无法验证的下载跳转。");
                url = target;
            }
        }
        throw new HttpRequestException("GitHub 下载跳转次数过多。");
    }

    private static bool IsGitHubReleaseAsset(Uri url, string tag, string name) =>
        IsHttpsDefaultPort(url) && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.Query.Length == 0 && Uri.UnescapeDataString(url.AbsolutePath).Equals(ReleasePath + tag + "/" + name, StringComparison.Ordinal);

    private static bool IsGitHubAssetHost(Uri url) =>
        IsHttpsDefaultPort(url) && url.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static bool IsHttpsDefaultPort(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 && url.Fragment.Length == 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (char digit in value)
            if (!Uri.IsHexDigit(digit)) return false;
        return true;
    }
}
