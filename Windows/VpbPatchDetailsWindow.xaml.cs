using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VPM.Services;

namespace VPM.Windows
{
    public partial class VpbPatchDetailsWindow : Window
    {
        private readonly string _gameFolder;
        private readonly ISettingsManager _settingsManager;
        private string _gitRef;
        private CancellationTokenSource _cts;
        private VpbPatchCheckResult _check;
        private bool _runCompleted;
        private bool _suppressBranchEvent;

        public VpbPatchDetailsWindow(string gameFolder, string gitRef, VpbPatchCheckResult check, ISettingsManager settingsManager = null)
        {
            InitializeComponent();

            _gameFolder = gameFolder ?? throw new ArgumentNullException(nameof(gameFolder));
            _settingsManager = settingsManager;
            _gitRef = _settingsManager?.Settings?.VpbPreferredBranch is { Length: > 0 } saved
                ? saved
                : (string.IsNullOrWhiteSpace(gitRef) ? "main" : gitRef);
            _check = check;
            _cts = new CancellationTokenSource();

            Loaded += (s, e) =>
            {
                try
                {
                    DarkTitleBarHelper.ApplyIfDark(this);
                }
                catch
                {
                }

                // Pre-populate with current branch immediately so the ComboBox is never blank
                BranchComboBox.ItemsSource = new[] { _gitRef };
                BranchComboBox.SelectedIndex = 0;

                RefreshUiFromCheck();
                _ = LoadBranchesAsync();
            };
        }

        private void RefreshUiFromCheck()
        {
            if (_check == null)
                return;

            FolderText.Text = _gameFolder;
            _suppressBranchEvent = true;
            if (BranchComboBox.SelectedItem as string != _gitRef)
                BranchComboBox.SelectedItem = _gitRef;
            _suppressBranchEvent = false;
            CountsText.Text = $"Patch files: {_check.TotalFiles}   Missing: {_check.MissingFiles}   Outdated: {_check.OutdatedFiles}   Patched: {_check.PatchedFiles}";

            MissingGrid.ItemsSource = _check.MissingDetails ?? Array.Empty<VpbPatchFileIssue>();
            OutdatedGrid.ItemsSource = _check.OutdatedDetails ?? Array.Empty<VpbPatchFileIssue>();
            PatchedGrid.ItemsSource = _check.PatchedDetails ?? Array.Empty<VpbPatchFileIssue>();

            MissingTab.Header = $"Missing ({_check.MissingFiles})";
            OutdatedTab.Header = $"Outdated ({_check.OutdatedFiles})";
            PatchedTab.Header = $"Patched ({_check.PatchedFiles})";

            var patchStatus = "Not installed";
            if (_check.Status == VpbPatchStatus.UpToDate)
                patchStatus = "Installed";
            else if (_check.Status == VpbPatchStatus.NeedsUpdate)
                patchStatus = "Outdated";

            PatchStatusText.Text = patchStatus;

            if (_check.Status == VpbPatchStatus.NeedsInstall)
            {
                PrimaryActionButton.Content = "Install Patch";
                PrimaryActionButton.IsEnabled = true;
                UninstallButton.Visibility = Visibility.Collapsed;
                ForceReinstallCheckBox.Visibility = Visibility.Visible;
            }
            else if (_check.Status == VpbPatchStatus.NeedsUpdate)
            {
                PrimaryActionButton.Content = "Update Patch";
                PrimaryActionButton.IsEnabled = true;
                UninstallButton.Visibility = Visibility.Collapsed;
                ForceReinstallCheckBox.Visibility = Visibility.Visible;
            }
            else
            {
                PrimaryActionButton.Content = "Update Patch";
                PrimaryActionButton.IsEnabled = false;
                UninstallButton.Visibility = Visibility.Visible;
                ForceReinstallCheckBox.Visibility = Visibility.Collapsed;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            Close();
        }

        private void SetBusy(bool busy, string message)
        {
            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = busy;
            ProgressText.Text = message ?? string.Empty;

            PrimaryActionButton.IsEnabled = !busy && _check != null && _check.Status != VpbPatchStatus.UpToDate;
            UninstallButton.IsEnabled = !busy;
            CancelButton.Content = busy || _runCompleted ? "Close" : "Cancel";
        }

        private async Task RefreshCheckAsync()
        {
            using var patcher = new VpbPatcherService();
            _check = await patcher.CheckAsync(_gameFolder, _gitRef, _cts.Token).ConfigureAwait(true);
            _gitRef = _check.GitRef;
            RefreshUiFromCheck();
        }

        private async Task LoadBranchesAsync()
        {
            try
            {
                using var patcher = new VpbPatcherService();
                // Use a separate token so disposing _cts on branch change doesn't cancel this
                using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var branches = await patcher.GetBranchesAsync(loadCts.Token).ConfigureAwait(true);

                _suppressBranchEvent = true;
                bool branchWasRemoved = false;
                try
                {
                    BranchComboBox.ItemsSource = branches;
                    BranchComboBox.SelectedItem = _gitRef;

                    // Saved branch no longer exists on the remote — fall back to "main" or first available
                    if (BranchComboBox.SelectedItem == null && branches.Count > 0)
                    {
                        var fallback = branches.Contains("main") ? "main" : branches[0];
                        _gitRef = fallback;
                        if (_settingsManager?.Settings != null)
                            _settingsManager.Settings.VpbPreferredBranch = fallback;
                        BranchComboBox.SelectedItem = fallback;
                        branchWasRemoved = true;
                    }
                }
                finally
                {
                    _suppressBranchEvent = false;
                }

                if (branchWasRemoved)
                    await RefreshCheckAsync().ConfigureAwait(true);
            }
            catch
            {
                // Branch list is optional — silently ignore failures
            }
        }

        private async void BranchComboBox_DropDownClosed(object sender, EventArgs e)
        {
            await ApplyBranchSelectionAsync().ConfigureAwait(true);
        }

        private async void BranchComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Only handle scroll-wheel changes (when dropdown is not open)
            if (BranchComboBox.IsDropDownOpen)
                return;
            await ApplyBranchSelectionAsync().ConfigureAwait(true);
        }

