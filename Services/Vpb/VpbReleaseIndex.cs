using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services.Vpb
{
    public sealed class VpbRelease
    {
        public string Version { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Commit { get; init; } = "";
        public string DateUtc { get; init; } = "";
        public string Notes { get; init; } = "";
        public int Schema { get; init; }

        public DateTimeOffset? Released
        {
            get
            {
                if (string.IsNullOrWhiteSpace(DateUtc))
                    return null;

                return DateTimeOffset.TryParse(
                    DateUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : null;
            }
        }

        public int AgeDays
        {
            get
            {
                var released = Released;
                if (released == null)
                    return -1;

                var days = (int)(DateTime.UtcNow.Date - released.Value.UtcDateTime.Date).TotalDays;
                return days < 0 ? 0 : days;
            }
        }

        public string ShortCommit =>
            string.IsNullOrEmpty(Commit) ? "" : Commit.Substring(0, Math.Min(7, Commit.Length));
    }

    public sealed class VpbReleaseIndex
    {
        public const string RepoPath = "releases/index.json";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, (VpbReleaseIndex Index, DateTime FetchedUtc)> Cache =
            new Dictionary<string, (VpbReleaseIndex, DateTime)>(StringComparer.Ordinal);

        public static VpbReleaseIndex Empty { get; } = new VpbReleaseIndex();

        public string Branch { get; private init; } = "";
        public string MinRollbackVersion { get; private init; } = "";
        public int ExcludedBelowMin { get; private init; }
        public DateTimeOffset? UpdatedUtc { get; private init; }
        public IReadOnlyList<VpbRelease> Releases { get; private init; } = Array.Empty<VpbRelease>();

        public bool IsEmpty => Releases.Count == 0;

        public VpbRelease Latest => Releases.Count > 0 ? Releases[0] : null;

        public VpbRelease Find(string version)
        {
            if (string.IsNullOrEmpty(version))
                return null;

            for (var i = 0; i < Releases.Count; i++)
            {
                if (string.Equals(Releases[i].Version, version, StringComparison.Ordinal))
                    return Releases[i];
            }

            return null;
        }

        public int IndexOfVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
                return -1;

            for (var i = 0; i < Releases.Count; i++)
            {
                if (string.Equals(Releases[i].Version, version, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        public bool TryCompareByShipOrder(string a, string b, out int result)
        {
            result = 0;
            var ia = IndexOfVersion(a);
            var ib = IndexOfVersion(b);
            if (ia < 0 || ib < 0)
                return false;
            if (ia == ib)
                return true;

            result = ia > ib ? -1 : 1;
            return true;
        }

        public bool IsOlderThan(string candidate, string current)
        {
            if (TryCompareByShipOrder(candidate, current, out var order))
                return order < 0;

            return CompareVersions(candidate, current) < 0;
        }

        public bool IsBelowFloor(string version)
        {
            if (string.IsNullOrEmpty(MinRollbackVersion) || string.IsNullOrEmpty(version))
                return false;

            return CompareVersions(version, MinRollbackVersion) < 0;
        }

        public static int CompareVersions(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return 1;

            var pa = a.Split('.');
            var pb = b.Split('.');
            var len = Math.Max(pa.Length, pb.Length);

            for (var i = 0; i < len; i++)
            {
                var va = i < pa.Length ? ParsePart(pa[i]) : 0;
                var vb = i < pb.Length ? ParsePart(pb[i]) : 0;
                if (va != vb)
                    return va < vb ? -1 : 1;
            }

            return 0;
        }

        private static int ParsePart(string s)
        {
            var value = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c < '0' || c > '9') break;
                value = value * 10 + (c - '0');
            }

            return value;
        }

        public static VpbReleaseIndex Parse(string json, string branch)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Empty;

            try
            {
                var dto = JsonSerializer.Deserialize<IndexDto>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (dto == null)
                    return Empty;

                var releases = (dto.Releases ?? new List<ReleaseDto>())
                    .Where(r => r != null
                                && !string.IsNullOrWhiteSpace(r.Version)
                                && !string.IsNullOrWhiteSpace(r.Tag)
                                && r.Tag.IndexOf('/') < 0)
                    .Select(r => new VpbRelease
                    {
                        Version = r.Version,
                        Tag = r.Tag,
                        Commit = r.Commit ?? "",
                        DateUtc = r.DateUtc ?? "",
                        Notes = r.Notes ?? "",
                        Schema = r.Schema
                    })
                    .ToList();

                DateTimeOffset? updated = null;
                if (!string.IsNullOrWhiteSpace(dto.UpdatedUtc)
                    && DateTimeOffset.TryParse(
                        dto.UpdatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsedUpdated))
                {
                    updated = parsedUpdated;
                }

                return new VpbReleaseIndex
                {
                    Branch = branch ?? "",
                    MinRollbackVersion = dto.MinRollbackVersion ?? "",
                    ExcludedBelowMin = dto.ExcludedBelowMin,
                    UpdatedUtc = updated,
                    Releases = releases
                };
            }
            catch
            {
                return Empty;
            }
        }

        public static async Task<VpbReleaseIndex> FetchAsync(
            HttpClient httpClient,
            string branch,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrWhiteSpace(branch))
                branch = VpbUpdateConfigFile.DefaultBranch;

            if (!forceRefresh)
            {
                lock (Gate)
                {
                    if (Cache.TryGetValue(branch, out var hit) && DateTime.UtcNow - hit.FetchedUtc < CacheTtl)
                        return hit.Index;
                }
            }

            VpbReleaseIndex index;
            try
            {
                var url = $"https://raw.githubusercontent.com/{VpbGitHubMetadata.RepoOwner}/{VpbGitHubMetadata.RepoName}/{Uri.EscapeDataString(branch)}/{RepoPath}";
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    index = Empty;
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    index = Parse(body, branch);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                index = Empty;
            }

            lock (Gate)
            {
                Cache[branch] = (index, DateTime.UtcNow);
            }

            return index;
        }

        public static void InvalidateCache()
        {
            lock (Gate)
            {
                Cache.Clear();
            }
        }

        private sealed class IndexDto
        {
            public int IndexVersion { get; set; }
            public string UpdatedUtc { get; set; }
            public string MinRollbackVersion { get; set; }
            public int ExcludedBelowMin { get; set; }
            public int Keep { get; set; }
            public List<ReleaseDto> Releases { get; set; }
        }

        private sealed class ReleaseDto
        {
            public string Version { get; set; }
            public string Tag { get; set; }
            public string Commit { get; set; }
            public string DateUtc { get; set; }
            public string Notes { get; set; }
            public int Schema { get; set; }
        }
    }
}
