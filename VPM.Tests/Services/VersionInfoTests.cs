using Xunit;
using VPM.Services;
using System;

namespace VPM.Tests.Services
{
    public class VersionInfoTests
    {
        [Fact]
        public void FullVersion_ReturnsValidVersionString()
        {
            string fullVersion = VersionInfo.FullVersion;

            Assert.NotNull(fullVersion);
            Assert.NotEmpty(fullVersion);
            Assert.Matches(@"\d+\.\d+\.\d+\.\d+", fullVersion);
        }

        [Fact]
        public void ShortVersion_ReturnsVersionWithoutBuildNumber()
        {
            string shortVersion = VersionInfo.ShortVersion;

            Assert.NotNull(shortVersion);
            Assert.NotEmpty(shortVersion);
            Assert.Matches(@"\d+\.\d+\.\d+", shortVersion);
            
            int dotCount = shortVersion.Split('.').Length - 1;
            Assert.Equal(2, dotCount);
        }

        [Fact]
        public void Major_ReturnsNonNegativeInteger()
        {
            int major = VersionInfo.Major;

            Assert.True(major >= 0);
        }

        [Fact]
        public void Minor_ReturnsNonNegativeInteger()
        {
            int minor = VersionInfo.Minor;

            Assert.True(minor >= 0);
        }

        [Fact]
        public void Patch_ReturnsNonNegativeInteger()
        {
            int patch = VersionInfo.Patch;

            Assert.True(patch >= 0);
        }

        [Fact]
        public void BuildNumber_ReturnsNonNegativeInteger()
        {
            int buildNumber = VersionInfo.BuildNumber;

            Assert.True(buildNumber >= 0);
        }

        [Fact]
        public void Version_ReturnsValidVersionObject()
        {
            Version version = VersionInfo.Version;

            Assert.NotNull(version);
            Assert.True(version.Major >= 0);
            Assert.True(version.Minor >= 0);
            Assert.True(version.Build >= 0);
            Assert.True(version.Revision >= 0);
        }

        [Fact]
        public void DisplayVersion_FormatsVersionWithBuildNumber()
        {
            string displayVersion = VersionInfo.DisplayVersion;

            Assert.NotNull(displayVersion);
            Assert.StartsWith("v", displayVersion);
            Assert.Contains("Build", displayVersion);
            Assert.Matches(@"v\d+\.\d+\.\d+ \(Build \d+\)", displayVersion);
        }

        [Fact]
        public void ShortVersion_StartsWithMajorVersion()
        {
            string shortVersion = VersionInfo.ShortVersion;
            string majorStr = VersionInfo.Major.ToString();

            Assert.StartsWith(majorStr, shortVersion);
        }

        [Fact]
        public void DisplayVersion_ContainsShortVersion()
        {
            string displayVersion = VersionInfo.DisplayVersion;
            string shortVersion = VersionInfo.ShortVersion;

            Assert.Contains(shortVersion, displayVersion);
        }

        [Fact]
        public void FullVersion_ContainsAllVersionComponents()
        {
            string fullVersion = VersionInfo.FullVersion;
            string[] parts = fullVersion.Split('.');

            Assert.Equal(4, parts.Length);
            Assert.All(parts, part => Assert.True(int.TryParse(part, out _)));
        }

        [Fact]
        public void Version_MajorMinorPatchMatchProperties()
        {
            Assert.Equal(VersionInfo.Version.Major, VersionInfo.Major);
            Assert.Equal(VersionInfo.Version.Minor, VersionInfo.Minor);
            Assert.Equal(VersionInfo.Version.Build, VersionInfo.Patch);
        }

        [Fact]
        public void BuildNumber_MatchesVersionRevision()
        {
            Assert.Equal(VersionInfo.Version.Revision, VersionInfo.BuildNumber);
        }

        [Fact]
        public void VersionProperties_AreConsistent()
        {
            string fullVersion = VersionInfo.FullVersion;
            string shortVersion = VersionInfo.ShortVersion;
            
            Assert.Equal($"{VersionInfo.Major}.{VersionInfo.Minor}.{VersionInfo.Patch}.{VersionInfo.BuildNumber}", fullVersion);
            Assert.Equal($"{VersionInfo.Major}.{VersionInfo.Minor}.{VersionInfo.Patch}", shortVersion);
        }

        [Fact]
        public void DisplayVersion_EndsWithBuildNumberInParentheses()
        {
            string displayVersion = VersionInfo.DisplayVersion;

            Assert.EndsWith($"Build {VersionInfo.BuildNumber})", displayVersion);
        }

        [Fact]
        public void AllVersionProperties_AreNonNull()
        {
            Assert.NotNull(VersionInfo.FullVersion);
            Assert.NotNull(VersionInfo.ShortVersion);
            Assert.NotNull(VersionInfo.DisplayVersion);
            Assert.NotNull(VersionInfo.Version);
        }

        [Fact]
        public void VersionNumbers_AreReasonablySized()
        {
            Assert.True(VersionInfo.Major <= 999, "Major version should be reasonable");
            Assert.True(VersionInfo.Minor <= 999, "Minor version should be reasonable");
            Assert.True(VersionInfo.Patch <= 999999, "Patch version should be reasonable");
            Assert.True(VersionInfo.BuildNumber <= 999999, "Build number should be reasonable");
        }
    }
}
