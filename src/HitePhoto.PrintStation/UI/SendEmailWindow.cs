using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.UI;

public class SendEmailWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly List<EmailTemplate> _templates;
    private readonly AppSettings _settings;
    private readonly Order _order;
    private readonly ComboBox _templateCombo;
    private readonly TextBox _subjectBox;
    private readonly TextBox _bodyBox;
    private readonly Button _pickupDefaultBtn;
    private readonly Button _mailDefaultBtn;

    public EmailTemplate? SelectedTemplate { get; private set; }
    public string FinalSubject => _subjectBox.Text;
    public string FinalBody => _bodyBox.Text;
    public bool TemplatesChanged { get; private set; }

    public SendEmailWindow(
        Order order,
        AppSettings settings)
    {
        _order = order;
        _settings = settings;
        _templates = settings.EmailTemplates;

        Title = $"Email — {order.ExternalOrderId}";
        Width = 520;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        // Position so Send button lands under cursor
        GetCursorPos(out var pt);
        Left = pt.X - 250;
        Top = pt.Y - 460;

        var root = new DockPanel { Margin = new Thickness(12) };

        // ── Template row ──
        var templateRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var managePanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(managePanel, Dock.Right);

        _pickupDefaultBtn = new Button
        {
            Content = "Pickup default",
            Width = 100, Margin = new Thickness(4, 0, 0, 0), FontSize = 11
        };
        _pickupDefaultBtn.Click += (_, _) => ToggleDefault(isShipped: false);

        _mailDefaultBtn = new Button
        {
            Content = "Mail default",
            Width = 90, Margin = new Thickness(4, 0, 0, 0), FontSize = 11
        };
        _mailDefaultBtn.Click += (_, _) => ToggleDefault(isShipped: true);

        var newBtn = new Button { Content = "New", Width = 50, Margin = new Thickness(4, 0, 0, 0), FontSize = 12 };
        newBtn.Click += NewTemplate_Click;
        var editBtn = new Button { Content = "Edit", Width = 50, Margin = new Thickness(4, 0, 0, 0), FontSize = 12 };
        editBtn.Click += EditTemplate_Click;
        var delBtn = new Button { Content = "Del", Width = 45, Margin = new Thickness(4, 0, 0, 0), FontSize = 12 };
        delBtn.Click += DeleteTemplate_Click;

        managePanel.Children.Add(_pickupDefaultBtn);
        managePanel.Children.Add(_mailDefaultBtn);
        managePanel.Children.Add(newBtn);
        managePanel.Children.Add(editBtn);
        managePanel.Children.Add(delBtn);
        templateRow.Children.Add(managePanel);

        var templateLabel = new TextBlock
        {
            Text = "Template:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        DockPanel.SetDock(templateLabel, Dock.Left);
        templateRow.Children.Add(templateLabel);

        _templateCombo = new ComboBox
        {
            DisplayMemberPath = "Name",
            ItemsSource = _templates,
            MaxWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140
        };
        _templateCombo.SelectionChanged += TemplateCombo_SelectionChanged;
        templateRow.Children.Add(_templateCombo);

        DockPanel.SetDock(templateRow, Dock.Top);
        root.Children.Add(templateRow);

        var toLabel = new TextBlock { Text = "To:", Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(toLabel, Dock.Top);
        root.Children.Add(toLabel);

        var toBox = new TextBox
        {
            Text = order.CustomerEmail ?? "",
            IsReadOnly = true,
            Margin = new Thickness(0, 0, 0, 8),
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };
        DockPanel.SetDock(toBox, Dock.Top);
        root.Children.Add(toBox);

        // ── Subject ──
        var subLabel = new TextBlock { Text = "Subject:", Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(subLabel, Dock.Top);
        root.Children.Add(subLabel);

        _subjectBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_subjectBox, Dock.Top);
        root.Children.Add(_subjectBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var sendBtn = new Button
        {
            Content = "Send",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            FontWeight = FontWeights.SemiBold
        };
        sendBtn.Click += (_, _) =>
        {
            SelectedTemplate = _templateCombo.SelectedItem as EmailTemplate;
            DialogResult = true;
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true
        };

        buttons.Children.Add(sendBtn);
        buttons.Children.Add(cancelBtn);
        root.Children.Add(buttons);

        var bodyLabel = new TextBlock { Text = "Body:", Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(bodyLabel, Dock.Top);
        root.Children.Add(bodyLabel);

        _bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(_bodyBox);

        Content = root;

        // Select default template based on this order's delivery method
        var isShipped = order.DeliveryMethodId == DeliveryMethodId.Ship;
        _templateCombo.SelectedItem = _settings.GetDefaultTemplate(isShipped) ?? _templates.FirstOrDefault();
    }

    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_templateCombo.SelectedItem is not EmailTemplate tmpl) return;
        _subjectBox.Text = EmailService.ReplacePlaceholders(tmpl.Subject, _order);
        _bodyBox.Text = EmailService.ReplacePlaceholders(tmpl.Body, _order);
        UpdateDefaultButtonsState();
    }

    private void UpdateDefaultButtonsState()
    {
        var current = _templateCombo.SelectedItem as EmailTemplate;
        var name = current?.Name ?? "";

        StyleDefaultButton(_pickupDefaultBtn, _settings.IsDefault(name, isShipped: false), name, "pickup");
        StyleDefaultButton(_mailDefaultBtn,   _settings.IsDefault(name, isShipped: true),  name, "mail");

        _pickupDefaultBtn.IsEnabled = current != null;
        _mailDefaultBtn.IsEnabled = current != null;
    }

    private static void StyleDefaultButton(Button btn, bool isActive, string name, string label)
    {
        btn.Background = isActive ? Brushes.LightGreen : SystemColors.ControlBrush;
        btn.FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal;
        btn.ToolTip = isActive
            ? $"\"{name}\" is the {label} default — click to clear"
            : $"Set \"{name}\" as the {label} default";
    }

    private void ToggleDefault(bool isShipped)
    {
        if (_templateCombo.SelectedItem is not EmailTemplate current) return;

        var newValue = _settings.IsDefault(current.Name, isShipped) ? "" : current.Name;
        _settings.SetDefault(newValue, isShipped);
        TemplatesChanged = true;
        UpdateDefaultButtonsState();
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TemplateEditorWindow(new EmailTemplate()) { Owner = this };
        if (editor.ShowDialog() != true) return;

        _templates.Add(editor.EditedTemplate);
        TemplatesChanged = true;
        RefreshCombo(editor.EditedTemplate);
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_templateCombo.SelectedItem is not EmailTemplate current) return;

        // Edit a copy, apply on OK
        var copy = new EmailTemplate
        {
            Name = current.Name,
            Subject = current.Subject,
            Body = current.Body
        };
        var editor = new TemplateEditorWindow(copy) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var oldName = current.Name;
        current.Name = editor.EditedTemplate.Name;
        current.Subject = editor.EditedTemplate.Subject;
        current.Body = editor.EditedTemplate.Body;

        _settings.RenameDefault(oldName, current.Name);
        TemplatesChanged = true;
        RefreshCombo(current);

        _subjectBox.Text = EmailService.ReplacePlaceholders(current.Subject, _order);
        _bodyBox.Text = EmailService.ReplacePlaceholders(current.Body, _order);
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_templateCombo.SelectedItem is not EmailTemplate current) return;
        if (_templates.Count <= 1)
        {
            MessageBox.Show("Cannot delete the last template.", "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"Delete template \"{current.Name}\"?",
            "Delete Template", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _templates.Remove(current);
        _settings.ClearDefaultIfMatches(current.Name);

        TemplatesChanged = true;
        RefreshCombo(_templates.FirstOrDefault());
    }

    private void RefreshCombo(EmailTemplate? select)
    {
        _templateCombo.ItemsSource = null;
        _templateCombo.ItemsSource = _templates;
        _templateCombo.SelectedItem = select;
    }
}

/// <summary>
/// Simple dialog for creating or editing an email template's name, subject, and body.
/// </summary>
public class TemplateEditorWindow : Window
{
    public EmailTemplate EditedTemplate { get; }

    private readonly TextBox _nameBox;
    private readonly TextBox _subjectBox;
    private readonly TextBox _bodyBox;

    public TemplateEditorWindow(EmailTemplate template)
    {
        EditedTemplate = template;
        Title = string.IsNullOrEmpty(template.Name) ? "New Template" : $"Edit — {template.Name}";
        Width = 480;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var panel = new DockPanel { Margin = new Thickness(12) };

        // Name
        var nameLabel = new TextBlock { Text = "Template Name:", Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(nameLabel, Dock.Top);
        panel.Children.Add(nameLabel);

        _nameBox = new TextBox { Text = template.Name, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_nameBox, Dock.Top);
        panel.Children.Add(_nameBox);

        // Subject
        var subLabel = new TextBlock
        {
            Text = "Subject (placeholders: {CustomerName}, {OrderId}, {StoreName}, {OrderDate}):",
            Margin = new Thickness(0, 0, 0, 2),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };
        DockPanel.SetDock(subLabel, Dock.Top);
        panel.Children.Add(subLabel);

        _subjectBox = new TextBox { Text = template.Subject, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_subjectBox, Dock.Top);
        panel.Children.Add(_subjectBox);

        // Buttons at bottom
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var saveBtn = new Button
        {
            Content = "Save",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
            {
                MessageBox.Show("Template name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            EditedTemplate.Name = _nameBox.Text.Trim();
            EditedTemplate.Subject = _subjectBox.Text;
            EditedTemplate.Body = _bodyBox.Text;
            DialogResult = true;
        };

        var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        buttons.Children.Add(saveBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        // Body (fills remaining)
        var bodyLabel = new TextBlock { Text = "Body:", Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(bodyLabel, Dock.Top);
        panel.Children.Add(bodyLabel);

        _bodyBox = new TextBox
        {
            Text = template.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(_bodyBox);

        Content = panel;
    }
}
