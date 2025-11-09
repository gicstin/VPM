using System;
using System.Globalization;
using System.Windows.Data;
using VPM.Models;

namespace VPM
{
    public class ContentCountsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not PackageItem package)
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>();

            if (package.MorphCount > 0)
                parts.Add($"Morphs: {package.MorphCount}");
            if (package.HairCount > 0)
                parts.Add($"Hair: {package.HairCount}");
            if (package.ClothingCount > 0)
                parts.Add($"Clothing: {package.ClothingCount}");
            if (package.SceneCount > 0)
                parts.Add($"Scenes: {package.SceneCount}");
            if (package.LooksCount > 0)
                parts.Add($"Looks: {package.LooksCount}");
            if (package.PosesCount > 0)
                parts.Add($"Poses: {package.PosesCount}");
            if (package.AssetsCount > 0)
                parts.Add($"Assets: {package.AssetsCount}");
            if (package.ScriptsCount > 0)
                parts.Add($"Scripts: {package.ScriptsCount}");
            if (package.PluginsCount > 0)
                parts.Add($"Plugins: {package.PluginsCount}");
            if (package.SubScenesCount > 0)
                parts.Add($"SubScenes: {package.SubScenesCount}");
            if (package.SkinsCount > 0)
                parts.Add($"Skins: {package.SkinsCount}");

            return string.Join("   ", parts);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

