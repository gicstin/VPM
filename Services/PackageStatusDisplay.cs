using System;
using VPM.Services.Vpb;

namespace VPM.Services
{
    public static class PackageStatusDisplay
    {
        public static string ForUi(string status)
        {
            // Null Current (first populate) still uses whitelist labels. FileMove opts out.
            if (ScanControlService.Current?.IsWhitelistMode == false)
                return status;
            return status switch
            {
                "Loaded" => "Whitelisted",
                "Available" => "On-demand",
                _ => status
            };
        }

        public static string FromUi(string display)
        {
            if (string.Equals(display, "Whitelisted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(display, "In scan", StringComparison.OrdinalIgnoreCase))
                return "Loaded";
            if (string.Equals(display, "On-demand", StringComparison.OrdinalIgnoreCase))
                return "Available";
            return display;
        }

        public static bool IsIncludeable(string status) =>
            status is "Available" or "On-demand";

        public static bool IsExcludable(string status) =>
            status is "Loaded" or "In scan" or "Whitelisted";

        public static bool UseWhitelistVerbs => ScanControlService.Current?.IsWhitelistMode == true;

        public static string IncludeVerb => UseWhitelistVerbs ? "✅ Whitelist" : "📥 Load";
        public static string ExcludeVerb => UseWhitelistVerbs ? "🚫 Unlist" : "📤 Unload";
        public static string IncludeDepsVerb => UseWhitelistVerbs ? "✅ Whitelist +Deps" : "📥 Load +Deps";

        public static string IncludeTooltip => UseWhitelistVerbs
            ? "Whitelist for VaM boot scan"
            : "Load selected packages";
        public static string ExcludeTooltip => UseWhitelistVerbs
            ? "Unlist from VaM boot scan (stays on disk, loads on demand)"
            : "Unload selected packages";
        public static string IncludeDepsTooltip => UseWhitelistVerbs
            ? "Whitelist package and its dependencies for VaM boot scan"
            : "Load selected packages and dependencies";
        public static string IncludeDepsRowTooltip => UseWhitelistVerbs
            ? "Whitelist selected dependencies for VaM boot scan"
            : "Load selected dependencies";
        public static string ExcludeDepsRowTooltip => UseWhitelistVerbs
            ? "Unlist selected dependencies from VaM boot scan"
            : "Unload selected dependencies";
    }
}
