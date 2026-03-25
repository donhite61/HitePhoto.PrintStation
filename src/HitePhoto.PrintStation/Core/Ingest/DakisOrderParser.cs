using System.Globalization;
using System.IO;
using YamlDotNet.Serialization;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses Dakis order.yml + folder structure into UnifiedOrder format.
/// Uses YamlDotNet for proper YAML deserialization.
/// All customer info comes from order.yml — no customer.txt needed.
/// </summary>
public class DakisOrderParser
{
    private static readonly IDeserializer s_yaml = new DeserializerBuilder().Build();

    public UnifiedOrder Parse(RawOrder raw)
    {
        var ymlLines = raw.RawData.Split('\n');
        var info = ReadOrderInfoFromLines(ymlLines, raw.ExternalOrderId);

        var customerFirstName = info.CustFirst;
        var customerLastName = info.CustLast;
        var folderPath = raw.Metadata?.GetValueOrDefault("folder_path");

        var items = new List<UnifiedOrderItem>();

        // Invoice-only: another store produces this order, skip image scanning
        if (info.IsInvoiceOnly)
        {
            return new UnifiedOrder
            {
                ExternalOrderId = raw.ExternalOrderId,
                ExternalSource = "dakis",
                OrderedAt = info.OrderedAt,
                CustomerFirstName = customerFirstName,
                CustomerLastName = customerLastName,
                CustomerEmail = info.Email,
                CustomerPhone = info.Phone,
                OrderTotal = info.ChargedPrice > 0 ? info.ChargedPrice : null,
                Paid = info.BeenPaid,
                Notes = info.Comment,
                FolderPath = folderPath,
                Location = info.StoreName,
                OrderType = info.OrderType,
                FulfillmentType = info.FulfillmentType,
                IsInvoiceOnly = true,
                BillingStoreId = info.BillingStoreId,
                CurrentStoreId = info.CurrentStoreId,
                Items = items
            };
        }

        // ── Build items from YML shopping cart (this store's counts) ──
        foreach (var kvp in info.ExpectedCounts)
        {
            info.OutlabCounts.TryGetValue(kvp.Key, out int outlabQty);
            items.Add(new UnifiedOrderItem
            {
                ExternalLineId = $"{raw.ExternalOrderId}_{kvp.Key}",
                SizeLabel = kvp.Key,
                FormatString = kvp.Key,
                ExpectedPrintCount = kvp.Value,
                OutlabCount = outlabQty,
                Quantity = kvp.Value
            });
        }

        // ── Add outlab-only sizes (fulfilled at other store, no images expected) ──
        foreach (var kvp in info.OutlabCounts)
        {
            if (!info.ExpectedCounts.ContainsKey(kvp.Key))
            {
                items.Add(new UnifiedOrderItem
                {
                    ExternalLineId = $"{raw.ExternalOrderId}_{kvp.Key}_outlab",
                    SizeLabel = kvp.Key,
                    FormatString = kvp.Key,
                    ExpectedPrintCount = 0,
                    OutlabCount = kvp.Value,
                    FulfillmentStore = "Other store",
                    Quantity = 0
                });
            }
        }

        // ── Scan prints/ subfolder for actual images ──
        if (!string.IsNullOrEmpty(folderPath))
        {
            var printsRoot = Path.Combine(folderPath, "prints");
            if (Directory.Exists(printsRoot))
            {
                foreach (var productDir in Directory.GetDirectories(printsRoot))
                {
                    string formatString = StripFormatSuffix(Path.GetFileName(productDir));

                    var existing = items.FirstOrDefault(i =>
                        i.SizeLabel != null &&
                        (i.SizeLabel.Equals(formatString, StringComparison.OrdinalIgnoreCase) ||
                         i.SizeLabel.Replace(".", "").Equals(formatString.Replace(".", ""), StringComparison.OrdinalIgnoreCase)));

                    if (existing == null)
                    {
                        foreach (var img in ScanImages(productDir))
                            items.Add(img with
                            {
                                ExternalLineId = $"{raw.ExternalOrderId}_{formatString}_{Path.GetFileName(img.ImageFilepath ?? "")}",
                                SizeLabel = formatString,
                                FormatString = formatString
                            });
                    }
                    else
                    {
                        var idx = items.IndexOf(existing);
                        var images = ScanImages(productDir);
                        if (images.Count > 0)
                        {
                            items[idx] = images[0] with
                            {
                                ExternalLineId = existing.ExternalLineId,
                                SizeLabel = existing.SizeLabel,
                                FormatString = existing.FormatString,
                                ExpectedPrintCount = existing.ExpectedPrintCount,
                                OutlabCount = existing.OutlabCount
                            };
                            for (int i = 1; i < images.Count; i++)
                            {
                                items.Add(images[i] with
                                {
                                    ExternalLineId = $"{raw.ExternalOrderId}_{existing.SizeLabel}_{i}",
                                    SizeLabel = existing.SizeLabel,
                                    FormatString = existing.FormatString
                                });
                            }
                        }
                    }
                }
            }

            // ── Scan photo_products/ for non-Noritsu items ──
            var giftRoot = Path.Combine(folderPath, "photo_products");
            if (Directory.Exists(giftRoot))
            {
                foreach (var productDir in Directory.GetDirectories(giftRoot))
                {
                    var label = StripFormatSuffix(Path.GetFileName(productDir));
                    foreach (var img in ScanImages(productDir))
                    {
                        items.Add(img with
                        {
                            ExternalLineId = $"{raw.ExternalOrderId}_product_{label}_{Path.GetFileName(img.ImageFilepath ?? "")}",
                            SizeLabel = label,
                            FormatString = label,
                            IsNoritsu = false
                        });
                    }
                }
            }
        }

        // ── Validation ──
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(raw.ExternalOrderId))
            errors.Add("ExternalOrderId is empty");
        if (string.IsNullOrWhiteSpace(info.BillingStoreId))
            errors.Add("BillingStoreId is empty");
        if (string.IsNullOrWhiteSpace(customerFirstName) && string.IsNullOrWhiteSpace(customerLastName))
            errors.Add("Customer name is empty");
        if (string.IsNullOrWhiteSpace(folderPath))
            errors.Add("FolderPath is empty");
        if (items.Count == 0)
            errors.Add("No items found");

