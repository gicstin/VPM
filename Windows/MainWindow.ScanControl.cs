using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VPM.Models;
using VPM.Services;
using VPM.Services.Vpb;

namespace VPM
{
    public partial class MainWindow
    {
        private ScanControlService _scanControl;

        private void EnsureScanControl()
        {
            var root = _settingsManager?.Settings?.SelectedFolder ?? _selectedFolder;
            if (string.IsNullOrEmpty(root) || _packageManager == null)
                return;
            if (_scanControl == null)
                _scanControl = new ScanControlService(root, _settingsManager.Settings, _packageManager, _packageFileManager);
            else
                _scanControl.UpdateVamRoot(root);
        }

        private void RefreshScanStatusChrome()
        {
            EnsureScanControl();
            var whitelist = _scanControl?.IsWhitelistMode == true;
            var vamRunning = _scanControl?.IsVaMRunning == true;

            if (StatusScanModeText != null)
            {
                if (!whitelist)
                    StatusScanModeText.Text = "Scan: File-move";
                else if (_scanControl.Whitelist?.Enabled == true)
                    StatusScanModeText.Text = "Scan: Whitelist";
                else
                    StatusScanModeText.Text = "Scan: Off";
            }

            if (StatusScanApplyText != null)
            {
                StatusScanApplyText.Visibility = whitelist && vamRunning ? Visibility.Visible : Visibility.Collapsed;
                StatusScanApplyText.Text = "Applies next launch";
            }

            if (StatusScanBudgetText != null)
            {
                if (whitelist)
                {
                    var budget = _scanControl.ComputeBudget();
                    StatusScanBudgetText.Text = budget.PackageCount > 0 ? budget.Format() : "";
                    StatusScanBudgetText.Visibility = string.IsNullOrEmpty(StatusScanBudgetText.Text)
                        ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    StatusScanBudgetText.Text = "";
                    StatusScanBudgetText.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async Task<bool> EnsureWhitelistLibraryAsync()
        {
            EnsureScanControl();
            if (_scanControl == null || !_scanControl.IsWhitelistMode)
                return false;

            _scanControl.EnsureSafeWhitelistFile();

            if (_settingsManager.Settings.WhitelistLibraryConsolidated)
            {
                _scanControl.ApplyStatuses();
                RefreshScanStatusChrome();
                return false;
            }

            var parked = _packageManager.PackageMetadata.Values.Count(m =>
                m?.FilePath != null && m.FilePath.Replace('\\', '/').Contains("AllPackages/", StringComparison.OrdinalIgnoreCase)
                && m.Status != "Archived");
            if (parked == 0)
            {
                _settingsManager.Settings.WhitelistLibraryConsolidated = true;
                _scanControl.ApplyStatuses();
                RefreshScanStatusChrome();
                return false;
            }

            var confirm = DarkMessageBox.Show(
                $"Whitelist mode keeps packages in AddonPackages.\n\nMove {parked:N0} package(s) from AllPackages into AddonPackages now? Required once.",
                "Consolidate library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                _settingsManager.Settings.ScanControlMode = ScanControlMode.FileMove;
                RefreshScanStatusChrome();
                return false;
            }

            ShowMainTableLoading("Moving packages into AddonPackages...", parked);
            try
            {
                var progress = new Progress<(int done, int total, string name)>(p =>
                {
                    UpdateMainTableLoading(p.done, p.total, p.name);
                    SetStatus($"Consolidating {p.done}/{p.total}...");
                });
                await _scanControl.ConsolidateAllPackagesAsync(progress);
                _settingsManager.Settings.WhitelistLibraryConsolidated = true;
                _packageFileManager?.InvalidatePackageIndex();
            }
            finally
            {
                HideMainTableLoading();
            }
            return true;
        }

        private bool TryWhitelistInclude(IEnumerable<PackageItem> packages, bool withDeps, bool exclusive = false)
        {
            EnsureScanControl();
            if (_scanControl?.IsWhitelistMode != true) return false;
            var keys = packages.Select(p => p.MetadataKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
            try
            {
                _scanControl.Include(keys, exclusive, withDeps);
                ApplyWhitelistStatusesToUi();
                SetStatus(withDeps ? "Added to scan set (+ deps)" : "Added to scan set");
                return true;
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show(ex.Message, "Scan set", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
        }

        private bool TryWhitelistExclude(IEnumerable<PackageItem> packages)
        {
            EnsureScanControl();
            if (_scanControl?.IsWhitelistMode != true) return false;
            var keys = packages.Select(p => p.MetadataKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
            try
            {
                _scanControl.Exclude(keys);
                ApplyWhitelistStatusesToUi();
                SetStatus("Removed from scan set");
                return true;
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show(ex.Message, "Scan set", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
        }

        private void ApplyWhitelistStatusesToUi()
        {
            _scanControl?.ApplyStatuses();
            if (_packageManager?.PackageMetadata == null) return;
            foreach (var pkg in Packages)
            {
                if (pkg == null) continue;
                if (_packageManager.PackageMetadata.TryGetValue(pkg.MetadataKey, out var meta) && meta != null)
                    pkg.Status = meta.Status;
            }
            RefreshDependencyRowStatuses();
            RefreshScanStatusChrome();
            UpdatePackageButtonBar();
            PopulateStatusFilterList();
        }

        private string ResolveDependencyMetadataKey(DependencyItem d)
        {
            if (d == null || string.IsNullOrEmpty(d.Name))
                return null;
            if (string.Equals(d.Name, "No dependencies", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Name, "No dependents", StringComparison.OrdinalIgnoreCase))
                return null;
            if (d.Status?.StartsWith("#") == true)
                return null;

            var meta = _packageManager?.PackageMetadata;
            if (meta == null) return null;

            if (!string.IsNullOrEmpty(d.DisplayName) && meta.ContainsKey(d.DisplayName))
                return d.DisplayName;
            if (meta.ContainsKey(d.Name))
                return d.Name;

            return meta
                .Where(kvp =>
                {
                    var uid = $"{kvp.Value.CreatorName}.{kvp.Value.PackageName}";
                    return string.Equals(uid, d.Name, StringComparison.OrdinalIgnoreCase)
                        || kvp.Key.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(kvp => kvp.Value.Version)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();
        }

        private void RefreshDependencyRowStatuses()
        {
            if (Dependencies == null || _packageManager?.PackageMetadata == null) return;
            foreach (var d in Dependencies)
            {
                if (d.Status?.StartsWith("#") == true) continue;
                var lookup = !string.IsNullOrEmpty(d.DisplayName) ? d.DisplayName : d.Name;
                if (_scanControl?.IsWhitelistMode == true)
                {
                    d.Status = _scanControl.ResolveDisplayStatus(lookup);
                    continue;
                }
                var key = ResolveDependencyMetadataKey(d);
                if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out var meta) && meta != null)
                    d.Status = meta.Status;
            }
            UpdateDependenciesButtonBar();
        }

        private bool TryWhitelistDependencies(IEnumerable<DependencyItem> deps, bool include)
        {
            EnsureScanControl();
            if (_scanControl?.IsWhitelistMode != true) return false;
            var keys = deps
                .Select(ResolveDependencyMetadataKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count == 0) return false;
            try
            {
                if (include)
                    _scanControl.Include(keys, exclusive: false, withDeps: false);
                else
                    _scanControl.Exclude(keys);
                ApplyWhitelistStatusesToUi();
                SetStatus(include
                    ? $"Added {keys.Count} package(s) to scan set"
                    : $"Removed {keys.Count} package(s) from scan set");
                return true;
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show(ex.Message, "Scan set", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
        }

        private HashSet<string> LoadVpbProtectedUids()
        {
            var set = VpbLockedPackagesStore.Load(_selectedFolder);
            try
            {
                using var db = new VpbLocalDbReader(_selectedFolder);
                foreach (var uid in db.LoadCleanupExclude())
                    set.Add(uid);
            }
            catch { }
            return set;
        }

        private List<T> FilterProtected<T>(IEnumerable<T> items, Func<T, string> uidOf, out int skipped)
        {
            var protectedUids = LoadVpbProtectedUids();
            skipped = 0;
            var kept = new List<T>();
            foreach (var item in items)
            {
                var uid = uidOf(item);
                if (!string.IsNullOrEmpty(uid) && protectedUids.Contains(uid))
                {
                    skipped++;
                    continue;
                }
                kept.Add(item);
            }
            return kept;
        }

        private void StatusScanModeText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (StatusScanModeText?.ContextMenu == null) return;
            StatusScanModeText.ContextMenu.PlacementTarget = StatusScanModeText;
            StatusScanModeText.ContextMenu.IsOpen = true;
        }

        private void SetScanControlMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse<ScanControlMode>(tag, out var mode)) return;
            _settingsManager.Settings.ScanControlMode = mode;
            if (mode != ScanControlMode.FileMove)
                _settingsManager.Settings.WhitelistLibraryConsolidated = false;
            EnsureScanControl();
            RefreshScanStatusChrome();
            _ = EnsureWhitelistLibraryAsync();
            ApplyWhitelistStatusesToUi();
        }

        private void PromoteOnDemandHits_Click(object sender, RoutedEventArgs e)
        {
            EnsureScanControl();
            if (_scanControl?.IsWhitelistMode != true)
            {
                DarkMessageBox.Show("Promote needs whitelist mode (VPB installed).", "Promote", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Dictionary<string, PackageUsageRow> usage;
            try
            {
                using var db = new VpbLocalDbReader(_selectedFolder);
                usage = db.LoadUsage();
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show("Could not read VPB database: " + ex.Message, "Promote", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var hot = usage.Values
                .Where(r => r.OnDemandHits > 0 && !_scanControl.IsInScanSet(r.Uid,
                    _packageManager.PackageMetadata.TryGetValue(r.Uid, out var m) ? m : null))
                .OrderByDescending(r => r.OnDemandHits)
                .Take(80)
                .ToList();

            if (hot.Count == 0)
            {
                DarkMessageBox.Show("No on-demand hits outside the scan set.", "Promote", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preview = string.Join("\n", hot.Take(12).Select(r => $"{r.Uid}  ×{r.OnDemandHits}"));
            if (hot.Count > 12) preview += $"\n… and {hot.Count - 12} more";
            var result = DarkMessageBox.Show(
                $"Promote {hot.Count} frequently on-demand package(s) into the scan set?\n\n{preview}",
                "Promote to scan set",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _scanControl.Include(hot.Select(r => r.Uid), exclusive: false, withDeps: false);
            ApplyWhitelistStatusesToUi();
        }

        private void IncludeSceneInScan_Click(object sender, RoutedEventArgs e)
        {
            EnsureScanControl();
            if (_scanControl?.IsWhitelistMode != true)
            {
                DarkMessageBox.Show("Scene include needs whitelist mode (VPB installed).", "Scan set", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ScenesDataGrid?.SelectedItem is not SceneItem scene)
            {
                DarkMessageBox.Show("Select a scene first.", "Scan set", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var key = scene.SourcePackage;
            if (string.IsNullOrWhiteSpace(key))
                key = Path.GetFileNameWithoutExtension(scene.FilePath);
            if (string.IsNullOrWhiteSpace(key))
            {
                DarkMessageBox.Show("Could not resolve the scene's package UID.", "Scan set", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                _scanControl.Include(new[] { key }, exclusive: false, withDeps: true);
                ApplyWhitelistStatusesToUi();
                SetStatus($"Included scene + deps in scan set ({key})");
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show(ex.Message, "Scan set", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadSceneInVaM_Click(object sender, RoutedEventArgs e)
        {
            if (ScenesDataGrid?.SelectedItem is not SceneItem scene)
            {
                DarkMessageBox.Show("Select a scene first.", "Load in VaM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var path = scene.FilePath;
            if (string.IsNullOrEmpty(path)) path = scene.Name;
            if (!VpbCompanionClient.TryLoadScene(path))
                DarkMessageBox.Show("VaM not connected. Scene load needs a running VaM with VPB.", "Load in VaM", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                SetStatus("Asked VaM to load scene");
        }
    }
}
