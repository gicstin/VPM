using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VPM.Services
{
    /// <summary>
    /// Helper class for creating and managing .NET 10 native animations using Storyboards
    /// Optimized for performance with fade-in/out effects and snappy easing functions
    /// </summary>
    public static class AnimationHelper
    {
        // Track active storyboards per element for proper cancellation and cleanup
        private static Dictionary<UIElement, Storyboard> _activeStoryboards = new Dictionary<UIElement, Storyboard>();

        /// <summary>
        /// Stops and cleans up any active storyboard on the element
        /// </summary>
        private static void StopActiveStoryboard(UIElement element)
        {
            if (element == null)
                return;

            if (_activeStoryboards.TryGetValue(element, out var storyboard))
            {
                storyboard.Stop();
                _activeStoryboards.Remove(element);
            }
        }
        /// <summary>
        /// Creates a fade-in animation for opacity (disabled for performance)
        /// </summary>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        /// <param name="fromOpacity">Starting opacity (default 0)</param>
        /// <param name="toOpacity">Ending opacity (default 1)</param>
        /// <returns>DoubleAnimation configured for fade-in</returns>
        public static DoubleAnimation CreateFadeInAnimation(int durationMilliseconds = 0, double fromOpacity = 0, double toOpacity = 1)
        {
            var animation = new DoubleAnimation
            {
                From = toOpacity,
                To = toOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            return animation;
        }

        /// <summary>
        /// Creates a fade-out animation for opacity
        /// </summary>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        /// <param name="fromOpacity">Starting opacity (default 1)</param>
        /// <param name="toOpacity">Ending opacity (default 0)</param>
        /// <returns>DoubleAnimation configured for fade-out</returns>
        public static DoubleAnimation CreateFadeOutAnimation(int durationMilliseconds = 300, double fromOpacity = 1, double toOpacity = 0)
        {
            var animation = new DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            return animation;
        }

        /// <summary>
        /// Applies a professional fade-in animation using storyboard (disabled for performance)
        /// </summary>
        /// <param name="element">The element to animate</param>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        /// <param name="completedCallback">Optional callback when animation completes</param>
        public static void FadeIn(UIElement element, int durationMilliseconds = 0, EventHandler completedCallback = null)
        {
            if (element == null)
                return;

            // Stop any existing animation
            StopActiveStoryboard(element);

            // Set opacity immediately without animation
            element.Opacity = 1;
            element.Visibility = Visibility.Visible;

            // Call completed callback immediately if provided
            completedCallback?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Applies a fade-out animation using storyboard (disabled for performance)
        /// </summary>
        /// <param name="element">The element to animate</param>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        /// <param name="completedCallback">Optional callback when animation completes</param>
        public static void FadeOut(UIElement element, int durationMilliseconds = 0, EventHandler completedCallback = null)
        {
            if (element == null)
                return;

            // Stop any existing animation
            StopActiveStoryboard(element);

            // Set opacity immediately without animation
            element.Opacity = 0;
            element.Visibility = Visibility.Collapsed;

            // Call completed callback immediately if provided
            completedCallback?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Applies a fade animation with automatic visibility management using storyboard
        /// </summary>
        /// <param name="element">The element to animate</param>
        /// <param name="fadeIn">True for fade-in, false for fade-out</param>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        public static void FadeWithVisibility(UIElement element, bool fadeIn, int durationMilliseconds = 300)
        {
            if (element == null)
                return;

            if (fadeIn)
            {
                element.Visibility = Visibility.Visible;
                FadeIn(element, durationMilliseconds);
            }
            else
            {
                FadeOut(element, durationMilliseconds, (s, e) =>
                {
                    element.Visibility = Visibility.Collapsed;
                });
            }
        }

        /// <summary>
        /// Creates a professional staggered fade-in with scale animation for multiple elements (disabled for performance)
        /// </summary>
        /// <param name="elements">Array of elements to animate</param>
        /// <param name="durationMilliseconds">Duration of each animation in milliseconds</param>
        /// <param name="staggerDelayMilliseconds">Delay between each element animation</param>
        public static void StaggeredFadeIn(UIElement[] elements, int durationMilliseconds = 0, int staggerDelayMilliseconds = 0)
        {
            if (elements == null || elements.Length == 0)
                return;

            // Disable staggered animation for performance - show all elements immediately
            foreach (var element in elements)
            {
                FadeIn(element, 0); // Set opacity immediately without delay
            }
        }

        /// <summary>
        /// Creates a snappy snap-in animation with fade-in effect
        /// Uses native .NET 10 storyboard animations optimized for performance
        /// </summary>
        /// <param name="element">The element to animate</param>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        public static void SnapIn(UIElement element, int durationMilliseconds = 250)
        {
            if (element == null)
                return;

            // Ensure we're on the UI thread
            if (element.Dispatcher.CheckAccess())
            {
                PerformSnapIn(element, durationMilliseconds);
            }
            else
            {
                element.Dispatcher.Invoke(() => PerformSnapIn(element, durationMilliseconds));
            }
        }

        /// <summary>
        /// Creates a snap-in animation that avoids flickering during rapid updates
        /// Only animates if element is not already visible or if animation is in progress
        /// </summary>
        /// <param name="element">The element to animate</param>
        /// <param name="durationMilliseconds">Duration of the animation in milliseconds</param>
        public static void SnapInSmooth(UIElement element, int durationMilliseconds = 250)
        {
            if (element == null)
                return;

            // Ensure we're on the UI thread
            if (element.Dispatcher.CheckAccess())
            {
                PerformSnapInSmooth(element, durationMilliseconds);
            }
            else
            {
                element.Dispatcher.Invoke(() => PerformSnapInSmooth(element, durationMilliseconds));
            }
        }

        /// <summary>
        /// Internal method to perform snap-in animation with flicker prevention (disabled for performance)
        /// </summary>
        private static void PerformSnapInSmooth(UIElement element, int durationMilliseconds)
        {
            // If element is already fully visible, skip
            if (element.Opacity >= 0.99)
            {
                return;
            }

            // Stop any existing animation
            StopActiveStoryboard(element);

            // Set opacity immediately without animation
            element.Opacity = 1;
            element.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Internal method to perform snap-in animation with snappy fade-in using storyboard (disabled for performance)
        /// </summary>
        private static void PerformSnapIn(UIElement element, int durationMilliseconds)
        {
            // Stop any existing animation
            StopActiveStoryboard(element);

            // Set opacity immediately without animation
            element.Opacity = 1;
            element.Visibility = Visibility.Visible;
        }
    }
}
