using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly PrintStationDb? _db;

    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings settings, SettingsManager settingsManager, PrintStationDb? db)
    {
        InitializeComponent();

        _settingsManager = settingsManager;
        _db = db;
        Settings = settings;

        // Populate fields from current settings
        DbHostBox.Text = settings.DbHost;
        DbPortBox.Text = settings.DbPort.ToString();
        DbNameBox.Text = settings.DbName;
        DbUserBox.Text = settings.DbUser;
        DbPasswordBox.Password = settings.DbPassword;

        NoritsuOutputBox.Text = settings.NoritsuOutputRoot;
        ChannelsCsvBox.Text = settings.ChannelsCsvPath;

        RefreshIntervalBox.Text = settings.RefreshIntervalSeconds.ToString();
        DevModeCheck.IsChecked = settings.DeveloperMode;
        LoggingCheck.IsChecked = settings.EnableLogging;

        // Theme combo
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if (item.Content?.ToString() == settings.Theme)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }

        // Load stores for combo
        LoadStoresAsync();
    }

    private async void LoadStoresAsync()
    {
        // Try to load stores from DB for the combo
        var testDb = new PrintStationDb(BuildConnectionString());
        var connError = await testDb.TestConnectionAsync();

        if (connError == null)
        {
            var stores = await testDb.GetStoresAsync();
            StoreCombo.Items.Clear();
            foreach (var store in stores)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{store.StoreName} (ID: {store.Id})",
                    Tag = store.Id
                };
                StoreCombo.Items.Add(item);

                if (store.Id == Settings.StoreId)
                    StoreCombo.SelectedItem = item;
            }
        }
        else
        {
            // Can't connect — just show current store ID
            StoreCombo.Items.Clear();
            StoreCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Store ID: {Settings.StoreId} (DB offline)",
                Tag = Settings.StoreId,
                IsSelected = true
            });
        }
    }

    private string BuildConnectionString()
    {
        var host = DbHostBox.Text.Trim();
        var port = int.TryParse(DbPortBox.Text.Trim(), out var p) ? p : 3306;
        var db = DbNameBox.Text.Trim();
        var user = DbUserBox.Text.Trim();
        var pass = DbPasswordBox.Password;

        return $"Server={host};Port={port};Database={db};User={user};Password={pass};" +
               "SslMode=None;AllowPublicKeyRetrieval=true;ConnectionTimeout=5";
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionTestResult.Text = "Testing...";
        ConnectionTestResult.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");

        var testDb = new PrintStationDb(BuildConnectionString());
        var error = await testDb.TestConnectionAsync();

        if (error == null)
        {
            ConnectionTestResult.Text = "Connected successfully!";
            ConnectionTestResult.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");

            // Refresh store combo with live data
            LoadStoresAsync();
        }
        else
        {
            ConnectionTestResult.Text = $"Failed: {error}";
            ConnectionTestResult.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate port
        if (!int.TryParse(DbPortBox.Text.Trim(), out var port))
        {
            MessageBox.Show("Port must be a number.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(RefreshIntervalBox.Text.Trim(), out var refreshInterval) || refreshInterval < 5)
        {
            MessageBox.Show("Refresh interval must be a number >= 5 seconds.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Build updated settings
        Settings = new AppSettings
        {
            DbHost = DbHostBox.Text.Trim(),
            DbPort = port,
            DbName = DbNameBox.Text.Trim(),
            DbUser = DbUserBox.Text.Trim(),
            DbPassword = DbPasswordBox.Password,
            NoritsuOutputRoot = NoritsuOutputBox.Text.Trim(),
            ChannelsCsvPath = ChannelsCsvBox.Text.Trim(),
            RefreshIntervalSeconds = refreshInterval,
            DeveloperMode = DevModeCheck.IsChecked == true,
            EnableLogging = LoggingCheck.IsChecked == true,
            Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dark"
        };

        // Store ID from combo
        if (StoreCombo.SelectedItem is ComboBoxItem storeItem && storeItem.Tag is int storeId)
        {
            Settings.StoreId = storeId;
        }

        _settingsManager.Save(Settings);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