        if (errors.Count > 0)
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                $"Dakis order validation failed: {string.Join("; ", errors)}",
                orderId: raw.ExternalOrderId);
            throw new InvalidOperationException(
                $"Dakis order '{raw.ExternalOrderId}' failed validation: {string.Join("; ", errors)}");
        }

        return new UnifiedOrder
        {
            ExternalOrderId = raw.ExternalOrderId,
            ExternalSource = "dakis",
            OrderedAt = info.OrderedAt,
            CustomerFirstName = customerFirstName,
            CustomerLastName = customerLastName,
            CustomerEmail = info.Email,
            CustomerPhone = info.Phone,
            OrderTotal = info.ChargedPrice > 0 ? info.ChargedPrice : null,
            Paid = info.BeenPaid,
            Notes = info.Comment,
            FolderPath = folderPath,
            Location = info.StoreName,
            OrderType = info.OrderType,
            FulfillmentType = info.FulfillmentType,
            IsInvoiceOnly = false,
            BillingStoreId = info.BillingStoreId,
            CurrentStoreId = info.CurrentStoreId,
            Items = items
        };
    }

    // ── YamlDotNet-based order info extraction ───────────────────────────

    private static DakisOrderInfo ReadOrderInfoFromLines(string[] lines, string orderId)
    {
        var info = new DakisOrderInfo();

        try
        {
            var yaml = string.Join("\n", lines);
            var root = s_yaml.Deserialize<object>(yaml);

            info.Comment = YStr(root, ":comment:");
            info.BeenPaid = YBool(root, ":been_paid:");
            info.OrderType = YStr(root, ":type:");
            var ymlOrderId = YStr(root, ":id:");
            decimal.TryParse(YStr(root, ":charged_price:"), NumberStyles.Number, CultureInfo.InvariantCulture, out var chargedPrice);
            info.ChargedPrice = chargedPrice;

            var receivedAt = YStr(root, ":created_at:");
            int oYear = YInt(root, ":year:"), oMonth = YInt(root, ":month:"), oDay = YInt(root, ":day:");
            if (oYear > 0 && oMonth > 0 && oDay > 0)
            {
                try
                {
                    var dt = new DateTime(oYear, oMonth, oDay,
                        YInt(root, ":hour:"), YInt(root, ":minute:"), YInt(root, ":second:"), DateTimeKind.Utc);
                    info.OrderedAt = dt.ToLocalTime();
                }
                catch { }
            }
            if (!info.OrderedAt.HasValue && !string.IsNullOrEmpty(receivedAt))
            {
                if (DateTime.TryParse(receivedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    info.OrderedAt = parsed;
            }

            // Customer — all from order.yml
            var cust = YGet(root, ":customer:");
            info.CustFirst = YStr(cust, ":first_name:");
            info.CustLast = YStr(cust, ":last_name:");
            info.Phone = YStr(cust, ":phone:");
            info.Email = YStr(cust, ":email:");

            // Store
            info.StoreName = YStr(YGet(root, ":store:"), ":name:");

            // Fulfillment
            var fulfillment = YGet(root, ":fulfillment:") ?? YGet(root, ":order_fulfillment:");
            var currentStoreId = YStr(fulfillment, ":current_store:");
            var billingStoreId = YStr(fulfillment, ":billing_store:");

            if (string.IsNullOrEmpty(billingStoreId))
            {
                var storePhotos = YList(root, ":photos:");
                if (storePhotos != null)
                {
                    foreach (var photo in storePhotos)
                    {
                        var printsList = YList(photo, ":prints:");
                        if (printsList == null) continue;
                        foreach (var print in printsList)
                        {
                            var fsid = YStr(print, ":fulfillment_store_id:");
                            if (!string.IsNullOrEmpty(fsid))
                            {
                                billingStoreId = fsid;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(billingStoreId)) break;
                    }
                }
            }

            if (string.IsNullOrEmpty(billingStoreId))
            {
                var storeName = YStr(YGet(root, ":store:"), ":name:");
                if (!string.IsNullOrEmpty(storeName))
                {
                    var lookedUpId = LookupStoreIdByName(root, storeName);
                    if (!string.IsNullOrEmpty(lookedUpId))
                        billingStoreId = lookedUpId;
                }
            }

            info.BillingStoreId = billingStoreId;
            info.CurrentStoreId = currentStoreId;

            if (string.IsNullOrEmpty(info.StoreName))
            {
                var storeId = currentStoreId;
                if (string.IsNullOrEmpty(storeId))
                    storeId = billingStoreId;
                if (!string.IsNullOrEmpty(storeId))
                    info.StoreName = LookupStoreNameById(root, storeId) ?? storeId;
            }
            info.FulfillmentType = YStr(fulfillment, ":fulfillment_type:");

            if (!string.IsNullOrEmpty(currentStoreId))
            {
                var fulfillers = YList(fulfillment, ":fulfillers:");
                if (fulfillers != null && fulfillers.Count > 0)
                    info.IsInvoiceOnly = !fulfillers.Any(f => f?.ToString() == currentStoreId);
            }

            // Expected counts
            var photosList = YList(root, ":photos:");
            bool isMultiStore = !string.IsNullOrEmpty(currentStoreId) && photosList != null;

            if (isMultiStore)
            {
                foreach (var photo in photosList!)
                {
                    var printsList = YList(photo, ":prints:");
                    if (printsList == null || printsList.Count == 0) continue;

                    foreach (var print in printsList)
                    {
                        var text = YStr(print, ":text:");
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        int qty = YInt(print, ":quantity:");
                        if (qty <= 0) qty = 1;
                        var printStoreId = YStr(print, ":fulfillment_store_id:");

                        if (printStoreId == currentStoreId)
                        {
                            if (info.ExpectedCounts.ContainsKey(text))
                                info.ExpectedCounts[text] += qty;
                            else
                                info.ExpectedCounts[text] = qty;
                        }
                        else if (!string.IsNullOrEmpty(printStoreId))
                        {
                            if (info.OutlabCounts.ContainsKey(text))
                                info.OutlabCounts[text] += qty;
                            else
                                info.OutlabCounts[text] = qty;
                        }
                    }
                }
            }
            else
            {
                var cartContent = YGet(root, ":shopping_cart_content:");
                var cartItems = YList(cartContent, ":shopping_cart_items:") ?? YList(root, ":shopping_cart_items:");
                if (cartItems != null)
                {
                    bool inMyOrder = true;
                    foreach (var item in cartItems)
                    {
                        var objectType = YStr(item, ":object_type:");
                        var text = YStr(item, ":text:");

                        if (objectType == "PrintingOrder")
                        {
                            inMyOrder = string.IsNullOrEmpty(ymlOrderId) ||
                                        text.Contains(ymlOrderId, StringComparison.OrdinalIgnoreCase);
                        }
                        else if (objectType == "PrintFormat" && inMyOrder && !string.IsNullOrWhiteSpace(text))
                        {
                            int qty = YInt(item, ":quantity:");
                            if (qty > 0)
                            {
                                if (info.ExpectedCounts.ContainsKey(text))
                                    info.ExpectedCounts[text] += qty;
                                else
                                    info.ExpectedCounts[text] = qty;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                "YML parsing failed", orderId: orderId, ex: ex);
        }

        return info;
    }

    // ── YAML helpers ──

    private static object? YGet(object? node, string key)
    {
        if (node is Dictionary<object, object> dict)
        {
            if (dict.TryGetValue(key, out var val)) return val;
            if (key.EndsWith(':') && dict.TryGetValue(key[..^1], out val)) return val;
        }
        return null;
    }

    private static string YStr(object? node, string key)
        => YGet(node, key)?.ToString()?.Trim().Trim('"') ?? string.Empty;

    private static int YInt(object? node, string key)
        => int.TryParse(YGet(node, key)?.ToString()?.Trim(), out var n) ? n : 0;

    private static bool YBool(object? node, string key)
        => YGet(node, key)?.ToString()?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static string? LookupStoreNameById(object? root, string storeId)
    {
        var allStores = YList(root, ":all_stores:");
        if (allStores == null) return null;
        foreach (var store in allStores)
        {
            if (YStr(store, ":id:") == storeId)
                return YStr(store, ":name:");
        }
        return null;
    }

    private static string? LookupStoreIdByName(object? root, string storeName)
    {
        var allStores = YList(root, ":all_stores:");
        if (allStores == null) return null;
        foreach (var store in allStores)
        {
            if (string.Equals(YStr(store, ":name:"), storeName, StringComparison.OrdinalIgnoreCase))
                return YStr(store, ":id:");
        }
        return null;
    }

    private static List<object>? YList(object? node, string key)
        => YGet(node, key) as List<object>;

    // ── Disk scan helpers ──

    private static string StripFormatSuffix(string folderName)
        => folderName.EndsWith(" format", StringComparison.OrdinalIgnoreCase)
            ? folderName[..^7].Trim()
            : folderName.Trim();

    private static List<UnifiedOrderItem> ScanImages(string productDir)
    {
        var results = new List<UnifiedOrderItem>();

        foreach (var qtyDir in Directory.GetDirectories(productDir))
        {
            int qty = ParseQty(Path.GetFileName(qtyDir));
            foreach (var imageFile in Directory.GetFiles(qtyDir).Where(IsImageFile))
            {
                results.Add(new UnifiedOrderItem
                {
                    ImageFilename = Path.GetFileName(imageFile),
                    ImageFilepath = imageFile,
                    Quantity = qty
                });
            }
        }

        foreach (var imageFile in Directory.GetFiles(productDir).Where(IsImageFile))
        {
            results.Add(new UnifiedOrderItem
            {
                ImageFilename = Path.GetFileName(imageFile),
                ImageFilepath = imageFile,
                Quantity = 1
            });
        }

        return results;
    }

    private static int ParseQty(string dirName)
    {
        var parts = dirName.Split(' ');
        return parts.Length > 0 && int.TryParse(parts[0], out int qty) && qty > 0 ? qty : 1;
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";
    }

    // ── Internal model ──

    private class DakisOrderInfo
    {
        public string Comment { get; set; } = "";
        public bool BeenPaid { get; set; }
        public string Phone { get; set; } = "";
        public string Email { get; set; } = "";
        public string StoreName { get; set; } = "";
        public string CustFirst { get; set; } = "";
        public string CustLast { get; set; } = "";
        public DateTime? OrderedAt { get; set; }
        public string OrderType { get; set; } = "";
        public string FulfillmentType { get; set; } = "";
        public decimal ChargedPrice { get; set; }
        public bool IsInvoiceOnly { get; set; }
        public string BillingStoreId { get; set; } = "";
        public string CurrentStoreId { get; set; } = "";
        public Dictionary<string, int> ExpectedCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> OutlabCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
