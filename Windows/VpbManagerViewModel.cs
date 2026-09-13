using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VPM.Services;
using VPM.Services.Vpb;

namespace VPM.Windows
{
    public enum VpbStatusKind
    {
        Ok,
        Info,
        Warn,
        Error
    }

    public sealed class VpbVersionRow : ViewModelBase
    {
        private bool _isInstalled;
        private bool _isPinned;
        private bool _isBusy;

        public VpbVersionRow(VpbRelease release, int position)
        {
            Release = release ?? throw new ArgumentNullException(nameof(release));
            Position = position;
        }

        public VpbRelease Release { get; }

        public int Position { get; }

        public string Version => Release.Version;
        public string Tag => Release.Tag;
        public int Schema => Release.Schema;

        public string Notes =>
            string.IsNullOrWhiteSpace(Release.Notes) ? "No release notes." : Release.Notes.Trim();

        public string DateText
        {
            get
            {
                var released = Release.Released;
                return released == null
                    ? ""
                    : released.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            }
        }

        public string AgeText
        {
            get
            {
                var days = Release.AgeDays;
                if (days < 0) return "";
                if (days == 0) return "today";
                if (days == 1) return "yesterday";
                if (days < 30) return $"{days}d ago";
                if (days < 365) return $"{days / 30}mo ago";
                return $"{days / 365}y ago";
            }
        }

        public string MetaText
        {
            get
            {
                var parts = new List<string>(3);
                if (DateText.Length > 0) parts.Add(DateText);
                if (AgeText.Length > 0) parts.Add(AgeText);
                if (Release.ShortCommit.Length > 0) parts.Add(Release.ShortCommit);
                return string.Join("  ·  ", parts);
            }
        }

