using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VPM.Services.Vpb
{
    public sealed class ScanWhitelistData
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("whitelistedFolders")]
        public List<string> WhitelistedFolders { get; set; } = new();

        [JsonPropertyName("includedPackageUids")]
        public List<string> IncludedPackageUids { get; set; } = new();

        public bool IsEnabledEmpty =>
            Enabled
            && (WhitelistedFolders == null || WhitelistedFolders.Count == 0)
            && (IncludedPackageUids == null || IncludedPackageUids.Count == 0);
    }

    public readonly struct ScanBudget
    {
        public int PackageCount { get; init; }
        public long TotalBytes { get; init; }
        public int FileCount { get; init; }

        public string Format()
        {
            var gb = TotalBytes / (1024d * 1024d * 1024d);
            var size = gb >= 1
                ? $"{gb:0.0} GB"
                : $"{TotalBytes / (1024d * 1024d):0} MB";
            return $"{PackageCount:N0} packages · {size} · {FileCount:N0} files";
        }
    }
}
