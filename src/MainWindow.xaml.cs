using Microsoft.UI.Xaml;
using System;

namespace Glance;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set the window icon using absolute path lookup
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        else
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        // Subscribe to closing event to check for unsaved changes
        this.AppWindow.Closing += AppWindow_Closing;
    }

    private bool _isClosingConfirmed = false;

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isClosingConfirmed) return;

        // Check if MainPage has unsaved changes
        if (RootFrame.Content is MainPage mainPage)
        {
            if (mainPage.HasUnsavedChanges)
            {
                // Cancel the closing event synchronously
                args.Cancel = true;

                // Load AutoSaveOnExit preference (defaults to true)
                bool autoSave = true;
                try
                {
                    var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    if (localSettings.Values.TryGetValue("AutoSaveOnExit", out object? autoSaveValue) && autoSaveValue is bool savedAutoSave)
                    {
                        autoSave = savedAutoSave;
                    }
                }
                catch { }

                if (autoSave)
                {
                    // Run the save operation on a background thread to prevent UI thread dispatcher deadlocks
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await mainPage.SaveOriginalPdfSilentAsync(true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to auto-save on close: {ex.Message}");
                        }
                        _isClosingConfirmed = true;
                        System.Environment.Exit(0);
                    });
                }
                else
                {
                    _ = ShowCloseDialog(mainPage);
                }
            }
            else
            {
                // No unsaved changes, exit immediately
                _isClosingConfirmed = true;
                System.Environment.Exit(0);
            }
        }
    }

    private async System.Threading.Tasks.Task ShowCloseDialog(MainPage mainPage)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = Glance.Services.LocalizationService.Get("UnsavedChangesTitle"),
            Content = Glance.Services.LocalizationService.Get("UnsavedChangesContent"),
            PrimaryButtonText = Glance.Services.LocalizationService.Get("SaveAndExit"),
            SecondaryButtonText = Glance.Services.LocalizationService.Get("ExitWithoutSaving"),
            CloseButtonText = Glance.Services.LocalizationService.Get("Cancel"),
            XamlRoot = this.Content.XamlRoot
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                // Run the save operation on a background thread and exit
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await mainPage.SaveOriginalPdfSilentAsync(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to auto-save on close: {ex.Message}");
                    }
                    _isClosingConfirmed = true;
                    System.Environment.Exit(0);
                });
            }
            else if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
            {
                // User selected "Exit without saving"
                _isClosingConfirmed = true;
                System.Environment.Exit(0);
            }
            // If result is CloseButton (Cancel), do nothing (app stays open)
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing close confirmation dialog: {ex.Message}");
            _isClosingConfirmed = true;
            System.Environment.Exit(0);
        }
    }
}
