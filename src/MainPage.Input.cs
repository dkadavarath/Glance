using System;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Glance.Services;

namespace Glance;

/// <summary>Where a zoom keeps the document fixed.</summary>
public enum ZoomAnchorMode
{
    /// <summary>The point under the cursor stays put. Falls back to the viewport
    /// centre when the cursor is outside the page area.</summary>
    Cursor,

    /// <summary>The centre of the viewport stays put, wherever the cursor is.</summary>
    PageCenter
}

// Viewer input: zoom anchoring, modifier-wheel routing, the space-bar hand tool and
// drag-and-drop. Kept out of MainPage.xaml.cs, which is already far too large.
public sealed partial class MainPage
{
    private const string ZoomAnchorSettingKey = "ZoomAnchor";

    private ZoomAnchorMode _zoomAnchorMode = ZoomAnchorMode.Cursor;
    private Point? _lastViewerPointerPosition;
    private bool _suppressZoomAnchorComboEvent;

    // Hand tool. _handToolArmed = space is held; _handToolPanning = also dragging.
    private bool _handToolArmed;
    private bool _handToolPanning;
    private Point _handDragStart;
    private double _handStartHorizontalOffset;
    private double _handStartVerticalOffset;

    // Inertia, in view pixels per millisecond.
    private double _handVelocityX;
    private double _handVelocityY;
    private Point _handLastSamplePoint;
    private long _handLastSampleTicks;
    private bool _inertiaRunning;
    private long _inertiaLastTicks;

    // A flick decays to a stop in roughly half a second. Tuned to feel like the
    // ScrollViewer's own touch inertia rather than to match any particular spec.
    private const double InertiaDecayPerSecond = 0.0015;
    private const double InertiaMinSpeed = 0.02;

