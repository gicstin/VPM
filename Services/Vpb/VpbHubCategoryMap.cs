using System;
using System.Collections.Generic;

namespace VPM.Services.Vpb
{
    public static class VpbHubCategoryMap
    {
        private static readonly Dictionary<string, string[]> Map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Looks"] = new[] { "Looks" },
                ["Scenes"] = new[] { "Scenes" },
                ["Clothing"] = new[] { "Clothing" },
                ["Hairstyles"] = new[] { "Hair" },
                ["Assets + Accessories"] = new[] { "Assets" },
                ["Plugins + Scripts"] = new[] { "Plugins", "Scripts" },
                ["Morphs"] = new[] { "Morphs" },
                ["Blend Shapes"] = new[] { "Morphs" },
                ["Poses"] = new[] { "Poses" },
                ["Textures"] = new[] { "Textures" },
                ["Environments"] = new[] { "Environments" },
                ["Audio"] = new[] { "Audio" },
                ["Guides"] = new[] { "Guides" },
                ["Demo + Lite"] = new[] { "Demo" },
                ["Toolkits + Templates"] = new[] { "Toolkits" },
                ["Lighting + HDRI"] = new[] { "Lighting" },
                ["Mocap + Animation"] = new[] { "Animation" },
                ["Comics + Storytelling"] = new[] { "Comics" },
                ["Voxta Content"] = new[] { "Voxta" },
                ["Other"] = new[] { "Other" },
            };

        public static string[] ToVpmCategories(string hubCategory)
        {
            if (string.IsNullOrEmpty(hubCategory)) return Array.Empty<string>();
            if (Map.TryGetValue(hubCategory, out var mapped)) return mapped;
            return new[] { VpbHubCategoryNames.Display(hubCategory) };
        }
    }
}
