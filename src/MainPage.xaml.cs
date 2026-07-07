using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Data.Pdf;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Glance.Services;

namespace Glance;

public enum EditMode
{
    Navigate,
    Highlight,
    Note,
    Pen,
    Eraser
}

public enum AnnotationType
{
    Highlight,
    Note,
    Pen
}

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    public bool HasUnsavedChanges { get; set; } = false;

    private Windows.Storage.StorageFile? _pendingFileToLoad;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Windows.Storage.StorageFile file)
        {
            _pendingFileToLoad = file;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private PdfRenderService? _pdfRenderService;
    private CancellationTokenSource? _backgroundRenderCts;
    private Task? _backgroundRenderTask;
    private readonly SemaphoreSlim _ffiSemaphore = new(1, 1);
    private PdfDocument? _pdfDocument;  // Keep reference for lazy rendering
    private ObservableCollection<PdfPageViewModel> _pages = new();
    private int _currentPageIndex = 0;
    private bool _isScrollingProgrammatically = false;
    private bool _isUnloading = false;
    private string _currentPdfPath = "";
    private PersistenceService _persistenceService = new();
    private AnnotationService _annotationService = new();
    private uint _totalPages = 0;
    
    // Annotation states
    private EditMode _currentMode = EditMode.Navigate;
    private bool _isDrawingHighlight = false;
    private bool _isErasing = false;
    private Point _startPoint;
    private AnnotationViewModel? _activeHighlight;
    private string _activeColorHex = "#FFFF00"; // Default Yellow

    // Undo/Redo history stack
    private List<(PdfPageViewModel Page, AnnotationViewModel Annotation)> _annotationHistory = new();

    // Recent Files collection
    private ObservableCollection<RecentDocumentViewModel> _recentDocs = new();

    // Secondary PDF pages (for Split View comparison)
    private ObservableCollection<PdfPageViewModel> _pagesRight = new();

    // Search states
    private List<SearchMatch> _searchMatches = new();
    private int _currentMatchIndex = -1;
    private Microsoft.UI.Xaml.DispatcherTimer? _searchDebounceTimer;

    public MainPage()
    {
        Current = this;
        this.InitializeComponent();
        PagesRepeater.ItemsSource = _pages;
        PageListView.ItemsSource = _pages;
        RecentGridView.ItemsSource = _recentDocs;

        // Start at welcome screen
        WelcomePanel.Visibility = Visibility.Visible;
        SidebarSplitView.Visibility = Visibility.Collapsed;

        LoadRecentFiles();
        this.Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeSettings();
        InitializeLocalization();
        if (_pendingFileToLoad != null)
        {
            DocTitleText.Text = _pendingFileToLoad.Name;
            await LoadPdfAsync(_pendingFileToLoad);
            _pendingFileToLoad = null;
        }
    }

    private void InitializeLocalization()
    {
        try
        {
            // Set all UI element strings based on system language (detected in LocalizationService)
            bool isEs = Glance.Services.LocalizationService.IsSpanish;

            // Toolbars & Buttons ToolTips
            if (ToggleSidebarButton != null) ToolTipService.SetToolTip(ToggleSidebarButton, Glance.Services.LocalizationService.Get("ToggleSidebar"));
            if (OpenPdfButton != null) ToolTipService.SetToolTip(OpenPdfButton, Glance.Services.LocalizationService.Get("OpenPdfFile"));
            if (OpenButtonText != null) OpenButtonText.Text = Glance.Services.LocalizationService.Get("Open");
            if (HomeButton != null) ToolTipService.SetToolTip(HomeButton, Glance.Services.LocalizationService.Get("HomeTooltip"));
            if (HomeButtonText != null) HomeButtonText.Text = Glance.Services.LocalizationService.Get("Home");

            if (PrevPageButton != null) ToolTipService.SetToolTip(PrevPageButton, Glance.Services.LocalizationService.Get("PrevPage"));
            if (NextPageButton != null) ToolTipService.SetToolTip(NextPageButton, Glance.Services.LocalizationService.Get("NextPage"));

            if (ReadModeButton != null) ToolTipService.SetToolTip(ReadModeButton, Glance.Services.LocalizationService.Get("ReadMode"));
            if (HighlightModeButton != null) ToolTipService.SetToolTip(HighlightModeButton, Glance.Services.LocalizationService.Get("HighlightMode"));
            if (NoteModeButton != null) ToolTipService.SetToolTip(NoteModeButton, Glance.Services.LocalizationService.Get("NoteMode"));
            if (PenModeButton != null) ToolTipService.SetToolTip(PenModeButton, Glance.Services.LocalizationService.Get("PenMode"));
            if (EraserModeButton != null) ToolTipService.SetToolTip(EraserModeButton, Glance.Services.LocalizationService.Get("EraserMode"));

            // Colors ToolTips
            if (ColorYellowButton != null) ToolTipService.SetToolTip(ColorYellowButton, Glance.Services.LocalizationService.Get("ColorYellow"));
            if (ColorGreenButton != null) ToolTipService.SetToolTip(ColorGreenButton, Glance.Services.LocalizationService.Get("ColorGreen"));
            if (ColorCyanButton != null) ToolTipService.SetToolTip(ColorCyanButton, Glance.Services.LocalizationService.Get("ColorCyan"));
            if (ColorMagentaButton != null) ToolTipService.SetToolTip(ColorMagentaButton, Glance.Services.LocalizationService.Get("ColorMagenta"));
            if (ColorRedButton != null) ToolTipService.SetToolTip(ColorRedButton, Glance.Services.LocalizationService.Get("ColorRed"));
            if (ColorBlueButton != null) ToolTipService.SetToolTip(ColorBlueButton, Glance.Services.LocalizationService.Get("ColorBlue"));
            if (ColorBlackButton != null) ToolTipService.SetToolTip(ColorBlackButton, Glance.Services.LocalizationService.Get("ColorBlack"));

            // Sliders
            if (ThicknessLabel != null) ThicknessLabel.Text = Glance.Services.LocalizationService.Get("Thickness");
            if (OpacityLabel != null) OpacityLabel.Text = Glance.Services.LocalizationService.Get("Opacity");

            // ComboBox & Buttons
            if (ZoomFitWidthItem != null) ZoomFitWidthItem.Content = Glance.Services.LocalizationService.Get("ThemeSystem").Equals("System default") ? "Fit Width" : "Ajustar Ancho"; // Special fallback for zoom option
            if (RotateButton != null) ToolTipService.SetToolTip(RotateButton, Glance.Services.LocalizationService.Get("Rotate"));
            if (SplitViewToggleButton != null) ToolTipService.SetToolTip(SplitViewToggleButton, Glance.Services.LocalizationService.Get("SplitView"));
            if (SaveButton != null) ToolTipService.SetToolTip(SaveButton, Glance.Services.LocalizationService.Get("SaveOriginal"));
            if (SettingsButton != null) ToolTipService.SetToolTip(SettingsButton, Glance.Services.LocalizationService.Get("Settings"));

            // Settings Panel
            if (SettingsTitleText != null) SettingsTitleText.Text = Glance.Services.LocalizationService.Get("Settings");
            if (AppThemeLabel != null) AppThemeLabel.Text = Glance.Services.LocalizationService.Get("AppTheme");

            if (ThemeLightItem != null) ThemeLightItem.Content = Glance.Services.LocalizationService.Get("ThemeLight");
            if (ThemeDarkItem != null) ThemeDarkItem.Content = Glance.Services.LocalizationService.Get("ThemeDark");
            if (ThemeDefaultItem != null) ThemeDefaultItem.Content = Glance.Services.LocalizationService.Get("ThemeSystem");

            if (AutoSaveToggle != null) AutoSaveToggle.Header = Glance.Services.LocalizationService.Get("AutoSaveHeader");
            if (AutoSaveSubtext != null) AutoSaveSubtext.Text = Glance.Services.LocalizationService.Get("AutoSaveSubtext");

            if (AboutTitleText != null) AboutTitleText.Text = Glance.Services.LocalizationService.Get("AboutTitle");
            if (DeveloperText != null) DeveloperText.Text = Glance.Services.LocalizationService.Get("Developer");

            if (WebsiteLink != null) WebsiteLink.Content = Glance.Services.LocalizationService.Get("WebsiteLink");
            if (GitHubLink != null) GitHubLink.Content = Glance.Services.LocalizationService.Get("GitHubLink");

            // Welcome Screen
            if (WelcomeSubtitleText != null) WelcomeSubtitleText.Text = Glance.Services.LocalizationService.Get("WelcomeSubtitle");
            if (WelcomeOpenButtonText != null) WelcomeOpenButtonText.Text = Glance.Services.LocalizationService.Get("WelcomeOpenButton");
            if (RecientesHeader != null) RecientesHeader.Text = Glance.Services.LocalizationService.Get("RecentDocuments");

            // Sidebar
            if (ThumbnailsHeader != null) ThumbnailsHeader.Text = Glance.Services.LocalizationService.Get("Thumbnails");

            // Comparison
            if (OpenSecondPdfButton != null) ToolTipService.SetToolTip(OpenSecondPdfButton, Glance.Services.LocalizationService.Get("OpenSecondPdf"));
            if (OpenSecondPdfButtonText != null) OpenSecondPdfButtonText.Text = Glance.Services.LocalizationService.Get("OpenSecondPdfText");
            if (SecondaryTitleText != null) SecondaryTitleText.Text = Glance.Services.LocalizationService.Get("SideBySideComparer");
            if (CloseSecondPdfButton != null) ToolTipService.SetToolTip(CloseSecondPdfButton, Glance.Services.LocalizationService.Get("CloseComparison"));

            // Search Panel
            if (SearchTextBox != null) SearchTextBox.PlaceholderText = Glance.Services.LocalizationService.Get("SearchPlaceholder");
            if (SearchPrevButton != null) ToolTipService.SetToolTip(SearchPrevButton, Glance.Services.LocalizationService.Get("SearchPrev"));
            if (SearchNextButton != null) ToolTipService.SetToolTip(SearchNextButton, Glance.Services.LocalizationService.Get("SearchNext"));
            if (SearchCloseButton != null) ToolTipService.SetToolTip(SearchCloseButton, Glance.Services.LocalizationService.Get("SearchClose"));

            UpdateSearchStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing localization: {ex.Message}");
        }
    }

    private void InitializeSettings()
    {
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            
            // Load theme
            string themeStr = "Default";
            if (localSettings.Values.TryGetValue("AppTheme", out object? themeValue) && themeValue is string savedTheme)
            {
                themeStr = savedTheme;
            }

            // Set ComboBox index
            int selectedIndex = 2; // Default (System)
            if (themeStr == "Light") selectedIndex = 0;
            else if (themeStr == "Dark") selectedIndex = 1;

            if (ThemeComboBox != null)
            {
                ThemeComboBox.SelectedIndex = selectedIndex;
            }

            // Apply theme
            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                var mainWindow = (Application.Current as App)?.MainWindow;
                if (mainWindow?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                }
            }

            // Load AutoSave toggle
            bool autoSave = true;
            if (localSettings.Values.TryGetValue("AutoSaveOnExit", out object? autoSaveValue) && autoSaveValue is bool savedAutoSave)
            {
                autoSave = savedAutoSave;
            }

            if (AutoSaveToggle != null)
            {
                AutoSaveToggle.IsOn = autoSave;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing settings: {ex.Message}");
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox == null) return;
        
        if (ThemeComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string themeStr)
        {
            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                var mainWindow = (Application.Current as App)?.MainWindow;
                if (mainWindow?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                }

                try
                {
                    var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    localSettings.Values["AppTheme"] = themeStr;
                }
                catch { }
            }
        }
    }

    private void AutoSaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoSaveToggle == null) return;
        
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["AutoSaveOnExit"] = AutoSaveToggle.IsOn;
        }
        catch { }
    }

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        
        // WinUI 3 desktop apps need to associate the picker with the window handle (HWND)
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        StorageFile file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            DocTitleText.Text = file.Name;
            await LoadPdfAsync(file);
        }
    }

    private async void SavePdf_Click(object sender, RoutedEventArgs e)
    {
        await SaveOriginalPdfAsync();
    }

    private async void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        _isUnloading = true;
        if (HasUnsavedChanges)
        {
            // Load AutoSaveOnExit preference
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
                    await SaveOriginalPdfSilentAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to auto-save on returning home: {ex.Message}");
                }
            }
            else
            {
                // Show confirmation dialog
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
                        try
                        {
                            await SaveOriginalPdfSilentAsync();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to save on returning home: {ex.Message}");
                        }
                    }
                    else if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
                    {
                        // Proceed without saving
                    }
                    else
                    {
                        // Cancel going home
                        _isUnloading = false;
                        return;
                    }
                }
                catch
                {
                    // Fallback
                }
            }
        }

        // Cancel background rendering FIRST to release the semaphore and prevent deadlocks!
        if (_backgroundRenderCts != null)
        {
            try { _backgroundRenderCts.Cancel(); } catch { }
        }

        // Proceed to unload document and return home under the FFI semaphore lock
        await _ffiSemaphore.WaitAsync();
        try
        {
            await CancelBackgroundRenderAsync();
            
            _pdfDocument = null;
            if (_pdfRenderService != null)
            {
                _pdfRenderService.Dispose();
                _pdfRenderService = null;
            }

            _pages.Clear();
            _annotationHistory.Clear();
            _currentPageIndex = 0;
            _currentPdfPath = "";
            HasUnsavedChanges = false;

            DocTitleText.Text = "Glance";
            TotalPagesText.Text = "/ 0";
            PageNumberInput.Text = "1";

            // Toggle panels visibility
            WelcomePanel.Visibility = Visibility.Visible;
            SidebarSplitView.Visibility = Visibility.Collapsed;
            SaveButton.Visibility = Visibility.Collapsed;
            if (SplitViewToggleButton != null) SplitViewToggleButton.Visibility = Visibility.Collapsed;
            if (HomeButton != null) HomeButton.Visibility = Visibility.Collapsed;

            LoadRecentFiles();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error unloading document: {ex.Message}");
        }
        finally
        {
            _ffiSemaphore.Release();
        }
    }

    public async Task CleanupForExitAsync()
    {
        // Cancel background rendering FIRST to release the semaphore and prevent deadlocks!
        if (_backgroundRenderCts != null)
        {
            try { _backgroundRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            await CancelBackgroundRenderAsync();
            _pdfDocument = null;
            if (_pdfRenderService != null)
            {
                _pdfRenderService.Dispose();
                _pdfRenderService = null;
            }
        }
        finally
        {
            _ffiSemaphore.Release();
        }
    }

    public async Task<bool> SaveOriginalPdfSilentAsync(bool onExit = false)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return false;

        // Cancel background rendering FIRST to release the semaphore and prevent deadlocks!
        if (_backgroundRenderCts != null)
        {
            try { _backgroundRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            // 1. Gather all current annotations
            var savedList = new List<SavedAnnotation>();
            foreach (var page in _pages)
            {
                foreach (var anno in page.Annotations)
                {
                    var savedAnno = new SavedAnnotation
                    {
                        PageIndex = page.PageIndex,
                        Type = anno.Type,
                        X = anno.X,
                        Y = anno.Y,
                        Width = anno.Width,
                        Height = anno.Height,
                        Content = anno.Content ?? "",
                        ColorHex = anno.ColorHex,
                        Thickness = anno.Thickness,
                        RotationAngle = page.RotationAngle
                    };

                    foreach (var pt in anno.PointsCollection)
                    {
                        savedAnno.Points.Add(new SavedPoint { X = pt.X, Y = pt.Y });
                    }

                    savedList.Add(savedAnno);
                }
            }

            var pageRotations = new Dictionary<int, double>();
            foreach (var page in _pages)
            {
                pageRotations[page.PageIndex] = page.RotationAngle;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
            
            try
            {
                if (SaveButton != null) SaveButton.IsEnabled = false;

                // Cancel background rendering task to avoid accessing native engine during/after cleanup
                await CancelBackgroundRenderAsync();

                // 1. Clear resources to unlock original file BEFORE exporting (so PdfSharp can access it)
                _pdfDocument = null;
                if (_pdfRenderService != null)
                {
                    _pdfRenderService.Dispose();
                    _pdfRenderService = null;
                }

                // 2. Export annotations to the temporary file
                var exportService = new Glance.Services.PdfExportService();
                await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, tempFile, savedList, pageRotations);

                // 3. Overwrite original file
                File.Copy(tempFile, _currentPdfPath, true);
                try { File.Delete(tempFile); } catch { }

                // Reset the rotation angle since rotation is now permanently applied in the PDF file itself.
                // Keep the annotations in memory so they remain editable and clickable.
                foreach (var page in _pages)
                {
                    page.RotationAngle = 0.0;
                }
                HasUnsavedChanges = false;

                // Sync the annotations back to the JSON file (since rotation was reset to 0)
                await SaveAnnotationsInternalAsync();

                if (onExit)
                {
                    // Exit early without re-rendering or reloading PDF in memory to avoid shutdown deadlocks
                    return true;
                }

                // 4. Reload PDF structures in memory without resetting the page list (prevents scroll jumps and double refreshes)
                StorageFile file = await StorageFile.GetFileFromPathAsync(_currentPdfPath);
                using (var fileStream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var memStream = new InMemoryRandomAccessStream();
                    await RandomAccessStream.CopyAsync(fileStream, memStream);
                    memStream.Seek(0);
                    _pdfDocument = await PdfDocument.LoadFromStreamAsync(memStream);
                }

                // Re-initialize Rust PDF engine with the updated file
                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(_currentPdfPath);

                // Re-render the current page smoothly
                await RenderPageAsync(_pdfDocument, (uint)_currentPageIndex);

                // Start background rendering of remaining pages silently to update their cache
                _backgroundRenderCts = new CancellationTokenSource();
                _backgroundRenderTask = RenderRemainingPagesAsync(_pdfDocument, 0, _backgroundRenderCts.Token);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error silent saving PDF: {ex.Message}");
                try
                {
                    string logPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "save_error.txt");
                    File.WriteAllText(logPath, $"Time: {DateTime.Now}\nError: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException?.Message}");
                }
                catch { }
                return false;
            }
        }
        finally
        {
            _ffiSemaphore.Release();
            if (SaveButton != null) SaveButton.IsEnabled = true;
        }
    }

    private async Task SaveOriginalPdfAsync()
    {
        bool success = await SaveOriginalPdfSilentAsync();
        if (!success)
        {
            // If overwrite fails (e.g. read-only), fallback to Save As
            var dialog = new ContentDialog
            {
                Title = "No se pudo sobrescribir",
                Content = "No se pudo sobrescribir el archivo original. ¿Deseas guardar una copia en otra ubicación?",
                PrimaryButtonText = "Guardar Copia",
                CloseButtonText = "Cancelar",
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                ExportPdf_Click(this, new RoutedEventArgs());
            }
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return;

        var picker = new FileSavePicker();
        
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_currentPdfPath) + "_annotated";

        StorageFile file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {

                // 1. Gather all current annotations
                var savedList = new List<SavedAnnotation>();
                foreach (var page in _pages)
                {
                    foreach (var anno in page.Annotations)
                    {
                        var savedAnno = new SavedAnnotation
                        {
                            PageIndex = page.PageIndex,
                            Type = anno.Type,
                            X = anno.X,
                            Y = anno.Y,
                            Width = anno.Width,
                            Height = anno.Height,
                            Content = anno.Content ?? "",
                            ColorHex = anno.ColorHex,
                            Thickness = anno.Thickness,
                            RotationAngle = page.RotationAngle
                        };

                        foreach (var pt in anno.PointsCollection)
                        {
                            savedAnno.Points.Add(new SavedPoint { X = pt.X, Y = pt.Y });
                        }

                        savedList.Add(savedAnno);
                    }
                }

                var pageRotations = new Dictionary<int, double>();
                foreach (var page in _pages)
                {
                    pageRotations[page.PageIndex] = page.RotationAngle;
                }

                // 2. Export using PdfExportService
                var exportService = new Glance.Services.PdfExportService();
                await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, file.Path, savedList, pageRotations);

                HasUnsavedChanges = false;

                // 3. Show success dialog
                var dialog = new ContentDialog
                {
                    Title = "Exportación Exitosa",
                    Content = $"El archivo PDF con tus firmas y anotaciones se ha guardado en:\n\n{file.Path}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "Error de Exportación",
                    Content = $"No se pudo exportar el PDF: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
            }
        }
    }

    private async Task CancelBackgroundRenderAsync()
    {
        var cts = _backgroundRenderCts;

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cancelling CTS: {ex.Message}");
            }
        }

        if (cts == _backgroundRenderCts) _backgroundRenderCts = null;
        _backgroundRenderTask = null;

        try
        {
            cts?.Dispose();
        }
        catch { }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Render all remaining pages sequentially in background
    /// </summary>
    private async Task RenderRemainingPagesAsync(PdfDocument pdfDocument, uint startPage, CancellationToken cancellationToken)
    {
        try
        {
            for (uint i = startPage; i < pdfDocument.PageCount; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Add small delay to prevent UI lag
                await Task.Delay(10, cancellationToken);

                if (cancellationToken.IsCancellationRequested) break;

                await RenderPageAsync(pdfDocument, i, cancellationToken);
            }

            System.Diagnostics.Debug.WriteLine("✓ All pages rendered");
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Background rendering task cancelled.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ Error rendering remaining pages: {ex.Message}");
        }
    }

    /// <summary>
    /// Render a single page on demand (lazy loading)
    /// </summary>
    private async Task RenderPageAsync(PdfDocument pdfDocument, uint pageIndex, CancellationToken cancellationToken = default)
    {
        if (_isUnloading || pageIndex >= _pages.Count) return;

        try
        {
            var renderService = _pdfRenderService;
            if (renderService != null && !_isUnloading)
            {
                try
                {
                    if (pageIndex >= _pages.Count || _isUnloading) return;
                    var pageVm = _pages[(int)pageIndex];
                    uint width = (uint)(pageVm.PageWidth * 2.0);
                    uint height = (uint)(pageVm.PageHeight * 2.0);
                    
                    if (cancellationToken.IsCancellationRequested || _isUnloading) return;
                    
                    byte[] pngBytes = await renderService.RenderPageAsync(pageIndex, width, height, 96.0f, cancellationToken);
                    
                    if (cancellationToken.IsCancellationRequested || _isUnloading || pageIndex >= _pages.Count) return;
                    
                    var rustStream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(rustStream))
                    {
                        writer.WriteBytes(pngBytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }
                    
                    if (_isUnloading || pageIndex >= _pages.Count) return;
                    var rustBitmap = new BitmapImage();
                    rustStream.Seek(0);
                    await rustBitmap.SetSourceAsync(rustStream);
                    
                    if (_isUnloading || pageIndex >= _pages.Count) return;
                    _pages[(int)pageIndex].ImageSource = rustBitmap;
                    _pages[(int)pageIndex].IsLoading = false;
                    
                    if (pageIndex == 0 && !string.IsNullOrEmpty(_currentPdfPath) && !_isUnloading)
                    {
                        try
                        {
                            string safeName = _currentPdfPath.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
                            string thumbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, $"thumb_{safeName}.png");
                            using (var fileStream = File.Create(thumbPath))
                            {
                                fileStream.Write(pngBytes, 0, pngBytes.Length);
                            }
                        }
                        catch { }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"✓ Page {pageIndex} rendered using Rust PDFium");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Rust PDFium render failed, falling back to UWP: {ex.Message}");
                }
            }

            using PdfPage page = pdfDocument.GetPage(pageIndex);

            // Render page to stream
            var renderStream = new InMemoryRandomAccessStream();
            var renderOptions = new PdfPageRenderOptions();
            renderOptions.DestinationWidth = (uint)(page.Size.Width * 2.0);

            await page.RenderToStreamAsync(renderStream, renderOptions);

            // Create bitmap from stream
            var bitmap = new BitmapImage();
            renderStream.Seek(0);
            await bitmap.SetSourceAsync(renderStream);

            // Update page in collection
            _pages[(int)pageIndex].ImageSource = bitmap;
            _pages[(int)pageIndex].IsLoading = false;

            System.Diagnostics.Debug.WriteLine($"✓ Page {pageIndex} rendered");

            // Save thumbnail if first page
            if (pageIndex == 0)
            {
                try
                {
                    string safeName = _currentPdfPath.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
                    string thumbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, $"thumb_{safeName}.png");

                    renderStream.Seek(0);
                    using (var fileStream = File.Create(thumbPath))
                    {
                        renderStream.AsStreamForRead().CopyTo(fileStream);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ Failed to render page {pageIndex}: {ex.Message}");
        }
    }

    private async Task LoadPdfAsync(StorageFile file)
    {
        if (file == null) return;

        // Cancel background rendering FIRST to release the semaphore and prevent deadlocks!
        if (_backgroundRenderCts != null)
        {
            try { _backgroundRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            // Cancel background rendering task if any is running
            await CancelBackgroundRenderAsync();
            // Block all ScrollViewer layout and scroll sync events during loading
            _isScrollingProgrammatically = true;
            _isUnloading = false;

            _pages.Clear();
            _annotationHistory.Clear();
            _currentPageIndex = 0;
            PageNumberInput.Text = "1";
            TotalPagesText.Text = "/ 0";
            _currentPdfPath = file.Path;
            HasUnsavedChanges = false;

            // Hide welcome screen, show document panel and home button
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            SaveButton.Visibility = Visibility.Visible;
            if (HomeButton != null) HomeButton.Visibility = Visibility.Visible;
            if (SplitViewToggleButton != null) SplitViewToggleButton.Visibility = Visibility.Visible;

            // Initialize Rust PDF engine (Phase 4)
            try
            {
                _pdfRenderService?.Dispose();
                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(file.Path);
                System.Diagnostics.Debug.WriteLine("✓ Rust PDF engine initialized for rendering");
            }
            catch (Exception ex)
            {
                _pdfRenderService = null;
                System.Diagnostics.Debug.WriteLine($"Rust backend failed to initialize: {ex.Message}");
            }

            // Always load structure with Windows.Data.Pdf (to get page metadata and dimensions)
            try
            {
                using (var fileStream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var memStream = new InMemoryRandomAccessStream();
                    await RandomAccessStream.CopyAsync(fileStream, memStream);
                    memStream.Seek(0);
                    _pdfDocument = await PdfDocument.LoadFromStreamAsync(memStream);
                }

                if (_pdfDocument == null)
                {
                    throw new Exception("Could not load PDF document structure");
                }

                System.Diagnostics.Debug.WriteLine($"✓ PDF loaded: {_pdfDocument.PageCount} pages");
                _totalPages = _pdfDocument.PageCount;
                TotalPagesText.Text = $"/ {_pdfDocument.PageCount}";

                System.Diagnostics.Debug.WriteLine($"Creating placeholders for {_pdfDocument.PageCount} pages...");

                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                {
                    using PdfPage page = _pdfDocument.GetPage(i);

                    // Create placeholder with page dimensions but no image yet (lazy rendering)
                    _pages.Add(new PdfPageViewModel
                    {
                        ImageSource = null,  // Will render on demand
                        PageIndex = (int)i,
                        PageWidth = page.Size.Width,
                        PageHeight = page.Size.Height,
                        IsLoading = true,
                        AnnotationsBackground = (_currentMode == EditMode.Navigate) ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
                    });
                }

                System.Diagnostics.Debug.WriteLine($"✓ Placeholders created. PDF ready");

                // Render first N pages immediately for smooth scrolling
                uint pagesToRenderImmediately = Math.Min(10, _pdfDocument.PageCount);
                System.Diagnostics.Debug.WriteLine($"Rendering first {pagesToRenderImmediately} pages...");

                for (uint i = 0; i < pagesToRenderImmediately; i++)
                {
                    await RenderPageAsync(_pdfDocument, i);
                }

                System.Diagnostics.Debug.WriteLine($"✓ First {pagesToRenderImmediately} pages rendered");

                // Start rendering remaining pages in background (non-blocking)
                if (_pdfDocument.PageCount > pagesToRenderImmediately)
                {
                    _backgroundRenderCts = new CancellationTokenSource();
                    System.Diagnostics.Debug.WriteLine("Starting background rendering of remaining pages...");
                    _backgroundRenderTask = RenderRemainingPagesAsync(_pdfDocument, pagesToRenderImmediately, _backgroundRenderCts.Token);
                }

                // Generate thumbnail and add to recents list
                try
                {
                    using PdfPage page = _pdfDocument.GetPage(0);
                    var tempThumbStream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(tempThumbStream);
                    
                    string safeName = file.Path.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
                    string thumbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, $"thumb_{safeName}.png");
                    
                    using (var outStream = File.Create(thumbPath))
                    {
                        var buffer = new byte[4096];
                        tempThumbStream.Seek(0);
                        using (var readStream = tempThumbStream.AsStreamForRead())
                        {
                            int bytesRead;
                            while ((bytesRead = await readStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await outStream.WriteAsync(buffer, 0, bytesRead);
                            }
                        }
                    }

                    AddToRecentFiles(file.Path, file.Name, thumbPath);
                }
                catch (Exception thumbEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to generate fallback thumbnail: {thumbEx.Message}");
                    AddToRecentFiles(file.Path, file.Name, "");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗ Error in PDF loading: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"  Stack: {ex.StackTrace}");
                throw;
            }

            // Load annotations if they exist for this PDF
            await LoadAnnotationsAsync(_currentPdfPath);

            // Reset zoom to 100% (default Index 2)
            ZoomComboBox.SelectedIndex = 2;
            PdfScrollViewer.ChangeView(null, null, 1.0f);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading PDF: {ex.Message}");
            var dialog = new ContentDialog
            {
                Title = "Error al cargar PDF",
                Content = $"Error: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            _ = dialog.ShowAsync();
        }
        finally
        {
            // Re-enable ScrollViewer scroll sync events
            _isScrollingProgrammatically = false;
            _ffiSemaphore.Release();
        }
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        SidebarSplitView.IsPaneOpen = !SidebarSplitView.IsPaneOpen;
    }

    private void PageListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        var clickedItem = e.ClickedItem as PdfPageViewModel;
        if (clickedItem != null && clickedItem.PageIndex != _currentPageIndex)
        {
            NavigateToPage(clickedItem.PageIndex);
        }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages > 0 && _currentPageIndex > 0)
        {
            NavigateToPage(_currentPageIndex - 1);
        }
    }
 
    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages > 0 && _currentPageIndex < _totalPages - 1)
        {
            NavigateToPage(_currentPageIndex + 1);
        }
    }
 
    private async void NavigateToPage(int index)
    {
        if (index >= 0 && _totalPages > 0 && index < _totalPages)
        {
            _currentPageIndex = index;
            
            // Temporary block ViewChanged scroll sync to avoid recursive selection jumps
            _isScrollingProgrammatically = true;
            
            PageNumberInput.Text = (index + 1).ToString();
            PageListView.SelectedIndex = index;
            
            var element = PagesRepeater.GetOrCreateElement(index) as UIElement;
            if (element != null)
            {
                element.StartBringIntoView(new BringIntoViewOptions 
                { 
                    VerticalAlignmentRatio = 0.0 // Scroll to top of the item
                });
            }
            
            await Task.Delay(500);
            _isScrollingProgrammatically = false;
        }
    }

    private void PageNumberInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            JumpToInputPage();
            // Lose focus from input box
            DocTitleText.Focus(FocusState.Programmatic);
        }
    }

    private void PageNumberInput_LosingFocus(object sender, RoutedEventArgs e)
    {
        JumpToInputPage();
    }

    private void JumpToInputPage()
    {
        if (int.TryParse(PageNumberInput.Text, out int pageNum))
        {
            NavigateToPage(pageNum - 1);
        }
        else
        {
            // Reset to current page
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
        }
    }

    private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PdfScrollViewer == null) return;

        float zoomFactor = 1.0f;
        switch (ZoomComboBox.SelectedIndex)
        {
            case 0: zoomFactor = 0.5f; break; // 50%
            case 1: zoomFactor = 0.75f; break; // 75%
            case 2: zoomFactor = 1.0f; break; // 100%
            case 3: zoomFactor = 1.25f; break; // 125%
            case 4: zoomFactor = 1.5f; break; // 150%
            case 5: zoomFactor = 2.0f; break; // 200%
            case 6: // Fit Width
                if (_pdfRenderService != null && _totalPages > 0)
                {
                    // Standard letter width in points = 612
                    double pageWidth = 612.0;
                    double viewportWidth = PdfScrollViewer.ViewportWidth;
                    // Account for margin spacing (16 * 2 + scrollbars)
                    zoomFactor = (float)((viewportWidth - 48.0) / pageWidth);
                    zoomFactor = Math.Max(0.2f, Math.Min(zoomFactor, 4.0f));
                }
                break;
        }

        PdfScrollViewer.ChangeView(null, null, zoomFactor);
    }

    private void PdfScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isScrollingProgrammatically) return;
        if (_pdfRenderService == null || _totalPages == 0 || _pages.Count == 0) return;

        double offset = PdfScrollViewer.VerticalOffset;

        if (_totalPages > 0)
        {
            double zoom = PdfScrollViewer.ZoomFactor;
            double currentOffset = offset / (zoom > 0 ? zoom : 1.0); // normalize offset to 100% zoom
            
            int activePageIndex = 0;
            double accumulatedHeight = 16.0; // PagesRepeater top margin (16)

            for (int i = 0; i < _pages.Count; i++)
            {
                double pageHeight = _pages[i].PageHeight;
                double itemHeight = pageHeight + 18.0; // Height of Border including thickness and margin

                if (currentOffset < accumulatedHeight + itemHeight + 8.0) // Closer to this page than next (8.0 is half of spacing)
                {
                    activePageIndex = i;
                    break;
                }

                accumulatedHeight += itemHeight + 16.0; // add item height + StackLayout spacing (16)
                activePageIndex = i;
            }

            activePageIndex = Math.Max(0, Math.Min(activePageIndex, (int)_totalPages - 1));

            if (activePageIndex != _currentPageIndex)
            {
                _currentPageIndex = activePageIndex;
                PageNumberInput.Text = (_currentPageIndex + 1).ToString();
                
                // Highlight item in sidebar
                _isScrollingProgrammatically = true;
                PageListView.SelectedIndex = _currentPageIndex;
                PageListView.ScrollIntoView(PageListView.SelectedItem);
                _isScrollingProgrammatically = false;
            }
        }
    }

    // Annotation Mode Handlers
    private void ReadMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Navigate);
    }

    private void HighlightMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Highlight);
    }

    private void NoteMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Note);
    }

    private void PenMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Pen);
    }

    private void EraserMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Eraser);
    }

    private void SetEditMode(EditMode mode)
    {
        _currentMode = mode;
        ReadModeButton.IsChecked = (mode == EditMode.Navigate);
        HighlightModeButton.IsChecked = (mode == EditMode.Highlight);
        NoteModeButton.IsChecked = (mode == EditMode.Note);
        PenModeButton.IsChecked = (mode == EditMode.Pen);
        if (EraserModeButton != null) EraserModeButton.IsChecked = (mode == EditMode.Eraser);

        // Toggle annotations background to allow clicking notes and scrolling in Navigate mode
        Microsoft.UI.Xaml.Media.Brush? bg = (mode == EditMode.Navigate)
            ? null
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        
        foreach (var page in _pages)
        {
            page.AnnotationsBackground = bg;
        }

        // Toggle visibility of the color selector panel for drawing modes
        bool showColors = (mode == EditMode.Highlight || mode == EditMode.Pen);
        ColorSelectorPanel.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        ColorSeparator.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;

        // Toggle visibility of the brush settings panel
        if (BrushSettingsPanel != null)
        {
            BrushSettingsPanel.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
            if (showColors)
            {
                ThicknessContainer.Visibility = (mode == EditMode.Pen) ? Visibility.Visible : Visibility.Collapsed;
                OpacityContainer.Visibility = (mode == EditMode.Highlight) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private string GetActiveColorHexWithOpacity()
    {
        string color = _activeColorHex;
        if (string.IsNullOrEmpty(color)) color = "#FFFF00";
        
        string clean = color.Replace("#", "");
        if (clean.Length == 8)
        {
            clean = clean.Substring(2); // Strip existing alpha
        }
        
        if (_currentMode == EditMode.Highlight && OpacitySlider != null)
        {
            byte alpha = (byte)Math.Clamp(OpacitySlider.Value * 255.0 / 100.0, 10.0, 255.0);
            return $"#{alpha:X2}{clean}";
        }
        else
        {
            return $"#FF{clean}";
        }
    }

    private void ThicknessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ThicknessValueText != null)
        {
            ThicknessValueText.Text = $"{e.NewValue:0.0}px";
        }
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (OpacityValueText != null)
        {
            OpacityValueText.Text = $"{e.NewValue:0}%";
        }
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null && button.Tag != null)
        {
            _activeColorHex = button.Tag.ToString() ?? "#FFFF00";
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfRenderService == null) return;

        foreach (var page in _pages)
        {
            // Swap width and height for layout calculation
            double temp = page.PageWidth;
            page.PageWidth = page.PageHeight;
            page.PageHeight = temp;

            page.RotationAngle = (page.RotationAngle + 90.0) % 360.0;
        }
    }

    private void SplitViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SplitViewToggleButton == null) return;

        if (SplitViewToggleButton.IsChecked == true)
        {
            // Open split view (50/50 columns)
            SecondaryViewerColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryViewerContainer.Visibility = Visibility.Visible;
            ViewerDivider.Visibility = Visibility.Visible;
            PagesRepeaterRight.ItemsSource = _pagesRight;
        }
        else
        {
            // Close split view
            SecondaryViewerColumn.Width = new GridLength(0);
            SecondaryViewerContainer.Visibility = Visibility.Collapsed;
            ViewerDivider.Visibility = Visibility.Collapsed;
            _pagesRight.Clear();
            if (SecondaryTitleText != null)
            {
                SecondaryTitleText.Text = Glance.Services.LocalizationService.Get("SideBySideComparer");
            }
        }
    }

    private void CloseSecondPdf_Click(object sender, RoutedEventArgs e)
    {
        if (SplitViewToggleButton != null)
        {
            SplitViewToggleButton.IsChecked = false;
            SplitViewToggle_Click(this, new RoutedEventArgs());
        }
    }

    private async void OpenSecondPdf_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        StorageFile file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            await LoadSecondPdfAsync(file);
        }
    }

    private async Task LoadSecondPdfAsync(StorageFile file)
    {
        try
        {
            SecondaryTitleText.Text = $"Cargando: {file.Name}...";
            _pagesRight.Clear();

            // Load into memory to avoid file lock
            InMemoryRandomAccessStream memoryStream = new InMemoryRandomAccessStream();
            using (var fileStream = await file.OpenAsync(FileAccessMode.Read))
            {
                await RandomAccessStream.CopyAsync(fileStream, memoryStream);
            }
            memoryStream.Seek(0);

            var docRight = await PdfDocument.LoadFromStreamAsync(memoryStream);
            
            // Populate pages view model for right column
            for (uint i = 0; i < docRight.PageCount; i++)
            {
                using (var page = docRight.GetPage(i))
                {
                    var size = page.Size;
                    var pageVm = new PdfPageViewModel
                    {
                        PageIndex = (int)i,
                        PageWidth = size.Width,
                        PageHeight = size.Height
                    };
                    _pagesRight.Add(pageVm);
                }
            }

            SecondaryTitleText.Text = file.Name;

            // Render pages in background (non-blocking)
            _ = RenderSecondPdfPagesAsync(docRight);
        }
        catch (Exception ex)
        {
            SecondaryTitleText.Text = "Error al cargar PDF";
            System.Diagnostics.Debug.WriteLine($"Error loading second PDF: {ex.Message}");
        }
    }

    private async Task RenderSecondPdfPagesAsync(PdfDocument doc)
    {
        try
        {
            for (int i = 0; i < _pagesRight.Count; i++)
            {
                var pageVm = _pagesRight[i];
                using (var page = doc.GetPage((uint)i))
                {
                    InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream);
                    
                    var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await image.SetSourceAsync(stream);
                    
                    pageVm.ImageSource = image;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error rendering second PDF pages: {ex.Message}");
        }
    }

    // Canvas Pointer Events for Drawing
    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_currentMode == EditMode.Navigate) return;

        var element = sender as FrameworkElement;
        if (element == null) return;
        
        var pageVm = element.DataContext as PdfPageViewModel;
        if (pageVm == null) return;

        var point = e.GetCurrentPoint(element).Position;

        if (_currentMode == EditMode.Eraser)
        {
            _isErasing = true;
            EraseAtPoint(pageVm, point);
            element.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        HasUnsavedChanges = true;

        if (_currentMode == EditMode.Highlight)
        {
            _isDrawingHighlight = true;
            _startPoint = point;
            
            _activeHighlight = new AnnotationViewModel
            {
                Type = AnnotationType.Highlight,
                X = point.X,
                Y = point.Y,
                Width = 0,
                Height = 0,
                ColorHex = GetActiveColorHexWithOpacity()
            };
            pageVm.Annotations.Add(_activeHighlight);
            _annotationHistory.Add((pageVm, _activeHighlight));
            
            element.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        else if (_currentMode == EditMode.Note)
        {
            var note = new AnnotationViewModel
            {
                Type = AnnotationType.Note,
                X = point.X,
                Y = point.Y,
                Content = ""
            };
            pageVm.Annotations.Add(note);
            _annotationHistory.Add((pageVm, note));
            _ = SaveAnnotationsAsync();
            e.Handled = true;
        }
        else if (_currentMode == EditMode.Pen)
        {
            _isDrawingHighlight = true; // Use drawing flag
            _activeHighlight = new AnnotationViewModel
            {
                Type = AnnotationType.Pen,
                X = 0, // No translation needed for absolute polyline coordinates
                Y = 0,
                ColorHex = GetActiveColorHexWithOpacity(),
                Thickness = ThicknessSlider != null ? ThicknessSlider.Value : 3.5
            };
            _activeHighlight.PointsCollection.Add(point);
            pageVm.Annotations.Add(_activeHighlight);
            _annotationHistory.Add((pageVm, _activeHighlight));

            element.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentMode == EditMode.Eraser && _isErasing)
        {
            var element = sender as FrameworkElement;
            if (element != null)
            {
                var pageVm = element.DataContext as PdfPageViewModel;
                if (pageVm != null)
                {
                    var point = e.GetCurrentPoint(element).Position;
                    EraseAtPoint(pageVm, point);
                }
            }
            e.Handled = true;
            return;
        }

        if (_isDrawingHighlight && _activeHighlight != null)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var currentPoint = e.GetCurrentPoint(element).Position;

            if (_currentMode == EditMode.Highlight)
            {
                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double w = Math.Abs(_startPoint.X - currentPoint.X);
                double h = Math.Abs(_startPoint.Y - currentPoint.Y);

                _activeHighlight.X = x;
                _activeHighlight.Y = y;
                _activeHighlight.Width = w;
                _activeHighlight.Height = h;
            }
            else if (_currentMode == EditMode.Pen)
            {
                _activeHighlight.PointsCollection.Add(currentPoint);
            }
            
            e.Handled = true;
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentMode == EditMode.Eraser && _isErasing)
        {
            _isErasing = false;
            var element = sender as FrameworkElement;
            element?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_isDrawingHighlight)
        {
            _isDrawingHighlight = false;
            _activeHighlight = null;
            
            var element = sender as FrameworkElement;
            element?.ReleasePointerCapture(e.Pointer);
            
            _ = SaveAnnotationsAsync();
            e.Handled = true;
        }
    }

    private void EraseAtPoint(PdfPageViewModel pageVm, Point p)
    {
        AnnotationViewModel? toRemove = null;
        
        foreach (var anno in pageVm.Annotations)
        {
            if (anno.Type == AnnotationType.Highlight)
            {
                // Check if point is inside rectangle
                if (p.X >= anno.X && p.X <= anno.X + anno.Width &&
                    p.Y >= anno.Y && p.Y <= anno.Y + anno.Height)
                {
                    toRemove = anno;
                    break;
                }
            }
            else if (anno.Type == AnnotationType.Note)
            {
                // Check if point is near the comment icon (24px hit area)
                double dist = Math.Sqrt(Math.Pow(p.X - anno.X, 2) + Math.Pow(p.Y - anno.Y, 2));
                if (dist <= 16.0)
                {
                    toRemove = anno;
                    break;
                }
            }
            else if (anno.Type == AnnotationType.Pen)
            {
                // Check if point is near any point of the polyline
                double threshold = Math.Max(anno.Thickness + 6.0, 10.0);
                foreach (var pt in anno.PointsCollection)
                {
                    double dist = Math.Sqrt(Math.Pow(p.X - pt.X, 2) + Math.Pow(p.Y - pt.Y, 2));
                    if (dist <= threshold)
                    {
                        toRemove = anno;
                        break;
                    }
                }
                if (toRemove != null) break;
            }
        }

        if (toRemove != null)
        {
            pageVm.Annotations.Remove(toRemove);
            HasUnsavedChanges = true;
            
            // Remove from history
            _annotationHistory.RemoveAll(item => item.Annotation == toRemove);
            
            _ = SaveAnnotationsAsync();
        }
    }

    // Keyboard Accelerator for Undo (Ctrl+Z)
    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        UndoLastAnnotation();
        args.Handled = true;
    }

    // Keyboard Accelerator for Save (Ctrl+S)
    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        await SaveOriginalPdfAsync();
        args.Handled = true;
    }

    private void UndoLastAnnotation()
    {
        if (_annotationHistory.Count > 0)
        {
            // Remove the last annotation from the history stack and page collection
            var last = _annotationHistory[_annotationHistory.Count - 1];
            _annotationHistory.RemoveAt(_annotationHistory.Count - 1);

            last.Page.Annotations.Remove(last.Annotation);
            HasUnsavedChanges = true;
            _ = SaveAnnotationsAsync();
        }
    }

    // Recent Files Management
    private void AddToRecentFiles(string filePath, string fileName, string thumbnailPath)
    {
        // Remove if it already exists to place it back at the top
        for (int i = 0; i < _recentDocs.Count; i++)
        {
            if (_recentDocs[i].FilePath == filePath)
            {
                _recentDocs.RemoveAt(i);
                break;
            }
        }

        var item = new RecentDocumentViewModel
        {
            FilePath = filePath,
            FileName = fileName,
            ThumbnailPath = thumbnailPath
        };

        try
        {
            if (File.Exists(thumbnailPath))
            {
                item.ThumbnailImage = new BitmapImage(new Uri(thumbnailPath));
            }
        }
        catch { }

        _recentDocs.Insert(0, item);

        // Cap list at 8 recent items
        while (_recentDocs.Count > 8)
        {
            _recentDocs.RemoveAt(_recentDocs.Count - 1);
        }

        SaveRecentFilesJson();
        UpdateRecentHeaderVisibility();
    }

    private void RemoveFromRecentFiles(string filePath)
    {
        for (int i = 0; i < _recentDocs.Count; i++)
        {
            if (_recentDocs[i].FilePath == filePath)
            {
                _recentDocs.RemoveAt(i);
                break;
            }
        }
        SaveRecentFilesJson();
        UpdateRecentHeaderVisibility();
    }

    private void SaveRecentFilesJson()
    {
        try
        {
            var savedList = new List<SavedRecentDoc>();
            foreach (var doc in _recentDocs)
            {
                savedList.Add(new SavedRecentDoc
                {
                    FilePath = doc.FilePath,
                    FileName = doc.FileName,
                    ThumbnailPath = doc.ThumbnailPath
                });
            }
            string json = System.Text.Json.JsonSerializer.Serialize(savedList);
            string recentPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "recent_files.json");
            File.WriteAllText(recentPath, json);
        }
        catch { }
    }

    private void LoadRecentFiles()
    {
        try
        {
            _recentDocs.Clear();
            string recentPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "recent_files.json");
            if (!File.Exists(recentPath))
            {
                UpdateRecentHeaderVisibility();
                return;
            }

            string json = File.ReadAllText(recentPath);
            var savedList = System.Text.Json.JsonSerializer.Deserialize<List<SavedRecentDoc>>(json);

            if (savedList != null)
            {
                foreach (var saved in savedList)
                {
                    if (File.Exists(saved.FilePath))
                    {
                        var doc = new RecentDocumentViewModel
                        {
                            FilePath = saved.FilePath,
                            FileName = saved.FileName,
                            ThumbnailPath = saved.ThumbnailPath
                        };

                        if (File.Exists(saved.ThumbnailPath))
                        {
                            doc.ThumbnailImage = new BitmapImage(new Uri(saved.ThumbnailPath));
                        }
                        _recentDocs.Add(doc);
                    }
                }
            }
            UpdateRecentHeaderVisibility();
        }
        catch
        {
            UpdateRecentHeaderVisibility();
        }
    }

    private void UpdateRecentHeaderVisibility()
    {
        RecientesHeader.Visibility = _recentDocs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RecentGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        var recent = e.ClickedItem as RecentDocumentViewModel;
        if (recent != null)
        {
            if (File.Exists(recent.FilePath))
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(recent.FilePath);
                    DocTitleText.Text = file.Name;
                    await LoadPdfAsync(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error opening recent file: {ex.Message}");
                    RemoveFromRecentFiles(recent.FilePath);
                }
            }
            else
            {
                // File no longer exists, clean it up
                RemoveFromRecentFiles(recent.FilePath);
            }
        }
    }

    // Save and Load Annotations (Auto-save via Rust backend)
    public async Task SaveAnnotationsAsync()
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return;

        await _ffiSemaphore.WaitAsync();
        try
        {
            await SaveAnnotationsInternalAsync();
        }
        finally
        {
            _ffiSemaphore.Release();
        }
    }

    private async Task SaveAnnotationsInternalAsync()
    {
        try
        {
            var savedList = new List<SavedAnnotation>();
            foreach (var page in _pages)
            {
                foreach (var anno in page.Annotations)
                {
                    var savedAnno = new SavedAnnotation
                    {
                        PageIndex = page.PageIndex,
                        Type = anno.Type,
                        X = anno.X,
                        Y = anno.Y,
                        Width = anno.Width,
                        Height = anno.Height,
                        Content = anno.Content ?? "",
                        ColorHex = anno.ColorHex,
                        Thickness = anno.Thickness,
                        RotationAngle = page.RotationAngle
                    };

                    foreach (var pt in anno.PointsCollection)
                    {
                        savedAnno.Points.Add(new SavedPoint { X = pt.X, Y = pt.Y });
                    }

                    savedList.Add(savedAnno);
                }
            }

            string json = System.Text.Json.JsonSerializer.Serialize(savedList, _jsonOptions);
            string filePath = GetAnnotationFilePath(_currentPdfPath);

            // Validate annotations before saving
            await _annotationService.ValidateAnnotationsAsync(json);

            // Use Rust backend for persistence
            await _persistenceService.SaveAnnotationsAsync(json, filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving annotations: {ex.Message}");
        }
    }

    private async Task LoadAnnotationsAsync(string pdfPath)
    {
        try
        {
            string filePath = GetAnnotationFilePath(pdfPath);
            if (!File.Exists(filePath)) return;

            // Use Rust backend for persistence
            string json = await _persistenceService.LoadAnnotationsAsync(filePath);
            var savedList = System.Text.Json.JsonSerializer.Deserialize<List<SavedAnnotation>>(json, _jsonOptions);

            if (savedList == null) return;

            foreach (var saved in savedList)
            {
                if (saved.PageIndex >= 0 && saved.PageIndex < _pages.Count)
                {
                    var annoVm = new AnnotationViewModel
                    {
                        Type = saved.Type,
                        X = saved.X,
                        Y = saved.Y,
                        Width = saved.Width,
                        Height = saved.Height,
                        Content = saved.Content,
                        ColorHex = saved.ColorHex ?? "#FFFF00",
                        Thickness = saved.Thickness > 0 ? saved.Thickness : 3.5
                    };

                    if (saved.Points != null && saved.Points.Count > 0)
                    {
                        var pc = new Microsoft.UI.Xaml.Media.PointCollection();
                        foreach (var pt in saved.Points)
                        {
                            pc.Add(new Point(pt.X, pt.Y));
                        }
                        annoVm.PointsCollection = pc;
                    }

                    _pages[saved.PageIndex].Annotations.Add(annoVm);
                    _annotationHistory.Add((_pages[saved.PageIndex], annoVm));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading annotations: {ex.Message}");
        }
    }

    private string GetAnnotationFilePath(string pdfPath)
    {
        string safeName = pdfPath.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
        string localFolder = ApplicationData.Current.LocalFolder.Path;
        return Path.Combine(localFolder, $"{safeName}.json");
    }

    // ========================================================================
    // Search (Ctrl+F) Functions
    // ========================================================================

    private void Search_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ShowSearch();
        args.Handled = true;
    }

    private void ShowSearch()
    {
        SearchPanel.Visibility = Visibility.Visible;
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        ClearSearchHighlights();
        SearchTextBox.Text = "";
        _searchMatches.Clear();
        _currentMatchIndex = -1;
    }

    private void ClearSearchHighlights()
    {
        foreach (var page in _pages)
        {
            page.SearchHighlights.Clear();
        }
    }

    private async Task PerformSearchAsync(string query)
    {
        ClearSearchHighlights();

        if (string.IsNullOrWhiteSpace(query) || _pdfRenderService == null)
        {
            _searchMatches.Clear();
            _currentMatchIndex = -1;
            UpdateSearchStatus();
            return;
        }

        try
        {
            string jsonResult = await _pdfRenderService.SearchTextAsync(query);
            
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _searchMatches = System.Text.Json.JsonSerializer.Deserialize<List<SearchMatch>>(jsonResult, options) ?? new();

            int totalRects = 0;
            foreach (var match in _searchMatches)
            {
                var page = _pages.FirstOrDefault(p => p.PageIndex == match.PageIndex);
                if (page != null)
                {
                    foreach (var rect in match.Rects)
                    {
                        var highlight = new SearchHighlightViewModel
                        {
                            X = rect.X * (96.0 / 72.0),
                            Y = rect.Y * (96.0 / 72.0),
                            Width = rect.Width * (96.0 / 72.0),
                            Height = rect.Height * (96.0 / 72.0),
                            IsCurrent = false
                        };
                        page.SearchHighlights.Add(highlight);
                        totalRects++;
                    }
                }
            }

            if (totalRects > 0)
            {
                _currentMatchIndex = 0;
                HighlightCurrentMatch(true);
            }
            else
            {
                _currentMatchIndex = -1;
            }

            UpdateSearchStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error performing search: {ex.Message}");
        }
    }

    private void HighlightCurrentMatch(bool scrollToMatch)
    {
        int currentIndex = 0;
        SearchHighlightViewModel? targetHighlight = null;
        int targetPageIndex = -1;

        foreach (var page in _pages)
        {
            foreach (var h in page.SearchHighlights)
            {
                if (currentIndex == _currentMatchIndex)
                {
                    h.IsCurrent = true;
                    targetHighlight = h;
                    targetPageIndex = page.PageIndex;
                }
                else
                {
                    h.IsCurrent = false;
                }
                currentIndex++;
            }
        }

        if (scrollToMatch && targetHighlight != null && targetPageIndex >= 0)
        {
            NavigateToPage(targetPageIndex);
        }
    }

    private void UpdateSearchStatus()
    {
        if (SearchStatusText == null) return;
        
        int totalRects = _pages.Sum(p => p.SearchHighlights.Count);
        if (totalRects == 0)
        {
            SearchStatusText.Text = Glance.Services.LocalizationService.IsSpanish ? "Sin resultados" : "No results";
            if (SearchPrevButton != null) SearchPrevButton.IsEnabled = false;
            if (SearchNextButton != null) SearchNextButton.IsEnabled = false;
        }
        else
        {
            SearchStatusText.Text = string.Format(Glance.Services.LocalizationService.Get("SearchStatus"), _currentMatchIndex + 1, totalRects);
            if (SearchPrevButton != null) SearchPrevButton.IsEnabled = true;
            if (SearchNextButton != null) SearchNextButton.IsEnabled = true;
        }
    }

    private void SearchPrev_Click(object sender, RoutedEventArgs e)
    {
        int totalRects = _pages.Sum(p => p.SearchHighlights.Count);
        if (totalRects == 0) return;

        _currentMatchIndex = (_currentMatchIndex - 1 + totalRects) % totalRects;
        HighlightCurrentMatch(true);
        UpdateSearchStatus();
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e)
    {
        int totalRects = _pages.Sum(p => p.SearchHighlights.Count);
        if (totalRects == 0) return;

        _currentMatchIndex = (_currentMatchIndex + 1) % totalRects;
        HighlightCurrentMatch(true);
        UpdateSearchStatus();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e)
    {
        CloseSearch();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new Microsoft.UI.Xaml.DispatcherTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchDebounceTimer.Tick += async (s, args) =>
            {
                _searchDebounceTimer.Stop();
                await PerformSearchAsync(SearchTextBox.Text);
            };
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _searchDebounceTimer?.Stop(); // Cancel pending debounced search

            int totalRects = _pages.Sum(p => p.SearchHighlights.Count);
            if (totalRects > 0)
            {
                _currentMatchIndex = (_currentMatchIndex + 1) % totalRects;
                HighlightCurrentMatch(true);
                UpdateSearchStatus();
            }
            else
            {
                await PerformSearchAsync(SearchTextBox.Text);
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _searchDebounceTimer?.Stop(); // Cancel pending debounced search
            CloseSearch();
        }
    }
}

public class RecentDocumentViewModel
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public ImageSource? ThumbnailImage { get; set; }
}

