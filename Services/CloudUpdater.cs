using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    public static class CloudUpdater
    {
        private const string Repository = "xxwz77/SoulLink";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        static CloudUpdater()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulLink-Updater");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public sealed class Manifest
        {
            public string Version { get; init; } = "";
            public string Notes { get; init; } = "";
            internal Asset Core { get; init; } = null!;
            internal Asset Agent { get; init; } = null!;
            internal Asset Broker { get; init; } = null!;
        }

        internal sealed record Asset(string Name, Version Version, string Hash, long Size);

        private sealed class Release
        {
            [JsonPropertyName("tag_name")] public string? Tag { get; init; }
            [JsonPropertyName("body")] public string? Notes { get; init; }
            [JsonPropertyName("draft")] public bool Draft { get; init; }
            [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
            [JsonPropertyName("assets")] public ReleaseAsset[]? Assets { get; init; }
        }

        private sealed class ReleaseAsset
        {
            [JsonPropertyName("name")] public string? Name { get; init; }
            [JsonPropertyName("state")] public string? State { get; init; }
            [JsonPropertyName("digest")] public string? Digest { get; init; }
            [JsonPropertyName("size")] public long Size { get; init; }
        }

        public static string CurrentVersion => typeof(CloudUpdater).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        public static async Task<Manifest?> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                return await FetchUpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        }

        internal static async Task<Manifest?> FetchUpdateAsync(CancellationToken ct = default)
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<Release>(stream, cancellationToken: ct).ConfigureAwait(false);
            return GetUpdate(release, Version.Parse(CurrentVersion));
        }

        internal static Manifest? ParseRelease(string json, Version local)
            => GetUpdate(JsonSerializer.Deserialize<Release>(json), local);

        private static Manifest? GetUpdate(Release? release, Version local)
        {
            if (release is null || release.Draft || release.Prerelease || release.Tag is not { } tag
                || !tag.StartsWith('v') || !TryVersion(tag[1..], out var remote) || remote <= local)
                return null;

            var core = FindAsset(release, "update-", remote);
            var agent = FindAsset(release, "agent-");
            var broker = FindAsset(release, "broker-");
            if (core == null || agent == null || broker == null)
                throw new InvalidDataException("发布包缺少必要的组件或校验信息。");

            return new Manifest { Version = remote.ToString(3), Notes = release.Notes ?? "", Core = core, Agent = agent, Broker = broker };
        }

        private static Asset? FindAsset(Release release, string prefix, Version? required = null)
        {
            Asset? match = null;
            foreach (var item in release.Assets ?? Array.Empty<ReleaseAsset>())
            {
                string? name = item.Name;
                if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal)
                    || !TryVersion(name[prefix.Length..^4], out var version) || (required != null && version != required))
                    continue;
                string? digest = item.Digest;
                if (match != null || item.State != "uploaded" || item.Size <= 0 || digest == null || digest.Length != 71
                    || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
                    return null;
                match = new Asset(name, version, digest[7..], item.Size);
            }
            return match;
        }

        private static bool TryVersion(string text, out Version version)
        {
            version = new Version(0, 0, 0);
            if (!Version.TryParse(text, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString(3) != text)
                return false;
            version = parsed;
            return true;
        }

        internal static bool NeedsComponent(string directory, string exeName, Version offered)
            => EngineComponentVersions.NeedsUpdate(Path.Combine(directory, exeName), exeName, offered);

        public static async Task<string> DownloadAsync(Manifest manifest, IProgress<double>? progress, CancellationToken ct = default)
        {
            var assets = new List<Asset> { manifest.Core };
            string installDir = AppContext.BaseDirectory;
            if (NeedsComponent(installDir, EngineComponentVersions.AgentFile, manifest.Agent.Version)) assets.Add(manifest.Agent);
            if (NeedsComponent(installDir, EngineComponentVersions.BrokerFile, manifest.Broker.Version)) assets.Add(manifest.Broker);

            var directory = Directory.CreateTempSubdirectory("SoulLinkUpdate-");
            try
            {
                long expected = assets.Sum(a => a.Size);
                long done = 0;
                foreach (var asset in assets)
                {
                    long alreadyDownloaded = done;
                    await DownloadAssetAsync(manifest, asset, directory.FullName,
                        n => progress?.Report((double)(alreadyDownloaded + n) / expected), ct).ConfigureAwait(false);
                    done += asset.Size;
                }

                string package = Path.Combine(directory.FullName, manifest.Core.Name);
                if (assets.Count > 1)
                {
                    // The existing standalone updater accepts a single ZIP.
                    string combined = Path.Combine(directory.FullName, "combined.zip");
                    await MergeAsync(combined, directory.FullName, assets, ct).ConfigureAwait(false);
                    foreach (var asset in assets) File.Delete(Path.Combine(directory.FullName, asset.Name));
                    File.Move(combined, package);
                }
                return package;
            }
            catch
            {
                try { directory.Delete(recursive: true); } catch { }
                throw;
            }
        }

        private static async Task DownloadAssetAsync(Manifest manifest, Asset asset, string directory, Action<long> received, CancellationToken ct)
        {
            string url = $"https://github.com/{Repository}/releases/download/v{manifest.Version}/{asset.Name}";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = File.Create(Path.Combine(directory, asset.Name));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long count = 0;
            int length;
            while ((length = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                count += length;
                if (count > asset.Size) throw new InvalidDataException("下载的文件校验不通过。");
                await output.WriteAsync(buffer.AsMemory(0, length), timeout.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, length);
                received(count);
            }
            if (count != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载的文件校验不通过。");
        }

        private static async Task MergeAsync(string target, string directory, List<Asset> assets, CancellationToken ct)
        {
            using var output = ZipFile.Open(target, ZipArchiveMode.Create);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                using var input = ZipFile.OpenRead(Path.Combine(directory, asset.Name));
                foreach (var entry in input.Entries)
                {
                    if (entry.Name.Length == 0) continue;
                    string name = entry.FullName.Replace('\\', '/');
                    if (!names.Add(name)) throw new InvalidDataException("发布包包含重复文件。");
                    using var source = entry.Open();
                    using var destination = output.CreateEntry(name, CompressionLevel.Fastest).Open();
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
