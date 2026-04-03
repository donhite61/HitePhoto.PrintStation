using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class InvoicePrinter
{
    // 5.5" x 8.5" half sheet at 96 DPI
    private const double PageW = 528;
    private const double PageH = 816;

    private readonly IOrderRepository _orders;

    public InvoicePrinter(IOrderRepository orders)
    {
        _orders = orders;
    }

    public void PrintTransferInvoice(string orderId, string fromStoreName, string comment)
    {
        var doc = BuildTransferInvoice(orderId, fromStoreName, comment);
        if (doc == null) return;

        try
        {
            var printQueue = LocalPrintServer.GetDefaultPrintQueue();
            if (printQueue == null)
            {
                AlertCollector.Error(AlertCategory.Printing,
                    "No default printer found — invoice not printed",
                    orderId: orderId,
                    detail: "Attempted: print transfer invoice. Expected: default printer. Found: null.");
                return;
            }

            var writer = PrintQueue.CreateXpsDocumentWriter(printQueue);
            var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
            paginator.PageSize = new Size(PageW, PageH);
            writer.Write(paginator);

            AppLog.Info($"Transfer invoice printed for order {orderId}");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Printing,
                $"Failed to print transfer invoice for order {orderId}",
                orderId: orderId,
                detail: $"Attempted: print transfer invoice. Expected: success. Found: {ex.Message}. " +
                        $"Context: printer={LocalPrintServer.GetDefaultPrintQueue()?.Name ?? "unknown"}. " +
                        $"State: {ex.GetType().Name}");
        }
    }

    public Window PreviewTransferInvoice(string orderId, string fromStoreName, string comment)
    {
        var doc = BuildTransferInvoice(orderId, fromStoreName, comment);
        if (doc == null) return null!;

        var viewer = new FlowDocumentReader
        {
            Document = doc,
            ViewingMode = FlowDocumentReaderViewingMode.Page,
            IsFindEnabled = false,
            IsTwoPageViewEnabled = false
        };

        return new Window
        {
            Title = $"Invoice Preview — Order {orderId}",
            Width = 580,
            Height = 860,
            Content = viewer,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }

    private FlowDocument? BuildTransferInvoice(string orderId, string fromStoreName, string comment)
    {
        var order = _orders.GetFullOrder(orderId);
        if (order == null) return null;

        var items = _orders.GetItems(orderId);

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(36, 28, 36, 28),
            PageWidth = PageW,
            PageHeight = PageH,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            ColumnWidth = 999
        };

        // Customer name
        var name = $"{order.CustomerFirstName} {order.CustomerLastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            doc.Blocks.Add(new Paragraph(new Bold(new Run(name)))
                { FontSize = 26, Margin = new Thickness(0, 0, 0, 2) });

        // Phone
        if (!string.IsNullOrWhiteSpace(order.CustomerPhone))
            doc.Blocks.Add(new Paragraph(new Bold(new Run(order.CustomerPhone)))
                { FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });

        // Email
        if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
            doc.Blocks.Add(new Paragraph(new Run(order.CustomerEmail))
                { Margin = new Thickness(0, 0, 0, 12) });

        // Header
        doc.Blocks.Add(new Paragraph(new Bold(new Run("TRANSFER ORDER INVOICE")))
            { FontSize = 16, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"From {fromStoreName}  —  {DateTime.Now:MMM d, yyyy  h:mm tt}"))
            { FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 10) });

        // Pickup location
        if (!string.IsNullOrWhiteSpace(order.StoreName))
            doc.Blocks.Add(new Paragraph(new Bold(new Run($"Pickup: {order.StoreName}")))
                { FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });

        // Order info
        doc.Blocks.Add(new Paragraph(new Run($"Order #: {order.ExternalOrderId}"))
            { Margin = new Thickness(0, 0, 0, 1) });
        if (order.OrderedAt.HasValue)
            doc.Blocks.Add(new Paragraph(new Run($"Date: {order.OrderedAt.Value:MMM d, yyyy  h:mm tt}"))
                { Margin = new Thickness(0, 0, 0, 8) });

        // Separator
        doc.Blocks.Add(new Paragraph(new Run("─────────────────────────────────────────"))
            { Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4) });

        // Items
        doc.Blocks.Add(new Paragraph(new Bold(new Run("ITEMS")))
            { FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });

        int totalPrints = 0;
        foreach (var item in items)
        {
            totalPrints += item.Quantity;
            doc.Blocks.Add(new Paragraph(new Run($"  {item.SizeLabel,-24}  {item.Quantity}x  {item.ImageFilename}"))
                { FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 1, 0, 1) });
        }

        // Total
        doc.Blocks.Add(new Paragraph(new Run("─────────────────────────────────────────"))
            { Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4) });
        doc.Blocks.Add(new Paragraph(new Bold(new Run($"Total: {totalPrints} prints  ({items.Count} items)")))
            { FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });

        // Comment/notes
        if (!string.IsNullOrWhiteSpace(comment))
        {
            doc.Blocks.Add(new Paragraph(new Bold(new Run("Notes:")))
                { FontSize = 11, Margin = new Thickness(0, 4, 0, 2) });
            doc.Blocks.Add(new Paragraph(new Run(comment))
                { FontStyle = FontStyles.Italic });
        }

        return doc;
    }
}
