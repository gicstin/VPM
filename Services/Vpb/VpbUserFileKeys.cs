using System;
using System.IO;
using VPM.Models;

namespace VPM.Services.Vpb
{
    /// <summary>VPB identity for loose Custom/Saves files: ratings.json uid is the vam-relative path; SQL tags use empty pkg_uid plus that same path.</summary>
    public static class VpbUserFileKeys
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Replace('\\', '/').Trim().TrimStart('/');
        }

        public static string RelativeUid(string vamRoot, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return "";
            if (string.IsNullOrWhiteSpace(vamRoot)) return NormalizePath(filePath);

            try
            {
                var full = Path.GetFullPath(filePath);
                var root = Path.GetFullPath(vamRoot);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return NormalizePath(filePath);
                return NormalizePath(full.Substring(root.Length));
            }
            catch
            {
                return NormalizePath(filePath);
            }
        }

        /// <summary>VPB gallery category for a custom-mode row. Close enough that cat_mem lookups still match.</summary>
        public static string GalleryCategory(CustomAtomItem item)
        {
            var content = item?.ContentType ?? "";
            if (content.Equals("Scene", StringComparison.OrdinalIgnoreCase)) return "Scenes";
            if (content.Equals("Appearance", StringComparison.OrdinalIgnoreCase)) return "Appearance";

            var category = item?.Category ?? "";
            if (category.Equals("Scene", StringComparison.OrdinalIgnoreCase)) return "Scenes";
            if (category.Equals("Atom Person", StringComparison.OrdinalIgnoreCase)) return "Person";
            if (category.Equals("Morphs", StringComparison.OrdinalIgnoreCase)) return "Morphs";
            if (category.Length > 0) return category;
            return "Other";
        }
    }
}
