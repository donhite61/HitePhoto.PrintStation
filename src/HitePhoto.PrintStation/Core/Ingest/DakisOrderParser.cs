using System.Globalization;
using System.IO;
using HitePhoto.Shared.Parsers;
using YamlDotNet.Serialization;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses Dakis order.yml + folder structure into UnifiedOrder format.
/// Uses YamlDotNet for proper YAML deserialization.
/// Order type detection: :print_formats: → print, :photo_gift_orders: → gift.
/// Shopping cart is NOT used for item data (it's a billing artifact shared across split orders).
/// </summary>
public class DakisOrderParser
{
    private static readonly IDeserializer s_yaml = new DeserializerBuilder().Build();

    // ── Main entry point ─────────────────────────────────────────────────

    public UnifiedOrder Parse(RawOrder raw)
    {
        var yaml = raw.RawData;
        var root = s_yaml.Deserialize<object>(yaml);
        if (root is null)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                "Dakis order.yml deserialized to null",
                orderId: raw.ExternalOrderId,
                detail: $"Attempted: deserialize order.yml. Expected: valid YAML root object. " +
                        $"Found: null. Context: RawOrder '{raw.ExternalOrderId}'. " +
                        $"State: cannot parse without YAML root.");
            throw new InvalidOperationException($"Dakis order.yml deserialized to null for '{raw.ExternalOrderId}'");
        }

        var info = ReadOrderHeader(root, raw.ExternalOrderId);

        // Validate order ID — source of truth from YML
        if (string.IsNullOrWhiteSpace(info.OrderId))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Dakis order.yml missing :id: field",
                orderId: raw.ExternalOrderId,
                detail: $"Attempted: read :id: from order.yml. Expected: numeric order ID. " +
                        $"Found: empty/missing. Context: parsing folder '{raw.Metadata?.GetValueOrDefault("folder_path")}'. " +
                        $"State: order cannot be ingested without an ID.");
            throw new InvalidOperationException($"Dakis order.yml missing :id: field in '{raw.Metadata?.GetValueOrDefault("folder_path")}'");
        }

        var folderPath = raw.Metadata?.GetValueOrDefault("folder_path");

        // ── Order type detection ──
        var printFormats = YList(root, ":print_formats:");
        var giftOrders = YList(root, ":photo_gift_orders:");
        bool hasPrints = printFormats != null && printFormats.Count > 0;
        bool hasGifts = giftOrders != null && giftOrders.Count > 0;

        if (hasPrints && hasGifts)
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Dakis order has both :print_formats: and :photo_gift_orders: — Dakis should split these",
                orderId: info.OrderId,
                detail: $"Attempted: detect order type. Expected: one or the other populated. " +
                        $"Found: both populated ({printFormats!.Count} print formats, {giftOrders!.Count} gift orders). " +
                        $"Context: order {info.OrderId}. State: cannot determine order type.");
            throw new InvalidOperationException($"Dakis order '{info.OrderId}' has both print_formats and photo_gift_orders");
        }
        if (!hasPrints && !hasGifts)
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Dakis order has neither :print_formats: nor :photo_gift_orders:",
                orderId: info.OrderId,
                detail: $"Attempted: detect order type. Expected: at least one populated. " +
                        $"Found: both empty. Context: order {info.OrderId}. " +
                        $"State: order inserted with 0 items — likely abandoned kiosk order.");
            return BuildUnifiedOrder(info, folderPath, items: [], isInvoiceOnly: false);
        }

        info.DakisOrderType = hasPrints ? "print" : "gift";

        // ── Build items by type ──
        List<UnifiedOrderItem> items;
        if (hasPrints)
            items = BuildPrintItems(root, info, folderPath);
        else
            items = BuildGiftItems(root, info, folderPath);

        // ── Multi-fulfiller detection ──
        // If any item is fulfilled at a different store than current_store, this is a split order
        // Includes invoice-only (all items at other store) and mixed (items at multiple stores)
        bool isMultiFulfiller = items.Any(i => i.FulfillmentStore != info.CurrentStoreId);

        // ── Validate ──
        ValidateOrder(info, items, folderPath);

        return BuildUnifiedOrder(info, folderPath, items, isInvoiceOnly: false, isMultiFulfiller: isMultiFulfiller);
    }

    // ── Order header extraction ──────────────────────────────────────────

    private static DakisOrderInfo ReadOrderHeader(object root, string fallbackOrderId)
    {
        var info = new DakisOrderInfo();

        try
        {
            // Format version check
            int formatVersion = YInt(root, ":format_version:");
            if (formatVersion != 4 && formatVersion != 0)
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Dakis order format_version is {formatVersion}, expected 4",
                    orderId: fallbackOrderId,
                    detail: $"Attempted: check format_version. Expected: 4. " +
                            $"Found: {formatVersion}. Context: order {fallbackOrderId}. " +
                            $"State: parsing will continue but results may be unreliable.");
            }

            // Core fields
            info.OrderId = YStr(root, ":id:");
            info.Comment = YStr(root, ":comment:");
            info.BeenPaid = YBool(root, ":been_paid:");
            info.OrderType = YStr(root, ":type:");
            info.PaymentDate = YStr(root, ":payment_date:");

            decimal.TryParse(YStr(root, ":charged_price:"), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var chargedPrice);
            info.ChargedPrice = chargedPrice;

            // Date with server_time_offset
            int oYear = YInt(root, ":year:"), oMonth = YInt(root, ":month:"), oDay = YInt(root, ":day:");
            if (oYear > 0 && oMonth > 0 && oDay > 0)
            {
                try
                {
                    var dt = new DateTime(oYear, oMonth, oDay,
                        YInt(root, ":hour:"), YInt(root, ":minute:"), YInt(root, ":second:"), DateTimeKind.Utc);

                    var offsetStr = YStr(root, ":server_time_offset:");
                    if (int.TryParse(offsetStr, out int offsetHours) && offsetHours != 0)
                        dt = dt.AddHours(-offsetHours);

                    info.OrderedAt = dt.ToLocalTime();
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Parsing,
                        "Failed to parse Dakis order date",
                        orderId: info.OrderId,
                        detail: $"Attempted: parse date {oYear}-{oMonth}-{oDay}. " +
                                $"Expected: valid DateTime. Found: exception. " +
                                $"Context: order {info.OrderId}. State: OrderedAt will be null.",
                        ex: ex);
                }
            }

            if (!info.OrderedAt.HasValue)
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    "Dakis order has no parseable date",
                    orderId: info.OrderId,
                    detail: $"Attempted: parse date from :year:/:month:/:day:. Expected: valid date. " +
                            $"Found: year={oYear}, month={oMonth}, day={oDay}. " +
                            $"Context: order {info.OrderId}. State: OrderedAt is null.");
            }

            // Customer
            var cust = YGet(root, ":customer:");
            info.CustomerId = YStr(cust, ":id:");
            info.CustFirst = YStr(cust, ":first_name:");
            info.CustLast = YStr(cust, ":last_name:");
            info.Phone = YStr(cust, ":phone:");
            info.Email = YStr(cust, ":email:");
            info.CustomerCompany = YStr(cust, ":company:");
            info.BillingAddress1 = YStr(cust, ":billing_address_1:");
            info.BillingCity = YStr(cust, ":billing_city:");
            info.BillingState = YStr(cust, ":billing_state:");
            info.BillingPostalCode = YStr(cust, ":billing_postal_code:");
            info.BillingCountry = YStr(cust, ":billing_country:");

            // Shipping
            var shipping = YGet(root, ":shipping:");
            info.ShippingMethod = YStr(shipping, ":method:");
            info.PickupInStore = YBool(shipping, ":pickup_in_store:");
            info.ShippingFirstName = YStr(cust, ":shipping_first_name:");
            info.ShippingLastName = YStr(cust, ":shipping_last_name:");
            info.ShippingAddress1 = YStr(cust, ":address_1:");
            info.ShippingAddress2 = YStr(cust, ":address_2:");
            info.ShippingCity = YStr(cust, ":city:");
            info.ShippingState = YStr(cust, ":state:");
            info.ShippingZip = YStr(cust, ":postal_code:");
            info.ShippingCountry = YStr(cust, ":country:");

            // Store
            info.StoreName = YStr(YGet(root, ":store:"), ":name:");

            // Shopping cart — channel only
            var cart = YGet(root, ":shopping_cart:");
            info.Channel = YStr(cart, ":channel:");

            // Related orders
            var relatedList = YList(root, ":related_printing_orders:");
            if (relatedList != null)
            {
                foreach (var rel in relatedList)
                {
                    var relId = YStr(rel, ":id:");
                    var relType = YStr(rel, ":type:");
                    if (!string.IsNullOrEmpty(relId))
                        info.RelatedOrders.Add((relId, relType));
                }
            }

            // Fulfillment — Dakis writes :billing_store: and :current_store: only on
            // :multiple_fulfillers: orders. On :normal: orders neither field is present;
            // the store that owns the order is the per-item :fulfillment_store_id:.
            var fulfillment = YGet(root, ":fulfillment:") ?? YGet(root, ":order_fulfillment:");
            var fulfillmentMode = YStr(fulfillment, ":order_fulfillment:");
            string billingStoreId;
            string currentStoreId;

            if (fulfillmentMode == ":multiple_fulfillers")
            {
                billingStoreId = YStr(fulfillment, ":billing_store:");
                if (string.IsNullOrEmpty(billingStoreId))
                    throw new InvalidOperationException(
                        $"Dakis order '{info.OrderId}' is :multiple_fulfillers: but " +
                        $"has no :billing_store: in :fulfillment:");

                currentStoreId = YStr(fulfillment, ":current_store:");
                if (string.IsNullOrEmpty(currentStoreId))
                    throw new InvalidOperationException(
                        $"Dakis order '{info.OrderId}' is :multiple_fulfillers: but " +
                        $"has no :current_store: in :fulfillment:");
            }
            else
            {
                // :normal fulfillment — single store. Read fulfillment_store_id from the
                // first available item (prints first, then gifts).
                var owningStoreId = FindFirstItemFulfillmentStore(root)
                    ?? throw new InvalidOperationException(
                        $"Dakis order '{info.OrderId}' is :normal: fulfillment but no " +
                        $"item has :fulfillment_store_id: (checked :photos:[].:prints:[] " +
                        $"and :photo_gift_orders:[])");
                billingStoreId = owningStoreId;
                currentStoreId = owningStoreId;
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

            // Printing order options (for print orders)
            info.PrintingOrderOptions = YList(root, ":printing_order_options:");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                "YML header parsing failed",
                orderId: fallbackOrderId,
                detail: $"Attempted: parse order header from order.yml. Expected: valid header fields. " +
                        $"Found: exception during parsing. Context: order {fallbackOrderId}. " +
                        $"State: partial header data may be available.",
                ex: ex);
            throw;
        }

        return info;
    }

    // ── Print item builder ───────────────────────────────────────────────

    private static List<UnifiedOrderItem> BuildPrintItems(object root, DakisOrderInfo info, string? folderPath)
    {
        var items = new List<UnifiedOrderItem>();

        // Build options list from :printing_order_options:
        var options = new List<OrderItemOption>();
        if (info.PrintingOrderOptions != null)
        {
            foreach (var opt in info.PrintingOrderOptions)
            {
                var textEn = YStr(opt, ":text_en:");
                var groupTextEn = YStr(opt, ":group_text_en:");
                if (!string.IsNullOrEmpty(textEn))
                    options.Add(new OrderItemOption(groupTextEn, textEn));
            }
        }

        // One item per photo per print entry — YML is source of truth
        var photosList = YList(root, ":photos:");
        if (photosList != null)
        {
            foreach (var photo in photosList)
            {
                var filename = YRaw(photo, ":filename:");
                if (string.IsNullOrWhiteSpace(filename))
                {
                    AlertCollector.Error(AlertCategory.DataQuality,
                        "Photo entry missing :filename:",
                        orderId: info.OrderId,
                        detail: $"Attempted: read :filename: from photo entry. Expected: image filename. " +
                                $"Found: empty. Context: order {info.OrderId}. " +
                                $"State: skipping this photo — cannot calculate file path.");
                    continue;
                }

                var imgWidth = YInt(photo, ":width:");
                var imgHeight = YInt(photo, ":height:");

                var printsList = YList(photo, ":prints:");
                if (printsList == null) continue;

                foreach (var print in printsList)
                {
                    var sizeLabel = YStr(print, ":text:");
                    if (string.IsNullOrWhiteSpace(sizeLabel))
                    {
                        AlertCollector.Error(AlertCategory.DataQuality,
                            $"Print entry missing :text: for {filename}",
                            orderId: info.OrderId,
                            detail: $"Attempted: read :text: from print entry. Expected: size label. " +
                                    $"Found: empty. Context: order {info.OrderId}, photo '{filename}'. " +
                                    $"State: skipping this print entry.");
                        continue;
                    }

                    int qty = YInt(print, ":quantity:");
                    if (qty <= 0)
                    {
                        AlertCollector.Error(AlertCategory.DataQuality,
                            $"Print entry has invalid quantity for {filename}",
                            orderId: info.OrderId,
                            detail: $"Attempted: read :quantity:. Expected: > 0. Found: {qty}. " +
                                    $"Context: order {info.OrderId}, size '{sizeLabel}', photo '{filename}'. " +
                                    $"State: skipping this print entry.");
                        continue;
                    }

                    var fulfillmentStoreId = YStr(print, ":fulfillment_store_id:");

                    // Calculate expected path
                    // Dakis strips dots from folder names (e.g. "2.5x3.5" → "25x35")
                    // Dakis keeps trailing spaces from :text: in folder names
                    var diskSizeLabel = YRaw(print, ":text:").Replace(".", "");
                    var printFilename = filename;
                    var expectedPath = "";
                    var (itemFulfillmentStore, isLocalItem) = ResolveFulfillment(fulfillmentStoreId, info.CurrentStoreId);

                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        var printDir = Path.Combine(folderPath, "prints", $"{diskSizeLabel} format", $"{qty} prints");
                        expectedPath = Path.Combine(printDir, printFilename);

                        // Dakis converts non-JPG originals to JPG in the prints folder
                        if (OrderHelpers.VerifyFile(expectedPath) != null &&
                            !Path.GetExtension(printFilename).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                        {
                            printFilename = Path.ChangeExtension(printFilename, ".jpg");
                            expectedPath = Path.Combine(printDir, printFilename);
                        }
                        if (isLocalItem)
                        {
                            var verifyError = OrderHelpers.VerifyFile(expectedPath);
                            if (verifyError != null)
                            {
                                AlertCollector.Error(AlertCategory.DataQuality,
                                    $"Dakis file not at expected path: {printFilename}",
                                    orderId: info.OrderId,
                                    detail: $"Attempted: verify file at '{expectedPath}'. " +
                                            $"Expected: valid image for size '{sizeLabel}'. " +
                                            $"Found: {verifyError}. " +
                                            $"Context: order {info.OrderId}, qty {qty}. " +
                                            $"State: file missing or invalid — operator must fix.");
                            }
                        }
                    }

                    items.Add(new UnifiedOrderItem
                    {
                        ExternalLineId = $"{info.OrderId}_{sizeLabel}_{printFilename}",
                        SizeLabel = sizeLabel,
                        FormatString = sizeLabel,
                        Quantity = qty,
                        ImageFilename = printFilename,
                        ImageFilepath = expectedPath,
                        IsNoritsu = true,
                        IsLocalProduction = isLocalItem,
                        FulfillmentStore = itemFulfillmentStore,
                        ImageWidth = imgWidth,
                        ImageHeight = imgHeight,
                        Options = options
                    });
                }
            }
        }

        return items;
    }

    // ── Gift item builder ────────────────────────────────────────────────

    private static List<UnifiedOrderItem> BuildGiftItems(object root, DakisOrderInfo info, string? folderPath)
    {
        var items = new List<UnifiedOrderItem>();
        var giftOrders = YList(root, ":photo_gift_orders:");
        if (giftOrders == null) return items;

        foreach (var gift in giftOrders)
        {
            var text = YStr(gift, ":text:");
            var category = YStr(gift, ":category:");
            var subCategory = YStr(gift, ":sub_category:");
            var fulfillmentStoreId = YStr(gift, ":fulfillment_store_id:");
            var giftingOrderId = YRaw(gift, ":gifting_order_id:");
            int qty = YInt(gift, ":quantity:");
            if (qty <= 0) qty = 1;

            decimal.TryParse(YStr(gift, ":price:"), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var price);

            if (string.IsNullOrWhiteSpace(text))
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    "Gift order entry missing :text: (product name)",
                    orderId: info.OrderId,
                    detail: $"Attempted: read :text: from photo_gift_orders entry. Expected: product name. " +
                            $"Found: empty. Context: order {info.OrderId}, gifting_order_id={giftingOrderId}. " +
                            $"State: skipping this gift entry.");
                continue;
            }

            // Build options with category/price metadata
            var giftOptions = new List<OrderItemOption>();
            if (!string.IsNullOrEmpty(category))
                giftOptions.Add(new OrderItemOption("Category", category));
            if (!string.IsNullOrEmpty(subCategory))
                giftOptions.Add(new OrderItemOption("SubCategory", subCategory));
            if (price > 0)
                giftOptions.Add(new OrderItemOption("Price", price.ToString(CultureInfo.InvariantCulture)));

            // Find images in photo_products/ subfolder
            string? imageFilepath = null;
            string? imageFilename = null;

            if (!string.IsNullOrEmpty(folderPath) && !string.IsNullOrEmpty(giftingOrderId))
            {
                var giftRoot = Path.Combine(folderPath, "photo_products");
                if (Directory.Exists(giftRoot))
                {
                    // Match folder by trailing gifting_order_id
                    var matchingDir = Directory.GetDirectories(giftRoot)
                        .FirstOrDefault(d => Path.GetFileName(d).EndsWith(giftingOrderId, StringComparison.OrdinalIgnoreCase));

                    if (matchingDir != null)
                    {
                        // Find Page N *.jpg files — exclude toprint.jpg and canvas PNGs
                        var pageFiles = Directory.GetFiles(matchingDir)
                            .Where(f =>
                            {
                                var fname = Path.GetFileName(f);
                                if (fname.Equals("toprint.jpg", StringComparison.OrdinalIgnoreCase))
                                    return false;
                                if (!IsImageFile(f))
                                    return false;
                                if (fname.StartsWith("canvas", StringComparison.OrdinalIgnoreCase)
                                    && Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase))
                                    return false;
                                return true;
                            })
                            .OrderBy(f => f)
                            .ToList();

                        if (pageFiles.Count > 0)
                        {
                            imageFilepath = pageFiles[0];
                            imageFilename = Path.GetFileName(pageFiles[0]);
                        }
                    }
                }
            }

            var (giftFulfillmentStore, isLocalGift) = ResolveFulfillment(fulfillmentStoreId, info.CurrentStoreId);

            items.Add(new UnifiedOrderItem
            {
                ExternalLineId = $"{info.OrderId}_gift_{giftingOrderId}",
                SizeLabel = text,
                MediaType = subCategory,
                FormatString = text,
                Quantity = qty,
                ImageFilepath = imageFilepath,
                ImageFilename = imageFilename,
                IsNoritsu = false,
                IsLocalProduction = isLocalGift,
                FulfillmentStore = giftFulfillmentStore,
                Options = giftOptions
            });
        }

        return items;
    }

    // ── Validation ───────────────────────────────────────────────────────

    private static void ValidateOrder(DakisOrderInfo info, List<UnifiedOrderItem> items, string? folderPath)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(info.OrderId))
            errors.Add("ExternalOrderId is empty");
        if (string.IsNullOrWhiteSpace(info.BillingStoreId))
            errors.Add("BillingStoreId is empty");
        if (string.IsNullOrWhiteSpace(info.CustFirst) && string.IsNullOrWhiteSpace(info.CustLast))
            errors.Add("Customer name is empty (both first and last)");
        if (string.IsNullOrWhiteSpace(folderPath))
            errors.Add("FolderPath is empty");
        if (!info.OrderedAt.HasValue)
            errors.Add("OrderedAt has no value");
        if (string.IsNullOrWhiteSpace(info.CurrentStoreId))
            errors.Add("CurrentStoreId is empty");

        // Item-level validation
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.SizeLabel))
                errors.Add($"Item[{i}] SizeLabel is empty");
            if (item.Quantity <= 0)
                errors.Add($"Item[{i}] Quantity is {item.Quantity} (expected > 0)");
            if (string.IsNullOrWhiteSpace(item.FulfillmentStore))
                errors.Add($"Item[{i}] FulfillmentStore is empty");

            // Gift-specific validation
            if (!item.IsNoritsu)
            {
                if (string.IsNullOrWhiteSpace(item.MediaType))
                    errors.Add($"Item[{i}] gift MediaType (sub_category) is empty");
            }
        }

        if (items.Count == 0)
            errors.Add("No items found for this store");

        if (errors.Count > 0)
        {
            var errorText = string.Join("; ", errors);
            AlertCollector.Error(AlertCategory.DataQuality,
                $"Dakis order validation failed: {errorText}",
                orderId: info.OrderId,
                detail: $"Attempted: validate order {info.OrderId} ({info.DakisOrderType}). " +
                        $"Expected: all fields valid. Found: {errors.Count} error(s). " +
                        $"Context: folder '{folderPath}'. " +
                        $"State: order will not be ingested.");
            throw new InvalidOperationException(
                $"Dakis order '{info.OrderId}' failed validation: {errorText}");
        }
    }

    // ── Build final UnifiedOrder ─────────────────────────────────────────

    private static UnifiedOrder BuildUnifiedOrder(DakisOrderInfo info, string? folderPath,
        List<UnifiedOrderItem> items, bool isInvoiceOnly, bool isMultiFulfiller = false)
    {
        return new UnifiedOrder
        {
            ExternalOrderId = info.OrderId,
            ExternalSource = "dakis",
            OrderedAt = info.OrderedAt,
            CustomerFirstName = info.CustFirst,
            CustomerLastName = info.CustLast,
            CustomerEmail = info.Email,
            CustomerPhone = info.Phone,
            OrderTotal = info.ChargedPrice > 0 ? info.ChargedPrice : null,
            Paid = info.BeenPaid,
            Notes = info.Comment,
            FolderPath = folderPath,
            Location = info.StoreName,
            OrderType = info.OrderType,
            FulfillmentType = info.FulfillmentType,
            IsInvoiceOnly = isInvoiceOnly,
            BillingStoreId = info.BillingStoreId,
            CurrentStoreId = info.CurrentStoreId,
            Channel = info.Channel,
            DeliveryMethodId = info.PickupInStore ? DeliveryMethodId.Pickup : DeliveryMethodId.Ship,
            ShippingFirstName = info.ShippingFirstName,
            ShippingLastName = info.ShippingLastName,
            ShippingAddress1 = info.ShippingAddress1,
            ShippingAddress2 = info.ShippingAddress2,
            ShippingCity = info.ShippingCity,
            ShippingState = info.ShippingState,
            ShippingZip = info.ShippingZip,
            ShippingCountry = info.ShippingCountry,
            ShippingMethod = info.ShippingMethod,
            IsMultiFulfiller = isMultiFulfiller,
            Items = items
        };
    }

    // ── YAML helpers ─────────────────────────────────────────────────────

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

    /// <summary>Read a YAML string without trimming whitespace. Strip quotes only.
    /// Use for values that Dakis uses in folder/file names where spaces matter.</summary>
    private static string YRaw(object? node, string key)
        => YGet(node, key)?.ToString()?.Trim('"') ?? string.Empty;

    private static int YInt(object? node, string key)
        => int.TryParse(YGet(node, key)?.ToString()?.Trim(), out var n) ? n : 0;

    private static bool YBool(object? node, string key)
        => YGet(node, key)?.ToString()?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static List<object>? YList(object? node, string key)
        => YGet(node, key) as List<object>;

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

    /// <summary>
    /// For :normal: fulfillment orders, the only store info Dakis writes is per-item
    /// :fulfillment_store_id:. Returns the first one found in :photos:[].:prints:[]
    /// (print orders) or :photo_gift_orders:[] (gift orders), or null if neither has any.
    /// </summary>
    private static string? FindFirstItemFulfillmentStore(object? root)
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
                    if (!string.IsNullOrEmpty(fsid)) return fsid;
                }
            }
        }

        var gifts = YList(root, ":photo_gift_orders:");
        if (gifts != null)
        {
            foreach (var gift in gifts)
            {
                var fsid = YStr(gift, ":fulfillment_store_id:");
                if (!string.IsNullOrEmpty(fsid)) return fsid;
            }
        }

        return null;
    }

    private static (string Store, bool IsLocal) ResolveFulfillment(string? fulfillmentStoreId, string currentStoreId)
    {
        var store = !string.IsNullOrEmpty(fulfillmentStoreId) ? fulfillmentStoreId : currentStoreId;
        return (store, store == currentStoreId);
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";
    }

    // ── Internal model ───────────────────────────────────────────────────

    private class DakisOrderInfo
    {
        // Core
        public string OrderId { get; set; } = "";
        public string Comment { get; set; } = "";
        public bool BeenPaid { get; set; }
        public string OrderType { get; set; } = "";
        public string PaymentDate { get; set; } = "";
        public decimal ChargedPrice { get; set; }
        public DateTime? OrderedAt { get; set; }

        // Customer
        public string CustomerId { get; set; } = "";
        public string CustFirst { get; set; } = "";
        public string CustLast { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Email { get; set; } = "";
        public string CustomerCompany { get; set; } = "";
        public string ShippingFirstName { get; set; } = "";
        public string ShippingLastName { get; set; } = "";
        public string ShippingAddress1 { get; set; } = "";
        public string ShippingAddress2 { get; set; } = "";
        public string ShippingCity { get; set; } = "";
        public string ShippingState { get; set; } = "";
        public string ShippingZip { get; set; } = "";
        public string ShippingCountry { get; set; } = "";
        public string BillingAddress1 { get; set; } = "";
        public string BillingCity { get; set; } = "";
        public string BillingState { get; set; } = "";
        public string BillingPostalCode { get; set; } = "";
        public string BillingCountry { get; set; } = "";

        // Shipping
        public string ShippingMethod { get; set; } = "";
        public bool PickupInStore { get; set; }

        // Store / fulfillment
        public string StoreName { get; set; } = "";
        public string FulfillmentType { get; set; } = "";
        public bool IsInvoiceOnly { get; set; }
        public string BillingStoreId { get; set; } = "";
        public string CurrentStoreId { get; set; } = "";

        // Channel
        public string Channel { get; set; } = "";

        // Order type (set by type detection)
        public string DakisOrderType { get; set; } = "";

        // Related orders
        public List<(string Id, string Type)> RelatedOrders { get; } = [];

        // Raw YML lists (for builders)
        public List<object>? PrintingOrderOptions { get; set; }
    }
}
