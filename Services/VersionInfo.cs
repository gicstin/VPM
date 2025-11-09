using System;
using System.Reflection;

namespace VPM.Services
{
    /// <summary>
    /// Provides easy access to application version information
    /// </summary>
    public static class VersionInfo
    {
        private static readonly Version _version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

        /// <summary>
        /// Gets the full version string (e.g., "1.0.0.142")
        /// </summary>
        public static string FullVersion => _version.ToString();

        /// <summary>
        /// Gets the version without build number (e.g., "1.0.0")
        /// </summary>
        public static string ShortVersion => $"{_version.Major}.{_version.Minor}.{_version.Build}";

        /// <summary>
        /// Gets the major version number
        /// </summary>
        public static int Major => _version.Major;

        /// <summary>
        /// Gets the minor version number
        /// </summary>
        public static int Minor => _version.Minor;

        /// <summary>
        /// Gets the patch version number
        /// </summary>
        public static int Patch => _version.Build;

        /// <summary>
        /// Gets the build number (auto-incremented on each compile)
        /// </summary>
        public static int BuildNumber => _version.Revision;

        /// <summary>
        /// Gets the Version object
        /// </summary>
        public static Version Version => _version;

        /// <summary>
        /// Gets a display-friendly version string (e.g., "v1.0.0 (Build 142)")
        /// </summary>
        public static string DisplayVersion => $"v{ShortVersion} (Build {BuildNumber})";
    }
}

