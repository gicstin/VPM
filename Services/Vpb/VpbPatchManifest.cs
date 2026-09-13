using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services.Vpb
{
    public sealed class VpbPatchManifestEntry
    {
        public string RelativePath { get; init; } = "";
        public bool IsDirectory { get; init; }

        public string Sha1 { get; init; } = "";
        public long Size { get; init; }
    }

    public sealed class VpbPatchManifest
    {
        public const string RepoPath = "patch_manifest2.json";

        public int ManifestVersion { get; private init; }
        public string Version { get; private init; } = "";
        public string BuiltUtc { get; private init; } = "";
        public int Schema { get; private init; }
        public IReadOnlyList<VpbPatchManifestEntry> Files { get; private init; } = Array.Empty<VpbPatchManifestEntry>();

        public bool IsComplete =>
            Files.Count > 0 && Files.All(f => f.IsDirectory || !string.IsNullOrWhiteSpace(f.Sha1));

        public static VpbPatchManifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var dto = JsonSerializer.Deserialize<ManifestDto>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (dto?.Files == null || dto.Files.Count == 0)
                    return null;

                return new VpbPatchManifest
                {
                    ManifestVersion = dto.ManifestVersion,
                    Version = dto.Version ?? "",
                    BuiltUtc = dto.BuiltUtc ?? "",
                    Schema = dto.Schema,
                    Files = dto.Files
                        .Where(f => f != null && !string.IsNullOrWhiteSpace(f.RelativePath))
                        .Select(f => new VpbPatchManifestEntry
                        {
                            RelativePath = f.RelativePath.Replace('\\', '/').TrimStart('/'),
                            IsDirectory = f.IsDirectory,
                            Sha1 = f.Sha1 ?? "",
                            Size = f.Size
                        })
                        .ToList()
                };
            }
            catch
            {
                return null;
            }
        }

        public static async Task<VpbPatchManifest> TryFetchAsync(
            HttpClient httpClient,
            string gitRef,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));

            try
            {
                var url = VpbGitHubMetadata.GetRawUrl(gitRef, RepoPath);
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var manifest = Parse(body);
                return manifest is { IsComplete: true } ? manifest : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private sealed class ManifestDto
        {
            public int ManifestVersion { get; set; }
            public string Version { get; set; }
            public string BuiltUtc { get; set; }
            public int Schema { get; set; }
            public List<FileDto> Files { get; set; }
        }

        private sealed class FileDto
        {
            public string RelativePath { get; set; }
            public bool IsDirectory { get; set; }
            public string Sha1 { get; set; }
            public long Size { get; set; }
        }
    }
}
