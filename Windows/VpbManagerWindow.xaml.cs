using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using VPM.Services;
using VPM.Services.Vpb;

namespace VPM.Windows
{
    public partial class VpbManagerWindow : Window
    {
        private readonly string _gameFolder;
        private readonly ISettingsManager _settingsManager;
        private readonly VpbManagerViewModel _vm = new VpbManagerViewModel();

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private VpbPatchCheckResult _check;
        private VpbReleaseIndex _index = VpbReleaseIndex.Empty;
        private VpbUpdateConfigFile _config;
        private int _localDbSchema;
        private bool _suppressBranchEvent = true;
        private bool _anythingApplied;

        private bool _branchSelectionArmed;

        public bool ChangedInstall => _anythingApplied;

        public VpbManagerWindow(string gameFolder, ISettingsManager settingsManager = null)
        {
            InitializeComponent();

            _gameFolder = gameFolder ?? throw new ArgumentNullException(nameof(gameFolder));
            _settingsManager = settingsManager;

            DataContext = _vm;
            _vm.FolderPath = _gameFolder;

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DarkTitleBarHelper.ApplyIfDark(this);
            }
            catch
            {
            }

            _vm.PropertyChanged += OnViewModelPropertyChanged;

            var view = CollectionViewSource.GetDefaultView(_vm.Versions);
            view.Filter = FilterVersionRow;

            await RefreshAllAsync(forceIndexRefresh: false).ConfigureAwait(true);
            _ = LoadBranchesAsync();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(VpbManagerViewModel.VersionFilter):
                    CollectionViewSource.GetDefaultView(_vm.Versions)?.Refresh();
                    break;

