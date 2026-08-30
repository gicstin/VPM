using System;
using System.Globalization;

namespace VPM.Models
{
    /// <summary>User-facing UI scale. 100% is the app default (layout 0.75 relative to OS DPI — the old 75% step). Range 50%–150% in 5% steps.</summary>
    public static class UiScaleLevels
    {
        public const double Default = 1.0;
        public const double Min = 0.5;
        public const double Max = 1.5;
        public const double Step = 0.05;

        /// <summary>Layout multiplier at 100%. Old 75% step is the new default size.</summary>
        public const double LayoutBaseline = 0.75;

        public static readonly double[] MenuPresets = { 0.5, 0.75, 1.0, 1.25, 1.5 };

        public static int TickCount => (int)Math.Round((Max - Min) / Step) + 1;

        public static double Snap(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return Default;

            int percent = (int)Math.Round(Math.Clamp(value, Min, Max) * 100);
            int stepPercent = (int)Math.Round(Step * 100);
            percent = (int)Math.Round(percent / (double)stepPercent) * stepPercent;
            percent = Math.Clamp(percent, (int)Math.Round(Min * 100), (int)Math.Round(Max * 100));
            return percent / 100.0;
        }

        public static int IndexOf(double value)
        {
            int stepPercent = (int)Math.Round(Step * 100);
            int percent = (int)Math.Round(Snap(value) * 100);
            return (percent - (int)Math.Round(Min * 100)) / stepPercent;
        }

        public static double FromIndex(int index)
        {
            index = Math.Clamp(index, 0, TickCount - 1);
            return Snap(Min + index * Step);
        }

        public static bool CanIncrease(double value) => IndexOf(value) < TickCount - 1;

        public static bool CanDecrease(double value) => IndexOf(value) > 0;

        public static double ToLayout(double userScale) => Snap(userScale) * LayoutBaseline;

        public static int ToPercent(double value) => (int)Math.Round(Snap(value) * 100);

        public static string FormatPercent(double value) =>
            ToPercent(value).ToString(CultureInfo.InvariantCulture) + "%";
    }
}
