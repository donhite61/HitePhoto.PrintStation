using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Converts Shared PixfizzTxtParser output into PrintStation's UnifiedOrderItem type.
/// </summary>
public static class TxtItemConverter
{
    public static List<UnifiedOrderItem> ToUnifiedItems(PixfizzTxtResult result)
    {
        var sharedItems = PixfizzTxtParser.ToItems(result);
        var items = new List<UnifiedOrderItem>(sharedItems.Count);

        foreach (var si in sharedItems)
        {
            items.Add(new UnifiedOrderItem
            {
                ExternalLineId = $"{si.OrderId}_{si.FormatString}_{si.ImageFilename}",
                SizeLabel = si.SizeLabel,
                MediaType = si.MediaType,
                FormatString = si.FormatString,
                Quantity = si.Quantity,
                ImageFilename = si.ImageFilename,
                ImageFilepath = si.ImageFilepath,
                Options = si.Options,
                ExpectedPrintCount = si.ExpectedPrintCount
            });
        }

        return items;
    }
}
