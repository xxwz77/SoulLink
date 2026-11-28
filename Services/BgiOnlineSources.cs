using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services;

internal static partial class BgiOnlineSources
{
    internal enum Source { Local, Coordinator, GitHubReleases, GitHubActions, CnbMirror }
    internal sealed record Package(string Version, string Name, Uri Url, long? Size, string? Sha256,
        EntityTagHeaderValue? Tag = null, bool IsActionsArchive = false)
    {
        public string DisplayLabel => Size is long size ? $"{Version} · {size / 1048576.0:F0} MiB" : Version;
    }

    private const string Mirror = "https://cnb.cool/asdbr/BetterGI_LCB/-/releases/";
    private const string MirrorTag = "/asdbr/BetterGI_LCB/-/releases/tag/";
    private static readonly Regex VersionPattern = new(@"\A[0-9][0-9A-Za-z.+_-]{0,159}\z", RegexOptions.CultureInvariant);
    private static readonly byte[] SevenZip = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { UserAgent = { new("SoulLink-BGI-Updater", "1.0") } }
    };

    internal static string Label(Source source) => source switch
    {
        Source.Coordinator => "协调服务器",
        Source.GitHubReleases => "GitHub Releases",
        Source.GitHubActions => "GitHub Actions 构建",
        Source.CnbMirror => "CNB 茶包镜像（H工源）",
        _ => "本地安装包"
    };

    internal static async Task<Package> CheckAsync(Source source, string coordinator, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;

        if (source == Source.Coordinator)
        {
            if (!Uri.TryCreate(coordinator.Trim(), UriKind.Absolute, out var root) || !IsHttp(root) || root.Query.Length > 0)
                throw new InvalidOperationException("请填写有效的 BGI 协调服务器地址。");
            var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/bgi-update/latest.json");
            using var response = await OpenAsync(endpoint, ct).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseCoordinator(json.RootElement, endpoint);
        }

        if (source == Source.CnbMirror)
        {
            using var response = await OpenAsync(new Uri(Mirror + "latest"), ct).ConfigureAwait(false);
            string path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            if (!path.StartsWith(MirrorTag, StringComparison.Ordinal))
                throw new InvalidDataException("CNB 镜像没有返回最新发布版本。");
            string version = ValidateVersion(Uri.UnescapeDataString(path[MirrorTag.Length..].TrimEnd('/')));
            string name = "BetterGI_v" + version + ".7z";
            return await InspectArchiveAsync(version,
                new Uri(Mirror + "download/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(name)),
                ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("本地安装包没有云端版本。");
    }

    internal static Package ParseCoordinator(JsonElement manifest, Uri endpoint)
    {
        string name = ReadString(manifest, "fileName");
        if (!name.StartsWith("BetterGI_v", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("协调服务器清单没有提供 BetterGI 便携 7z 安装包。");

        string version = ValidateVersion(name[10..^3]);
        string link = ReadString(manifest, "url");
        string digest = ReadString(manifest, "sha256");
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest[7..];
        if (digest.Length > 0 && (digest.Length != 64 || !Regex.IsMatch(digest, @"\A[0-9a-fA-F]{64}\z")))
            throw new InvalidDataException("协调服务器清单的 SHA-256 无效。");

        if (!Uri.TryCreate(endpoint, link.Length == 0 ? Uri.EscapeDataString(name) : link, out var asset) ||
            !IsHttp(asset) || (!SameOrigin(asset, endpoint) && (digest.Length == 0 || asset.Scheme != Uri.UriSchemeHttps)))
            throw new InvalidDataException("协调服务器清单的下载地址无效。");

        long? size = null;
        if (manifest.TryGetProperty("sizeBytes", out var sizeValue) && sizeValue.ValueKind != JsonValueKind.Null)
        {
            if (sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out long bytes) || bytes <= 0)
                throw new InvalidDataException("协调服务器清单的文件大小无效。");
            size = bytes;
        }

        return new Package(version, name, asset, size, digest.Length > 0 ? digest : null);
    }

    internal static async Task<string> DownloadAsync(Package package, string directory, IProgress<int>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        ct = timeout.Token;
        using var response = await OpenAsync(package.Url, ct, tag: package.Tag).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            (package.Tag != null && !package.Tag.Equals(response.Headers.ETag)) ||
            (package.Size is long size && response.Content.Headers.ContentLength is long length && size != length))
            throw new InvalidDataException("云端安装包已变更，请刷新更新源后重试。");

        long? expected = package.Size ?? response.Content.Headers.ContentLength;
        string path = Path.Combine(directory, package.Name);
        string downloaded = package.IsActionsArchive ? Path.Combine(directory, "BetterGI_7z.zip") : path;
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            await ReceivePackageAsync(input, downloaded, expected, package.Sha256, progress, ct).ConfigureAwait(false);
        }

        if (package.IsActionsArchive)
        {
            using var outer = ZipFile.OpenRead(downloaded);
            var matches = outer.Entries.Where(entry => entry.Name == package.Name && entry.Length > SevenZip.Length).Take(2).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Actions 产物中没有唯一匹配所选版本的 7z 安装包。");
            await using var entry = matches[0].Open();
            await ReceivePackageAsync(entry, path, matches[0].Length, null, null, ct).ConfigureAwait(false);
        }

        await using (var archive = File.OpenRead(path))
        {
            await CheckSignatureAsync(archive, ct).ConfigureAwait(false);
        }
        progress?.Report(100);
        return path;
    }

    private static async Task ReceivePackageAsync(Stream source, string path, long? expected, string? sha256,
        IProgress<int>? progress, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = sha256 == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int lastPercent = -1;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (expected is long max && received > max)
                throw new InvalidDataException("下载大小超出清单记录。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            hash?.AppendData(buffer, 0, count);
            if (expected is > 0 && (int)(100.0 * received / expected.Value) is int percent && percent != lastPercent)
            {
                progress?.Report(percent);
                lastPercent = percent;
            }
        }
        if (expected is long total && received != total)
            throw new InvalidDataException("下载包大小校验失败。");
        if (hash != null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载包 SHA-256 校验失败。");
    }

    private static async Task<Package> InspectArchiveAsync(string version, Uri url, CancellationToken ct)
    {
        using var response = await OpenAsync(url, ct, probe: true).ConfigureAwait(false);
        var tag = response.Headers.ETag is { IsWeak: false } strong ? strong : null;
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            response.Content.Headers.ContentRange is not { From: 0, Length: > 63 } range)
            throw new InvalidDataException("云端没有返回可验证的 7z 附件。");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await CheckSignatureAsync(stream, ct).ConfigureAwait(false);
        return new Package(version, "BetterGI_v" + version + ".7z", url, range.Length, null, tag);
    }

    private static async Task CheckSignatureAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[SevenZip.Length];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual(SevenZip))
            throw new InvalidDataException("下载的文件不是 BetterGI 7z 安装包。");
    }

    private static async Task<HttpResponseMessage> OpenAsync(Uri initial, CancellationToken ct,
        bool probe = false, EntityTagHeaderValue? tag = null)
    {
        var current = initial;
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (probe) request.Headers.Range = new RangeHeaderValue(0, 63);
            if (tag != null) request.Headers.IfMatch.Add(tag);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400)
                    response.EnsureSuccessStatusCode();
                if (response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var target) ||
                    !IsHttp(target) || !IsDownloadRedirect(initial, target))
                    throw new InvalidDataException("更新源返回了无法验证的跳转。");
                current = target;
            }
        }
        throw new HttpRequestException("更新源跳转次数过多。");
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;

    private static string ValidateVersion(string value)
    {
        if (!VersionPattern.IsMatch(value)) throw new InvalidDataException("更新源返回的 BGI 版本格式无效。");
        return value;
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
