using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPM.Models
{
    /// <summary>
    /// Represents a resource from the VaM Hub
    /// </summary>
    public class HubResource : INotifyPropertyChanged
    {
        [JsonPropertyName("resource_id")]
        public string ResourceId { get; set; }

        [JsonPropertyName("discussion_thread_id")]
        public string DiscussionThreadId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("tag_line")]
        public string TagLine { get; set; }

        [JsonPropertyName("version_string")]
        public string VersionString { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }  // "Free" or "Paid"

        [JsonPropertyName("type")]
        public string Type { get; set; }  // "Scenes", "Looks", "Assets", etc.

        [JsonPropertyName("username")]
        public string Creator { get; set; }

        [JsonPropertyName("icon_url")]
        public string IconUrl { get; set; }

        [JsonPropertyName("image_url")]
        public string ImageUrl { get; set; }

        [JsonPropertyName("hubDownloadable")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool HubDownloadable { get; set; }

        [JsonPropertyName("hubHosted")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool HubHosted { get; set; }

        [JsonPropertyName("dependency_count")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int DependencyCount { get; set; }

        [JsonPropertyName("download_count")]
        public string DownloadCountStr { get; set; }

        public int DownloadCount => int.TryParse(DownloadCountStr, out var count) ? count : 0;

        [JsonPropertyName("rating_count")]
        public string RatingCountStr { get; set; }

        public int RatingCount => int.TryParse(RatingCountStr, out var count) ? count : 0;

        [JsonPropertyName("rating_avg")]
        [JsonConverter(typeof(FlexibleFloatConverter))]
        public float RatingAvg { get; set; }

        [JsonPropertyName("last_update")]
        [JsonConverter(typeof(FlexibleLongConverter))]
        public long LastUpdateTimestamp { get; set; }

        public DateTime LastUpdate => LastUpdateTimestamp > 0 
            ? DateTimeOffset.FromUnixTimeSeconds(LastUpdateTimestamp).LocalDateTime 
            : DateTime.MinValue;

        [JsonPropertyName("hubFiles")]
        public List<HubFile> HubFiles { get; set; }

        // UI helper properties
        private bool _inLibrary;
        public bool InLibrary
        {
            get => _inLibrary;
            set { _inLibrary = value; OnPropertyChanged(nameof(InLibrary)); }
        }

        private bool _updateAvailable;
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set { _updateAvailable = value; OnPropertyChanged(nameof(UpdateAvailable)); }
        }

        private string _updateMessage;
        public string UpdateMessage
        {
            get => _updateMessage;
            set { _updateMessage = value; OnPropertyChanged(nameof(UpdateMessage)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Represents a file within a Hub resource
    /// </summary>
    public class HubFile
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        [JsonPropertyName("file_size")]
        public string FileSizeStr { get; set; }

        public long FileSize => long.TryParse(FileSizeStr, out var size) ? size : 0;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("urlHosted")]
        public string UrlHosted { get; set; }

        [JsonPropertyName("licenseType")]
        public string LicenseType { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("latest_version")]
        public string LatestVersion { get; set; }

        [JsonPropertyName("latestUrl")]
        public string LatestUrl { get; set; }

        [JsonPropertyName("promotional_link")]
        public string PromotionalLink { get; set; }

        // Computed properties
        public string EffectiveDownloadUrl => !string.IsNullOrEmpty(DownloadUrl) && DownloadUrl != "null" 
            ? DownloadUrl 
            : UrlHosted;

        public string PackageName => Filename?.Replace(".var", "") ?? "";
    }

    /// <summary>
    /// Represents detailed information about a Hub resource
    /// </summary>
    public class HubResourceDetail : HubResource
    {
        [JsonPropertyName("download_url")]
        public string ExternalDownloadUrl { get; set; }

        [JsonPropertyName("promotional_link")]
        public string PromotionalLink { get; set; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, List<HubFile>> Dependencies { get; set; }

        [JsonPropertyName("review_count")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int ReviewCount { get; set; }

        [JsonPropertyName("update_count")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int UpdateCount { get; set; }

        // Computed URLs
        public string OverviewUrl => $"https://hub.virtamate.com/resources/{ResourceId}/overview-panel";
        public string UpdatesUrl => $"https://hub.virtamate.com/resources/{ResourceId}/updates-panel";
        public string ReviewsUrl => $"https://hub.virtamate.com/resources/{ResourceId}/review-panel";
        public string HistoryUrl => $"https://hub.virtamate.com/resources/{ResourceId}/history-panel";
        public string DiscussionUrl => !string.IsNullOrEmpty(DiscussionThreadId) 
            ? $"https://hub.virtamate.com/threads/{DiscussionThreadId}/discussion-panel" 
            : null;
    }

    /// <summary>
    /// Search parameters for Hub API
    /// </summary>
    public class HubSearchParams
    {
        public int Page { get; set; } = 1;
        public int PerPage { get; set; } = 48;
        public string Location { get; set; } = "Hub And Dependencies";  // "All", "Hub And Dependencies"
        public string Search { get; set; }
        public string PayType { get; set; } = "Free";  // "All", "Free", "Paid"
        public string Category { get; set; } = "All";  // "All", "Scenes", "Looks", "Assets", etc.
        public string Creator { get; set; } = "All";
        public string Tags { get; set; } = "All";
        public string Sort { get; set; } = "Latest Update";
        public bool OnlyDownloadable { get; set; } = true;
    }

    /// <summary>
    /// Response from Hub search API
    /// </summary>
    public class HubSearchResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        [JsonPropertyName("pagination")]
        public HubPagination Pagination { get; set; }

        [JsonPropertyName("resources")]
        public List<HubResource> Resources { get; set; }

        public bool IsSuccess => Status == "success";
    }

    /// <summary>
    /// Pagination info from Hub API
    /// </summary>
    public class HubPagination
    {
        [JsonPropertyName("total_found")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int TotalFound { get; set; }

        [JsonPropertyName("total_pages")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int TotalPages { get; set; }
    }

    /// <summary>
    /// Response from Hub resource detail API
    /// </summary>
    public class HubResourceDetailResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        // The detail fields are at root level, not nested
        public HubResourceDetail Resource { get; set; }

        public bool IsSuccess => Status == "success";
    }

    /// <summary>
    /// Response from Hub findPackages API
    /// </summary>
    public class HubFindPackagesResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        [JsonPropertyName("packages")]
        public Dictionary<string, HubFile> Packages { get; set; }

        public bool IsSuccess => Status == "success";
    }

    /// <summary>
    /// Information about a package available for download from Hub
    /// </summary>
    public class HubPackageInfo
    {
        public string PackageName { get; set; }
        public string DownloadUrl { get; set; }
        public string LatestUrl { get; set; }
        public long FileSize { get; set; }
        public string LicenseType { get; set; }
        public string ResourceId { get; set; }
        public bool IsDependency { get; set; }
        public bool NotOnHub { get; set; }
        public int Version { get; set; }
        public int LatestVersion { get; set; }
    }

    /// <summary>
    /// Download progress for Hub packages
    /// </summary>
    public class HubDownloadProgress
    {
        public string PackageName { get; set; }
        public float Progress { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public bool IsQueued { get; set; }
        public bool IsDownloading { get; set; }
        public bool IsCompleted { get; set; }
        public bool HasError { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// JSON converter that handles int values that may come as strings or numbers
    /// </summary>
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                if (reader.TokenType == JsonTokenType.Number)
                {
                    return reader.GetInt32();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    var str = reader.GetString();
                    if (int.TryParse(str, out var result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FlexibleIntConverter error: {ex.Message}");
            }
            return 0;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// JSON converter that handles boolean values that may come as strings ("true"/"false", "1"/"0", "Y"/"N")
    /// </summary>
    public class FlexibleBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                if (reader.TokenType == JsonTokenType.True)
                    return true;
                if (reader.TokenType == JsonTokenType.False)
                    return false;
                if (reader.TokenType == JsonTokenType.Number)
                    return reader.GetInt32() != 0;
                if (reader.TokenType == JsonTokenType.String)
                {
                    var str = reader.GetString()?.ToLowerInvariant();
                    return str == "true" || str == "1" || str == "y" || str == "yes";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FlexibleBoolConverter error: {ex.Message}");
            }
            return false;
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }

    /// <summary>
    /// JSON converter that handles long values that may come as strings or numbers
    /// </summary>
    public class FlexibleLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                if (reader.TokenType == JsonTokenType.Number)
                {
                    return reader.GetInt64();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    var str = reader.GetString();
                    if (long.TryParse(str, out var result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FlexibleLongConverter error: {ex.Message}");
            }
            return 0L;
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// JSON converter that handles float values that may come as strings or numbers
    /// </summary>
    public class FlexibleFloatConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                if (reader.TokenType == JsonTokenType.Number)
                {
                    return reader.GetSingle();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    var str = reader.GetString();
                    if (float.TryParse(str, out var result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FlexibleFloatConverter error: {ex.Message}");
            }
            return 0f;
        }

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