                case nameof(VpbManagerViewModel.SelectedBranch):
                    _ = OnBranchSelectedAsync();
                    break;
            }
        }

        private bool FilterVersionRow(object item)
        {
            var text = _vm.VersionFilter;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (item is not VpbVersionRow row)
                return false;

            return row.Version.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || row.Notes.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || row.Tag.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        // ── Loading ───────────────────────────────────────────────────────────────────────────

        private async Task RefreshAllAsync(bool forceIndexRefresh)
        {
            ResetCancellation();
            var token = _cts.Token;

            SetBusy(true, "Checking VPB…", indeterminate: true);

            try
            {
                _config = VpbUpdateConfigFile.Load(_gameFolder);

                if (!_config.Pinned
                    && _settingsManager?.Settings?.VpbPreferredBranch is { Length: > 0 } preferred
                    && !string.Equals(preferred, _config.Channel, StringComparison.Ordinal))
                {
                    _config.Channel = preferred;
                }

                var effectiveRef = _config.EffectiveRef;

                using var patcher = new VpbPatcherService();

                var indexTask = patcher.GetReleaseIndexAsync(_config.Channel, forceIndexRefresh, token);
                var checkTask = patcher.CheckAsync(_gameFolder, effectiveRef, token);
                var schemaTask = Task.Run(ReadLocalDbSchema, token);

                await Task.WhenAll(indexTask, checkTask, schemaTask).ConfigureAwait(true);

                _index = await indexTask.ConfigureAwait(true) ?? VpbReleaseIndex.Empty;
                _check = await checkTask.ConfigureAwait(true);
                _localDbSchema = await schemaTask.ConfigureAwait(true);

                _vm.Apply(_check, _index, _config, _localDbSchema, VpbPresence.IsVaMRunning());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _check = null;
                _vm.Apply(null, _index, _config, _localDbSchema, VpbPresence.IsVaMRunning());
                _vm.StatusDetail = ex.Message;
                _vm.NoticeKind = VpbStatusKind.Error;
                _vm.NoticeText = ex.Message;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private int ReadLocalDbSchema()
        {
            try
            {
                using var reader = new VpbLocalDbReader(_gameFolder);
                return reader.Schema.SchemaVersion;
            }
            catch
            {
                return 0;
            }
        }

        private async Task LoadBranchesAsync()
        {
            try
            {
                using var patcher = new VpbPatcherService();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var branches = await patcher.GetBranchesAsync(timeout.Token).ConfigureAwait(true);

                _suppressBranchEvent = true;
                try
                {
                    _vm.Branches.Clear();
                    foreach (var branch in branches)
                        _vm.Branches.Add(branch);

                    var current = _config?.Channel ?? VpbUpdateConfigFile.DefaultBranch;
                    if (!_vm.Branches.Contains(current))
                        _vm.Branches.Insert(0, current);

                    _vm.SelectedBranch = current;
                }
                finally
                {
                    _suppressBranchEvent = false;
                }

                ArmBranchSelection();
            }
            catch
            {
                _suppressBranchEvent = true;
                try
                {
                    var current = _config?.Channel ?? VpbUpdateConfigFile.DefaultBranch;
                    if (_vm.Branches.Count == 0)
                        _vm.Branches.Add(current);
                    _vm.SelectedBranch = current;
                }
                finally
                {
                    _suppressBranchEvent = false;
                }

                ArmBranchSelection();
            }
        }

        private void ArmBranchSelection()
        {
            Dispatcher.BeginInvoke(
                new Action(() => _branchSelectionArmed = true),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ── Applying ──────────────────────────────────────────────────────────────────────────

        private async Task ApplyAsync(string gitRef, VpbRelease pinTo, string verb)
        {
            if (!ConfirmVamClosed(verb))
                return;

            ResetCancellation();
            var token = _cts.Token;

            SetBusy(true, $"{verb}…", indeterminate: true);

            try
            {
                using var probe = new VpbPatcherService();
                if (!await probe.RefIsAvailableAsync(gitRef, token).ConfigureAwait(true))
                {
                    CustomMessageBox.Show(
                        $"'{gitRef}' has no patch files on GitHub.\n\n"
                        + "The tag may not have been published yet, or it may have been removed. "
                        + "Nothing was changed — your current build is untouched.",
                        "Build not available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (pinTo != null)
                    _config.Pin(pinTo);
                else
                    _config.Unpin();

                try
                {
                    _config.Save();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(
                        $"Could not write {VpbUpdateConfigFile.FileName}:\n\n{ex.Message}\n\n"
                        + "The files can still be installed, but VPB will update away from this build "
                        + "the next time VaM starts.",
                        "VPB",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                var progress = new Progress<VpbPatcherProgress>(ReportProgress);

                using var patcher = new VpbPatcherService();
                var result = await patcher
                    .InstallOrUpdateAsync(_gameFolder, gitRef, _vm.ForceReinstall, progress, token)
                    .ConfigureAwait(true);

                _anythingApplied = true;

                if (result.FailedFiles is { Count: > 0 })
                    ShowFailureReport(result);
            }
            catch (OperationCanceledException)
            {
                _vm.NoticeKind = VpbStatusKind.Warn;
                _vm.NoticeText = "Stopped. Some files may already have been replaced — check again to see what is left.";
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(
                    $"{verb} failed:\n\n{ex.Message}",
                    "VPB",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
            }

            await RefreshAllAsync(forceIndexRefresh: false).ConfigureAwait(true);
        }

        private void ShowFailureReport(VpbPatchApplyResult result)
        {
            var failed = result.FailedFiles;
            var report = new StringBuilder();
            report.AppendLine("Some patch files could not be written:");
            report.AppendLine();

            var show = Math.Min(failed.Count, 12);
            for (var i = 0; i < show; i++)
                report.AppendLine($"• {failed[i].RelativePath}: {failed[i].ErrorMessage}");
            if (failed.Count > show)
                report.AppendLine($"… and {failed.Count - show} more.");

            report.AppendLine();
            report.AppendLine(
                "This is almost always a locked file: VaM, the Hub, or an antivirus scanner still has it "
                + "open. Close them and apply again — already-written files are skipped.");

            CustomMessageBox.Show(report.ToString(), "VPB — partial failure", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool ConfirmVamClosed(string verb)
        {
            if (!VpbPresence.IsVaMRunning())
                return true;

            var answer = CustomMessageBox.Show(
                $"VaM is running. It keeps the VPB files open, so {verb.ToLowerInvariant()} will fail for "
                + "most of them.\n\nClose VaM first, then try again.\n\nContinue anyway?",
                "VaM is running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes;
        }

        private void ReportProgress(VpbPatcherProgress p)
        {
            if (p == null) return;

            try
            {
                if (p.Total > 0)
                {
                    _vm.ProgressIndeterminate = false;
                    _vm.ProgressMaximum = p.Total;
                    _vm.ProgressValue = p.Index;
                    _vm.ProgressText = string.IsNullOrEmpty(p.RelativePath)
                        ? $"{p.Message}  ({p.Index}/{p.Total})"
                        : $"{p.Message}  {p.RelativePath}  ({p.Index}/{p.Total})";
                }
                else
                {
                    _vm.ProgressIndeterminate = true;
                    _vm.ProgressText = p.Message ?? "";
                }
            }
            catch
            {
            }
        }

        private void SetBusy(bool busy, string message, bool indeterminate = true)
        {
            _vm.Busy = busy;
            _vm.ProgressVisibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (busy)
            {
                _vm.ProgressIndeterminate = indeterminate;
                _vm.ProgressValue = 0;
                _vm.ProgressMaximum = 1;
                _vm.ProgressText = message ?? "";
                _vm.CanPrimaryAction = false;
            }
            else
            {
                _vm.ProgressText = "";
            }

            foreach (var row in _vm.Versions)
                row.IsBusy = busy;
        }

        private void ResetCancellation()
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            _cts = new CancellationTokenSource();
        }

        // ── Handlers ──────────────────────────────────────────────────────────────────────────

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllAsync(forceIndexRefresh: true).ConfigureAwait(true);
        }

        private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (_check == null || _config == null)
                return;

            var verb = _check.Status == VpbPatchStatus.NeedsInstall ? "Installing" : "Applying";

            var pinTo = _config.Pinned ? _index.Find(_config.PinnedVersion) : null;
            if (_config.Pinned && pinTo == null)
            {
                await ApplyKeepingConfigAsync(_config.EffectiveRef, verb).ConfigureAwait(true);
                return;
            }

            await ApplyAsync(_config.EffectiveRef, pinTo, verb).ConfigureAwait(true);
        }

        private async Task ApplyKeepingConfigAsync(string gitRef, string verb)
        {
            if (!ConfirmVamClosed(verb))
                return;

            ResetCancellation();
            SetBusy(true, $"{verb}…");

            try
            {
                using var patcher = new VpbPatcherService();
                var result = await patcher
                    .InstallOrUpdateAsync(_gameFolder, gitRef, _vm.ForceReinstall, new Progress<VpbPatcherProgress>(ReportProgress), _cts.Token)
                    .ConfigureAwait(true);

                _anythingApplied = true;
                if (result.FailedFiles is { Count: > 0 })
                    ShowFailureReport(result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"{verb} failed:\n\n{ex.Message}", "VPB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
            }

            await RefreshAllAsync(forceIndexRefresh: false).ConfigureAwait(true);
        }

        private async void VersionAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VpbVersionRow row })
                return;
            if (_config == null || row.IsBlocked)
                return;

            if (!ConfirmVersionChange(row))
                return;

            var verb = row.IsRollback ? "Rolling back" : "Switching";
            await ApplyAsync(row.Tag, row.Release, verb).ConfigureAwait(true);
        }

        private bool ConfirmVersionChange(VpbVersionRow row)
        {
            if (row.IsInstalled && row.IsPinned)
                return true;

            var message = new StringBuilder();
            message.AppendLine(row.IsRollback
                ? $"Roll back to VPB {row.Version}?"
                : $"Switch to VPB {row.Version}?");
            message.AppendLine();

            if (row.DateText.Length > 0)
                message.AppendLine($"Released {row.DateText}. {row.Notes}");
            else
                message.AppendLine(row.Notes);

            message.AppendLine();
            message.AppendLine(
                $"VPB will stay on {row.Version} and stop updating itself until you return to latest.");

            if (row.HasSchemaWarning)
            {
                message.AppendLine();
                message.AppendLine(row.SchemaWarning);
            }

            message.AppendLine();
            message.AppendLine("Restart VaM afterwards for the change to take effect.");

            return CustomMessageBox.Show(
                message.ToString(),
                row.IsRollback ? "Roll back VPB" : "Switch VPB build",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private async void ReturnToLatest_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null)
                return;

            var channel = _config.Channel;
            var latest = _index?.Latest;
            var target = latest != null ? $" ({latest.Version})" : "";

            var answer = CustomMessageBox.Show(
                $"Stop following the pinned build and track {channel} again{target}?\n\n"
                + "The newest build on that branch will be installed now.",
                "Return to latest",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            await ApplyAsync(channel, null, "Returning to latest").ConfigureAwait(true);
        }

        private async Task OnBranchSelectedAsync()
        {
            if (_suppressBranchEvent || !_branchSelectionArmed || _config == null)
                return;

            var selected = _vm.SelectedBranch;
            if (string.IsNullOrWhiteSpace(selected)
                || string.Equals(selected, _config.Channel, StringComparison.Ordinal))
            {
                return;
            }

            if (_config.Pinned)
            {
                var answer = CustomMessageBox.Show(
                    $"Switching to '{selected}' clears the pin on {_config.PinnedVersion}.\n\n"
                    + "Each branch publishes its own builds, so a pin only means something on the branch "
                    + "that produced it.\n\nContinue?",
                    "Change branch",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    _suppressBranchEvent = true;
                    try { _vm.SelectedBranch = _config.Channel; }
                    finally { _suppressBranchEvent = false; }
                    return;
                }
            }

            _config.SetChannel(selected);
            try
            {
                _config.Save();
            }
            catch
            {
            }

            if (_settingsManager?.Settings != null)
                _settingsManager.Settings.VpbPreferredBranch = selected;

            await RefreshAllAsync(forceIndexRefresh: true).ConfigureAwait(true);
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var answer = CustomMessageBox.Show(
                "Remove the VPB patch from this VaM folder?\n\n"
                + "Files VPB replaced are restored from its backup, and files it added are deleted. "
                + "Your ratings, tags, and index database are not touched.",
                "Uninstall VPB",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;

            if (!ConfirmVamClosed("Uninstalling"))
                return;

            ResetCancellation();
            SetBusy(true, "Uninstalling…");

            try
            {
                using var patcher = new VpbPatcherService();
                await patcher
                    .UninstallAsync(_gameFolder, _config?.EffectiveRef ?? VpbUpdateConfigFile.DefaultBranch,
                        new Progress<VpbPatcherProgress>(ReportProgress), _cts.Token)
                    .ConfigureAwait(true);

                _anythingApplied = true;

                if (_config != null)
                {
                    _config.Unpin();
                    try { _config.Save(); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Uninstall failed:\n\n{ex.Message}", "VPB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
            }

            await RefreshAllAsync(forceIndexRefresh: false).ConfigureAwait(true);
        }

        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null)
                return;

            var answer = CustomMessageBox.Show(
                $"Re-download and rewrite every VPB file for '{_config.EffectiveRef}'?\n\n"
                + "Use this when an install is damaged or a previous apply was interrupted. "
                + "Your pin, ratings, tags, and index database are not affected.",
                "Reinstall current build",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            var previousForce = _vm.ForceReinstall;
            _vm.ForceReinstall = true;
            try
            {
                await ApplyKeepingConfigAsync(_config.EffectiveRef, "Reinstalling").ConfigureAwait(true);
            }
            finally
            {
                _vm.ForceReinstall = previousForce;
            }
        }

        private void CopyReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("VPB report");
                sb.AppendLine($"Folder:    {_gameFolder}");
                sb.AppendLine($"Channel:   {_config?.Channel}");
                sb.AppendLine($"Ref:       {_check?.GitRef ?? _config?.EffectiveRef}");
                sb.AppendLine($"Pinned:    {(_config?.Pinned == true ? _config.PinnedVersion : "no")}");
                sb.AppendLine($"Installed: {_check?.InstalledVersion ?? "not installed"}");
                sb.AppendLine($"Remote:    {_check?.RemoteVersion}");
                sb.AppendLine($"Schema:    remote {_check?.RemoteSchema}, local database {_localDbSchema}");
                sb.AppendLine($"Manifest:  {(_check?.UsedFastPath == true ? "patch_manifest2.json" : "legacy + tree API")}");
                sb.AppendLine($"Files:     {_vm.FileTotal} total, {_vm.FileMissing} missing, {_vm.FileOutdated} outdated, {_vm.FilePatched} ok");
                sb.AppendLine();

                foreach (var row in _vm.Files.Where(f => f.Status != "OK"))
                    sb.AppendLine($"- [{row.Status}] {row.RelativePath} | {row.Reason} | expected={row.ExpectedSha} | local={row.LocalSha}");

                Clipboard.SetText(sb.ToString());
                _vm.NoticeKind = VpbStatusKind.Info;
                _vm.NoticeText = "Report copied to the clipboard.";
            }
            catch
            {
            }
        }

        // ── Advanced panel sizing ─────────────────────────────────────────────────────────────

        private double _heightBeforeExpand;

        private void Advanced_Expanded(object sender, RoutedEventArgs e)
        {
            if (WindowState != WindowState.Normal)
                return;

            _heightBeforeExpand = ActualHeight;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (WindowState != WindowState.Normal || AdvancedContent == null)
                        return;

                    var needed = AdvancedContent.ActualHeight + AdvancedContent.Margin.Top;
                    if (needed <= 0)
                        return;

                    var available = SystemParameters.WorkArea.Height;
                    var target = Math.Min(ActualHeight + needed, available);

                    if (target > ActualHeight + 1)
                    {
                        Height = target;

                        // Keep the grown window on screen rather than hanging off the bottom.
                        var bottom = Top + target;
                        if (bottom > SystemParameters.WorkArea.Bottom)
                            Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - target);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Advanced_Collapsed(object sender, RoutedEventArgs e)
        {
            if (WindowState != WindowState.Normal)
                return;

            if (_heightBeforeExpand > 0 && _heightBeforeExpand < ActualHeight)
                Height = _heightBeforeExpand;
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_vm.Busy)
            {
                Close();
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

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
