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
        private string _gitRef;
        private CancellationTokenSource _cts;
        private VpbPatchCheckResult _check;

        public VpbPatchDetailsWindow(string gameFolder, string gitRef, VpbPatchCheckResult check)
        {
            InitializeComponent();

            _gameFolder = gameFolder ?? throw new ArgumentNullException(nameof(gameFolder));
            _gitRef = string.IsNullOrWhiteSpace(gitRef) ? "main" : gitRef;
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

                RefreshUiFromCheck();
            };
        }

        private void RefreshUiFromCheck()
        {
            if (_check == null)
                return;

            FolderText.Text = _gameFolder;
            GitRefText.Text = _check.GitRef;
            CountsText.Text = $"Patch files: {_check.TotalFiles}   Missing: {_check.MissingFiles}   Outdated: {_check.OutdatedFiles}";

            MissingGrid.ItemsSource = _check.MissingDetails ?? Array.Empty<VpbPatchFileIssue>();
            OutdatedGrid.ItemsSource = _check.OutdatedDetails ?? Array.Empty<VpbPatchFileIssue>();

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
            CancelButton.Content = busy ? "Close" : "Cancel";
        }

        private async Task RefreshCheckAsync()
        {
            using var patcher = new VpbPatcherService();
            _check = await patcher.CheckAsync(_gameFolder, _gitRef, _cts.Token).ConfigureAwait(true);
            _gitRef = _check.GitRef;
            RefreshUiFromCheck();
        }

        private async Task RunInstallOrUpdateAsync()
        {
            var force = ForceReinstallCheckBox.IsChecked == true;
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
                await patcher.InstallOrUpdateAsync(_gameFolder, _gitRef, force, progress, _cts.Token).ConfigureAwait(true);
            }
            finally
            {
                SetBusy(false, string.Empty);
            }

            await RefreshCheckAsync().ConfigureAwait(true);
        }

        private async Task RunUninstallAsync()
        {
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
