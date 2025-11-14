using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VPM.Services;
using VPM.Models;

namespace VPM
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Set shutdown mode to manual so closing setup window doesn't close app
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Global exception handlers to catch crashes
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            // Check if this is the first launch
            HandleFirstLaunch();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            LogException("AppDomain.UnhandledException", exception);
            
            MessageBox.Show(
                $"A fatal error occurred:\n\n{exception?.Message}\n\nStack Trace:\n{exception?.StackTrace}",
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("Dispatcher.UnhandledException", e.Exception);
            
            MessageBox.Show(
                $"An unhandled error occurred:\n\n{e.Exception.Message}\n\nStack Trace:\n{e.Exception.StackTrace}",
                "Unhandled Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            
            // Mark as handled to prevent crash
            e.Handled = true;
        }

        private void LogException(string source, Exception exception)
        {
            if (exception == null) return;
            
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"[{source}] EXCEPTION CAUGHT");
            Console.WriteLine($"{'='*80}");
            Console.WriteLine($"Type: {exception.GetType().FullName}");
            Console.WriteLine($"Message: {exception.Message}");
            Console.WriteLine($"Stack Trace:\n{exception.StackTrace}");
            
            if (exception.InnerException != null)
            {
                Console.WriteLine($"\nInner Exception:");
                Console.WriteLine($"Type: {exception.InnerException.GetType().FullName}");
                Console.WriteLine($"Message: {exception.InnerException.Message}");
                Console.WriteLine($"Stack Trace:\n{exception.InnerException.StackTrace}");
            }
            
            Console.WriteLine($"{'='*80}\n");
        }

        /// <summary>
        /// Handles first launch setup
        /// </summary>
        private void HandleFirstLaunch()
        {
            try
            {
                // Load settings to check if this is first launch
                var settingsManager = new SettingsManager();
                
                Console.WriteLine($"[App] IsFirstLaunch: {settingsManager.Settings.IsFirstLaunch}");
                Console.WriteLine($"[App] SelectedFolder: {settingsManager.Settings.SelectedFolder}");
                
                if (settingsManager.Settings.IsFirstLaunch)
                {
                    // Show first launch setup window
                    var setupWindow = new FirstLaunchSetup();
                    var result = setupWindow.ShowDialog();
                    
                    if (result == true && !string.IsNullOrEmpty(setupWindow.SelectedGamePath))
                    {
                        // Save the selected path
                        settingsManager.Settings.SelectedFolder = setupWindow.SelectedGamePath;
                        settingsManager.Settings.IsFirstLaunch = false;
                        
                        try
                        {
                            settingsManager.SaveSettingsImmediate();
                            Console.WriteLine($"[App] Settings saved successfully after first launch setup");
                        }
                        catch (Exception saveEx)
                        {
                            Console.WriteLine($"[App] Warning: Failed to save settings after first launch setup: {saveEx.Message}");
                            MessageBox.Show(
                                $"Settings could not be saved to disk:\n\n{saveEx.Message}\n\n" +
                                "The application will continue to work, but your settings may not persist between sessions.",
                                "Settings Save Warning",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                        
                        // Create and show main window with the settings manager instance
                        try
                        {
                            var mainWindow = new MainWindow(settingsManager);
                            MainWindow = mainWindow;
                            
                            // Change shutdown mode to close when main window closes
                            ShutdownMode = ShutdownMode.OnMainWindowClose;
                            
                            mainWindow.Show();
                        }
                        catch (Exception mainWindowEx)
                        {
                            MessageBox.Show(
                                $"Failed to create main window:\n\n{mainWindowEx.Message}\n\nStack Trace:\n{mainWindowEx.StackTrace}",
                                "Main Window Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            Shutdown();
                        }
                    }
                    else
                    {
                        // User cancelled setup, exit application
                        MessageBox.Show(
                            "Setup was cancelled. The application will now close.\n\n" +
                            "You can run the application again to complete the setup.",
                            "Setup Cancelled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    // Not first launch, show main window normally with loaded settings
                    try
                    {
                        var mainWindow = new MainWindow(settingsManager);
                        MainWindow = mainWindow;
                        
                        // Change shutdown mode to close when main window closes
                        ShutdownMode = ShutdownMode.OnMainWindowClose;
                        
                        mainWindow.Show();
                    }
                    catch (Exception mainWindowEx)
                    {
                        MessageBox.Show(
                            $"Failed to create main window:\n\n{mainWindowEx.Message}\n\nStack Trace:\n{mainWindowEx.StackTrace}",
                            "Main Window Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        Shutdown();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"An error occurred during first launch setup:\n\n{ex.Message}",
                    "Setup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }
    }
}

