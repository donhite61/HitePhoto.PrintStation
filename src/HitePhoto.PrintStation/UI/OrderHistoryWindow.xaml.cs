using System.Collections.Generic;
using System.Windows;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class OrderHistoryWindow : Window
{
    public OrderHistoryWindow(string orderLabel, List<HistoryEntry> notes)
    {
        InitializeComponent();
        HeaderText.Text = $"History — {orderLabel}";
        HistoryListView.ItemsSource = notes;
    }
}
