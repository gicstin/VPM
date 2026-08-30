using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services.Vpb
{
    public sealed class ScanControlService
    {
        public static ScanControlService Current { get; private set; }

        private readonly PackageManager _packageManager;
        private readonly PackageFileManager _packageFileManager;
        private readonly AppSettings _settings;
        private string _vamRoot;
        private ScanWhitelistData _whitelist = new() { Enabled = false };
        private ScanWhitelistIndex _index = ScanWhitelistIndex.From(null);

        public ScanControlService(string vamRoot, AppSettings settings, PackageManager packageManager, PackageFileManager packageFileManager)
        {
            _vamRoot = vamRoot;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _packageManager = packageManager ?? throw new ArgumentNullException(nameof(packageManager));
            _packageFileManager = packageFileManager;
            Current = this;
            Reload();
        }

        public string VamRoot => _vamRoot;

        public ScanControlMode ConfiguredMode => _settings.ScanControlMode;

        public bool IsWhitelistMode
        {
            get
            {
                return ConfiguredMode switch
                {
                    ScanControlMode.Whitelist => VpbPresence.IsPluginInstalled(_vamRoot),
                    ScanControlMode.FileMove => false,
                    _ => VpbPresence.IsPluginInstalled(_vamRoot)
                };
            }
        }

        public bool IsVaMRunning => VpbPresence.IsVaMRunning();

        public ScanWhitelistData Whitelist => _whitelist;

        /// <summary>Hash-backed view of <see cref="Whitelist"/>, rebuilt on every mutation.</summary>
        public ScanWhitelistIndex Index => _index;

        private void SetWhitelist(ScanWhitelistData data)
        {
            _whitelist = data ?? new ScanWhitelistData { Enabled = false };
            _index = ScanWhitelistIndex.From(_whitelist);
        }

        public void Reload()
        {
            if (string.IsNullOrEmpty(_vamRoot) || !Directory.Exists(_vamRoot))
            {
                SetWhitelist(new ScanWhitelistData { Enabled = false });
                return;
            }
            SetWhitelist(ScanWhitelistStore.Load(_vamRoot));
        }

        /// <summary>True when the whitelist actually narrows VaM's boot scan. A disabled whitelist does not — VaM still scans all of AddonPackages, so treating disabled as empty would mark the whole library on-demand.</summary>
        private bool IsNarrowingScan => IsWhitelistMode && _whitelist?.Enabled == true;

        public void ApplyStatuses()
        {
            if (_packageManager?.PackageMetadata == null) return;
            if (!IsWhitelistMode)
                return;

            bool narrowing = IsNarrowingScan;

            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var meta = kvp.Value;
                if (meta == null || meta.IsExternal || meta.IsCorrupted) continue;
                var status = meta.Status;
                if (status is "Archived" or "Missing" or "Unknown") continue;
                if (IsAllPackagesPath(meta.FilePath))
                {
                    meta.Status = "Available";
                    continue;
                }
                if (!IsAddonPath(meta.FilePath)) continue;

                var rel = RelativeVarPath(meta.FilePath);
                meta.Status = !narrowing || _index.Contains(rel, kvp.Key)
                    ? "Loaded"
                    : "Available";
            }
        }

        public ScanBudget ComputeBudget()
        {
            int pkgs = 0, files = 0;
            long bytes = 0;
            if (_packageManager?.PackageMetadata == null) return new ScanBudget();
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var meta = kvp.Value;
                if (meta == null) continue;
                if (!IsInScanSet(kvp.Key, meta)) continue;
                pkgs++;
                bytes += meta.FileSize;
                files += meta.FileCount;
            }
            return new ScanBudget { PackageCount = pkgs, TotalBytes = bytes, FileCount = files };
        }

        public bool IsInScanSet(string uid, VarMetadata meta)
        {
            if (!IsWhitelistMode)
                return string.Equals(meta?.Status, "Loaded", StringComparison.OrdinalIgnoreCase);
            if (meta == null) return false;
            // Parked in AllPackages is out of the scan set whatever the whitelist says.
            if (IsAllPackagesPath(meta.FilePath)) return false;
            if (!IsNarrowingScan) return IsAddonPath(meta.FilePath);
            return _index.Contains(RelativeVarPath(meta.FilePath), uid);
        }

        public string ResolveDisplayStatus(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return "Unknown";

            if (!IsWhitelistMode)
                return _packageFileManager?.GetPackageStatus(packageName) ?? "Unknown";

            var fs = _packageFileManager?.GetPackageStatus(packageName) ?? "Unknown";
            if (!string.IsNullOrEmpty(fs) && fs.StartsWith("#", StringComparison.Ordinal))
                return fs;
            if (fs is "Missing" or "Unknown" or "Archived")
                return fs;

            var key = ResolveMetadataKey(packageName);
            if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out var meta) && meta != null)
            {
                if (meta.IsExternal && !string.IsNullOrEmpty(meta.ExternalDestinationColorHex))
                    return meta.ExternalDestinationColorHex;
                if (meta.Status is "Archived" or "Missing" or "Unknown")
                    return meta.Status;
                return IsInScanSet(key, meta) ? "Loaded" : "Available";
            }

            return string.Equals(fs, "Loaded", StringComparison.OrdinalIgnoreCase) ? "Available" : fs;
        }

        private string ResolveMetadataKey(string packageName)
        {
            var dict = _packageManager?.PackageMetadata;
            if (dict == null) return null;
            if (dict.ContainsKey(packageName)) return packageName;

            var info = DependencyVersionInfo.Parse(packageName);
            var baseName = string.IsNullOrEmpty(info.BaseName) ? packageName : info.BaseName;

            string bestKey = null;
            var bestVersion = int.MinValue;
            foreach (var kvp in dict)
            {
                var m = kvp.Value;
                if (m == null) continue;
                var uidBase = $"{m.CreatorName}.{m.PackageName}";
                if (!string.Equals(uidBase, baseName, StringComparison.OrdinalIgnoreCase)
                    && !kvp.Key.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!info.IsSatisfiedBy(m.Version))
                    continue;
                if (m.Version < bestVersion) continue;
                bestVersion = m.Version;
                bestKey = kvp.Key;
            }
            return bestKey;
        }

        public void Commit(ScanWhitelistData data, bool allowEnabledEmpty = false)
        {
            ScanWhitelistStore.Save(_vamRoot, data, allowEnabledEmpty);
            SetWhitelist(data);
            ApplyStatuses();
            VpbCompanionClient.TryReloadWhitelist();
        }

        public void EnsureSafeWhitelistFile()
        {
            var path = VpbPaths.ScanWhitelistPath(_vamRoot);
            if (File.Exists(path)) return;
            Commit(new ScanWhitelistData
            {
                SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                Enabled = false
            });
        }

        public async Task<int> ConsolidateAllPackagesAsync(IProgress<(int done, int total, string name)> progress)
        {
            if (_packageFileManager == null || _packageManager?.PackageMetadata == null)
                return 0;

            var toLoad = _packageManager.PackageMetadata
                .Where(kvp => IsAllPackagesPath(kvp.Value?.FilePath) && kvp.Value.Status != "Archived")
                .Select(kvp => kvp.Key)
                .ToList();

            int done = 0;
            foreach (var key in toLoad)
            {
                progress?.Report((done, toLoad.Count, key));
                await _packageFileManager.LoadPackageAsync(key);
                done++;
                progress?.Report((done, toLoad.Count, key));
            }
            return done;
        }

        public void ActivateCompiled(ScanWhitelistData compiled, bool allowEnabledEmpty = false)
        {
            Commit(compiled, allowEnabledEmpty);
        }

        public void Include(IEnumerable<string> keys, bool exclusive, bool withDeps)
        {
            var list = keys?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? new List<string>();

            // Compile resolves the closure itself, so the graph is what decides whether dependencies come along. Handing it one regardless would ignore withDeps.
            var compiled = WhitelistCompiler.Compile(
                list,
                withDeps ? _packageManager.DependencyGraph : null,
                _packageManager.PackageMetadata,
                _vamRoot,
                _whitelist,
                exclusive);
            Commit(compiled);
        }

        public void Exclude(IEnumerable<string> keys)
        {
            var compiled = WhitelistCompiler.ExcludeUids(
                keys,
                _packageManager.PackageMetadata,
                _vamRoot,
                _whitelist);
            if (compiled.IsEnabledEmpty)
                throw new InvalidOperationException("Exclude would empty the scan set. Activate a playlist or include packages first.");
            Commit(compiled);
        }

        public void UpdateVamRoot(string vamRoot)
        {
            _vamRoot = vamRoot;
            Reload();
        }

        private string RelativeVarPath(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(_vamRoot))
                return path?.Replace('\\', '/');
            try
            {
                var root = Path.GetFullPath(_vamRoot).TrimEnd('\\', '/');
                var full = Path.GetFullPath(path);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch { }
            return path.Replace('\\', '/');
        }

        private static bool IsAllPackagesPath(string path) =>
            !string.IsNullOrEmpty(path) && path.Replace('\\', '/').Contains("AllPackages/", StringComparison.OrdinalIgnoreCase);

        private static bool IsAddonPath(string path) =>
            !string.IsNullOrEmpty(path) && path.Replace('\\', '/').Contains("AddonPackages/", StringComparison.OrdinalIgnoreCase);
    }
}
