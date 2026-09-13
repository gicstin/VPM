using System;
using System.Text;

namespace VPM.Services.Vpb
{
    public static class VpbHubCategoryNames
    {
        private static readonly string[] Canonical =
        {
            "Looks", "Scenes", "Clothing", "Assets + Accessories", "Hairstyles",
            "Plugins + Scripts", "Environments", "Textures", "Demo + Lite", "Guides",
            "Morphs", "Poses", "Audio", "Toolkits + Templates", "Lighting + HDRI",
            "Mocap + Animation", "Other", "Comics + Storytelling", "Voxta Content",
            "Blend Shapes",
        };

        public static string Display(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            foreach (var canonical in Canonical)
            {
                if (string.Equals(canonical, value, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }

            var s = value.Trim();
            if (s.Length == 0) return "";

            var sb = new StringBuilder(s.Length);
            var capitalize = true;
            foreach (var c in s)
            {
                if (c == ' ' || c == '+' || c == '-' || c == '/')
                {
                    sb.Append(c);
                    capitalize = true;
                    continue;
                }
                sb.Append(capitalize ? char.ToUpperInvariant(c) : c);
                capitalize = false;
            }
            return sb.ToString();
        }
    }
}
