using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services.Vpb
{
    public static class VpbGitHubMetadata
    {
        public const string RepoOwner = "gicstin";
        public const string RepoName = "VPB";
        public const string PatchRoot = "vam_patch/";
        public const string VpbDllRepoPath = "vam_patch/BepInEx/plugins/VPB/VPB.dll";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
        private static readonly object Gate = new object();

        private static string _treeRef;
        private static Dictionary<string, string> _treeShas;
        private static DateTime _treeUtc;

        private static IReadOnlyList<string> _branches;
        private static DateTime _branchesUtc;

        public static string GetRawUrl(string gitRef, string relativePathUnderPatch)
        {
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = "main";

            var rel = (relativePathUnderPatch ?? "").Replace('\\', '/').TrimStart('/');
            return $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{gitRef}/{PatchRoot}{rel}";
        }

        public static string GetVpbDllDownloadUrl(string gitRef)
        {
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = "main";
            return $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{gitRef}/{VpbDllRepoPath}";
        }

        public static bool TryGetCachedBlobSha(string gitRef, string fullRepoPath, out string sha)
        {
            sha = null;
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = "main";

            lock (Gate)
            {
                if (_treeShas == null
                    || !string.Equals(_treeRef, gitRef, StringComparison.Ordinal)
                    || DateTime.UtcNow - _treeUtc >= CacheTtl)
                {
                    return false;
                }

                return _treeShas.TryGetValue(fullRepoPath, out sha);
            }
        }

        public static async Task<IReadOnlyDictionary<string, string>> GetPatchBlobShasAsync(
            HttpClient httpClient,
            string gitRef,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrWhiteSpace(gitRef))
                gitRef = "main";

            lock (Gate)
            {
                if (_treeShas != null
                    && string.Equals(_treeRef, gitRef, StringComparison.Ordinal)
                    && DateTime.UtcNow - _treeUtc < CacheTtl)
                {
                    return _treeShas;
                }
            }

            var treeUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1";
            using var response = await httpClient.GetAsync(treeUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw CreateApiException(response, body, "VPB git tree");

            var treeResponse = JsonSerializer.Deserialize<GitHubTreeResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (treeResponse?.Truncated == true)
            {
                throw new IOException(
                    "GitHub returned a truncated git tree for VPB. Cannot verify patch checksums. Retry later or check the VPB repo size.");
            }

            var map = treeResponse?.Tree
                ?.Where(x => x != null
                             && string.Equals(x.Type, "blob", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrEmpty(x.Path)
                             && x.Path.StartsWith(PatchRoot, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Path, x => x.Sha, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (Gate)
            {
                _treeRef = gitRef;
                _treeShas = map;
                _treeUtc = DateTime.UtcNow;
            }

            return map;
        }

        public static async Task<IReadOnlyList<string>> GetBranchesAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));

            lock (Gate)
            {
                if (_branches != null && DateTime.UtcNow - _branchesUtc < CacheTtl)
                    return _branches;
            }

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/branches";
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw CreateApiException(response, body, "VPB branches");

            var items = JsonSerializer.Deserialize<List<GitHubBranchItem>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var names = items?
                .Select(b => b.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n == "main" ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            lock (Gate)
            {
                _branches = names;
                _branchesUtc = DateTime.UtcNow;
            }

            return names;
        }

        public static IOException CreateApiException(HttpResponseMessage response, string body, string what)
        {
            var status = (int)response.StatusCode;
            var rateLimited = response.StatusCode == HttpStatusCode.Forbidden
                              || status == 429
                              || (body != null && body.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0);

            if (rateLimited)
            {
                var resetText = "a few minutes";
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
                {
                    var raw = values.FirstOrDefault();
                    if (long.TryParse(raw, out var epoch))
                    {
                        var local = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
                        resetText = local.ToString("HH:mm:ss");
                    }
                }

                return new IOException(
                    $"GitHub API rate limit while fetching {what}. " +
                    $"Unauthenticated limit is 60 requests/hour. Try again after {resetText}.");
            }

            var snippet = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase
                : body.Trim();
            if (snippet != null && snippet.Length > 240)
                snippet = snippet.Substring(0, 240) + "...";

            return new IOException($"GitHub API error ({status}) fetching {what}: {snippet}");
        }

        private sealed class GitHubTreeResponse
        {
            public List<GitHubTreeItem> Tree { get; set; }
            public bool Truncated { get; set; }
        }

        private sealed class GitHubTreeItem
        {
            public string Path { get; set; }
            public string Sha { get; set; }
            public string Type { get; set; }
        }

        private sealed class GitHubBranchItem
        {
            public string Name { get; set; }
        }
    }
}