        public bool IsLatest => Position == 0;

        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (SetProperty(ref _isInstalled, value))
                    RaiseDerived();
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (SetProperty(ref _isPinned, value))
                    RaiseDerived();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                    OnPropertyChanged(nameof(CanApply));
            }
        }

        public string BlockedReason { get; set; }

        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);

        public string SchemaWarning { get; set; }

        public bool HasSchemaWarning => !string.IsNullOrEmpty(SchemaWarning);

        public Visibility SchemaWarningVisibility =>
            HasSchemaWarning ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRollback { get; set; }

        public string BadgeText
        {
            get
            {
                if (IsPinned && IsInstalled) return "PINNED · RUNNING";
                if (IsPinned) return "PINNED";
                if (IsInstalled) return "INSTALLED";
                if (IsLatest) return "LATEST";
                return "";
            }
        }

        public Visibility BadgeVisibility =>
            BadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Brush BadgeBrush
        {
            get
            {
                if (IsPinned) return VpbPalette.Info;
                if (IsInstalled) return VpbPalette.Ok;
                if (IsLatest) return VpbPalette.Muted;
                return VpbPalette.Muted;
            }
        }

        public Brush RailBrush
        {
            get
            {
                if (IsPinned) return VpbPalette.Info;
                if (IsInstalled) return VpbPalette.Ok;
                return Brushes.Transparent;
            }
        }

        public string ActionText
        {
            get
            {
                if (IsInstalled && IsPinned) return "Reinstall";
                if (IsInstalled) return "Pin this build";
                return IsRollback ? "Roll back to this" : "Switch to this";
            }
        }

        public bool CanApply => !IsBlocked && !IsBusy;

        public Visibility ActionVisibility =>
            IsBlocked ? Visibility.Collapsed : Visibility.Visible;

        public Visibility BlockedVisibility =>
            IsBlocked ? Visibility.Visible : Visibility.Collapsed;

        public string Tooltip
        {
            get
            {
                var lines = new List<string> { Notes, "" };

                if (DateText.Length > 0)
                {
                    var age = AgeText.Length > 0 ? $" ({AgeText})" : "";
                    lines.Add($"Released {DateText}{age}");
                }

                lines.Add(Schema > 0
                    ? $"Database schema {Schema}."
                    : "Database schema unknown for this build.");

                if (HasSchemaWarning) lines.Add(SchemaWarning);
                if (Release.Commit.Length > 0) lines.Add($"Commit {Release.ShortCommit}  ·  tag {Tag}");
                if (IsBlocked) lines.Add("");
                if (IsBlocked) lines.Add(BlockedReason);

                return string.Join(Environment.NewLine, lines);
            }
        }

        public override string ToString()
        {
            var badge = BadgeText.Length > 0 ? $" — {BadgeText}" : "";
            var meta = MetaText.Length > 0 ? $" — {MetaText}" : "";
            return $"VPB {Version}{meta}{badge}";
        }

        public string ActionAccessibleName => $"{ActionText}: VPB {Version}";

        private void RaiseDerived()
        {
            OnPropertyChanged(nameof(ActionAccessibleName));
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(BadgeVisibility));
            OnPropertyChanged(nameof(BadgeBrush));
            OnPropertyChanged(nameof(RailBrush));
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    internal static class VpbPalette
    {
        public static readonly Brush Ok = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50));
        public static readonly Brush Info = Freeze(Color.FromRgb(0x4C, 0x8E, 0xDA));
        public static readonly Brush Warn = Freeze(Color.FromRgb(0xD2, 0x99, 0x22));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xF8, 0x51, 0x49));
        public static readonly Brush Muted = Freeze(Color.FromRgb(0x8B, 0x8B, 0x8B));

        public static Brush For(VpbStatusKind kind) => kind switch
        {
            VpbStatusKind.Ok => Ok,
            VpbStatusKind.Info => Info,
            VpbStatusKind.Warn => Warn,
            VpbStatusKind.Error => Error,
            _ => Muted
        };

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    public sealed class VpbManagerViewModel : ViewModelBase
    {
        private string _statusHeadline = "Checking VPB…";
        private string _statusDetail = "";
        private VpbStatusKind _statusKind = VpbStatusKind.Info;
        private string _primaryActionText = "Check again";
        private bool _canPrimaryAction;
        private Visibility _primaryActionVisibility = Visibility.Collapsed;
        private string _noticeText;
        private VpbStatusKind _noticeKind = VpbStatusKind.Warn;
        private bool _busy;
        private bool _loaded;
        private string _versionFilter = "";
        private string _versionsUnavailableText = "";
        private Visibility _versionsUnavailableVisibility = Visibility.Collapsed;
        private string _versionListCaption = "";
        private bool _forceReinstall;
        private string _selectedBranch = VpbUpdateConfigFile.DefaultBranch;
        private Visibility _returnToLatestVisibility = Visibility.Collapsed;
        private string _returnToLatestText = "Return to latest";
        private Visibility _progressVisibility = Visibility.Collapsed;
        private bool _progressIndeterminate = true;
        private double _progressValue;
        private double _progressMaximum = 1;
        private string _progressText = "";
        private string _folderPath = "";
        private int _fileTotal;
        private int _fileMissing;
        private int _fileOutdated;
        private int _filePatched;
        private bool _uninstallVisible;

        public ObservableCollection<VpbVersionRow> Versions { get; } = new ObservableCollection<VpbVersionRow>();

        public ObservableCollection<VpbFileRow> Files { get; } = new ObservableCollection<VpbFileRow>();

        public ObservableCollection<string> Branches { get; } = new ObservableCollection<string>();

        public string FolderPath
        {
            get => _folderPath;
            set => SetProperty(ref _folderPath, value);
        }

        public string StatusHeadline
        {
            get => _statusHeadline;
            set => SetProperty(ref _statusHeadline, value);
        }

        public string StatusDetail
        {
            get => _statusDetail;
            set => SetProperty(ref _statusDetail, value);
        }

        public VpbStatusKind StatusKind
        {
            get => _statusKind;
            set
            {
                if (SetProperty(ref _statusKind, value))
                    OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush => VpbPalette.For(StatusKind);

        public string PrimaryActionText
        {
            get => _primaryActionText;
            set => SetProperty(ref _primaryActionText, value);
        }

        public bool CanPrimaryAction
        {
            get => _canPrimaryAction;
            set => SetProperty(ref _canPrimaryAction, value);
        }

        public Visibility PrimaryActionVisibility
        {
            get => _primaryActionVisibility;
            set => SetProperty(ref _primaryActionVisibility, value);
        }

        public string NoticeText
        {
            get => _noticeText;
            set
            {
                if (SetProperty(ref _noticeText, value))
                    OnPropertyChanged(nameof(NoticeVisibility));
            }
        }

        public VpbStatusKind NoticeKind
        {
            get => _noticeKind;
            set
            {
                if (SetProperty(ref _noticeKind, value))
                    OnPropertyChanged(nameof(NoticeBrush));
            }
        }

        public Brush NoticeBrush => VpbPalette.For(NoticeKind);

        public Visibility NoticeVisibility =>
            string.IsNullOrWhiteSpace(NoticeText) ? Visibility.Collapsed : Visibility.Visible;

        public bool Busy
        {
            get => _busy;
            set
            {
                if (SetProperty(ref _busy, value))
                    OnPropertyChanged(nameof(NotBusy));
            }
        }

        public bool NotBusy => !Busy;

        public bool Loaded
        {
            get => _loaded;
            set => SetProperty(ref _loaded, value);
        }

        public string VersionFilter
        {
            get => _versionFilter;
            set => SetProperty(ref _versionFilter, value ?? "");
        }

        public string VersionListCaption
        {
            get => _versionListCaption;
            set => SetProperty(ref _versionListCaption, value);
        }

        public string VersionsUnavailableText
        {
            get => _versionsUnavailableText;
            set => SetProperty(ref _versionsUnavailableText, value);
        }

        public Visibility VersionsUnavailableVisibility
        {
            get => _versionsUnavailableVisibility;
            set => SetProperty(ref _versionsUnavailableVisibility, value);
        }

        public bool ForceReinstall
        {
            get => _forceReinstall;
            set => SetProperty(ref _forceReinstall, value);
        }

        public string SelectedBranch
        {
            get => _selectedBranch;
            set => SetProperty(ref _selectedBranch, value);
        }

        public Visibility ReturnToLatestVisibility
        {
            get => _returnToLatestVisibility;
            set => SetProperty(ref _returnToLatestVisibility, value);
        }

        public string ReturnToLatestText
        {
            get => _returnToLatestText;
            set => SetProperty(ref _returnToLatestText, value);
        }

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set => SetProperty(ref _progressVisibility, value);
        }

        public bool ProgressIndeterminate
        {
            get => _progressIndeterminate;
            set => SetProperty(ref _progressIndeterminate, value);
        }

        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        public double ProgressMaximum
        {
            get => _progressMaximum;
            set => SetProperty(ref _progressMaximum, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        public int FileTotal
        {
            get => _fileTotal;
            set => SetProperty(ref _fileTotal, value);
        }

        public int FileMissing
        {
            get => _fileMissing;
            set => SetProperty(ref _fileMissing, value);
        }

        public int FileOutdated
        {
            get => _fileOutdated;
            set => SetProperty(ref _fileOutdated, value);
        }

        public int FilePatched
        {
            get => _filePatched;
            set => SetProperty(ref _filePatched, value);
        }

        public Visibility UninstallVisibility
        {
            get => _uninstallVisible ? Visibility.Visible : Visibility.Collapsed;
            set => SetUninstallVisible(value == Visibility.Visible);
        }

        public void SetUninstallVisible(bool visible)
        {
            if (_uninstallVisible == visible) return;
            _uninstallVisible = visible;
            OnPropertyChanged(nameof(UninstallVisibility));
        }

        public void Apply(
            VpbPatchCheckResult check,
            VpbReleaseIndex index,
            VpbUpdateConfigFile config,
            int localDbSchema,
            bool vamRunning)
        {
            var installedVersion = check?.InstalledVersion;
            var installed = check?.Status != VpbPatchStatus.NeedsInstall || !string.IsNullOrEmpty(installedVersion);
            if (check == null) installed = false;

            RebuildVersions(index, config, installedVersion, localDbSchema);
            RebuildFiles(check);

            SetUninstallVisible(installed);

            var channel = string.IsNullOrWhiteSpace(config?.Channel)
                ? VpbUpdateConfigFile.DefaultBranch
                : config.Channel;

            var pinned = config?.Pinned == true;

            // ── Headline ────────────────────────────────────────────────────────────────────────
            if (check == null)
            {
                StatusKind = VpbStatusKind.Error;
                StatusHeadline = "Could not check VPB";
                StatusDetail = "";
                PrimaryActionVisibility = Visibility.Collapsed;
            }
            else if (!installed)
            {
                StatusKind = VpbStatusKind.Info;
                StatusHeadline = "VPB is not installed";
                StatusDetail = Join(
                    check.RemoteVersion is { Length: > 0 } v ? $"{v} available on {channel}" : $"Following {channel}",
                    $"{check.TotalFiles} files");
                PrimaryActionText = check.RemoteVersion is { Length: > 0 } iv ? $"Install {iv}" : "Install VPB";
                PrimaryActionVisibility = Visibility.Visible;
                CanPrimaryAction = true;
            }
            else if (check.Status == VpbPatchStatus.UpToDate
                     || check.MissingFiles + check.OutdatedFiles == 0)
            {
                StatusKind = pinned ? VpbStatusKind.Info : VpbStatusKind.Ok;
                StatusHeadline = pinned
                    ? $"Pinned to {installedVersion ?? config.PinnedVersion}"
                    : $"Up to date — {installedVersion ?? check.RemoteVersion}";
                StatusDetail = pinned
                    ? Join($"This build stays put; {channel} will not update it", DescribeAge(index, installedVersion))
                    : Join($"Following {channel}", DescribeAge(index, installedVersion), $"{check.TotalFiles} files verified");
                PrimaryActionVisibility = Visibility.Collapsed;
                CanPrimaryAction = false;
            }
            else
            {
                var target = check.RemoteVersion;
                var rollingBack = target is { Length: > 0 }
                                  && installedVersion is { Length: > 0 }
                                  && index != null
                                  && index.IsOlderThan(target, installedVersion);

                StatusKind = VpbStatusKind.Warn;

                if (pinned)
                {
                    StatusHeadline = target is { Length: > 0 }
                        ? $"Ready to apply {target}"
                        : "Ready to apply the pinned build";
                    StatusDetail = Join(
                        $"Pinned to {config.PinnedVersion}",
                        installedVersion is { Length: > 0 } ? $"{installedVersion} on disk" : null,
                        DescribeWork(check));
                }
                else if (rollingBack)
                {
                    StatusHeadline = $"Rolling back to {target}";
                    StatusDetail = Join($"{installedVersion} on disk", DescribeWork(check));
                }
                else
                {
                    StatusHeadline = target is { Length: > 0 }
                        ? $"Update available — {installedVersion} → {target}"
                        : "Update available";
                    StatusDetail = Join($"Following {channel}", DescribeAge(index, target), DescribeWork(check));
                }

                PrimaryActionText = target is { Length: > 0 }
                    ? (rollingBack ? $"Roll back to {target}" : $"Update to {target}")
                    : "Apply patch";
                PrimaryActionVisibility = Visibility.Visible;
                CanPrimaryAction = true;
            }

            // ── Return to latest ────────────────────────────────────────────────────────────────
            ReturnToLatestVisibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            ReturnToLatestText = $"Return to latest on {channel}";

            // ── Notice ──────────────────────────────────────────────────────────────────────────
            NoticeText = BuildNotice(check, index, config, localDbSchema, vamRunning, channel);

            SelectedBranch = channel;
            Loaded = true;
        }

        private string BuildNotice(
            VpbPatchCheckResult check,
            VpbReleaseIndex index,
            VpbUpdateConfigFile config,
            int localDbSchema,
            bool vamRunning,
            string channel)
        {
            if (vamRunning)
            {
                NoticeKind = VpbStatusKind.Warn;
                return "VaM is running. Close it before applying — its files are locked while it is open.";
            }

            if (config?.Pinned == true && index != null && !index.IsEmpty && index.Find(config.PinnedVersion) == null)
            {
                NoticeKind = VpbStatusKind.Warn;
                return $"Pinned build {config.PinnedVersion} is no longer listed on {channel}. "
                       + "It may still install, but returning to latest is the reliable way out.";
            }

            if (config?.Pinned == true && localDbSchema > 0 && config.PinnedSchema > 0 && config.PinnedSchema < localDbSchema)
            {
                NoticeKind = VpbStatusKind.Warn;
                return $"This build expects database schema {config.PinnedSchema}; yours is {localDbSchema}. "
                       + "VPB may rebuild its index on first launch.";
            }

            if (check != null && !check.UsedFastPath)
            {
                NoticeKind = VpbStatusKind.Info;
                return "This ref predates patch_manifest2.json, so checks use the GitHub API "
                       + "(60 requests an hour unauthenticated).";
            }

            return null;
        }

        private static string DescribeWork(VpbPatchCheckResult check)
        {
            var changed = check.MissingFiles + check.OutdatedFiles;
            if (changed <= 0) return null;

            var text = $"{changed} of {check.TotalFiles} files differ";
            if (check.BytesToDownload > 0)
                text += $"  ·  {FormatBytes(check.BytesToDownload)} to download";
            return text;
        }

        private static string DescribeAge(VpbReleaseIndex index, string version)
        {
            var release = index?.Find(version);
            if (release == null) return null;

            var days = release.AgeDays;
            if (days < 0) return null;
            if (days == 0) return "released today";
            if (days == 1) return "released yesterday";
            return $"released {days}d ago";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 KB";
            if (bytes < 1024L * 1024L) return $"{Math.Ceiling(bytes / 1024d):0} KB";
            return $"{bytes / (1024d * 1024d):0.#} MB";
        }

        private static string Join(params string[] parts) =>
            string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private void RebuildVersions(
            VpbReleaseIndex index,
            VpbUpdateConfigFile config,
            string installedVersion,
            int localDbSchema)
        {
            Versions.Clear();

            if (index == null || index.IsEmpty)
            {
                VersionsUnavailableVisibility = Visibility.Visible;
                VersionsUnavailableText =
                    $"No build list published on {config?.Channel ?? VpbUpdateConfigFile.DefaultBranch}, "
                    + "or GitHub could not be reached. Updating still works — only rolling back needs the list.";
                VersionListCaption = "";
                return;
            }

            VersionsUnavailableVisibility = Visibility.Collapsed;
            VersionsUnavailableText = "";

            for (var i = 0; i < index.Releases.Count; i++)
            {
                var release = index.Releases[i];
                var row = new VpbVersionRow(release, i)
                {
                    IsInstalled = !string.IsNullOrEmpty(installedVersion)
                                  && string.Equals(release.Version, installedVersion, StringComparison.Ordinal),
                    IsPinned = config?.Pinned == true
                               && string.Equals(release.Version, config.PinnedVersion, StringComparison.Ordinal),
                    IsRollback = !string.IsNullOrEmpty(installedVersion)
                                 && index.IsOlderThan(release.Version, installedVersion)
                };

                if (index.IsBelowFloor(release.Version))
                {
                    row.BlockedReason =
                        $"{release.Version} predates the single-folder plugin layout ({index.MinRollbackVersion}). "
                        + "The patcher only migrates forward, so it cannot be installed.";
                }

                if (localDbSchema > 0 && release.Schema > 0 && release.Schema < localDbSchema)
                {
                    row.SchemaWarning =
                        $"Expects database schema {release.Schema}; yours is {localDbSchema} — VPB may rebuild its index.";
                }

                Versions.Add(row);
            }

            var channel = config?.Channel ?? VpbUpdateConfigFile.DefaultBranch;
            VersionListCaption = index.Releases.Count == 1
                ? $"1 build published on {channel}"
                : $"{index.Releases.Count} builds published on {channel}";

            if (index.ExcludedBelowMin > 0)
            {
                VersionListCaption +=
                    $"  ·  {index.ExcludedBelowMin} older build(s) excluded below {index.MinRollbackVersion}";
            }
        }

        private void RebuildFiles(VpbPatchCheckResult check)
        {
            Files.Clear();

            FileTotal = check?.TotalFiles ?? 0;
            FileMissing = check?.MissingFiles ?? 0;
            FileOutdated = check?.OutdatedFiles ?? 0;
            FilePatched = check?.PatchedFiles ?? 0;

            if (check == null) return;

            foreach (var issue in check.MissingDetails ?? Array.Empty<VpbPatchFileIssue>())
                Files.Add(VpbFileRow.From(issue));
            foreach (var issue in check.OutdatedDetails ?? Array.Empty<VpbPatchFileIssue>())
                Files.Add(VpbFileRow.From(issue));
            foreach (var issue in check.PatchedDetails ?? Array.Empty<VpbPatchFileIssue>())
                Files.Add(VpbFileRow.From(issue));
        }
    }

    public sealed class VpbFileRow
    {
        public string Status { get; init; }
        public string RelativePath { get; init; }
        public string Reason { get; init; }
        public string ExpectedSha { get; init; }
        public string LocalSha { get; init; }
        public bool IsDirectory { get; init; }
        public Brush StatusBrush { get; init; }

        public static VpbFileRow From(VpbPatchFileIssue issue)
        {
            var (status, brush) = issue.IssueType switch
            {
                VpbPatchIssueType.Missing => ("Missing", VpbPalette.Error),
                VpbPatchIssueType.Outdated => ("Outdated", VpbPalette.Warn),
                _ => ("OK", VpbPalette.Ok)
            };

            return new VpbFileRow
            {
                Status = status,
                StatusBrush = brush,
                RelativePath = issue.RelativePath,
                Reason = issue.Reason,
                ExpectedSha = issue.ExpectedSha,
                LocalSha = issue.LocalSha,
                IsDirectory = issue.IsDirectory
            };
        }
    }
}