        private async Task ApplyBranchSelectionAsync()
        {
            if (_suppressBranchEvent)
                return;

            var selected = BranchComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected) || selected == _gitRef)
                return;

            // Save previous state so we can revert if the check fails
            var previousGitRef = _gitRef;
            _gitRef = selected;

            if (_settingsManager?.Settings != null)
                _settingsManager.Settings.VpbPreferredBranch = selected;

            // Cancel any in-flight check before disposing — without Cancel() first,
            // the old operation can complete after us and overwrite _check with stale data
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch { }

            _cts = new CancellationTokenSource();

            SetBusy(true, $"Checking branch '{selected}'...");
            try
            {
                await RefreshCheckAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Triggered by a newer branch selection or window close — don't revert
            }
            catch (Exception ex)
            {
                // Check failed — revert to the previous branch so UI stays consistent
                _gitRef = previousGitRef;
                if (_settingsManager?.Settings != null)
                    _settingsManager.Settings.VpbPreferredBranch = previousGitRef;
                _suppressBranchEvent = true;
                try { BranchComboBox.SelectedItem = previousGitRef; }
                finally { _suppressBranchEvent = false; }

                CustomMessageBox.Show(
                    $"Failed to check branch '{selected}':\n\n{ex.Message}",
                    "VPB Patch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, string.Empty);
            }
        }

        private async Task RunInstallOrUpdateAsync()
        {
            var force = ForceReinstallCheckBox.IsChecked == true;
            _runCompleted = false;
            SetBusy(true, "Applying patch...");

            var progress = new Progress<VpbPatcherProgress>(p =>
            {
                try
                {
                    ProgressText.Text = $"{p.Message}: {p.RelativePath} ({p.Index}/{p.Total})";
                }
                catch
                {
                }
            });

            try
            {
                using var patcher = new VpbPatcherService();
                var applyResult = await patcher.InstallOrUpdateAsync(_gameFolder, _gitRef, force, progress, _cts.Token).ConfigureAwait(true);
                if (applyResult.FailedFiles is { Count: > 0 } failed)
                {
                    var report = new StringBuilder();
                    report.AppendLine("Some patch files could not be updated:");
                    report.AppendLine();
                    var show = Math.Min(failed.Count, 12);
                    for (var i = 0; i < show; i++)
                        report.AppendLine($"• {failed[i].RelativePath}: {failed[i].ErrorMessage}");
                    if (failed.Count > show)
                        report.AppendLine($"... and {failed.Count - show} more.");
                    report.AppendLine();
                    report.AppendLine("Common causes: VaM or a plugin still has the file open (close the game and Hub), or antivirus locking the folder.");
                    CustomMessageBox.Show(
                        report.ToString(),
                        "VPB Patch — Partial failure",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                _runCompleted = true;
                SetBusy(false, string.Empty);
            }

            await RefreshCheckAsync().ConfigureAwait(true);
        }

        private async Task RunUninstallAsync()
        {
            _runCompleted = false;
            SetBusy(true, "Uninstalling patch...");

            var progress = new Progress<VpbPatcherProgress>(p =>
            {
                try
                {
                    ProgressText.Text = $"{p.Message}: {p.RelativePath} ({p.Index}/{p.Total})";
                }
                catch
                {
                }
            });

            try
            {
                using var patcher = new VpbPatcherService();
                await patcher.UninstallAsync(_gameFolder, _gitRef, progress, _cts.Token).ConfigureAwait(true);
            }
            finally
            {
                _runCompleted = true;
                SetBusy(false, string.Empty);
            }

            await RefreshCheckAsync().ConfigureAwait(true);
        }

        private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (_check == null)
                return;

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            _cts = new CancellationTokenSource();

            try
            {
                await RunInstallOrUpdateAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(
                    $"VPB patch failed:\n\n{ex.Message}",
                    "VPB Patch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var confirm = CustomMessageBox.Show(
                "This will remove VPB patch files from your VaM folder. Continue?",
                "Uninstall VPB Patch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            _cts = new CancellationTokenSource();

            try
            {
                await RunUninstallAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(
                    $"VPB uninstall failed:\n\n{ex.Message}",
                    "VPB Patch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CopyReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("VPB Patch Report");
                sb.AppendLine($"Folder: {_gameFolder}");
                sb.AppendLine($"Git Ref: {_gitRef}");
                sb.AppendLine($"Patch Status: {PatchStatusText.Text}");
                sb.AppendLine($"Counts: {CountsText.Text}");
                sb.AppendLine();

                sb.AppendLine("Missing:");
                foreach (var item in (_check?.MissingDetails ?? Array.Empty<VpbPatchFileIssue>()))
                {
                    sb.AppendLine($"- {item.RelativePath} | required={item.IsRequired} | dir={item.IsDirectory} | reason={item.Reason} | expected={item.ExpectedSha}");
                }

                sb.AppendLine();
                sb.AppendLine("Outdated:");
                foreach (var item in (_check?.OutdatedDetails ?? Array.Empty<VpbPatchFileIssue>()))
                {
                    sb.AppendLine($"- {item.RelativePath} | required={item.IsRequired} | reason={item.Reason} | expected={item.ExpectedSha} | local={item.LocalSha}");
                }

                Clipboard.SetText(sb.ToString());
            }
            catch
            {
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            base.OnClosed(e);
        }
    }
}
