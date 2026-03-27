using System.Windows;
using System.Windows.Controls;

namespace HitePhoto.PrintStation.UI;

public class SendEmailWindow : Window
{
    public string ToAddress { get; private set; }
    public string Subject { get; private set; }
    public string Body { get; private set; }

    public SendEmailWindow(string to, string subject, string body)
    {
        ToAddress = to;
        Subject = subject;
        Body = body;

        Title = "Send Email";
        Width = 500;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "Send Email to Customer",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        // To
        panel.Children.Add(new TextBlock { Text = "To", Margin = new Thickness(0, 0, 0, 2) });
        var toBox = new TextBox { Text = to, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(toBox);

        // Subject
        panel.Children.Add(new TextBlock { Text = "Subject", Margin = new Thickness(0, 0, 0, 2) });
        var subjectBox = new TextBox { Text = subject, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(subjectBox);

        // Body
        panel.Children.Add(new TextBlock { Text = "Body", Margin = new Thickness(0, 0, 0, 2) });
        var bodyBox = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 200,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(bodyBox);

        // Buttons
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var sendBtn = new Button
        {
            Content = "Send",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        sendBtn.Click += (s, e) => { DialogResult = true; };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true
        };

        buttons.Children.Add(sendBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
