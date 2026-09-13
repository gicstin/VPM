using System.IO;

namespace VPM.Services.Vpb
{
    public static class VpbPaths
    {
        public const string ScanWhitelistFileName = "scan_whitelist.json";
        public const string LockedPackagesFileName = "locked_packages.json";
        public const string RatingsFileName = "ratings.json";
        public const string CreatorRatingsFileName = "creator_ratings.json";
        public const string DatabaseFileName = "VpbLocalDatabase.sqlite3";

        public static string PluginDataDirectory(string vamRoot)
        {
            return Path.Combine(vamRoot ?? "", "Saves", "PluginData", "VPB");
        }

        public static string ScanWhitelistPath(string vamRoot) =>
            Path.Combine(PluginDataDirectory(vamRoot), ScanWhitelistFileName);

        public static string LockedPackagesPath(string vamRoot) =>
            Path.Combine(PluginDataDirectory(vamRoot), LockedPackagesFileName);

        public static string RatingsPath(string vamRoot) =>
            Path.Combine(PluginDataDirectory(vamRoot), RatingsFileName);

        public static string CreatorRatingsPath(string vamRoot) =>
            Path.Combine(PluginDataDirectory(vamRoot), CreatorRatingsFileName);

        public static string DatabasePath(string vamRoot) =>
            Path.Combine(PluginDataDirectory(vamRoot), DatabaseFileName);

        public static string VpbDllPath(string vamRoot) =>
            Path.Combine(vamRoot ?? "", "BepInEx", "plugins", "VPB", "VPB.dll");

        public static string LegacyVpbDllPath(string vamRoot) =>
            Path.Combine(vamRoot ?? "", "BepInEx", "plugins", "VPB.dll");

        public static string ResolveInstalledVpbDllPath(string vamRoot)
        {
            var nested = VpbDllPath(vamRoot);
            if (File.Exists(nested))
                return nested;

            var legacy = LegacyVpbDllPath(vamRoot);
            if (File.Exists(legacy))
                return legacy;

            return nested;
        }
    }
}
