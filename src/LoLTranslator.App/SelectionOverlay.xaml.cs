using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LoLTranslator.App;

public partial class SelectionOverlay : Window
{
    private Point _startPoint;
    private bool _isDragging;

    public Rect? SelectedRegion { get; private set; }

    public SelectionOverlay()
    {
        InitializeComponent();
    }

    private void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    private void Window_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(OverlayCanvas);
        _isDragging = true;

        Canvas.SetLeft(SelectionBorder, _startPoint.X);
        Canvas.SetTop(SelectionBorder, _startPoint.Y);

        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;
        SelectionBorder.Visibility = Visibility.Visible;

        CaptureMouse();
    }

    private void Window_MouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Point currentPoint = e.GetPosition(OverlayCanvas);

        double left =
            Math.Min(_startPoint.X, currentPoint.X);

        double top =
            Math.Min(_startPoint.Y, currentPoint.Y);

        double width =
            Math.Abs(currentPoint.X - _startPoint.X);

        double height =
            Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);

        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }

    private void Window_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();

        Point endPoint =
            e.GetPosition(OverlayCanvas);

        double localWidth =
            Math.Abs(endPoint.X - _startPoint.X);

        double localHeight =
            Math.Abs(endPoint.Y - _startPoint.Y);

        if (localWidth < 20 || localHeight < 20)
        {
            SelectionBorder.Visibility =
                Visibility.Collapsed;

            return;
        }

        // 转换为真实的屏幕像素坐标。
        // 这样可以正确处理 Windows 显示缩放。
        Point startScreenPoint =
            PointToScreen(_startPoint);

        Point endScreenPoint =
            PointToScreen(endPoint);

        double screenLeft =
            Math.Min(
                startScreenPoint.X,
                endScreenPoint.X);

        double screenTop =
            Math.Min(
                startScreenPoint.Y,
                endScreenPoint.Y);

        double screenWidth =
            Math.Abs(
                endScreenPoint.X -
                startScreenPoint.X);

        double screenHeight =
            Math.Abs(
                endScreenPoint.Y -
                startScreenPoint.Y);

        SelectedRegion = new Rect(
            screenLeft,
            screenTop,
            screenWidth,
            screenHeight);

        DialogResult = true;
    }

    private void Window_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}