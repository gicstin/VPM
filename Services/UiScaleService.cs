using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>Global UI scale for every window, popup, tooltip, and combo dropdown. Multiplies layout on top of OS DPI. Ctrl+Alt++ / Ctrl+Alt+- / Ctrl+Alt+0.</summary>
    public static class UiScaleService
    {
        private static readonly DependencyProperty BaseCaptionHeightProperty =
            DependencyProperty.RegisterAttached(
                "BaseCaptionHeight",
                typeof(double),
                typeof(UiScaleService),
                new PropertyMetadata(double.NaN));

        private static readonly DependencyProperty PopupHookedProperty =
            DependencyProperty.RegisterAttached(
                "PopupHooked",
                typeof(bool),
                typeof(UiScaleService),
                new PropertyMetadata(false));

        private static ISettingsManager _settings;
        private static bool _initialized;

        public static double Current { get; private set; } = UiScaleLevels.Default;

        public static event Action<double> ScaleChanged;

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(OnPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler(OnContextMenuOpened));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
                new RoutedEventHandler(OnToolTipOpened));
            EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnPopupLoaded));
        }

        public static void Bind(ISettingsManager settings)
        {
            Initialize();
            _settings = settings;
            if (settings?.Settings == null)
                return;

            var snapped = UiScaleLevels.Snap(settings.Settings.UiScale);
            Current = snapped;
            if (Math.Abs(settings.Settings.UiScale - snapped) > 0.0001)
                settings.Settings.UiScale = snapped;

            ApplyToAllWindows();
        }

        public static void SetScale(double scale)
        {
            Initialize();
            var snapped = UiScaleLevels.Snap(scale);
            bool changed = Math.Abs(Current - snapped) > 0.0001;
            Current = snapped;

            if (_settings?.Settings != null && Math.Abs(_settings.Settings.UiScale - Current) > 0.0001)
                _settings.Settings.UiScale = Current;

            ApplyToAllWindows();

            if (changed)
                ScaleChanged?.Invoke(Current);
        }

        public static void Step(int delta)
        {
            SetScale(UiScaleLevels.FromIndex(UiScaleLevels.IndexOf(Current) + delta));
        }

        public static void Reset() => SetScale(UiScaleLevels.Default);

        public static void ApplyToWindow(Window window)
        {
            if (window == null)
                return;

            if (window.Content is FrameworkElement content)
                ApplyTransform(content);

            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome == null)
                return;

            double original = (double)window.GetValue(BaseCaptionHeightProperty);
            if (double.IsNaN(original))
            {
                original = chrome.CaptionHeight;
                window.SetValue(BaseCaptionHeightProperty, original);
            }

            if (original > 0)
                chrome.CaptionHeight = original * UiScaleLevels.ToLayout(Current);
        }

        public static void ApplyToAllWindows()
        {
            var app = Application.Current;
            if (app == null)
                return;

            foreach (Window window in app.Windows)
                ApplyToWindow(window);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
                ApplyToWindow(window);
        }

        private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
                ApplyTransform(menu);
        }

        private static void OnToolTipOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ToolTip tip)
                ApplyTransform(tip);
        }

        private static void OnPopupLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Popup popup)
                return;
            if ((bool)popup.GetValue(PopupHookedProperty))
                return;

            popup.SetValue(PopupHookedProperty, true);
            popup.Opened += (_, _) => ApplyToPopup(popup);
            if (popup.IsOpen)
                ApplyToPopup(popup);
        }

        private static void ApplyToPopup(Popup popup)
        {
            if (popup?.Child is FrameworkElement child)
                ApplyTransform(child);
        }

        private static void ApplyTransform(FrameworkElement element)
        {
            if (element == null)
                return;

            double layout = UiScaleLevels.ToLayout(Current);
            if (Math.Abs(layout - 1.0) < 0.0001)
            {
                element.LayoutTransform = Transform.Identity;
                return;
            }

            var transform = new ScaleTransform(layout, layout);
            transform.Freeze();
            element.LayoutTransform = transform;
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;

            if (mods != (ModifierKeys.Control | ModifierKeys.Alt))
                return;

            if (key is Key.OemPlus or Key.Add)
            {
                Step(1);
                e.Handled = true;
            }
            else if (key is Key.OemMinus or Key.Subtract)
            {
                Step(-1);
                e.Handled = true;
            }
            else if (key is Key.D0 or Key.NumPad0)
            {
                Reset();
                e.Handled = true;
            }
        }
    }
}
