using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly AppSettings _settings;

    private readonly ComboBox[] _slotCombos = new ComboBox[23];
    private List<RoutingRuleRow> _routingRows = new();

    public bool SettingsChanged { get; private set; }

    public SettingsWindow(AppSettings settings, SettingsManager settingsManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        _settings = settings;

        PopulateSettingsUi();
        PopulateSlotLayoutTab();
        RefreshRoutingGrid();
        RefreshLayoutGrid();
        PopulateRoutingChannelCombo();
    }

    // Compat with old MainWindow call signature
    public SettingsWindow(AppSettings settings, SettingsManager settingsManager, object? _)
        : this(settings, settingsManager) { }

    // ══════════════════════════════════════════════════════════════════════
    //  Populate / Save
    // ══════════════════════════════════════════════════════════════════════

    private void PopulateSettingsUi()
    {
        // Sources
        DakisEnabledCheck.IsChecked = _settings.DakisEnabled;
        DakisWatchBox.Text = _settings.DakisWatchFolder;
        NoritsuOutputBox.Text = _settings.NoritsuOutputRoot;
        ChannelsCsvBox.Text = _settings.ChannelsCsvPath;
        RefreshIntervalBox.Text = _settings.RefreshIntervalSeconds.ToString();
        StoreIdBox.Text = _settings.StoreId.ToString();
        PollIntervalBox.Text = _settings.PollIntervalSeconds.ToString();
        DaysToLoadBox.Text = _settings.DaysToLoad.ToString();

        // Pixfizz
        PixfizzEnabledCheck.IsChecked = _settings.PixfizzEnabled;
        PixfizzApiUrlBox.Text = _settings.PixfizzApiUrl;
        PixfizzApiKeyBox.Password = _settings.PixfizzApiKey;
        PixfizzOrgBox.Text = _settings.PixfizzOrganizationId;
        PixfizzLocationBox.Text = _settings.PixfizzLocationId;
        OrderOutputBox.Text = _settings.OrderOutputPath;
        PixfizzFtpServerBox.Text = _settings.PixfizzFtpServer;
        PixfizzFtpPortBox.Text = _settings.PixfizzFtpPort.ToString();
        PixfizzFtpArtworkBox.Text = _settings.PixfizzFtpArtworkFolder;
        PixfizzFtpDarkroomBox.Text = _settings.PixfizzFtpDarkroomFolder;
        PixfizzFtpUserBox.Text = _settings.PixfizzFtpUsername;
        PixfizzFtpPassBox.Password = _settings.PixfizzFtpPassword;

        // Auto Update
        UpdateLocalBox.Text = _settings.UpdateLocalFolder;
        UpdateSftpHostBox.Text = _settings.UpdateSftpHost;
        UpdateSftpPortBox.Text = _settings.UpdateSftpPort.ToString();
        UpdateSftpUserBox.Text = _settings.UpdateSftpUsername;
        UpdateSftpPassBox.Password = _settings.UpdateSftpPassword;
        UpdateSftpFolderBox.Text = _settings.UpdateSftpFolder;

        // Appearance
        var theme = _settings.Theme ?? "Light";
        foreach (ComboBoxItem item in ThemeCombo.Items)
            if (item.Tag as string == theme) { ThemeCombo.SelectedItem = item; break; }

        var font = _settings.TreeFontFamily ?? "Segoe UI";
        foreach (ComboBoxItem item in FontFamilyCombo.Items)
            if (item.Tag as string == font) { FontFamilyCombo.SelectedItem = item; break; }
        OrderFontSizeBox.Text = _settings.OrderFontSize.ToString();
        SizeFontSizeBox.Text = _settings.SizeFontSize.ToString();

        // Notifications
        EmailEnabledCheck.IsChecked = _settings.NotificationsEnabled;
        foreach (ComboBoxItem item in PixfizzNotifyCombo.Items)
            if (item.Content?.ToString() == _settings.PixfizzNotifyMode) { PixfizzNotifyCombo.SelectedItem = item; break; }
        SmtpHostBox.Text = _settings.SmtpHost;
        SmtpPortBox.Text = _settings.SmtpPort.ToString();
        SmtpUserBox.Text = _settings.SmtpUsername;
        SmtpPassBox.Password = _settings.SmtpPassword;
        EmailFromBox.Text = _settings.NotificationFromEmail;
        PopulateTemplateList();

        // Diagnostics
        DevModeCheck.IsChecked = _settings.DeveloperMode;
        LoggingCheck.IsChecked = _settings.EnableLogging;

        // Correction strengths
        var cs = _settings.CorrectionStrengths;
        StrBrightnessBox.Text = cs.Brightness.ToString("F2");
        StrContrastBox.Text = cs.Contrast.ToString("F2");
        StrShadowsBox.Text = cs.Shadows.ToString("F2");
        StrHighlightsBox.Text = cs.Highlights.ToString("F2");
        StrSaturationBox.Text = cs.Saturation.ToString("F2");
        StrColorTempBox.Text = cs.ColorTemp.ToString("F2");
        StrRedBox.Text = cs.Red.ToString("F2");
        StrGreenBox.Text = cs.Green.ToString("F2");
        StrBlueBox.Text = cs.Blue.ToString("F2");
        StrSigmoidalBox.Text = cs.SigmoidalContrast.ToString("F2");
        StrClaheBox.Text = cs.Clahe.ToString("F2");
        StrContrastStretchBox.Text = cs.ContrastStretch.ToString("F2");
        StrLevelsBox.Text = cs.Levels.ToString("F2");

        // Exposure ratios
        var er = _settings.ExposureRatios;
        ExpRatioBrightnessBox.Text = er.Brightness.ToString("F2");
        ExpRatioContrastBox.Text = er.Contrast.ToString("F2");
        ExpRatioShadowsBox.Text = er.Shadows.ToString("F2");
        ExpRatioHighlightsBox.Text = er.Highlights.ToString("F2");

        // Control parameters
        var cp = _settings.ControlParameters;
        WarmthRedBox.Text = cp.WarmthRed.ToString("F2");
        WarmthBlueBox.Text = cp.WarmthBlue.ToString("F2");
        SigmoidalMidpointBox.Text = cp.SigmoidalMidpoint.ToString("F1");
        ClaheXTilesBox.Text = cp.ClaheXTiles.ToString();
        ClaheYTilesBox.Text = cp.ClaheYTiles.ToString();
        ClaheBinsBox.Text = cp.ClaheBins.ToString();
        CSBlackBox.Text = cp.ContrastStretchBlack.ToString("F2");
        CSWhiteBox.Text = cp.ContrastStretchWhite.ToString("F2");
        LevelBlackBox.Text = cp.LevelBlack.ToString();
        LevelWhiteBox.Text = cp.LevelWhite.ToString();

        // Profiles
        RefreshProfileCombo();
    }

    private void SaveSettings()
    {
        // Sources
        _settings.DakisEnabled = DakisEnabledCheck.IsChecked == true;
        _settings.DakisWatchFolder = DakisWatchBox.Text.Trim();
        _settings.NoritsuOutputRoot = NoritsuOutputBox.Text.Trim();
        _settings.ChannelsCsvPath = ChannelsCsvBox.Text.Trim();
        _settings.RefreshIntervalSeconds = int.TryParse(RefreshIntervalBox.Text.Trim(), out int ri) && ri >= 0 ? ri : 30;
        _settings.StoreId = int.TryParse(StoreIdBox.Text.Trim(), out int sid) && sid > 0 ? sid : _settings.StoreId;
        _settings.PollIntervalSeconds = int.TryParse(PollIntervalBox.Text.Trim(), out int pi) && pi >= 5 ? pi : 30;
        _settings.DaysToLoad = int.TryParse(DaysToLoadBox.Text.Trim(), out int dtl) && dtl >= 0 ? dtl : 14;

        // Pixfizz
        _settings.PixfizzEnabled = PixfizzEnabledCheck.IsChecked == true;
        _settings.PixfizzApiUrl = PixfizzApiUrlBox.Text.Trim();
        _settings.PixfizzApiKey = PixfizzApiKeyBox.Password;
        _settings.PixfizzOrganizationId = PixfizzOrgBox.Text.Trim();
        _settings.PixfizzLocationId = PixfizzLocationBox.Text.Trim();
        _settings.OrderOutputPath = OrderOutputBox.Text.Trim();
        _settings.PixfizzFtpServer = PixfizzFtpServerBox.Text.Trim();
        _settings.PixfizzFtpPort = int.TryParse(PixfizzFtpPortBox.Text.Trim(), out int fp) && fp > 0 ? fp : 21;
        _settings.PixfizzFtpArtworkFolder = PixfizzFtpArtworkBox.Text.Trim();
        _settings.PixfizzFtpDarkroomFolder = PixfizzFtpDarkroomBox.Text.Trim();
        _settings.PixfizzFtpUsername = PixfizzFtpUserBox.Text.Trim();
        _settings.PixfizzFtpPassword = PixfizzFtpPassBox.Password;

        // Auto Update
        _settings.UpdateLocalFolder = UpdateLocalBox.Text.Trim();
        _settings.UpdateSftpHost = UpdateSftpHostBox.Text.Trim();
        _settings.UpdateSftpPort = int.TryParse(UpdateSftpPortBox.Text.Trim(), out int up) ? up : 22;
        _settings.UpdateSftpUsername = UpdateSftpUserBox.Text.Trim();
        _settings.UpdateSftpPassword = UpdateSftpPassBox.Password;
        _settings.UpdateSftpFolder = UpdateSftpFolderBox.Text.Trim();

        // Appearance
        if (ThemeCombo.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is string themeName)
            _settings.Theme = themeName;
        if (FontFamilyCombo.SelectedItem is ComboBoxItem fontItem && fontItem.Tag is string fontName)
            _settings.TreeFontFamily = fontName;
        _settings.OrderFontSize = int.TryParse(OrderFontSizeBox.Text.Trim(), out int ofs) && ofs >= 6 && ofs <= 30 ? ofs : 14;
        _settings.SizeFontSize = int.TryParse(SizeFontSizeBox.Text.Trim(), out int sfs) && sfs >= 6 && sfs <= 30 ? sfs : 13;

        // Notifications
        _settings.NotificationsEnabled = EmailEnabledCheck.IsChecked == true;
        _settings.PixfizzNotifyMode = (PixfizzNotifyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Pixfizz";
        _settings.SmtpHost = SmtpHostBox.Text.Trim();
        _settings.SmtpPort = int.TryParse(SmtpPortBox.Text.Trim(), out var sp) ? sp : 465;
        _settings.SmtpUsername = SmtpUserBox.Text.Trim();
        _settings.SmtpPassword = SmtpPassBox.Password;
        _settings.NotificationFromEmail = EmailFromBox.Text.Trim();
        SaveCurrentTemplate();
        _settings.EmailTemplates = _editableTemplates.ToList();
        var first = _settings.EmailTemplates.FirstOrDefault();
        if (first != null)
        {
            _settings.NotificationSubject = first.Subject;
            _settings.NotificationBodyTemplate = first.Body;
        }

        // Controls — slot layout
        _settings.CorrectionSlotLayout = ReadSlotLayoutFromUi();

        // Correction strengths
        var cs = _settings.CorrectionStrengths;
        cs.Brightness = ParseFloat(StrBrightnessBox.Text, cs.Brightness);
        cs.Contrast = ParseFloat(StrContrastBox.Text, cs.Contrast);
        cs.Shadows = ParseFloat(StrShadowsBox.Text, cs.Shadows);
        cs.Highlights = ParseFloat(StrHighlightsBox.Text, cs.Highlights);
        cs.Saturation = ParseFloat(StrSaturationBox.Text, cs.Saturation);
        cs.ColorTemp = ParseFloat(StrColorTempBox.Text, cs.ColorTemp);
        cs.Red = ParseFloat(StrRedBox.Text, cs.Red);
        cs.Green = ParseFloat(StrGreenBox.Text, cs.Green);
        cs.Blue = ParseFloat(StrBlueBox.Text, cs.Blue);
        cs.SigmoidalContrast = ParseFloat(StrSigmoidalBox.Text, cs.SigmoidalContrast);
        cs.Clahe = ParseFloat(StrClaheBox.Text, cs.Clahe);
        cs.ContrastStretch = ParseFloat(StrContrastStretchBox.Text, cs.ContrastStretch);
        cs.Levels = ParseFloat(StrLevelsBox.Text, cs.Levels);

        // Exposure ratios
        var er = _settings.ExposureRatios;
        er.Brightness = ParseFloat(ExpRatioBrightnessBox.Text, er.Brightness);
        er.Contrast = ParseFloat(ExpRatioContrastBox.Text, er.Contrast);
        er.Shadows = ParseFloat(ExpRatioShadowsBox.Text, er.Shadows);
        er.Highlights = ParseFloat(ExpRatioHighlightsBox.Text, er.Highlights);

        // Control parameters
        var cp = _settings.ControlParameters;
        cp.WarmthRed = ParseFloat(WarmthRedBox.Text, cp.WarmthRed);
        cp.WarmthBlue = ParseFloat(WarmthBlueBox.Text, cp.WarmthBlue);
        cp.SigmoidalMidpoint = ParseFloat(SigmoidalMidpointBox.Text, cp.SigmoidalMidpoint);
        cp.ClaheXTiles = ParseInt(ClaheXTilesBox.Text, cp.ClaheXTiles);
        cp.ClaheYTiles = ParseInt(ClaheYTilesBox.Text, cp.ClaheYTiles);
        cp.ClaheBins = ParseInt(ClaheBinsBox.Text, cp.ClaheBins);
        cp.ContrastStretchBlack = ParseFloat(CSBlackBox.Text, cp.ContrastStretchBlack);
        cp.ContrastStretchWhite = ParseFloat(CSWhiteBox.Text, cp.ContrastStretchWhite);
        cp.LevelBlack = ParseInt(LevelBlackBox.Text, cp.LevelBlack);
        cp.LevelWhite = ParseInt(LevelWhiteBox.Text, cp.LevelWhite);

        // Diagnostics
        _settings.DeveloperMode = DevModeCheck.IsChecked == true;
        _settings.EnableLogging = LoggingCheck.IsChecked == true;
        AppLog.Enabled = _settings.EnableLogging;

        _settingsManager.Save(_settings);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Bottom bar
    // ══════════════════════════════════════════════════════════════════════

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        SettingsChanged = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Email template management
    // ══════════════════════════════════════════════════════════════════════

    private List<EmailTemplate> _editableTemplates = new();
    private bool _suppressTemplateSync;

    private void PopulateTemplateList()
    {
        _suppressTemplateSync = true;
        _editableTemplates = _settings.EmailTemplates
            .Select(t => new EmailTemplate { Name = t.Name, Subject = t.Subject, Body = t.Body })
            .ToList();
        TemplateListBox.ItemsSource = _editableTemplates;
        if (_editableTemplates.Count > 0)
            TemplateListBox.SelectedIndex = 0;
        _suppressTemplateSync = false;
    }

    private void TemplateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _suppressTemplateSync = true;
        var tmpl = TemplateListBox.SelectedItem as EmailTemplate;
        if (tmpl != null)
        {
            TemplateNameBox.Text = tmpl.Name;
            TemplateSubjectBox.Text = tmpl.Subject;
            TemplateBodyBox.Text = tmpl.Body;
            TemplateNameBox.IsEnabled = true;
            TemplateSubjectBox.IsEnabled = true;
            TemplateBodyBox.IsEnabled = true;
        }
        else
        {
            TemplateNameBox.Text = "";
            TemplateSubjectBox.Text = "";
            TemplateBodyBox.Text = "";
            TemplateNameBox.IsEnabled = false;
            TemplateSubjectBox.IsEnabled = false;
            TemplateBodyBox.IsEnabled = false;
        }
        _suppressTemplateSync = false;
    }

    private void TemplateField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressTemplateSync) return;
        SaveCurrentTemplate();
    }

    private void SaveCurrentTemplate()
    {
        if (TemplateListBox.SelectedItem is EmailTemplate tmpl)
        {
            var oldName = tmpl.Name;
            tmpl.Name = TemplateNameBox.Text.Trim();
            tmpl.Subject = TemplateSubjectBox.Text;
            tmpl.Body = TemplateBodyBox.Text;
            if (oldName != tmpl.Name)
            {
                var idx = TemplateListBox.SelectedIndex;
                TemplateListBox.ItemsSource = null;
                TemplateListBox.ItemsSource = _editableTemplates;
                TemplateListBox.SelectedIndex = idx;
            }
        }
    }

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        var tmpl = new EmailTemplate
        {
            Name = $"Template {_editableTemplates.Count + 1}",
            Subject = "Your photos are ready!",
            Body = "Hi {CustomerName},\n\nYour photo order is ready.\n\nThank you!\nHite Photo"
        };
        _editableTemplates.Add(tmpl);
        TemplateListBox.ItemsSource = null;
        TemplateListBox.ItemsSource = _editableTemplates;
        TemplateListBox.SelectedItem = tmpl;
    }

    private void RemoveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateListBox.SelectedItem is not EmailTemplate tmpl) return;
        if (_editableTemplates.Count <= 1)
        {
            MessageBox.Show("You must keep at least one template.", "Cannot Remove", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _editableTemplates.Remove(tmpl);
        TemplateListBox.ItemsSource = null;
        TemplateListBox.ItemsSource = _editableTemplates;
        if (_editableTemplates.Count > 0)
            TemplateListBox.SelectedIndex = 0;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Routing rules (reads from RoutingMap in settings)
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshRoutingGrid()
    {
        if (_settings.RoutingMap == null) return;

        _routingRows = _settings.RoutingMap.Select(kvp =>
        {
            var entry = kvp.Value;
            return new RoutingRuleRow
            {
                RoutingKey = kvp.Key,
                ChannelNumber = entry?.ChannelNumber ?? 0,
                ChannelName = entry?.LayoutName ?? "",
                Source = entry?.Source ?? ""
            };
        }).OrderBy(r => r.Source).ThenBy(r => r.RoutingKey).ToList();

        ApplyRoutingFilter();
    }

    private void ApplyRoutingFilter()
    {
        var q = RoutingSearchBox?.Text?.Trim() ?? "";
        string sourceFilter = (RoutingSourceFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        IEnumerable<RoutingRuleRow> filtered = _routingRows;

        if (sourceFilter == "Unmapped")
            filtered = filtered.Where(r => r.ChannelNumber == 0);
        else if (sourceFilter != "All")
            filtered = filtered.Where(r => r.Source.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(q))
            filtered = filtered.Where(r =>
                r.RoutingKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.ChannelName.Contains(q, StringComparison.OrdinalIgnoreCase));

        RoutingGrid.ItemsSource = filtered.ToList();
    }

    private void RoutingSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyRoutingFilter();
    private void RoutingSourceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RoutingGrid == null) return;
        ApplyRoutingFilter();
    }

    private void PopulateRoutingChannelCombo()
    {
        // TODO: populate with channels from ch_data.csv when loaded
        RoutingChannelCombo.ItemsSource = new List<ChannelDisplayItem>();
    }

    private void AssignRoutingChannel_Click(object sender, RoutedEventArgs e)
    {
        // TODO: wire when channel list is loaded
        MessageBox.Show("Channel assignment not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveRoutingRule_Click(object sender, RoutedEventArgs e)
    {
        if (RoutingGrid.SelectedItem is not RoutingRuleRow row) return;
        if (MessageBox.Show($"Remove mapping for:\n\n{row.RoutingKey}?",
                "Remove Routing Rule", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _settings.RoutingMap.Remove(row.RoutingKey);
        _settingsManager.Save(_settings);
        RefreshRoutingGrid();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Layouts (read-only for now)
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshLayoutGrid()
    {
        LayoutGrid.ItemsSource = (_settings.Layouts ?? new())
            .Select(l => new LayoutGridRow(l))
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Controls tab — slot layout
    // ══════════════════════════════════════════════════════════════════════

    private void PopulateSlotLayoutTab()
    {
        var options = new List<string> { "(Empty)" };
        options.AddRange(ControlRegistry.All.Select(c => c.Id));

        var layout = _settings.CorrectionSlotLayout ?? ControlRegistry.DefaultSlotLayout();

        for (int i = 0; i < 23; i++)
        {
            var combo = new ComboBox
            {
                ItemsSource = options,
                Margin = new Thickness(2),
                MinWidth = 70,
                FontSize = 11
            };

            string slotId = i < layout.Count ? layout[i] : "";
            combo.SelectedItem = string.IsNullOrEmpty(slotId) ? "(Empty)" : slotId;
            _slotCombos[i] = combo;

            if (i < 7) TopRowGrid.Children.Add(combo);
            else if (i < 14) BottomRow1Grid.Children.Add(combo);
            else BottomRow2Grid.Children.Add(combo);
        }
    }

    private List<string> ReadSlotLayoutFromUi()
    {
        var layout = new List<string>(23);
        for (int i = 0; i < 23; i++)
        {
            var selected = _slotCombos[i].SelectedItem?.ToString() ?? "(Empty)";
            layout.Add(selected == "(Empty)" ? "" : selected);
        }
        return layout;
    }

    private void ResetSlotLayout_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ControlRegistry.DefaultSlotLayout();
        for (int i = 0; i < 23; i++)
        {
            string id = i < defaults.Count ? defaults[i] : "";
            _slotCombos[i].SelectedItem = string.IsNullOrEmpty(id) ? "(Empty)" : id;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Correction settings profiles
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshProfileCombo()
    {
        ProfileCombo.Items.Clear();
        foreach (var p in _settings.CorrectionProfiles)
            ProfileCombo.Items.Add(p.Name);
        if (ProfileCombo.Items.Count > 0)
            ProfileCombo.SelectedIndex = 0;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForText("Save Profile", "Enter a name for this settings profile:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var s = new CorrectionStrengths();
        s.Brightness = ParseFloat(StrBrightnessBox.Text, 1f); s.Contrast = ParseFloat(StrContrastBox.Text, 1f);
        s.Shadows = ParseFloat(StrShadowsBox.Text, 1f); s.Highlights = ParseFloat(StrHighlightsBox.Text, 1f);
        s.Saturation = ParseFloat(StrSaturationBox.Text, 1f); s.ColorTemp = ParseFloat(StrColorTempBox.Text, 1f);
        s.Red = ParseFloat(StrRedBox.Text, 1f); s.Green = ParseFloat(StrGreenBox.Text, 1f); s.Blue = ParseFloat(StrBlueBox.Text, 1f);
        s.SigmoidalContrast = ParseFloat(StrSigmoidalBox.Text, 1f); s.Clahe = ParseFloat(StrClaheBox.Text, 1f);
        s.ContrastStretch = ParseFloat(StrContrastStretchBox.Text, 1f); s.Levels = ParseFloat(StrLevelsBox.Text, 1f);

        var r = new ExposureRatios();
        r.Brightness = ParseFloat(ExpRatioBrightnessBox.Text, 1f); r.Contrast = ParseFloat(ExpRatioContrastBox.Text, 0.4f);
        r.Shadows = ParseFloat(ExpRatioShadowsBox.Text, 0.6f); r.Highlights = ParseFloat(ExpRatioHighlightsBox.Text, -0.3f);

        var p = new ControlParameters();
        p.WarmthRed = ParseFloat(WarmthRedBox.Text, 1f); p.WarmthBlue = ParseFloat(WarmthBlueBox.Text, 1f);
        p.SigmoidalMidpoint = ParseFloat(SigmoidalMidpointBox.Text, 50f);
        p.ClaheXTiles = ParseInt(ClaheXTilesBox.Text, 8); p.ClaheYTiles = ParseInt(ClaheYTilesBox.Text, 8);
        p.ClaheBins = ParseInt(ClaheBinsBox.Text, 128);
        p.ContrastStretchBlack = ParseFloat(CSBlackBox.Text, 0.5f); p.ContrastStretchWhite = ParseFloat(CSWhiteBox.Text, 0.5f);
        p.LevelBlack = ParseInt(LevelBlackBox.Text, 0); p.LevelWhite = ParseInt(LevelWhiteBox.Text, 100);

        var profile = CorrectionSettingsProfile.FromSettings(name.Trim(), s, r, p);
        _settings.CorrectionProfiles.RemoveAll(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        _settings.CorrectionProfiles.Add(profile);
        _settingsManager.Save(_settings);
        RefreshProfileCombo();
        ProfileCombo.SelectedItem = name.Trim();
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not string name) return;
        var profile = _settings.CorrectionProfiles.Find(pr => pr.Name == name);
        if (profile == null) return;

        var s = profile.Strengths;
        StrBrightnessBox.Text = s.Brightness.ToString("F2"); StrContrastBox.Text = s.Contrast.ToString("F2");
        StrShadowsBox.Text = s.Shadows.ToString("F2"); StrHighlightsBox.Text = s.Highlights.ToString("F2");
        StrSaturationBox.Text = s.Saturation.ToString("F2"); StrColorTempBox.Text = s.ColorTemp.ToString("F2");
        StrRedBox.Text = s.Red.ToString("F2"); StrGreenBox.Text = s.Green.ToString("F2"); StrBlueBox.Text = s.Blue.ToString("F2");
        StrSigmoidalBox.Text = s.SigmoidalContrast.ToString("F2"); StrClaheBox.Text = s.Clahe.ToString("F2");
        StrContrastStretchBox.Text = s.ContrastStretch.ToString("F2"); StrLevelsBox.Text = s.Levels.ToString("F2");

        var r2 = profile.ExposureRatios;
        ExpRatioBrightnessBox.Text = r2.Brightness.ToString("F2"); ExpRatioContrastBox.Text = r2.Contrast.ToString("F2");
        ExpRatioShadowsBox.Text = r2.Shadows.ToString("F2"); ExpRatioHighlightsBox.Text = r2.Highlights.ToString("F2");

        var p2 = profile.Parameters;
        WarmthRedBox.Text = p2.WarmthRed.ToString("F2"); WarmthBlueBox.Text = p2.WarmthBlue.ToString("F2");
        SigmoidalMidpointBox.Text = p2.SigmoidalMidpoint.ToString("F1");
        ClaheXTilesBox.Text = p2.ClaheXTiles.ToString(); ClaheYTilesBox.Text = p2.ClaheYTiles.ToString();
        ClaheBinsBox.Text = p2.ClaheBins.ToString();
        CSBlackBox.Text = p2.ContrastStretchBlack.ToString("F2"); CSWhiteBox.Text = p2.ContrastStretchWhite.ToString("F2");
        LevelBlackBox.Text = p2.LevelBlack.ToString(); LevelWhiteBox.Text = p2.LevelWhite.ToString();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not string name) return;
        if (MessageBox.Show($"Delete profile '{name}'?", "Delete Profile",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _settings.CorrectionProfiles.RemoveAll(pr => pr.Name == name);
        _settingsManager.Save(_settings);
        RefreshProfileCombo();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Diagnostics
    // ══════════════════════════════════════════════════════════════════════

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HitePhoto.PrintStation", "printstation.log");
        if (File.Exists(logPath))
            Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
        else
            MessageBox.Show("No log file yet.", "Log File", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HitePhoto.PrintStation", "printstation.log");
        try
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(logPath + ".1")) File.Delete(logPath + ".1");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not clear log: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Seed from disk
    // ══════════════════════════════════════════════════════════════════════

    private void SeedFromDisk_Click(object sender, RoutedEventArgs e)
    {
        var repo = App.Services.GetRequiredService<IOrderRepository>();
        int pixfizzCount = 0, dakisCount = 0, skipped = 0, errors = 0;

        SeedBtn.IsEnabled = false;
        SeedResult.Text = "Scanning...";
        SeedResult.Foreground = FindBrush("TextMuted");

        try
        {
            // Seed Pixfizz orders from darkroom_ticket.txt files
            var pixfizzRoot = _settings.OrderOutputPath;
            if (!string.IsNullOrWhiteSpace(pixfizzRoot) && Directory.Exists(pixfizzRoot))
            {
                foreach (var dir in Directory.GetDirectories(pixfizzRoot))
                {
                    var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
                    if (!File.Exists(txtPath)) continue;

                    try
                    {
                        var lines = File.ReadAllLines(txtPath);
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var line in lines)
                        {
                            var eq = line.IndexOf('=');
                            if (eq > 0)
                                dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                        }

                        var orderId = dict.GetValueOrDefault("ExtOrderNum") ?? dict.GetValueOrDefault("Orderid") ?? Path.GetFileName(dir);

                        // Check if already exists
                        if (repo.FindOrderId(orderId, _settings.StoreId) != null)
                        {
                            skipped++;
                            continue;
                        }

                        var order = new UnifiedOrder
                        {
                            ExternalOrderId = orderId,
                            ExternalSource = "pixfizz",
                            CustomerFirstName = dict.GetValueOrDefault("OrderFirstName"),
                            CustomerLastName = dict.GetValueOrDefault("OrderLastName"),
                            CustomerEmail = dict.GetValueOrDefault("OrderEmail"),
                            OrderedAt = DateTime.TryParse(dict.GetValueOrDefault("OrderDateTime"), out var dt) ? dt : DateTime.Now,
                            Notes = dict.GetValueOrDefault("Ordernotes"),
                            FolderPath = dir,
                            Location = dict.GetValueOrDefault("Location"),
                            DownloadStatus = "complete",
                            Items = new List<UnifiedOrderItem>
                            {
                                new UnifiedOrderItem
                                {
                                    SizeLabel = dict.GetValueOrDefault("Size") ?? "unknown",
                                    MediaType = dict.GetValueOrDefault("Media") ?? dict.GetValueOrDefault("Finish Options") ?? "",
                                    Quantity = int.TryParse(dict.GetValueOrDefault("Qty"), out var q) ? q : 1,
                                    ImageFilepath = dict.GetValueOrDefault("Filepath") ?? "",
                                    ImageFilename = Path.GetFileName(dict.GetValueOrDefault("Filepath") ?? ""),
                                    IsNoritsu = true
                                }
                            }
                        };

                        repo.InsertOrder(order, _settings.StoreId);
                        pixfizzCount++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Info($"Seed error for {dir}: {ex.Message}");
                        errors++;
                    }
                }
            }

            // Seed Dakis orders from ingest_order.json files
            var dakisRoot = _settings.DakisWatchFolder;
            if (!string.IsNullOrWhiteSpace(dakisRoot) && Directory.Exists(dakisRoot))
            {
                foreach (var dir in Directory.GetDirectories(dakisRoot))
                {
                    var jsonPath = Path.Combine(dir, "ingest_order.json");
                    if (!File.Exists(jsonPath)) continue;

                    try
                    {
                        var json = File.ReadAllText(jsonPath);
                        var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        var orderId = root.GetProperty("external_order_id").GetString() ?? Path.GetFileName(dir);

                        if (repo.FindOrderId(orderId, _settings.StoreId) != null)
                        {
                            skipped++;
                            continue;
                        }

                        var customerName = root.GetProperty("customer_name").GetString() ?? "";
                        var nameParts = customerName.Split(' ', 2);

                        var items = new List<UnifiedOrderItem>();
                        if (root.TryGetProperty("items", out var itemsEl))
                        {
                            foreach (var item in itemsEl.EnumerateArray())
                            {
                                items.Add(new UnifiedOrderItem
                                {
                                    ExternalLineId = item.TryGetProperty("external_line_id", out var lid) ? lid.GetString() : null,
                                    SizeLabel = item.TryGetProperty("size_label", out var sl) ? sl.GetString() : "unknown",
                                    MediaType = item.TryGetProperty("media_type", out var mt) ? mt.GetString() : "",
                                    Quantity = item.TryGetProperty("quantity", out var qty) ? qty.GetInt32() : 1,
                                    ImageFilename = item.TryGetProperty("image_filename", out var ifn) ? ifn.GetString() : "",
                                    ImageFilepath = item.TryGetProperty("image_filepath", out var ifp) && ifp.ValueKind != JsonValueKind.Null ? ifp.GetString() : "",
                                    IsNoritsu = true
                                });
                            }
                        }

                        var order = new UnifiedOrder
                        {
                            ExternalOrderId = orderId,
                            ExternalSource = "dakis",
                            CustomerFirstName = nameParts.Length > 0 ? nameParts[0] : "",
                            CustomerLastName = nameParts.Length > 1 ? nameParts[1] : "",
                            CustomerEmail = root.TryGetProperty("customer_email", out var ce) ? ce.GetString() : "",
                            CustomerPhone = root.TryGetProperty("customer_phone", out var cp2) ? cp2.GetString() : "",
                            OrderTotal = root.TryGetProperty("order_total", out var ot) ? ot.GetDecimal() : 0,
                            Paid = root.TryGetProperty("paid", out var pd) && pd.GetBoolean(),
                            Notes = root.TryGetProperty("notes", out var nt) ? nt.GetString() : "",
                            FolderPath = root.TryGetProperty("folder_path", out var fp) ? fp.GetString() : dir,
                            DownloadStatus = "complete",
                            Items = items
                        };

                        repo.InsertOrder(order, _settings.StoreId);
                        dakisCount++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Info($"Seed error for {dir}: {ex.Message}");
                        errors++;
                    }
                }
            }

            var msg = $"Seeded {pixfizzCount} Pixfizz + {dakisCount} Dakis orders";
            if (skipped > 0) msg += $", {skipped} already existed";
            if (errors > 0) msg += $", {errors} errors";
            SeedResult.Text = msg;
            SeedResult.Foreground = FindBrush("AccentGreen");
        }
        catch (Exception ex)
        {
            SeedResult.Text = $"Error: {ex.Message}";
            SeedResult.Foreground = FindBrush("AccentRed");
        }
        finally
        {
            SeedBtn.IsEnabled = true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Browse helpers
    // ══════════════════════════════════════════════════════════════════════

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var tag = btn.Tag as string ?? "";
        var initial = tag == "DakisWatch" ? DakisWatchBox.Text
                    : tag == "NoritsuOutput" ? NoritsuOutputBox.Text
                    : tag == "OrderOutput" ? OrderOutputBox.Text
                    : tag == "UpdateLocal" ? UpdateLocalBox.Text : "";
        var path = BrowseForFolder(initial);
        if (path == null) return;
        if (tag == "DakisWatch") DakisWatchBox.Text = path;
        else if (tag == "NoritsuOutput") NoritsuOutputBox.Text = path;
        else if (tag == "OrderOutput") OrderOutputBox.Text = path;
        else if (tag == "UpdateLocal") UpdateLocalBox.Text = path;
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag) return;
        if (tag == "ChannelsCsv")
        {
            var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
            if (!string.IsNullOrWhiteSpace(ChannelsCsvBox.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(ChannelsCsvBox.Text);
            if (dlg.ShowDialog() == true) ChannelsCsvBox.Text = dlg.FileName;
        }
    }

    private static string? BrowseForFolder(string? initial)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select folder",
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "Select this folder",
            ValidateNames = false
        };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? Path.GetDirectoryName(dlg.FileName) : null;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════

    private static float ParseFloat(string text, float fallback) =>
        float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out int v) ? v : fallback;

    private string? PromptForText(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title, Width = 350, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize,
            Background = FindBrush("WindowBg")
        };
        var sp2 = new StackPanel { Margin = new Thickness(12) };
        var lbl = new TextBlock { Text = prompt, Foreground = FindBrush("TextPrimary"), Margin = new Thickness(0, 0, 0, 8) };
        var tb = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 70 };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        tb.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { result = tb.Text; dlg.Close(); } };
        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);
        sp2.Children.Add(lbl);
        sp2.Children.Add(tb);
        sp2.Children.Add(btnPanel);
        dlg.Content = sp2;
        dlg.ShowDialog();
        return result;
    }

    private SolidColorBrush FindBrush(string key)
    {
        try { return (SolidColorBrush)FindResource(key); }
        catch { return Brushes.Gray; }
    }
}

// ══════════════════════════════════════════════════════════════════════
//  View models for grids
// ══════════════════════════════════════════════════════════════════════

public class RoutingRuleRow
{
    public string RoutingKey { get; set; } = "";
    public int ChannelNumber { get; set; }
    public string ChannelName { get; set; } = "";
    public string Source { get; set; } = "";
}

public class ChannelDisplayItem
{
    public string DisplayName { get; set; } = "";
    public int ChannelNumber { get; set; }
}

public class LayoutGridRow
{
    public string Name { get; set; } = "";
    public string PrintSizeDisplay { get; set; } = "";
    public string GridDisplay { get; set; } = "";
    public int TargetChannelNumber { get; set; }

    public LayoutGridRow() { }
    public LayoutGridRow(LayoutDefinition l)
    {
        Name = l.Name;
        PrintSizeDisplay = l.PrintSizeDisplay;
        GridDisplay = $"{l.Columns}x{l.Rows}";
        TargetChannelNumber = l.TargetChannelNumber;
    }
}