    private void InitializeInput()
    {
        LoadZoomAnchorSetting();

        // The wheel handler goes on the host, not the ScrollViewer. The ScrollViewer acts
        // on the wheel before the event bubbles to a handler there, so marking it handled
        // at that point is too late -- which is what made Alt+wheel scroll diagonally.
        // The host is used rather than the repeater because it spans the whole viewport,
        // so the modifier still applies when the cursor is beside the page rather than on
        // it.
        PagesHost.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Pages_PointerWheelChanged), false);

        // These only observe, so handledEventsToo is right for them.
        PdfScrollViewer.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerMoved), true);
        PdfScrollViewer.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerPressed), true);
        PdfScrollViewer.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerReleased), true);
        PdfScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(PdfScrollViewer_PointerCaptureLost), true);
        PdfScrollViewer.PointerExited += PdfScrollViewer_PointerExited;

        PdfScrollViewer.SizeChanged += (_, _) => UpdatePageCentering();

        // Preview phase: the ScrollViewer treats Space as page-down, so a bubble-phase
        // handler would arrive after it had already scrolled.
        this.PreviewKeyDown += MainPage_PreviewKeyDown;
        this.PreviewKeyUp += MainPage_PreviewKeyUp;
    }

    // --------------------------------------------------------------- page centering

    /// <summary>
    /// Keeps the pages centred whenever they fit, and keeps panning unavailable until
    /// they do not.
    /// </summary>
    /// <remarks>
    /// An earlier attempt offset the repeater with a render transform to avoid a layout
    /// pass. It did not hold, and it could not have given the second half of the
    /// behaviour: a transform does not change the extent, so the view stayed pannable
    /// even with the whole page on screen. Sizing the host is what actually decides both.
    /// </remarks>
    private void UpdatePageCentering()
    {
        var sv = PdfScrollViewer;
        if (sv == null || PagesHost == null) return;

        double zoom = sv.ZoomFactor;
        if (zoom <= 0) zoom = 1.0;
        if (sv.ViewportWidth <= 0) return;

        // The host is measured before the zoom scales it, so a box that exactly fills the
        // viewport is ViewportWidth / zoom wide here. At or above that width the extent
        // matches the viewport, which leaves no horizontal range to pan; the repeater
        // centres itself inside the host. Below it -- zoomed in past the window -- the
        // pages decide the width and panning comes back.
        double required = sv.ViewportWidth / zoom;
        if (Math.Abs(PagesHost.MinWidth - required) > 1.0)
        {
            PagesHost.MinWidth = required;
        }
    }

    // ---------------------------------------------------------------- zoom anchoring

    /// <summary>
    /// Zooms to <paramref name="targetZoom"/> while keeping the document point under
    /// <paramref name="viewportAnchor"/> stationary. A null anchor means the viewport centre.
    /// </summary>
    private void ApplyZoom(float targetZoom, Point? viewportAnchor)
    {
        var sv = PdfScrollViewer;
        if (sv == null) return;

        float oldZoom = sv.ZoomFactor;
        if (oldZoom <= 0) oldZoom = 1f;

        targetZoom = Math.Clamp(targetZoom, sv.MinZoomFactor, sv.MaxZoomFactor);
        if (Math.Abs(targetZoom - oldZoom) < 0.0005f) return;

        Point anchor = viewportAnchor ?? new Point(sv.ViewportWidth / 2, sv.ViewportHeight / 2);

        // Offsets are in zoomed content space, so divide out the old zoom to get the
        // document point under the anchor, then re-multiply by the new one.
        double contentX = (sv.HorizontalOffset + anchor.X) / oldZoom;
        double contentY = (sv.VerticalOffset + anchor.Y) / oldZoom;

        double newHorizontal = contentX * targetZoom - anchor.X;
        double newVertical = contentY * targetZoom - anchor.Y;

        sv.ChangeView(Math.Max(0, newHorizontal), Math.Max(0, newVertical), targetZoom, true);
    }

    /// <summary>The anchor implied by the current setting, or null for the viewport centre.</summary>
    private Point? ResolveZoomAnchor() =>
        _zoomAnchorMode == ZoomAnchorMode.Cursor ? _lastViewerPointerPosition : null;

    private void LoadZoomAnchorSetting()
    {
        try
        {
            if (AppData.LocalSettings.Values.TryGetValue(ZoomAnchorSettingKey, out object? stored) &&
                stored is string name &&
                Enum.TryParse(name, out ZoomAnchorMode mode))
            {
                _zoomAnchorMode = mode;
            }
        }
        catch { }

        if (ZoomAnchorComboBox != null)
        {
            _suppressZoomAnchorComboEvent = true;
            ZoomAnchorComboBox.SelectedIndex = _zoomAnchorMode == ZoomAnchorMode.PageCenter ? 1 : 0;
            _suppressZoomAnchorComboEvent = false;
        }
    }

    private void ZoomAnchorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomAnchorComboEvent || ZoomAnchorComboBox == null) return;

        _zoomAnchorMode = ZoomAnchorComboBox.SelectedIndex == 1
            ? ZoomAnchorMode.PageCenter
            : ZoomAnchorMode.Cursor;

        try
        {
            AppData.LocalSettings.Values[ZoomAnchorSettingKey] = _zoomAnchorMode.ToString();
        }
        catch { }
    }

    // ------------------------------------------------------------------ wheel routing

    private void Pages_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var sv = PdfScrollViewer;
        if (sv == null) return;

        var point = e.GetCurrentPoint(sv);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var modifiers = e.KeyModifiers;

        if (point.Properties.IsHorizontalMouseWheel)
        {
            // A tilt wheel arriving mid-glide would be fighting our own eased scroll, so
            // hand control straight back to the wheel.
            StopSmoothHorizontalScroll();

            if (TryTurnPageSideways(delta > 0 ? 1 : -1))
            {
                e.Handled = true;
            }

            return; // otherwise let the ScrollViewer scroll it natively
        }

        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            StopInertia();
            StopSmoothHorizontalScroll();

            // Exponential so each notch is a constant ratio: one 120-unit notch ~ 20%.
            float step = MathF.Pow(1.0015f, delta);
            ApplyZoom(sv.ZoomFactor * step,
                _zoomAnchorMode == ZoomAnchorMode.Cursor ? point.Position : null);
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
        {
            StopInertia();

            if (TryTurnPageSideways(delta > 0 ? -1 : 1))
            {
                e.Handled = true;
                return;
            }

            // Wheel-up scrolls left, as Shift+wheel does elsewhere. Eased towards the
            // target rather than jumped, so repeated notches read as one movement.
            ScrollHorizontallyBy(-delta);
            e.Handled = true;
            return;
        }

        if (_viewMode == PageViewMode.SinglePage && modifiers == VirtualKeyModifiers.None)
        {
            // Page-wise scrolling: once the page bottoms out, the wheel moves to the next.
            if (delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight - 1 && _currentPageIndex < _totalPages - 1)
            {
                NavigateToPage(_currentPageIndex + 1);
                e.Handled = true;
            }
            else if (delta > 0 && sv.VerticalOffset <= 1 && _currentPageIndex > 0)
            {
                NavigateToPage(_currentPageIndex - 1);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Turns the page when a sideways gesture has nowhere to scroll: in single-page mode
    /// with the page fully on screen, sideways means "next page", not "nothing happens".
    /// </summary>
    private bool TryTurnPageSideways(int direction)
    {
        if (_viewMode != PageViewMode.SinglePage) return false;
        if (PdfScrollViewer.ScrollableWidth > 1) return false; // there is room to scroll

        return TurnPage(direction);
    }

    private bool TurnPage(int direction)
    {
        int target = _currentPageIndex + direction;
        if (target < 0 || _totalPages == 0 || target >= _totalPages) return false;

        NavigateToPage(target);
        return true;
    }

    // ------------------------------------------------------- smooth horizontal scroll

    private double _horizontalScrollTarget;
    private bool _horizontalScrollAnimating;
    private double _lastAppliedHorizontalOffset = double.NaN;

    /// <summary>Eases the view towards an accumulated horizontal target, so repeated
    /// notches blend into one movement instead of a series of jumps.</summary>
    private void ScrollHorizontallyBy(double delta)
    {
        var sv = PdfScrollViewer;
        double max = Math.Max(0, sv.ScrollableWidth);
        if (max <= 0) return;

        double basis = _horizontalScrollAnimating ? _horizontalScrollTarget : sv.HorizontalOffset;
        _horizontalScrollTarget = Math.Clamp(basis + delta, 0, max);

        if (!_horizontalScrollAnimating)
        {
            _horizontalScrollAnimating = true;
            CompositionTarget.Rendering += HorizontalScroll_Tick;
        }
    }

    private void StopSmoothHorizontalScroll()
    {
        if (!_horizontalScrollAnimating) return;
        _horizontalScrollAnimating = false;
        _lastAppliedHorizontalOffset = double.NaN;
        CompositionTarget.Rendering -= HorizontalScroll_Tick;
    }

    private void HorizontalScroll_Tick(object? sender, object e)
    {
        var sv = PdfScrollViewer;
        if (sv == null) { StopSmoothHorizontalScroll(); return; }

        double current = sv.HorizontalOffset;

        // If the view moved somewhere we did not put it, something else is driving it --
        // a tilt wheel, a scrollbar, a touch pan -- and continuing would drag it back.
        if (!double.IsNaN(_lastAppliedHorizontalOffset) &&
            Math.Abs(current - _lastAppliedHorizontalOffset) > 12.0)
        {
            StopSmoothHorizontalScroll();
            return;
        }

        double remaining = _horizontalScrollTarget - current;
        if (Math.Abs(remaining) < 0.5)
        {
            sv.ChangeView(_horizontalScrollTarget, null, null, true);
            StopSmoothHorizontalScroll();
            return;
        }

        double next = current + (remaining * 0.25);
        sv.ChangeView(next, null, null, true);
        _lastAppliedHorizontalOffset = next;
    }

    // -------------------------------------------------------------------- hand tool

    private void MainPage_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        // Arrows turn the page in single-page mode, where scrolling cannot reach the next.
        if ((e.Key == VirtualKey.Left || e.Key == VirtualKey.Right) &&
            _viewMode == PageViewMode.SinglePage && !IsKeyboardInputFocused())
        {
            if (TurnPage(e.Key == VirtualKey.Right ? 1 : -1)) e.Handled = true;
            return;
        }

        // Esc leaves full screen, but the search bar uses it too, so defer while typing.
        if (e.Key == VirtualKey.Escape && _isFullScreen && !IsKeyboardInputFocused())
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Space) return;
        if (e.KeyStatus.WasKeyDown) { e.Handled = true; return; } // swallow auto-repeat
        if (IsKeyboardInputFocused()) return;

        SetHandToolArmed(true);
        e.Handled = true;
    }

    private void MainPage_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !_handToolArmed) return;

        SetHandToolArmed(false);
        e.Handled = true;
    }

    private bool IsKeyboardInputFocused()
    {
        try
        {
            object? focused = FocusManager.GetFocusedElement(this.XamlRoot);

            // Space types in a text field and activates a focused button, so the hand
            // tool must not swallow it in either case.
            return focused is TextBox or AutoSuggestBox or RichEditBox or PasswordBox
                or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ComboBox or ToggleSwitch;
        }
        catch
        {
            return false;
        }
    }

    private void SetHandToolArmed(bool armed)
    {
        _handToolArmed = armed;

        if (!armed && _handToolPanning)
        {
            EndHandPan(startInertia: true);
        }

        UpdateHandCursor();
    }

    private void UpdateHandCursor()
    {
        try
        {
            // ProtectedCursor is only settable on this element, so the hand applies to the
            // whole page while space is held rather than just over the viewer.
            // Windows ships no open-hand cursor: IDC_HAND is the pointing finger, which
            // reads as "click this link". SizeAll at least says "drag to move".
            this.ProtectedCursor = _handToolArmed
                ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll)
                : null;
        }
        catch { }
    }

    private void PdfScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StopInertia();
        StopSmoothHorizontalScroll();
        BeginSwipeTracking(e);

        if (!_handToolArmed || _handToolPanning) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return; // touch already pans

        var sv = PdfScrollViewer;
        _handToolPanning = true;
        _handDragStart = e.GetCurrentPoint(sv).Position;
        _handStartHorizontalOffset = sv.HorizontalOffset;
        _handStartVerticalOffset = sv.VerticalOffset;

        _handLastSamplePoint = _handDragStart;
        _handLastSampleTicks = DateTime.UtcNow.Ticks;
        _handVelocityX = 0;
        _handVelocityY = 0;

        sv.CapturePointer(e.Pointer);
        UpdateHandCursor();
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var sv = PdfScrollViewer;
        if (sv == null) return;

        Point position = e.GetCurrentPoint(sv).Position;
        _lastViewerPointerPosition = position;

        if (_swipeTracking && e.Pointer.PointerId == _swipePointerId) _swipeLast = position;

        if (!_handToolPanning) return;

        // Dragging the page right must move the content right, so offsets go the other way.
        double targetH = _handStartHorizontalOffset - (position.X - _handDragStart.X);
        double targetV = _handStartVerticalOffset - (position.Y - _handDragStart.Y);
        sv.ChangeView(Math.Max(0, targetH), Math.Max(0, targetV), null, true);

        long now = DateTime.UtcNow.Ticks;
        double elapsedMs = (now - _handLastSampleTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (elapsedMs >= 8)
        {
            // Blend with the previous sample so a single jittery frame cannot define the flick.
            double sampleVx = (position.X - _handLastSamplePoint.X) / elapsedMs;
            double sampleVy = (position.Y - _handLastSamplePoint.Y) / elapsedMs;
            _handVelocityX = (_handVelocityX * 0.4) + (sampleVx * 0.6);
            _handVelocityY = (_handVelocityY * 0.4) + (sampleVy * 0.6);

            _handLastSamplePoint = position;
            _handLastSampleTicks = now;
        }

        e.Handled = true;
    }

    private void PdfScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (EndSwipeTracking(e))
        {
            e.Handled = true;
            return;
        }

        if (!_handToolPanning) return;

        PdfScrollViewer.ReleasePointerCapture(e.Pointer);
        EndHandPan(startInertia: true);
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // The ScrollViewer takes the pointer to pan, so a swipe usually ends here rather
        // than in PointerReleased.
        EndSwipeTracking(e);

        if (!_handToolPanning) return;
        EndHandPan(startInertia: false);
    }

    private void PdfScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Stop anchoring zoom to a cursor that is no longer over the page.
        if (!_handToolPanning) _lastViewerPointerPosition = null;
    }

    private void EndHandPan(bool startInertia)
    {
        _handToolPanning = false;
        UpdateHandCursor();

        if (!startInertia) return;

        // A slow release is a placement, not a flick.
        double speed = Math.Sqrt((_handVelocityX * _handVelocityX) + (_handVelocityY * _handVelocityY));
        if (speed < InertiaMinSpeed) return;

        // Stale velocity means the pointer had already stopped before release.
        double sinceLastSampleMs =
            (DateTime.UtcNow.Ticks - _handLastSampleTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (sinceLastSampleMs > 80) return;

        StartInertia();
    }

    private void StartInertia()
    {
        if (_inertiaRunning) return;
        _inertiaRunning = true;
        _inertiaLastTicks = DateTime.UtcNow.Ticks;
        CompositionTarget.Rendering += Inertia_Tick;
    }

    private void StopInertia()
    {
        if (!_inertiaRunning) return;
        _inertiaRunning = false;
        CompositionTarget.Rendering -= Inertia_Tick;
        _handVelocityX = 0;
        _handVelocityY = 0;
    }

    private void Inertia_Tick(object? sender, object e)
    {
        var sv = PdfScrollViewer;
        if (sv == null) { StopInertia(); return; }

        long now = DateTime.UtcNow.Ticks;
        double elapsedMs = (now - _inertiaLastTicks) / (double)TimeSpan.TicksPerMillisecond;
        _inertiaLastTicks = now;
        if (elapsedMs <= 0) return;

        // Clamp so a stalled frame cannot launch the view across the document.
        elapsedMs = Math.Min(elapsedMs, 64);

        double targetH = sv.HorizontalOffset - (_handVelocityX * elapsedMs);
        double targetV = sv.VerticalOffset - (_handVelocityY * elapsedMs);

        double clampedH = Math.Clamp(targetH, 0, Math.Max(0, sv.ScrollableWidth));
        double clampedV = Math.Clamp(targetV, 0, Math.Max(0, sv.ScrollableHeight));

        // Kill the component that hit an edge so the glide does not grind against it.
        if (Math.Abs(clampedH - targetH) > 0.5) _handVelocityX = 0;
        if (Math.Abs(clampedV - targetV) > 0.5) _handVelocityY = 0;

        sv.ChangeView(clampedH, clampedV, null, true);

        double decay = Math.Pow(InertiaDecayPerSecond, elapsedMs / 1000.0);
        _handVelocityX *= decay;
        _handVelocityY *= decay;

        double speed = Math.Sqrt((_handVelocityX * _handVelocityX) + (_handVelocityY * _handVelocityY));
        if (speed < InertiaMinSpeed) StopInertia();
    }


    // ------------------------------------------------------------------- page swipes

    // A sideways drag turns the page in single-page mode, where there is otherwise no way
    // out of the current page by gesture alone.
    private const double SwipeMinDistance = 60.0;
    private const double SwipeDirectionRatio = 1.5;

    private bool _swipeTracking;
    private uint _swipePointerId;
    private Point _swipeStart;
    private Point _swipeLast;

    private void BeginSwipeTracking(PointerRoutedEventArgs e)
    {
        _swipeTracking = false;
        if (_viewMode != PageViewMode.SinglePage) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
        if (_handToolArmed) return;

        _swipeTracking = true;
        _swipePointerId = e.Pointer.PointerId;
        _swipeStart = e.GetCurrentPoint(PdfScrollViewer).Position;
        _swipeLast = _swipeStart;
    }

    /// <summary>Completes a swipe and turns the page if the gesture qualifies.</summary>
    private bool EndSwipeTracking(PointerRoutedEventArgs e)
    {
        if (!_swipeTracking || e.Pointer.PointerId != _swipePointerId) return false;
        _swipeTracking = false;

        // Only when the page is fully on screen; otherwise sideways means panning.
        if (PdfScrollViewer.ScrollableWidth > 1) return false;

        double dx = _swipeLast.X - _swipeStart.X;
        double dy = _swipeLast.Y - _swipeStart.Y;

        if (Math.Abs(dx) < SwipeMinDistance) return false;
        if (Math.Abs(dx) < Math.Abs(dy) * SwipeDirectionRatio) return false; // mostly vertical

        // Dragging the page leftwards pulls the next one in, as on a touch screen.
        return TurnPage(dx < 0 ? 1 : -1);
    }
    // ------------------------------------------------------------------ drag and drop

    private void MainPage_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        try
        {
            e.DragUIOverride.Caption = LocalizationService.Get("DropToOpen");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        catch { }
        e.Handled = true;
    }

    private async void MainPage_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var pdf = items
                .OfType<StorageFile>()
                .FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));

            if (pdf != null)
            {
                await OpenPdfFileAsync(pdf);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Drop failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ------------------------------------------------------- visible-page throttling

    private DispatcherTimer? _visiblePagesTimer;
    private long _lastVisiblePagesTicks;
    private const int VisiblePagesThrottleMs = 100;

    /// <summary>
    /// Coalesces the expensive visible-page pass. Throttled rather than debounced: a long
    /// continuous scroll must still page content in, not wait for the gesture to finish.
    /// </summary>
    private void RequestVisiblePagesUpdate(bool immediate)
    {
        if (immediate)
        {
            _visiblePagesTimer?.Stop();
            RunVisiblePagesUpdate();
            return;
        }

        double sinceMs =
            (DateTime.UtcNow.Ticks - _lastVisiblePagesTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (sinceMs >= VisiblePagesThrottleMs)
        {
            RunVisiblePagesUpdate();
            return;
        }

        if (_visiblePagesTimer == null)
        {
            _visiblePagesTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(VisiblePagesThrottleMs)
            };
            _visiblePagesTimer.Tick += (_, _) =>
            {
                _visiblePagesTimer!.Stop();
                RunVisiblePagesUpdate();
            };
        }

        if (!_visiblePagesTimer.IsEnabled) _visiblePagesTimer.Start();
    }

    private void RunVisiblePagesUpdate()
    {
        _lastVisiblePagesTicks = DateTime.UtcNow.Ticks;
        UpdateVisiblePages();
    }

    /// <summary>Shared entry point for opening a file from outside the picker:
    /// drag-and-drop, the command line and file associations.</summary>
    internal async System.Threading.Tasks.Task OpenPdfFileAsync(StorageFile file)
    {
        DocTitleText.Text = file.Name;
        await LoadPdfAsync(file);
    }
}
