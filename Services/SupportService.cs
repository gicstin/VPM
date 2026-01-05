using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPM.Services
{
    public class SupporterItem
    {
        public string Name { get; set; }
        public string Tier { get; set; }
        public string Since { get; set; }
        public string IconColor { get; set; } = "#FFFFFF";
        public string Link { get; set; }

        // Helper for display: "👑 SupporterXYZ 👑 - Supporting since 03/05/2025"
        public string DisplayText => $"{Tier} {Name} {Tier} - Supporting since {Since}";
        
        public string InfoText => $" - Supporting since {Since}";
    }

    public class SupportInfo
    {
        public string PatreonLink { get; set; }
        public List<SupporterItem> Supporters { get; set; } = new List<SupporterItem>();
    }

    public class SupporterJson
    {
        public string name { get; set; }
        public string tier { get; set; }
        public string since { get; set; }
        public string iconColor { get; set; }
        public string link { get; set; }
    }

    public class SupportDataJson
    {
        public string patreonLink { get; set; }
        public List<SupporterJson> supporters { get; set; }
    }

    public static class SupportService
    {
        private const string DATA_URL = "https://raw.githubusercontent.com/gicstin/VPM/main/support_data.json";
        private static SupportInfo _cachedInfo;
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task<SupportInfo> GetSupportInfoAsync()
        {
            if (_cachedInfo != null)
                return _cachedInfo;

            try
            {
                // Add a cache buster or user agent if needed
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VPM/1.0");
                
                var json = await _httpClient.GetStringAsync(DATA_URL);
                var data = JsonSerializer.Deserialize<SupportDataJson>(json);

                _cachedInfo = new SupportInfo
                {
                    PatreonLink = Decode(data.patreonLink),
                    Supporters = new List<SupporterItem>()
                };

                if (data.supporters != null)
                {
                    foreach (var s in data.supporters)
                    {
                        var color = Decode(s.iconColor);
                        if (string.IsNullOrEmpty(color)) color = "#FFFFFF";

                        _cachedInfo.Supporters.Add(new SupporterItem
                        {
                            Name = Decode(s.name),
                            Tier = Decode(s.tier),
                            Since = Decode(s.since),
                            IconColor = color,
                            Link = Decode(s.link)
                        });
                    }
                }

                return _cachedInfo;
            }
            catch (Exception ex)
            {
                // Fallback or rethrow
                System.Diagnostics.Debug.WriteLine($"Error fetching support data: {ex.Message}");
                return new SupportInfo
                {
                    PatreonLink = "https://www.patreon.com/gicstin", // Fallback
                    Supporters = new List<SupporterItem> 
                    { 
                        new SupporterItem { Name = "Failed to load supporters list.", Tier = "⚠️", Since = "Now", IconColor = "#FF5555" } 
                    }
                };
            }
        }

        private static string Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return string.Empty;
            try
            {
                byte[] data = Convert.FromBase64String(encoded);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return encoded;
            }
        }
    }
}
