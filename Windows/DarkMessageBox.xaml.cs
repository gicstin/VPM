using System.Windows;

namespace VPM
{
    /// <summary>Compatibility wrapper — all calls go through CustomMessageBox (themed, not Topmost).</summary>
    public partial class DarkMessageBox : Window
    {
        public static MessageBoxResult Show(string message, string title = "Message",
            MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            return CustomMessageBox.Show(message, title, button, icon);
        }
    }
}
