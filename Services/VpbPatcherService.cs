using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        Outdated,
        UpToDate
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
        public int PatchedFiles { get; init; }
        public IReadOnlyList<string> MissingRelativePaths { get; init; }
        public IReadOnlyList<string> OutdatedRelativePaths { get; init; }
        public IReadOnlyList<VpbPatchFileIssue> MissingDetails { get; init; }
        public IReadOnlyList<VpbPatchFileIssue> OutdatedDetails { get; init; }
        public IReadOnlyList<VpbPatchFileIssue> PatchedDetails { get; init; }
    }

    public sealed class VpbPatchApplyResult
    {
        public string GitRef { get; init; }
        public int TotalFiles { get; init; }
        public int UpdatedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public IReadOnlyList<VpbPatchApplyFailure> FailedFiles { get; init; } = Array.Empty<VpbPatchApplyFailure>();
    }

    public sealed class VpbPatchApplyFailure
    {
        public string RelativePath { get; init; }
        public string ErrorMessage { get; init; }
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
        private const string BackupFileName = "vpb_backup.zip";

        private class PatchManifestItem
        {
            public string RelativePath { get; set; }
            public bool IsDirectory { get; set; }
        }

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

            // Cleanup any .bak.remove files from previous uninstall attempts
            foreach (var entry in manifest)
            {
                if (entry.IsDirectory) continue;
                try
                {
                    var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                    var bakPath = destPath + ".bak.remove";
                    if (File.Exists(bakPath))
                        File.Delete(bakPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var issuesByPath = new Dictionary<string, VpbPatchFileIssue>(StringComparer.OrdinalIgnoreCase);

            var parallelOptions = new ParallelOptions 
            { 
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount 
            };

            await Parallel.ForEachAsync(manifest.Where(x => !x.IsDirectory), parallelOptions, async (entry, ct) =>
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

                if (entry.BlobSha == "UNKNOWN")
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
                            Reason = "Cannot verify against GitHub (tree metadata missing or API error). Re-apply the patch; ensure api.github.com is reachable and not rate-limited.",
                            ExpectedSha = entry.BlobSha
                        };
                    }
                    return;
                }

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
                    else
                    {
                        lock (issuesByPath)
                        {
                            patched.Add(reqPath);
                            issuesByPath[reqPath] = new VpbPatchFileIssue
                            {
                                IssueType = VpbPatchIssueType.UpToDate,
                                RelativePath = reqPath,
                                IsDirectory = false,
                                IsRequired = true,
                                Reason = "Up to date",
                                ExpectedSha = entry.BlobSha,
                                LocalSha = localSha
                            };
                        }
                    }
                }
            });

            foreach(var req in manifest.Where(x => x.IsDirectory))
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
                else
                {
                     // Directory exists, consider it patched/up-to-date
                     // Though for directories we don't have SHA, so it's just existence check
                     lock (issuesByPath)
                     {
                         patched.Add(reqPath);
                         issuesByPath[reqPath] = new VpbPatchFileIssue
                         {
                             IssueType = VpbPatchIssueType.UpToDate,
                             RelativePath = reqPath,
                             IsDirectory = true,
                             IsRequired = true,
                             Reason = "Directory exists"
                         };
                     }
                }
            }

            var backupPath = Path.Combine(gameFolder, BackupFileName);
            var status = VpbPatchStatus.UpToDate;
            if (!File.Exists(backupPath) || missing.Count > 0)
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
                PatchedFiles = patched.Count,
                MissingRelativePaths = missing.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                OutdatedRelativePaths = outdated.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                MissingDetails = issuesByPath.Values
                    .Where(v => v.IssueType == VpbPatchIssueType.Missing)
                    .OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                OutdatedDetails = issuesByPath.Values
                    .Where(v => v.IssueType == VpbPatchIssueType.Outdated)
                    .OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                PatchedDetails = issuesByPath.Values
                    .Where(v => v.IssueType == VpbPatchIssueType.UpToDate)
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

            var backupPath = Path.Combine(gameFolder, BackupFileName);
            if (!File.Exists(backupPath))
            {
                await CreateBackupAsync(gameFolder, manifest, backupPath, progress, cancellationToken).ConfigureAwait(false);
            }

            var updated = 0;
            var skipped = 0;
            var current = 0;
            var failures = new ConcurrentBag<VpbPatchApplyFailure>();

            var parallelOptions = new ParallelOptions 
            { 
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) 
            };

            await Parallel.ForEachAsync(manifest, parallelOptions, async (entry, ct) =>
            {
                try
                {
                    var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                    
                    if (entry.IsDirectory)
                    {
                        if (!Directory.Exists(destPath))
                        {
                            Directory.CreateDirectory(destPath);
                            Interlocked.Increment(ref updated);
                        }
                        else
                        {
                            Interlocked.Increment(ref skipped);
                        }

                        var idx = Interlocked.Increment(ref current);
                        progress?.Report(new VpbPatcherProgress
                        {
                            Index = idx,
                            Total = manifest.Count,
                            RelativePath = entry.RelativePath,
                            Message = "Verified"
                        });
                        return;
                    }

                    var needsWrite = force || !File.Exists(destPath);

                    if (!needsWrite && entry.BlobSha != "UNKNOWN")
                    {
                        var localSha = await Task.Run(() => ComputeGitBlobSha1Hex(destPath), ct);
                        needsWrite = !string.Equals(localSha, entry.BlobSha, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (!needsWrite)
                    {
                        // Without a blob SHA we cannot trust the on-disk file; fetch raw content and compare.
                        needsWrite = true;
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
                        try
                        {
                            await DownloadFileAsync(GetRawUrl(usedGitRef, entry.RelativePath), tempPath, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw new IOException($"Failed to download '{entry.RelativePath}'. Make sure this file exists in the 'vam_patch' folder on GitHub. Details: {ex.Message}", ex);
                        }

                        if (entry.BlobSha != "UNKNOWN")
                        {
                            var downloadedSha = await Task.Run(() => ComputeGitBlobSha1Hex(tempPath), ct);
                            if (!string.Equals(downloadedSha, entry.BlobSha, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException($"Downloaded file checksum mismatch: {entry.RelativePath}");
                        }
                        else if (File.Exists(destPath) && SameGitBlobFileContent(destPath, tempPath))
                        {
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch
                            {
                            }

                            Interlocked.Increment(ref skipped);
                            progress?.Report(new VpbPatcherProgress
                            {
                                Index = idxDownload,
                                Total = manifest.Count,
                                RelativePath = entry.RelativePath,
                                Message = "Up to date"
                            });
                            return;
                        }

                        try
                        {
                            File.Move(tempPath, destPath, true);
                        }
                        catch (IOException ex)
                        {
                            throw new IOException(
                                $"Could not replace '{entry.RelativePath}'. Close VaM, Hub, and any BepInEx tools that load this file, then retry. Details: {ex.Message}",
                                ex);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            throw new IOException(
                                $"Access denied when writing '{entry.RelativePath}'. Close programs using this file or run VPM with sufficient permissions. Details: {ex.Message}",
                                ex);
                        }
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
                }
                catch (Exception ex)
                {
                    failures.Add(new VpbPatchApplyFailure
                    {
                        RelativePath = entry.RelativePath,
                        ErrorMessage = ex.Message
                    });
                    
                    progress?.Report(new VpbPatcherProgress
                    {
                        Index = Interlocked.Increment(ref current),
                        Total = manifest.Count,
                        RelativePath = entry.RelativePath,
                        Message = "Failed"
                    });
                }
            });

            return new VpbPatchApplyResult
            {
                GitRef = usedGitRef,
                TotalFiles = manifest.Count,
                UpdatedFiles = updated,
                SkippedFiles = skipped,
                FailedFiles = failures.ToList()
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

            var restoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            var skipped = 0;

            // 1. Restore from backup if it exists
            var backupPath = Path.Combine(gameFolder, BackupFileName);
            if (File.Exists(backupPath))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var archive = ZipFile.OpenRead(backupPath);
                        var totalEntries = archive.Entries.Count;
                        for (int i = 0; i < totalEntries; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var entry = archive.Entries[i];
                            if (string.IsNullOrEmpty(entry.Name)) continue; // Skip directories

                            var relPath = NormalizeRelativePath(entry.FullName);
                            progress?.Report(new VpbPatcherProgress
                            {
                                Index = i + 1,
                                Total = totalEntries,
                                RelativePath = relPath,
                                Message = "Restoring"
                            });

                            var destPath = Path.Combine(gameFolder, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            try
                            {
                                var dir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                entry.ExtractToFile(destPath, overwrite: true);
                                restoredFiles.Add(relPath);
                            }
                            catch
                            {
                                // Skip locked or inaccessible files during restore
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Backup open failed or critical error
                }
            }

            // 2. Remove files from manifest that were NOT in the backup (i.e., new files added by the patch)
            for (var i = 0; i < manifest.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = manifest[i];
                if (entry.IsDirectory) continue;

                var relPath = NormalizeRelativePath(entry.RelativePath);
                if (restoredFiles.Contains(relPath))
                {
                    // Already restored to original state (or tried to)
                    continue;
                }

                var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                if (!File.Exists(destPath))
                {
                    continue;
                }

                progress?.Report(new VpbPatcherProgress
                {
                    Index = i + 1,
                    Total = manifest.Count,
                    RelativePath = entry.RelativePath,
                    Message = "Removing"
                });

                try
                {
                    File.Delete(destPath);
                    removed++;
                }
                catch
                {
                    try
                    {
                        var removePath = destPath + ".bak.remove";
                        if (File.Exists(removePath)) File.Delete(removePath);
                        File.Move(destPath, removePath);
                        removed++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }

            try
            {
                var directories = manifest
                    .SelectMany(m =>
                    {
                        var path = NormalizeRelativePath(m.RelativePath);
                        if (m.IsDirectory)
                            return new[] { path, Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) };
                        return new[] { Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) };
                    })
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

            foreach (var entry in manifest)
            {
                if (entry.RelativePath.EndsWith("VPB.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                    if (File.Exists(destPath))
                    {
                        try { File.Delete(destPath); } 
                        catch { 
                            try { 
                                var removePath = destPath + ".bak.remove";
                                if (File.Exists(removePath)) File.Delete(removePath);
                                File.Move(destPath, removePath);
                            } catch {} 
                        }
                    }
                }
            }

            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
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

        private async Task CreateBackupAsync(string gameFolder, List<ManifestEntry> manifest, string backupPath, IProgress<VpbPatcherProgress> progress, CancellationToken cancellationToken)
        {
            progress?.Report(new VpbPatcherProgress
            {
                Index = 0,
                Total = manifest.Count,
                Message = "Creating backup..."
            });

            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                    
                    foreach (var entry in manifest)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.IsDirectory) continue;

                        var destPath = GetDestinationPath(gameFolder, entry.RelativePath);
                        if (File.Exists(destPath))
                        {
                            try
                            {
                                var zipEntryPath = entry.RelativePath.Replace('\\', '/');
                                archive.CreateEntryFromFile(destPath, zipEntryPath);
                            }
                            catch
                            {
                                // Skip files that cannot be read (e.g. locked)
                                // If we can't read them for backup, we likely can't overwrite them anyway
                            }
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // If backup fails, we should probably delete the partial zip
                try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                throw new IOException($"Failed to create backup: {ex.Message}", ex);
            }
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

        public async Task<IReadOnlyList<string>> GetBranchesAsync(CancellationToken cancellationToken = default)
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/branches";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<GitHubBranchItem>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var names = items?
                .Select(b => b.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n == "main" ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            return names;
        }

        private async Task<List<ManifestEntry>> GetManifestAsync(string gitRef, CancellationToken cancellationToken)
        {
            var treeUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/git/trees/{gitRef}?recursive=1";
            
            List<PatchManifestItem> requiredItems = null;
            try
            {
                var manifestUrl = GetRawUrl(gitRef, "patch_manifest.json");
                using var jsonResponse = await _httpClient.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
                if (jsonResponse.IsSuccessStatusCode)
                {
                     var jsonContent = await jsonResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                     requiredItems = JsonSerializer.Deserialize<List<PatchManifestItem>>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch
            {
                // Failed to fetch manifest
            }

            if (requiredItems == null)
            {
                return new List<ManifestEntry>();
            }

            var manifest = new List<ManifestEntry>();

            try
            {
                // Use the API to get file metadata (SHAs) for checksum validation
                using var response = await _httpClient.GetAsync(treeUrl, cancellationToken).ConfigureAwait(false);
                
                Dictionary<string, string> remoteFiles = null;
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var treeResponse = JsonSerializer.Deserialize<GitHubTreeResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    // Partial trees would leave many paths without SHAs and silently skip updates — treat as unavailable.
                    if (treeResponse?.Truncated == true)
                        remoteFiles = null;
                    else
                        remoteFiles = treeResponse?.Tree
                            .Where(x => x.Type == "blob" && x.Path.StartsWith(PatchRoot, StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(x => x.Path, x => x.Sha, StringComparer.OrdinalIgnoreCase);
                }

                foreach (var item in requiredItems)
                {
                    var fullPath = PatchRoot + item.RelativePath;
                    fullPath = fullPath.Replace('\\', '/');

                    string sha = "UNKNOWN";
                    if (!item.IsDirectory && remoteFiles != null && remoteFiles.TryGetValue(fullPath, out var foundSha))
                    {
                        sha = foundSha;
                    }

                    manifest.Add(new ManifestEntry
                    {
                        RelativePath = item.RelativePath,
                        BlobSha = sha,
                        GitRef = gitRef,
                        IsDirectory = item.IsDirectory
                    });
                }
            }
            catch
            {
                // Fallback on error
                 foreach (var item in requiredItems)
                 {
                     manifest.Add(new ManifestEntry
                     {
                         RelativePath = item.RelativePath,
                         BlobSha = "UNKNOWN",
                         GitRef = gitRef,
                         IsDirectory = item.IsDirectory
                     });
                 }
            }

            return manifest;
        }

        private static bool SameGitBlobFileContent(string pathA, string pathB)
        {
            if (!File.Exists(pathA) || !File.Exists(pathB))
                return false;

            var lenA = new FileInfo(pathA).Length;
            var lenB = new FileInfo(pathB).Length;
            if (lenA != lenB)
                return false;

            var shaA = ComputeGitBlobSha1Hex(pathA);
            var shaB = ComputeGitBlobSha1Hex(pathB);
            return string.Equals(shaA, shaB, StringComparison.OrdinalIgnoreCase);
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
            public bool IsDirectory { get; init; }
        }

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

        private sealed class GitHubBranchItem
        {
            public string Name { get; set; }
        }
    }
}
