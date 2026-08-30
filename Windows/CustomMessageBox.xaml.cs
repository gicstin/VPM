using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPM.Services;

namespace VPM
{
    /// <summary>Themed message box. No Topmost. Owner = active window. Esc/Enter wired via IsCancel/IsDefault.</summary>
    public partial class CustomMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private CustomMessageBox()
        {
            InitializeComponent();
            Loaded += (s, e) => DarkTitleBarHelper.Apply(this);
        }

        public static MessageBoxResult Show(string message, string title = "Message",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            var dialog = new CustomMessageBox
            {
                Title = title
            };

            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible && owner != dialog)
                dialog.Owner = owner;

            dialog.MessageTextBlock.Text = message;
            dialog.SetIcon(icon);
            dialog.SetButtons(buttons);

            dialog.ShowDialog();
            return dialog.Result;
        }

        private void SetIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Information:
                    IconTextBlock.Text = "ℹ️";
                    break;
                case MessageBoxImage.Warning:
                    IconTextBlock.Text = "⚠️";
                    break;
                case MessageBoxImage.Error:
                    IconTextBlock.Text = "❌";
                    break;
                case MessageBoxImage.Question:
                    IconTextBlock.Text = "❓";
                    break;
                default:
                    IconTextBlock.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void SetButtons(MessageBoxButton buttons)
        {
            ButtonPanel.Children.Clear();

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    AddButton("OK", MessageBoxResult.OK, true);
                    break;

                case MessageBoxButton.OKCancel:
                    AddButton("OK", MessageBoxResult.OK, true);
                    AddButton("Cancel", MessageBoxResult.Cancel, false, true);
                    break;

                case MessageBoxButton.YesNo:
                    AddButton("Yes", MessageBoxResult.Yes, true);
                    AddButton("No", MessageBoxResult.No, false, true);
                    break;

                case MessageBoxButton.YesNoCancel:
                    AddButton("Yes", MessageBoxResult.Yes, true);
                    AddButton("No", MessageBoxResult.No, false);
                    AddButton("Cancel", MessageBoxResult.Cancel, false, true);
                    break;
            }
        }

        private void AddButton(string content, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = content,
                IsDefault = isDefault,
                IsCancel = isCancel
            };

            button.Click += (s, e) =>
            {
                Result = result;
                DialogResult = result != MessageBoxResult.Cancel && result != MessageBoxResult.No;
                Close();
            };

            ButtonPanel.Children.Add(button);
        }
    }
}
