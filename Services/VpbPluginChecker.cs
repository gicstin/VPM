using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    }

    public class VpbPluginChecker : IDisposable
    {
        private readonly HttpClient _httpClient;
        private const string RepoOwner = "gicstin";
        private const string RepoName = "VPB";
        private const string FilePath = "vam_patch/BepInEx/plugins/VPB.dll";

        public VpbPluginChecker()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VPM/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        }

        public async Task<VpbPluginCheckResult> CheckAsync(string vamRoot, string gitRef = "main")
        {
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = "main";

            var result = new VpbPluginCheckResult();
            var localPath = Path.Combine(vamRoot, "BepInEx", "plugins", "VPB.dll");

            // 1. Check local file
            if (File.Exists(localPath))
            {
                result.IsInstalled = true;
                result.LocalSha = await Task.Run(() => ComputeGitBlobSha1Hex(localPath));
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

            // 2. Get remote info (Content for SHA, Commits for Date)
            try
            {
                // Get SHA
                var contentUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/contents/{FilePath}?ref={Uri.EscapeDataString(gitRef)}";
                var contentResponse = await _httpClient.GetAsync(contentUrl);
                
                if (contentResponse.IsSuccessStatusCode)
                {
                    var json = await contentResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("sha", out var shaElement))
                    {
                        result.RemoteSha = shaElement.GetString();
                    }

                    if (root.TryGetProperty("download_url", out var downloadUrlElement))
                    {
                        result.DownloadUrl = downloadUrlElement.GetString();
                    }
                }

                // Get Date (Commit)
                var commitsUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?path={FilePath}&per_page=1&sha={Uri.EscapeDataString(gitRef)}";
                var commitsResponse = await _httpClient.GetAsync(commitsUrl);

                if (commitsResponse.IsSuccessStatusCode)
                {
                    var json = await commitsResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var commitObj = doc.RootElement[0];
                        // commit -> committer -> date
                        if (commitObj.TryGetProperty("commit", out var commitProp) &&
                            commitProp.TryGetProperty("committer", out var committerProp) &&
                            committerProp.TryGetProperty("date", out var dateProp) &&
                            dateProp.TryGetDateTimeOffset(out var date))
                        {
                            result.RemoteLastModified = date;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Log or handle error? For now we just return what we have
            }

            // 3. Compare
            if (!string.IsNullOrEmpty(result.RemoteSha))
            {
                if (result.IsInstalled)
                {
                    if (!string.Equals(result.LocalSha, result.RemoteSha, StringComparison.OrdinalIgnoreCase))
                    {
                        result.IsUpdateAvailable = true;
                    }
                }
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
