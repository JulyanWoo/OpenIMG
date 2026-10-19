using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenIMG.App.Controls;

public partial class ImageComparisonSlider : UserControl
{
    private bool _isDragging;
    private bool _isPanning;
    private double _splitPosition = 0.5; // 0.0 to 1.0
    private double _zoom = 1.0;
    private Point _lastPanPosition;

    public static readonly DependencyProperty OriginalImageSourceProperty =
        DependencyProperty.Register(nameof(OriginalImageSource), typeof(ImageSource), typeof(ImageComparisonSlider),
            new PropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty RestoredImageSourceProperty =
        DependencyProperty.Register(nameof(RestoredImageSource), typeof(ImageSource), typeof(ImageComparisonSlider),
            new PropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty OriginalStatusTextProperty =
        DependencyProperty.Register(nameof(OriginalStatusText), typeof(string), typeof(ImageComparisonSlider),
            new PropertyMetadata("ORIGINAL"));

    public static readonly DependencyProperty RestoredStatusTextProperty =
        DependencyProperty.Register(nameof(RestoredStatusText), typeof(string), typeof(ImageComparisonSlider),
            new PropertyMetadata("GPT RESULT"));

    public ImageSource? OriginalImageSource
    {
        get => (ImageSource?)GetValue(OriginalImageSourceProperty);
        set => SetValue(OriginalImageSourceProperty, value);
    }

    public ImageSource? RestoredImageSource
    {
        get => (ImageSource?)GetValue(RestoredImageSourceProperty);
        set => SetValue(RestoredImageSourceProperty, value);
    }

    public string OriginalStatusText
    {
        get => (string)GetValue(OriginalStatusTextProperty);
        set => SetValue(OriginalStatusTextProperty, value);
    }

    public string RestoredStatusText
    {
        get => (string)GetValue(RestoredStatusTextProperty);
        set => SetValue(RestoredStatusTextProperty, value);
    }

    public ImageComparisonSlider()
    {
        InitializeComponent();
        Loaded += (s, e) => UpdateSplitter();
    }

    private static void OnImageSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ImageComparisonSlider viewer)
        {
            viewer.UpdateImageCanvasBounds();
            viewer.UpdateSplitter();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageCanvasBounds();
        UpdateSplitter();
    }

    private void UpdateImageCanvasBounds()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || OriginalImageSource == null)
            return;

        var sourceWidth = OriginalImageSource.Width;
        var sourceHeight = OriginalImageSource.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return;

        var sourceAspectRatio = sourceWidth / sourceHeight;
        var viewportAspectRatio = ActualWidth / ActualHeight;
        var canvasWidth = sourceAspectRatio > viewportAspectRatio
            ? ActualWidth
            : ActualHeight * sourceAspectRatio;
        var canvasHeight = sourceAspectRatio > viewportAspectRatio
            ? ActualWidth / sourceAspectRatio
            : ActualHeight;

        BeforeImage.Width = canvasWidth;
        BeforeImage.Height = canvasHeight;
        AfterImage.Width = canvasWidth;
        AfterImage.Height = canvasHeight;
    }

    private void OnThumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        HandleThumb.CaptureMouse();
    }

    private void OnThumbMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        HandleThumb.ReleaseMouseCapture();
    }

    private void OnThumbMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var pos = e.GetPosition(this);
        if (ActualWidth > 0)
        {
            _splitPosition = Math.Clamp(pos.X / ActualWidth, 0.0, 1.0);
            UpdateSplitter();
        }
    }

    private void UpdateSplitter()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var x = ActualWidth * _splitPosition;

        // Clip the Restored image to show from x to ActualWidth
        ClipGeometry.Rect = new Rect(x, 0, Math.Max(0, ActualWidth - x), ActualHeight);

        DividerLine.X1 = x;
        DividerLine.X2 = x;
        DividerLine.Y1 = 0;
        DividerLine.Y2 = ActualHeight;

        Canvas.SetLeft(HandleThumb, x - (HandleThumb.Width / 2));
        Canvas.SetTop(HandleThumb, (ActualHeight / 2) - (HandleThumb.Height / 2));
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        SetZoom(_zoom * factor);
        e.Handled = true;
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoom <= 1.0)
            return;

        _isPanning = true;
        _lastPanPosition = e.GetPosition(this);
        Mouse.Capture(this);
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
            return;

        _isPanning = false;
        Mouse.Capture(null);
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.RightButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(this);
        var delta = current - _lastPanPosition;
        _lastPanPosition = current;

        BeforeTranslate.X += delta.X;
        BeforeTranslate.Y += delta.Y;
        AfterTranslate.X = BeforeTranslate.X;
        AfterTranslate.Y = BeforeTranslate.Y;
        e.Handled = true;
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        FitToView();
        e.Handled = true;
    }

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.2);

    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.2);

    private void OnFitClicked(object sender, RoutedEventArgs e) => FitToView();

    private void FitToView()
    {
        _zoom = 1.0;
        BeforeTranslate.X = 0;
        BeforeTranslate.Y = 0;
        AfterTranslate.X = 0;
        AfterTranslate.Y = 0;
        ApplyZoom();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 1.0, 8.0);
        if (_zoom <= 1.001)
        {
            _zoom = 1.0;
            BeforeTranslate.X = 0;
            BeforeTranslate.Y = 0;
            AfterTranslate.X = 0;
            AfterTranslate.Y = 0;
        }

        ApplyZoom();
    }

    private void ApplyZoom()
    {
        BeforeScale.ScaleX = _zoom;
        BeforeScale.ScaleY = _zoom;
        AfterScale.ScaleX = _zoom;
        AfterScale.ScaleY = _zoom;
        ZoomHint.Text = $"{_zoom:P0}";
    }
}