public class SavedRecentDoc
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
}

public class PdfPageViewModel : INotifyPropertyChanged
{
    private double _pageWidth;
    private double _pageHeight;
    private double _rotationAngle = 0.0;
    private int _pageIndex;
    private ImageSource? _imageSource;
    private bool _isLoading = false;
    private Microsoft.UI.Xaml.Media.Brush? _annotationsBackground = null;

    public Microsoft.UI.Xaml.Media.Brush? AnnotationsBackground
    {
        get => _annotationsBackground;
        set => SetProperty(ref _annotationsBackground, value);
    }

    public ImageSource? ImageSource
    {
        get => _imageSource;
        set => SetProperty(ref _imageSource, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageNumberText));
            }
        }
    }
    
    public double PageWidth
    {
        get => _pageWidth;
        set => SetProperty(ref _pageWidth, value);
    }

    public double PageHeight
    {
        get => _pageHeight;
        set => SetProperty(ref _pageHeight, value);
    }

    public double RotationAngle
    {
        get => _rotationAngle;
        set => SetProperty(ref _rotationAngle, value);
    }

    public string PageNumberText => $"Página {PageIndex + 1}";
    public ObservableCollection<AnnotationViewModel> Annotations { get; set; } = new();
    public ObservableCollection<SearchHighlightViewModel> SearchHighlights { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SavedPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class SavedAnnotation
{
    public int PageIndex { get; set; }
    public AnnotationType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Content { get; set; } = "";
    public string ColorHex { get; set; } = "#FFFF00";
    public List<SavedPoint> Points { get; set; } = new();
    public double Thickness { get; set; } = 3.5;
    public double RotationAngle { get; set; } = 0.0;
}

public class AnnotationViewModel : INotifyPropertyChanged
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private string _content = "";
    private string _colorHex = "#FFFF00";
    private double _thickness = 3.5;
    private PointCollection _pointsCollection = new();

    public AnnotationType Type { get; set; }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                if (MainPage.Current != null)
                {
                    MainPage.Current.HasUnsavedChanges = true;
                }
                _ = MainPage.Current?.SaveAnnotationsAsync();
            }
        }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (SetProperty(ref _colorHex, value))
            {
                if (MainPage.Current != null)
                {
                    MainPage.Current.HasUnsavedChanges = true;
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorBrush)));
                _ = MainPage.Current?.SaveAnnotationsAsync();
            }
        }
    }

    public PointCollection PointsCollection
    {
        get => _pointsCollection;
        set => SetProperty(ref _pointsCollection, value);
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (SetProperty(ref _thickness, value))
            {
                if (MainPage.Current != null)
                {
                    MainPage.Current.HasUnsavedChanges = true;
                }
                _ = MainPage.Current?.SaveAnnotationsAsync();
            }
        }
    }

    public Brush ColorBrush
    {
        get
        {
            try
            {
                string hex = _colorHex.Replace("#", "");
                if (Type == AnnotationType.Highlight)
                {
                    // Use custom alpha if 8 characters (AARRGGBB), otherwise default to 31% opacity (50 hex)
                    if (hex.Length == 6)
                        hex = "50" + hex;
                }
                else
                {
                    // Solid alpha for pen drawing
                    if (hex.Length == 6)
                        hex = "FF" + hex;
                }

                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);

                return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
            }
            catch
            {
                return new SolidColorBrush(Microsoft.UI.Colors.Yellow);
            }
        }
    }

    public Visibility IsHighlightVisibility => Type == AnnotationType.Highlight ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsNoteVisibility => Type == AnnotationType.Note ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPenVisibility => Type == AnnotationType.Pen ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Type))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlightVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNoteVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPenVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorBrush)));
        }
        return true;
    }
}

public class SearchHighlightViewModel : INotifyPropertyChanged
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private bool _isCurrent;

    public double X { get => _x; set => SetProperty(ref _x, value); }
    public double Y { get => _y; set => SetProperty(ref _y, value); }
    public double Width { get => _width; set => SetProperty(ref _width, value); }
    public double Height { get => _height; set => SetProperty(ref _height, value); }
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(HighlightBrush));
            }
        }
    }

    public Microsoft.UI.Xaml.Media.Brush HighlightBrush => IsCurrent
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange) { Opacity = 0.5 }
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Yellow) { Opacity = 0.45 };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SearchMatch
{
    public uint PageIndex { get; set; }
    public List<MatchRect> Rects { get; set; } = new();
}

public class MatchRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
