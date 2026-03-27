using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class AlertHistoryWindow : Window
{
    private readonly IAlertRepository _alertRepo;

    public AlertHistoryWindow(IAlertRepository alertRepo)
    {
        InitializeComponent();
        _alertRepo = alertRepo;
        Loaded += (_, _) => LoadAlerts();
    }

    private void LoadAlerts()
    {
        int days = 7;
        if (DaysFilter.SelectedItem is ComboBoxItem daysItem)
            int.TryParse(daysItem.Content?.ToString(), out days);

        var alerts = _alertRepo.GetRecent(days);

        // Apply severity filter
        if (SeverityFilter.SelectedItem is ComboBoxItem sevItem
            && sevItem.Content?.ToString() != "All")
        {
            var sev = sevItem.Content.ToString();
            alerts = alerts.Where(a => a.Severity == sev).ToList();
        }

        // Apply category filter
        if (CategoryFilter.SelectedItem is ComboBoxItem catItem
            && catItem.Content?.ToString() != "All")
        {
            var cat = catItem.Content.ToString();
            alerts = alerts.Where(a => a.Category == cat).ToList();
        }

        AlertListView.ItemsSource = alerts;
        StatusText.Text = $"{alerts.Count} alert(s) in the last {days} day(s)";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LoadAlerts();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadAlerts();
    }

    private void Purge_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete alerts older than 30 days?",
            "Purge Old Alerts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _alertRepo.PurgeOlderThan(30);
            LoadAlerts();
        }
    }
}
