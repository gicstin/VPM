using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VPM.Services.Vpb;

namespace VPM.Services
{
    public class VpbPluginCheckResult
    {
        public bool IsInstalled { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string LocalVersion { get; set; }
        public string RemoteSha { get; set; }
        public string LocalSha { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset? RemoteLastModified { get; set; }

        public string GitRef { get; set; }

        public bool IsPinned { get; set; }

        public string PinnedVersion { get; set; }
    }

    public class VpbPluginChecker : IDisposable
    {
        private readonly HttpClient _httpClient;

        public VpbPluginChecker()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VPM/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public async Task<VpbPluginCheckResult> CheckAsync(string vamRoot, string gitRef = "main")
        {
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = VpbUpdateConfigFile.DefaultBranch;

            var config = VpbUpdateConfigFile.Load(vamRoot);
            var effectiveRef = config.Pinned ? config.EffectiveRef : gitRef;

            var result = new VpbPluginCheckResult
            {
                GitRef = effectiveRef,
                IsPinned = config.Pinned,
                PinnedVersion = config.Pinned ? config.PinnedVersion : null,
                DownloadUrl = VpbGitHubMetadata.GetVpbDllDownloadUrl(effectiveRef)
            };

            var localPath = VpbPaths.ResolveInstalledVpbDllPath(vamRoot);

            if (File.Exists(localPath))
            {
                result.IsInstalled = true;
                result.LocalSha = await Task.Run(() => ComputeGitBlobSha1Hex(localPath)).ConfigureAwait(false);
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(localPath);
                    result.LocalVersion = versionInfo.FileVersion;
                }
                catch { }
            }
            else
            {
                result.IsInstalled = false;
            }

            try
            {
                var manifest = await VpbPatchManifest.TryFetchAsync(_httpClient, effectiveRef).ConfigureAwait(false);
                if (manifest != null)
                {
                    foreach (var file in manifest.Files)
                    {
                        if (!file.IsDirectory
                            && file.RelativePath.EndsWith("plugins/VPB/VPB.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            result.RemoteSha = file.Sha1;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(result.RemoteSha))
                {
                    if (VpbGitHubMetadata.TryGetCachedBlobSha(effectiveRef, VpbGitHubMetadata.VpbDllRepoPath, out var cachedSha))
                    {
                        result.RemoteSha = cachedSha;
                    }
                    else
                    {
                        var tree = await VpbGitHubMetadata.GetPatchBlobShasAsync(_httpClient, effectiveRef).ConfigureAwait(false);
                        if (tree.TryGetValue(VpbGitHubMetadata.VpbDllRepoPath, out var sha))
                            result.RemoteSha = sha;
                    }
                }
            }
            catch
            {
                // Rate limit / network — leave RemoteSha unset so we do not falsely claim an update.
            }

            // 3. Compare
            if (!string.IsNullOrEmpty(result.RemoteSha) && result.IsInstalled)
            {
                if (!string.Equals(result.LocalSha, result.RemoteSha, StringComparison.OrdinalIgnoreCase))
                    result.IsUpdateAvailable = true;
            }

            return result;
        }

        private static string ComputeGitBlobSha1Hex(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var length = fileInfo.Length;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var header = Encoding.UTF8.GetBytes($"blob {length}\0");
            hasher.AppendData(header);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                }

                var hash = hasher.GetHashAndReset();
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
