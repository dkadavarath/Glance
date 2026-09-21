using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Glance.Services;

namespace Glance;

/// <summary>How pages are laid out in the viewer.</summary>
public enum PageViewMode
{
    /// <summary>Every page in one vertical run.</summary>
    Continuous,

    /// <summary>One page at a time; scrolling stays within it and navigation swaps it.</summary>
    SinglePage,

    /// <summary>Two pages side by side, wrapping into rows of two.</summary>
    TwoPage
}

// Page layout modes and full screen.
public sealed partial class MainPage
{
    private const string ViewModeSettingKey = "PageViewMode";

    private PageViewMode _viewMode = PageViewMode.Continuous;
    private bool _suppressViewModeComboEvent;
    private bool _isFullScreen;

    /// <summary>
    /// What the repeater actually shows. Holds the same view-model instances as _pages —
    /// never copies — so annotations and rendered bitmaps survive a mode change. In
    /// single-page mode it holds exactly one of them.
    /// </summary>
    private readonly ObservableCollection<PdfPageViewModel> _displayPages = new();

    private void InitializeViewModes()
    {
        try
        {
            if (AppData.LocalSettings.Values.TryGetValue(ViewModeSettingKey, out object? stored) &&
                stored is string name &&
                Enum.TryParse(name, out PageViewMode mode))
            {
                _viewMode = mode;
            }
        }
        catch { }

        if (ViewModeComboBox != null)
        {
            _suppressViewModeComboEvent = true;
            ViewModeComboBox.SelectedIndex = (int)_viewMode;
            _suppressViewModeComboEvent = false;
        }

        ApplyPageLayout();
    }

    private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressViewModeComboEvent || ViewModeComboBox == null) return;

        int index = ViewModeComboBox.SelectedIndex;
        if (index < 0) return;

        SetViewMode((PageViewMode)index);
    }

    private void SetViewMode(PageViewMode mode)
    {
        if (_viewMode == mode) return;

        _viewMode = mode;
        try
        {
            AppData.LocalSettings.Values[ViewModeSettingKey] = mode.ToString();
        }
        catch { }

        ApplyPageLayout();

        // Leaving single-page mode has to put the reader back where they were, which
        // the shared navigation path already knows how to do.
        if (_totalPages > 0) NavigateToPage(_currentPageIndex);
    }

    /// <summary>Swaps the repeater's layout and rebuilds what it is showing.</summary>
    private void ApplyPageLayout()
    {
        if (PagesRepeater == null) return;

        PagesRepeater.Layout = _viewMode == PageViewMode.TwoPage
            ? new UniformGridLayout
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 2,
                MinRowSpacing = 16,
                MinColumnSpacing = 16,
                ItemsJustification = UniformGridLayoutItemsJustification.Center
            }
            : new StackLayout { Spacing = 16 };

        RefreshDisplayPages();
    }

    /// <summary>Repopulates the displayed set for the current mode.</summary>
    private void RefreshDisplayPages()
    {
        if (_viewMode == PageViewMode.SinglePage)
        {
            ShowSinglePage(_currentPageIndex);
            return;
        }

        // Continuous and two-page both show everything; only the layout differs.
        if (_displayPages.Count == _pages.Count)
        {
            bool identical = true;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (!ReferenceEquals(_displayPages[i], _pages[i])) { identical = false; break; }
            }
            if (identical) return;
        }

        _displayPages.Clear();
        foreach (var page in _pages) _displayPages.Add(page);
    }

    /// <summary>Points the repeater at exactly one page, leaving _pages untouched.</summary>
    private void ShowSinglePage(int index)
    {
        if (_pages.Count == 0)
        {
            _displayPages.Clear();
            return;
        }

        index = Math.Clamp(index, 0, _pages.Count - 1);
        var target = _pages[index];

        if (_displayPages.Count == 1 && ReferenceEquals(_displayPages[0], target)) return;

        _displayPages.Clear();
        _displayPages.Add(target);
    }

    /// <summary>The page a repeater index refers to, which is not the index itself in
    /// single-page mode.</summary>
    private int ResolvePageIndex(int repeaterIndex)
    {
        if (repeaterIndex < 0 || repeaterIndex >= _displayPages.Count) return -1;
        return _displayPages[repeaterIndex].PageIndex;
    }

    // ------------------------------------------------------------------- full screen

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        var appWindow = (Application.Current as App)?.MainWindow?.AppWindow;
        if (appWindow == null) return;

        _isFullScreen = !_isFullScreen;

        try
        {
            appWindow.SetPresenter(_isFullScreen
                ? AppWindowPresenterKind.FullScreen
                : AppWindowPresenterKind.Default);
        }
        catch
        {
            _isFullScreen = !_isFullScreen; // presenter refused; keep the chrome consistent
            return;
        }

        // The toolbar is the only chrome left once the title bar goes, so hide it too and
        // leave Esc or F11 as the way back.
        if (HeaderBar != null)
        {
            HeaderBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_isFullScreen && SidebarSplitView != null)
        {
            SidebarSplitView.IsPaneOpen = false;
        }
    }
}
