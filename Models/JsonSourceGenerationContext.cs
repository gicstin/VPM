using System.Collections.Generic;
using System.Text.Json.Serialization;
using VPM.Models;

namespace VPM.Models
{
    /// <summary>
    /// JSON source generation context for .NET 10 performance optimization
    /// This provides compile-time JSON serialization for better performance and reduced memory allocation
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        GenerationMode = JsonSourceGenerationMode.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(JsonPackageDatabase))]
    [JsonSerializable(typeof(JsonPackageInfo))]
    [JsonSerializable(typeof(JsonPackageSources))]
    [JsonSerializable(typeof(FlatPackageEntry))]
    [JsonSerializable(typeof(List<FlatPackageEntry>))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, JsonPackageInfo>>))]
    [JsonSerializable(typeof(Dictionary<string, JsonPackageInfo>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, SerializableSortingState>))]
    [JsonSerializable(typeof(SerializableSortingState))]
    [JsonSerializable(typeof(AutoInstallData))]
    [JsonSerializable(typeof(ShadowAutoInstallData))]
    [JsonSerializable(typeof(FavoritesData))]
    [JsonSerializable(typeof(ShadowFavoritesData))]
    [JsonSerializable(typeof(List<PackageDownloadInfo>))]
    [JsonSerializable(typeof(PackageDownloadInfo))]
    public partial class JsonSourceGenerationContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Data models for auto-install functionality
    /// </summary>
    public class AutoInstallData
    {
        public List<string> Names { get; set; } = new List<string>();
    }

    public class ShadowAutoInstallData
    {
        public List<string> Additions { get; set; } = new List<string>();
        public List<string> Removals { get; set; } = new List<string>();
    }

    /// <summary>
    /// Data models for favorites functionality
    /// </summary>
    public class FavoritesData
    {
        public List<string> FavoriteNames { get; set; } = new List<string>();
    }

    public class ShadowFavoritesData
    {
        public List<string> Additions { get; set; } = new List<string>();
        public List<string> Removals { get; set; } = new List<string>();
    }
}
