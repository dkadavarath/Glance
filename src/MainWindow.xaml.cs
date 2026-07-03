using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FluentPdfViewer;

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

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isClosingConfirmed) return;

        // Check if MainPage has unsaved changes
        if (RootFrame.Content is MainPage mainPage && mainPage.HasUnsavedChanges)
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
                try
                {
                    // Attempt silent auto-save of original PDF before exiting
                    await mainPage.SaveOriginalPdfSilentAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to auto-save on close: {ex.Message}");
                }
                _isClosingConfirmed = true;
                this.Close(); // Close again
            }
            else
            {
                // Show confirmation dialog asynchronously
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "Cambios sin guardar",
                    Content = "Tienes dibujos, marcas o notas sin guardar en el archivo PDF. ¿Estás seguro de que deseas salir sin guardar?",
                    PrimaryButtonText = "Salir sin guardar",
                    CloseButtonText = "Cancelar",
                    XamlRoot = this.Content.XamlRoot
                };

                try
                {
                    var result = await dialog.ShowAsync();
                    if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    {
                        _isClosingConfirmed = true;
                        this.Close(); // Call Close() again to trigger the event and proceed
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing close confirmation dialog: {ex.Message}");
                    // In case of dialog error, allow close to prevent user being stuck
                    _isClosingConfirmed = true;
                    this.Close();
                }
            }
        }
    }
}
