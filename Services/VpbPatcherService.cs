using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    public enum VpbPatchStatus
    {
        UpToDate,
        NeedsInstall,
        NeedsUpdate
    }

    public enum VpbPatchIssueType
    {
        Missing,
        Outdated
    }

    public sealed class VpbPatchFileIssue
    {
        public VpbPatchIssueType IssueType { get; init; }
        public string RelativePath { get; init; }
        public bool IsDirectory { get; init; }
        public bool IsRequired { get; init; }
        public string Reason { get; init; }
        public string ExpectedSha { get; init; }
        public string LocalSha { get; init; }
    }

    public sealed class VpbPatchCheckResult
    {
        public VpbPatchStatus Status { get; init; }
        public string GitRef { get; init; }
        public int TotalFiles { get; init; }
        public int MissingFiles { get; init; }
        public int OutdatedFiles { get; init; }
        public IReadOnlyList<string> MissingRelativePaths { get; init; }
        public IReadOnlyList<string> OutdatedRelativePaths { get; init; }
        public IReadOnlyList<VpbPatchFileIssue> MissingDetails { get; init; }
        public IReadOnlyList<VpbPatchFileIssue> OutdatedDetails { get; init; }
    }

    public sealed class VpbPatchApplyResult
    {
        public string GitRef { get; init; }
        public int TotalFiles { get; init; }
        public int UpdatedFiles { get; init; }
        public int SkippedFiles { get; init; }
    }

    public sealed class VpbPatcherProgress
    {
        public int Index { get; init; }
        public int Total { get; init; }
        public string RelativePath { get; init; }
        public string Message { get; init; }
    }

    public sealed class VpbPatcherService : IDisposable
    {
        private const string RepoOwner = "gicstin";
        private const string RepoName = "VPB";
        private const string PatchRoot = "vam_patch/";

        private static readonly RequiredItem[] RequiredItems =
        [
            new RequiredItem("BepInEx/core/HarmonyXInterop.dll", false),
            new RequiredItem("BepInEx/core/0Harmony.dll", false),
            new RequiredItem("BepInEx/core/MonoMod.RuntimeDetour.dll", false),
            new RequiredItem("BepInEx/core/MonoMod.Utils.dll", false),
            new RequiredItem("BepInEx/core/Mono.Cecil.dll", false),
            new RequiredItem("BepInEx/core/Mono.Cecil.Mdb.dll", false),
            new RequiredItem("BepInEx/core/Mono.Cecil.Pdb.dll", false),
            new RequiredItem("BepInEx/core/Mono.Cecil.Rocks.dll", false),
            new RequiredItem("BepInEx/core/BepInEx.dll", false),
            new RequiredItem("BepInEx/core/BepInEx.Preloader.dll", false),
            new RequiredItem("BepInEx/core/0Harmony20.dll", false),
            new RequiredItem("BepInEx/core/BepInEx.Harmony.dll", false),
            new RequiredItem(".doorstop_version", false),
            new RequiredItem("changelog.txt", false),
            new RequiredItem("winhttp.dll", false),
            new RequiredItem("doorstop_config.ini", false),
            new RequiredItem("BepInEx/plugins", true),
            new RequiredItem("BepInEx/plugins/I18N.West.dll", false),
            new RequiredItem("BepInEx/plugins/VPB.dll", false),
            new RequiredItem("BepInEx/plugins/I18N.CJK.dll", false),
            new RequiredItem("BepInEx/plugins/I18N.dll", false),
            new RequiredItem("BepInEx/plugins/I18N.MidEast.dll", false),
            new RequiredItem("BepInEx/plugins/I18N.Other.dll", false),
            new RequiredItem("BepInEx/plugins/I18N.Rare.dll", false),
            new RequiredItem("Custom/Scripts/VPB/VPB-SessionPlugin.cs", false),
            new RequiredItem("VaM (Log Mode).bat", false)
        ];

        private readonly HttpClient _httpClient;
        private bool _disposed;

        public VpbPatcherService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VPM/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public async Task<VpbPatchCheckResult> CheckAsync(string gameFolder, string gitRef = "main", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gameFolder))
                throw new ArgumentException("Game folder is required", nameof(gameFolder));

            gameFolder = Path.GetFullPath(gameFolder);

            var vamExe = Path.Combine(gameFolder, "VaM.exe");
            if (!File.Exists(vamExe))
                throw new DirectoryNotFoundException($"VaM.exe not found in: {gameFolder}");

            try
            {
                var varBrowserPath = Path.Combine(gameFolder, "BepInEx", "plugins", "var_browser.dll");
                if (File.Exists(varBrowserPath))
                {
                    var info = new FileInfo(varBrowserPath);
                    if (info.Length > 220 * 1024)
                    {
                        var newPath = varBrowserPath + ".bak.remove";
                        if (File.Exists(newPath))
                            File.Delete(newPath);
                        File.Move(varBrowserPath, newPath);
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            var manifest = await GetManifestAsync(gitRef, cancellationToken).ConfigureAwait(false);
            var usedGitRef = manifest.Count > 0 ? manifest[0].GitRef : gitRef;

            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var issuesByPath = new Dictionary<string, VpbPatchFileIssue>(StringComparer.OrdinalIgnoreCase);

            var parallelOptions = new ParallelOptions 
            { 
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount 
            };

            await Parallel.ForEachAsync(manifest, parallelOptions, async (entry, ct) =>
            {
                var reqPath = NormalizeRelativePath(entry.RelativePath);
                var destPath = GetDestinationPath(gameFolder, reqPath);

                if (!File.Exists(destPath))
                {
                    lock (issuesByPath)
                    {
                        missing.Add(reqPath);
                        issuesByPath[reqPath] = new VpbPatchFileIssue
                        {
                            IssueType = VpbPatchIssueType.Missing,
                            RelativePath = reqPath,
                            IsDirectory = false,
                            IsRequired = true,
                            Reason = "File not found",
                            ExpectedSha = entry.BlobSha
                        };
                    }
                    return;
                }

                if (entry.BlobSha != "UNKNOWN")
                {
                    var localSha = await Task.Run(() => ComputeGitBlobSha1Hex(destPath), ct);
                    if (!string.Equals(localSha, entry.BlobSha, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (issuesByPath)
                        {
                            outdated.Add(reqPath);
                            issuesByPath[reqPath] = new VpbPatchFileIssue
                            {
                                IssueType = VpbPatchIssueType.Outdated,
                                RelativePath = reqPath,
                                IsDirectory = false,
                                IsRequired = true,
                                Reason = "Checksum mismatch",
                                ExpectedSha = entry.BlobSha,
                                LocalSha = localSha
                            };
                        }
                    }
                }
            });

            foreach(var req in RequiredItems.Where(x => x.IsDirectory))
            {
                var reqPath = NormalizeRelativePath(req.RelativePath);
                var destPath = GetDestinationPath(gameFolder, reqPath);
                if (!Directory.Exists(destPath))
                {
                     missing.Add(reqPath);
                     issuesByPath[reqPath] = new VpbPatchFileIssue
                     {
                        IssueType = VpbPatchIssueType.Missing,
                        RelativePath = reqPath,
                        IsDirectory = true,
                        IsRequired = true,
                        Reason = "Directory not found"
                     };
                }
            }

            var status = VpbPatchStatus.UpToDate;
            if (missing.Count > 0)
                status = VpbPatchStatus.NeedsInstall;
            else if (outdated.Count > 0)
                status = VpbPatchStatus.NeedsUpdate;

            return new VpbPatchCheckResult
            {
                Status = status,
                GitRef = usedGitRef,
                TotalFiles = manifest.Count,
                MissingFiles = missing.Count,
                OutdatedFiles = outdated.Count,
                MissingRelativePaths = missing.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                OutdatedRelativePaths = outdated.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                MissingDetails = issuesByPath.Values
                    .Where(v => v.IssueType == VpbPatchIssueType.Missing)
                    .OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                OutdatedDetails = issuesByPath.Values
                    .Where(v => v.IssueType == VpbPatchIssueType.Outdated)
                    .OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        public async Task<VpbPatchApplyResult> InstallOrUpdateAsync(
            string gameFolder,
            string gitRef = "main",
            bool force = false,
            IProgress<VpbPatcherProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gameFolder))
                throw new ArgumentException("Game folder is required", nameof(gameFolder));

            gameFolder = Path.GetFullPath(gameFolder);

            var manifest = await GetManifestAsync(gitRef, cancellationToken).ConfigureAwait(false);
            var usedGitRef = manifest.Count > 0 ? manifest[0].GitRef : gitRef;

            var updated = 0;
            var skipped = 0;
            var current = 0;

            var parallelOptions = new ParallelOptions 
            { 
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) 
            };

            await Parallel.ForEachAsync(manifest, parallelOptions, async (entry, ct) =>
            {
                var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                var needsWrite = force || !File.Exists(destPath);
                
                if (!needsWrite && entry.BlobSha != "UNKNOWN")
                {
                    var localSha = await Task.Run(() => ComputeGitBlobSha1Hex(destPath), ct);
                    needsWrite = !string.Equals(localSha, entry.BlobSha, StringComparison.OrdinalIgnoreCase);
                }

                if (!needsWrite)
                {
                    Interlocked.Increment(ref skipped);
                    var idx = Interlocked.Increment(ref current);
                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = idx,
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Up to date"
                    });
                    return;
                }

                var idxDownload = Interlocked.Increment(ref current);
                progress?.Report(new VpbPatcherProgress
                {
                    Index = idxDownload,
                    Total = manifest.Count,
                    RelativePath = entry.RelativePath,
                    Message = "Downloading"
                });

                Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? gameFolder);
                var tempPath = destPath + ".tmp_" + Guid.NewGuid().ToString("N");

                try
                {
                    await DownloadFileAsync(GetRawUrl(usedGitRef, entry.RelativePath), tempPath, ct).ConfigureAwait(false);

                    if (entry.BlobSha != "UNKNOWN")
                    {
                        var downloadedSha = await Task.Run(() => ComputeGitBlobSha1Hex(tempPath), ct);
                        if (!string.Equals(downloadedSha, entry.BlobSha, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Downloaded file checksum mismatch: {entry.RelativePath}");
                    }

                    File.Move(tempPath, destPath, true);
                    Interlocked.Increment(ref updated);

                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = idxDownload,
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Installed"
                    });
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            });

            return new VpbPatchApplyResult
            {
                GitRef = usedGitRef,
                TotalFiles = manifest.Count,
                UpdatedFiles = updated,
                SkippedFiles = skipped
            };
        }

        public async Task<VpbPatchApplyResult> UninstallAsync(
            string gameFolder,
            string gitRef = "main",
            IProgress<VpbPatcherProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gameFolder))
                throw new ArgumentException("Game folder is required", nameof(gameFolder));

            gameFolder = Path.GetFullPath(gameFolder);

            var manifest = await GetManifestAsync(gitRef, cancellationToken).ConfigureAwait(false);
            var usedGitRef = manifest.Count > 0 ? manifest[0].GitRef : gitRef;

            var removed = 0;
            var skipped = 0;

            for (var i = 0; i < manifest.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = manifest[i];
                var destPath = GetDestinationPath(gameFolder, entry.RelativePath);

                progress?.Report(new VpbPatcherProgress
                {
                    Index = i + 1,
                    Total = manifest.Count,
                    RelativePath = entry.RelativePath,
                    Message = "Removing"
                });

                if (!File.Exists(destPath))
                {
                    skipped++;
                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = i + 1,
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Not found"
                    });
                    continue;
                }

                try
                {
                    File.Delete(destPath);
                    removed++;

                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = i + 1,
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Removed"
                    });
                }
                catch
                {
                    skipped++;
                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = i + 1,
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Failed"
                    });
                }
            }

            try
            {
                var directories = manifest
                    .Select(m => NormalizeRelativePath(m.RelativePath))
                    .Select(p => Path.GetDirectoryName(p.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => p.Length)
                    .ToList();

                foreach (var relDir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fullDir = Path.Combine(gameFolder, relDir);
                    if (!Directory.Exists(fullDir))
                        continue;

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(fullDir).Any())
                            Directory.Delete(fullDir, recursive: false);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return new VpbPatchApplyResult
            {
                GitRef = usedGitRef,
                TotalFiles = manifest.Count,
                UpdatedFiles = removed,
                SkippedFiles = skipped
            };
        }

        private string GetRawUrl(string gitRef, string relativePath)
        {
            return $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{gitRef}/{PatchRoot}{NormalizeRelativePath(relativePath)}";
        }

        private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        private static string GetDestinationPath(string gameFolder, string relativePath)
        {
            if (relativePath == null)
                throw new ArgumentNullException(nameof(relativePath));

            var sanitized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            if (sanitized.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid relative path: {relativePath}");

            return Path.Combine(gameFolder, sanitized);
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return string.Empty;

            return relativePath.Replace('\\', '/').TrimStart('/');
        }

        private async Task<List<ManifestEntry>> GetManifestAsync(string gitRef, CancellationToken cancellationToken)
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/git/trees/{gitRef}?recursive=1";
            var manifest = new List<ManifestEntry>();

            try
            {
                // Use the API to get file metadata (SHAs) for checksum validation
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var treeResponse = JsonSerializer.Deserialize<GitHubTreeResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    var remoteFiles = treeResponse?.Tree
                        .Where(x => x.Type == "blob" && x.Path.StartsWith(PatchRoot, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(x => x.Path, x => x.Sha, StringComparer.OrdinalIgnoreCase);

                    foreach (var item in RequiredItems.Where(x => !x.IsDirectory))
                    {
                        var fullPath = PatchRoot + item.RelativePath;
                        fullPath = fullPath.Replace('\\', '/');

                        string sha = "UNKNOWN";
                        if (remoteFiles != null && remoteFiles.TryGetValue(fullPath, out var foundSha))
                        {
                            sha = foundSha;
                        }

                        manifest.Add(new ManifestEntry
                        {
                            RelativePath = item.RelativePath,
                            BlobSha = sha,
                            GitRef = gitRef
                        });
                    }
                }
                else
                {
                    // Fallback if API fails (e.g. rate limited): Assume UNKNOWN shas, only missing files will be detected
                    foreach (var item in RequiredItems.Where(x => !x.IsDirectory))
                    {
                        manifest.Add(new ManifestEntry
                        {
                            RelativePath = item.RelativePath,
                            BlobSha = "UNKNOWN",
                            GitRef = gitRef
                        });
                    }
                }
            }
            catch
            {
                // Fallback on error
                 foreach (var item in RequiredItems.Where(x => !x.IsDirectory))
                 {
                     manifest.Add(new ManifestEntry
                     {
                         RelativePath = item.RelativePath,
                         BlobSha = "UNKNOWN",
                         GitRef = gitRef
                     });
                 }
            }

            return manifest;
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
            if (_disposed)
                return;

            _disposed = true;
            _httpClient?.Dispose();
        }

        private sealed class ManifestEntry
        {
            public string RelativePath { get; init; }
            public string BlobSha { get; init; }
            public string GitRef { get; init; }
        }

        private readonly record struct RequiredItem(string RelativePath, bool IsDirectory);

        private sealed class GitHubTreeResponse
        {
            public List<GitHubTreeItem> Tree { get; set; }
            public bool Truncated { get; set; }
        }

        private sealed class GitHubTreeItem
        {
            public string Path { get; set; }
            public string Mode { get; set; }
            public string Type { get; set; }
            public string Sha { get; set; }
            public long Size { get; set; }
        }
    }
}
