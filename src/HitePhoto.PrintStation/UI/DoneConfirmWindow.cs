using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HitePhoto.PrintStation.UI;

public enum DoneAction { None, MarkDone, MarkDoneAndEmail }

public class DoneConfirmWindow : Window
{
    private readonly Button _markDoneBtn;
    private readonly Button _markDoneEmailBtn;
    private readonly Point _cursorAtCreate;
    private readonly bool _cursorOverEmail;

    public DoneAction Result { get; private set; } = DoneAction.None;

    public DoneConfirmWindow(string customerName, string orderId, bool alreadyDone, bool alreadyEmailed,
        bool cursorOverEmail = false)
    {
        _cursorAtCreate = GetMouseScreenPosition();
        _cursorOverEmail = cursorOverEmail;

        Title = "Mark Done";
        Width = 380;
        Height = 160;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = (Brush)FindResource("WindowBg");
        Foreground = (Brush)FindResource("TextPrimary");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var status = alreadyDone ? " (already done)" : "";
        var emailStatus = alreadyEmailed ? " (already sent)" : "";

        var msg = new TextBlock
        {
            Text = $"{orderId}  —  {customerName}{status}",
            FontSize = 14,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(msg, 0);
        grid.Children.Add(msg);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _markDoneBtn = new Button
        {
            Content = "Mark Printed",
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        _markDoneBtn.Click += (_, _) => { Result = DoneAction.MarkDone; DialogResult = true; };
        btnPanel.Children.Add(_markDoneBtn);

        _markDoneEmailBtn = new Button
        {
            Content = $"Printed + Email{emailStatus}",
            Width = 140,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _markDoneEmailBtn.Click += (_, _) => { Result = DoneAction.MarkDoneAndEmail; DialogResult = true; };
        btnPanel.Children.Add(_markDoneEmailBtn);

        var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancelBtn.Click += (_, _) => { DialogResult = false; };
        btnPanel.Children.Add(cancelBtn);

        Grid.SetRow(btnPanel, 1);
        grid.Children.Add(btnPanel);

        Content = grid;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;

        double cursorX = _cursorAtCreate.X * dpiScale;
        double cursorY = _cursorAtCreate.Y * dpiScale;

        var targetBtn = _cursorOverEmail ? _markDoneEmailBtn : _markDoneBtn;
        var btnScreen = targetBtn.PointToScreen(
            new Point(targetBtn.ActualWidth / 2, targetBtn.ActualHeight / 2));

        double btnScreenX = btnScreen.X * dpiScale;
        double btnScreenY = btnScreen.Y * dpiScale;

        double targetLeft = Left + (cursorX - btnScreenX);
        double targetTop = Top + (cursorY - btnScreenY);

        var wa = SystemParameters.WorkArea;
        targetLeft = Math.Max(wa.Left, Math.Min(targetLeft, wa.Right - ActualWidth));
        targetTop = Math.Max(wa.Top, Math.Min(targetTop, wa.Bottom - ActualHeight));

        Left = targetLeft;
        Top = targetTop;

        targetBtn.Focus();
    }

    private static Point GetMouseScreenPosition()
    {
        GetCursorPos(out POINT p);
        return new Point(p.X, p.Y);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
