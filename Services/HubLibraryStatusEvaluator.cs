using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Determines whether Hub resources are present in the local library.
    /// Single source of truth for card/detail "In Library" and "Update" badges.
    /// Badges consider the main package file(s) only — not the dependency stack.
    /// </summary>
    public sealed class HubLibraryStatusEvaluator
    {
        public readonly struct StatusResult
        {
            public StatusResult(bool inLibrary, bool updateAvailable)
            {
                InLibrary = inLibrary;
                UpdateAvailable = updateAvailable;
            }

            public bool InLibrary { get; }
            public bool UpdateAvailable { get; }
            public static StatusResult NotApplicable => new StatusResult(false, false);
        }

        public readonly struct DependencyStatusResult
        {
            public DependencyStatusResult(int installed, int missing, bool resolved)
            {
                Installed = installed;
                Missing = missing;
                Resolved = resolved;
            }

            public int Installed { get; }
            public int Missing { get; }
            public int Total => Installed + Missing;
            public bool Resolved { get; }
            public bool AllInstalled => Resolved && Total > 0 && Missing == 0;

            public static DependencyStatusResult Unresolved => new DependencyStatusResult(0, 0, false);
            public static DependencyStatusResult None => new DependencyStatusResult(0, 0, true);
        }

        private readonly HubService _hubService;

        public HubLibraryStatusEvaluator(HubService hubService)
        {
            _hubService = hubService ?? throw new ArgumentNullException(nameof(hubService));
        }

        public async Task<StatusResult> EvaluateAsync(
            HubResource resource,
            IReadOnlyDictionary<string, HashSet<int>> localVersionsByGroup,
            IReadOnlyCollection<string> localPackageNames,
            Func<string, CancellationToken, Task<HubResourceDetail>> detailLoader,
            CancellationToken cancellationToken = default,
            HubResourceDetail knownDetail = null)
        {
            if (resource == null || !resource.HubDownloadable)
                return StatusResult.NotApplicable;

            HubResourceDetail detail = knownDetail ?? resource as HubResourceDetail;
            // Only fetch detail when listing has no main files — deps are not used for badges
            var hasListingFiles = resource.HubFiles != null && resource.HubFiles.Count > 0;
            if (detail == null && !hasListingFiles && !string.IsNullOrEmpty(resource.ResourceId) && detailLoader != null)
            {
                try
                {
                    detail = await detailLoader(resource.ResourceId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    detail = null;
                }
            }

            var mainFiles = CollectMainPackageFiles(resource, detail);
            if (mainFiles.Count == 0)
                return StatusResult.NotApplicable;

            var checkedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allInLibrary = true;
            var updateAvailable = false;

            foreach (var file in mainFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetPackageGroup(file, out var pkgGroupName))
                {
                    allInLibrary = false;
                    continue;
                }

                if (!checkedGroups.Add(pkgGroupName))
                    continue;

                if (!IsPackageGroupInstalled(pkgGroupName, localVersionsByGroup, localPackageNames, out var localVersion))
                {
                    allInLibrary = false;
                    continue;
                }

                if (!updateAvailable && localVersion > 0 && IsUpdateAvailableForFile(file, pkgGroupName, localVersion))
                    updateAvailable = true;
            }

            return new StatusResult(allInLibrary, allInLibrary && updateAvailable);
        }

        /// <summary>
        /// Counts how many direct dependencies are installed vs missing for card dep badges.
        /// Does not affect In Library / Update badges.
        /// </summary>
        public async Task<DependencyStatusResult> EvaluateDependencyStatusAsync(
            HubResource resource,
            IReadOnlyDictionary<string, HashSet<int>> localVersionsByGroup,
            IReadOnlyCollection<string> localPackageNames,
            Func<string, CancellationToken, Task<HubResourceDetail>> detailLoader,
            CancellationToken cancellationToken = default,
            HubResourceDetail knownDetail = null)
        {
            if (resource == null || resource.IsExternallyHosted || resource.DependencyCount <= 0)
                return DependencyStatusResult.None;

            HubResourceDetail detail = knownDetail ?? resource as HubResourceDetail;
            if ((detail?.Dependencies == null || detail.Dependencies.Count == 0)
                && !string.IsNullOrEmpty(resource.ResourceId)
                && detailLoader != null)
            {
                try
                {
                    detail = await detailLoader(resource.ResourceId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    detail = null;
                }
            }

            return CountDependencyInstallStatus(detail, localVersionsByGroup, localPackageNames);
        }

        internal static DependencyStatusResult CountDependencyInstallStatus(
            HubResourceDetail detail,
            IReadOnlyDictionary<string, HashSet<int>> localVersionsByGroup,
            IReadOnlyCollection<string> localPackageNames)
        {
            if (detail?.Dependencies == null || detail.Dependencies.Count == 0)
                return DependencyStatusResult.Unresolved;

            var checkedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installed = 0;
            var missing = 0;

            foreach (var kvp in detail.Dependencies)
            {
                var countedFromFiles = false;

                if (kvp.Value != null)
                {
                    foreach (var file in kvp.Value)
                    {
                        // Hub API filenames often omit ".var" (e.g. "AcidBubbles.Timeline.178")
                        if (file == null || string.IsNullOrEmpty(file.Filename))
                            continue;

                        if (!TryGetPackageGroup(file, out var pkgGroupName))
                            continue;
                        if (!checkedGroups.Add(pkgGroupName))
                            continue;

                        countedFromFiles = true;
                        if (IsPackageGroupInstalled(pkgGroupName, localVersionsByGroup, localPackageNames, out _))
                            installed++;
                        else
                            missing++;
                    }
                }

                // Keys are often the parent package group (one key → many deps), not dep names.
                // Only fall back to the key when this entry has no usable file list.
                if (countedFromFiles || string.IsNullOrWhiteSpace(kvp.Key))
                    continue;

                var keyName = kvp.Key.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                    ? kvp.Key.Substring(0, kvp.Key.Length - 4)
                    : kvp.Key;
                var groupFromKey = GetPackageGroupName(keyName);
                if (string.IsNullOrEmpty(groupFromKey) || !checkedGroups.Add(groupFromKey))
                    continue;

                if (IsPackageGroupInstalled(groupFromKey, localVersionsByGroup, localPackageNames, out _))
                    installed++;
                else
                    missing++;
            }

            if (installed + missing == 0)
                return DependencyStatusResult.Unresolved;

            return new DependencyStatusResult(installed, missing, resolved: true);
        }

        /// <summary>
        /// Main package .var file(s) only. Dependencies (including N/A) must not affect badges.
        /// </summary>
        internal static List<HubFile> CollectMainPackageFiles(HubResource resource, HubResourceDetail detail)
        {
            IEnumerable<HubFile> source = null;
            if (detail?.HubFiles != null && detail.HubFiles.Count > 0)
                source = detail.HubFiles;
            else if (resource?.HubFiles != null && resource.HubFiles.Count > 0)
                source = resource.HubFiles;

            if (source == null)
                return new List<HubFile>();

            return source
                .Where(f => f != null
                    && !string.IsNullOrEmpty(f.Filename)
                    && f.Filename.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Kept for any callers/tests still using the old name
        internal static List<HubFile> CollectRequiredFiles(HubResource resource, HubResourceDetail detail)
            => CollectMainPackageFiles(resource, detail);

        internal static bool TryGetPackageGroup(HubFile file, out string pkgGroupName)
        {
            pkgGroupName = null;
            if (file == null || string.IsNullOrEmpty(file.Filename))
                return false;

            var resolvedName = ResolveHubFilePackageName(file);
            if (string.IsNullOrEmpty(resolvedName))
                return false;

            pkgGroupName = GetPackageGroupName(resolvedName);
            return !string.IsNullOrEmpty(pkgGroupName);
        }

        internal static bool IsPackageGroupInstalled(
            string pkgGroupName,
            IReadOnlyDictionary<string, HashSet<int>> localVersionsByGroup,
            IReadOnlyCollection<string> localPackageNames,
            out int localVersion)
        {
            localVersion = -1;
            if (string.IsNullOrEmpty(pkgGroupName))
                return false;

            if (localVersionsByGroup != null &&
                localVersionsByGroup.TryGetValue(pkgGroupName, out var versions) &&
                versions != null &&
                versions.Count > 0)
            {
                localVersion = versions.Max();
                return true;
            }

            if (localPackageNames == null)
                return false;

            foreach (var localName in localPackageNames)
            {
                if (string.Equals(GetPackageGroupName(localName), pkgGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    localVersion = ExtractVersionFromPackageName(localName);
                    return true;
                }
            }

            return false;
        }

        private bool IsUpdateAvailableForFile(HubFile file, string pkgGroupName, int localVersion)
        {
            var hubVersion = GetHubFileVersion(file);
            if (hubVersion > 0)
                return hubVersion > localVersion;

            var hubLatest = _hubService.GetLatestVersion(pkgGroupName);
            return hubLatest > localVersion;
        }

        internal static string ResolveHubFilePackageName(HubFile file)
        {
            var filename = file?.Filename;
            if (string.IsNullOrEmpty(filename))
                return null;

            var packageName = filename.Replace(".var", "", StringComparison.OrdinalIgnoreCase);
            if (!packageName.Contains(".latest", StringComparison.OrdinalIgnoreCase))
                return packageName;

            var latestVersion = file.LatestVersion;
            if (string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(file.LatestUrl))
                latestVersion = ExtractVersionFromUrl(file.LatestUrl, filename);
            if (string.IsNullOrEmpty(latestVersion))
            {
                var downloadUrl = file.EffectiveDownloadUrl;
                if (!string.IsNullOrEmpty(downloadUrl) && downloadUrl != "null")
                    latestVersion = ExtractVersionFromUrl(downloadUrl, filename);
            }

            if (string.IsNullOrEmpty(latestVersion))
                return packageName;

            if (packageName.Contains(".latest.", StringComparison.OrdinalIgnoreCase))
                return packageName.Replace(".latest.", $".{latestVersion}.", StringComparison.OrdinalIgnoreCase);

            if (packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                return packageName.Substring(0, packageName.Length - 7) + "." + latestVersion;

            return packageName;
        }

        internal static int GetHubFileVersion(HubFile file)
        {
            if (file == null)
                return -1;

            if (!string.IsNullOrEmpty(file.LatestVersion) && int.TryParse(file.LatestVersion, out var parsedLatest))
                return parsedLatest;

            if (!string.IsNullOrEmpty(file.Version) && int.TryParse(file.Version, out var parsedVersion))
                return parsedVersion;

            if (string.IsNullOrEmpty(file.Filename))
                return -1;

            var name = file.Filename;
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0 && lastDot < name.Length - 1 &&
                int.TryParse(name.Substring(lastDot + 1), out var version))
            {
                return version;
            }

            return -1;
        }

        internal static string GetPackageGroupName(string packageName)
        {
            var name = packageName ?? string.Empty;

            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);

            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0 && int.TryParse(name.Substring(lastDot + 1), out _))
                return name.Substring(0, lastDot);

            return name;
        }

        private static int ExtractVersionFromPackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return -1;

            var name = packageName;
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0 && lastDot < name.Length - 1 &&
                int.TryParse(name.Substring(lastDot + 1), out var version))
            {
                return version;
            }

            return -1;
        }

        private static string ExtractVersionFromUrl(string url, string originalFilename)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            try
            {
                var uri = new Uri(url);
                var urlFilename = System.IO.Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrEmpty(urlFilename))
                    return null;

                urlFilename = urlFilename.Replace(".var", "", StringComparison.OrdinalIgnoreCase);
                var baseName = GetPackageGroupName(originalFilename.Replace(".var", "", StringComparison.OrdinalIgnoreCase));
                if (urlFilename.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    var version = urlFilename.Substring(baseName.Length + 1);
                    if (!string.IsNullOrEmpty(version) && char.IsDigit(version[0]))
                        return version;
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
