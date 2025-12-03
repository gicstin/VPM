using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using VPM.Services;

namespace VPM
{
    public partial class DarkMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

        private DarkMessageBox()
        {
            InitializeComponent();
            Loaded += (s, e) => DarkTitleBarHelper.Apply(this);
        }

        public static MessageBoxResult Show(string message, string title = "Message", 
            MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            var messageBox = new DarkMessageBox();
            messageBox.TitleText.Text = title;
            messageBox.MessageText.Text = message;
            
            // Set icon
            switch (icon)
            {
                case MessageBoxImage.Information:
                    messageBox.IconText.Text = "ℹ️";
                    messageBox.IconText.Foreground = System.Windows.Media.Brushes.CornflowerBlue;
                    break;
                case MessageBoxImage.Warning:
                    messageBox.IconText.Text = "⚠️";
                    messageBox.IconText.Foreground = System.Windows.Media.Brushes.Orange;
                    break;
                case MessageBoxImage.Error:
                    messageBox.IconText.Text = "❌";
                    messageBox.IconText.Foreground = System.Windows.Media.Brushes.Red;
                    break;
                case MessageBoxImage.Question:
                    messageBox.IconText.Text = "?";
                    messageBox.IconText.Foreground = System.Windows.Media.Brushes.CornflowerBlue;
                    break;
            }
            
            // Set buttons
            switch (button)
            {
                case MessageBoxButton.OK:
                    messageBox.Button1.Content = "OK";
                    messageBox.Button1.IsDefault = true;
                    messageBox.Button2.Visibility = Visibility.Collapsed;
                    break;
                case MessageBoxButton.YesNo:
                    messageBox.Button1.Content = "Yes";
                    messageBox.Button1.IsDefault = true;
                    messageBox.Button2.Content = "No";
                    messageBox.Button2.Visibility = Visibility.Visible;
                    break;
                case MessageBoxButton.YesNoCancel:
                    messageBox.Button1.Content = "Yes";
                    messageBox.Button1.IsDefault = true;
                    messageBox.Button2.Content = "No";
                    messageBox.Button2.Visibility = Visibility.Visible;
                    // For simplicity, we'll treat this as YesNo for now
                    break;
            }
            
            messageBox.ShowDialog();
            return messageBox.Result;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Cancel;
            Close();
        }

        private void Button1_Click(object sender, RoutedEventArgs e)
        {
            if (Button1.Content.ToString() == "OK")
                Result = MessageBoxResult.OK;
            else if (Button1.Content.ToString() == "Yes")
                Result = MessageBoxResult.Yes;
            
            Close();
        }

        private void Button2_Click(object sender, RoutedEventArgs e)
        {
            if (Button2.Content.ToString() == "No")
                Result = MessageBoxResult.No;
            else
                Result = MessageBoxResult.Cancel;
            
            Close();
        }

        private void MessageText_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToVerticalOffset(textBox.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }
}

