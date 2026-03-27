using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Core.Processing;

namespace HitePhoto.PrintStation.UI;

public class EmailTemplateChooser : Window
{
    private readonly ComboBox _combo;

    public EmailTemplate? SelectedTemplate { get; private set; }

    public EmailTemplateChooser(List<EmailTemplate> templates, string? defaultName = null)
    {
        Title = "Email Template";
        Width = 400;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "Choose a template for this email:",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        row.Children.Add(new TextBlock
        {
            Text = "Template:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        _combo = new ComboBox
        {
            Width = 250,
            DisplayMemberPath = "Name",
            ItemsSource = templates,
            SelectedItem = templates.FirstOrDefault(t =>
                t.Name.Equals(defaultName ?? "", System.StringComparison.OrdinalIgnoreCase))
                ?? templates.FirstOrDefault()
        };
        row.Children.Add(_combo);
        panel.Children.Add(row);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var yesBtn = new Button
        {
            Content = "Yes",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        yesBtn.Click += (s, e) =>
        {
            SelectedTemplate = _combo.SelectedItem as EmailTemplate;
            DialogResult = true;
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true
        };

        buttons.Children.Add(yesBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
