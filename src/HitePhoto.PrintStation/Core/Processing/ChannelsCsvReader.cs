using System.IO;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Reads ch_data.csv — a tab-delimited Noritsu channel export.
/// Expected columns: ChannelNumber, SizeLabel, MediaType, Description
/// Header row is skipped.
/// </summary>
public class ChannelsCsvReader
{
    private readonly string _csvPath;

    public ChannelsCsvReader(string csvPath)
    {
        _csvPath = csvPath;
    }

    public List<ChannelInfo> Load()
    {
        var channels = new List<ChannelInfo>();

        if (!File.Exists(_csvPath))
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Channels CSV file not found",
                detail: $"Attempted: read channel definitions from '{_csvPath}'. " +
                        $"Expected: tab-delimited CSV with header. Found: file missing. " +
                        $"State: channel assignment will not work until CSV path is configured.");
            return channels;
        }

        try
        {
            var lines = File.ReadAllLines(_csvPath);
            bool firstLine = true;

            foreach (var line in lines)
            {
                if (firstLine)
                {
                    firstLine = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split('\t');
                if (parts.Length < 3)
                    continue;

                if (!int.TryParse(parts[0].Trim(), out int channelNumber))
                    continue;

                channels.Add(new ChannelInfo
                {
                    ChannelNumber = channelNumber,
                    SizeLabel     = parts.Length > 1 ? parts[1].Trim() : "",
                    MediaType     = parts.Length > 2 ? parts[2].Trim() : "",
                    Description   = parts.Length > 3 ? parts[3].Trim() : ""
                });
            }
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Failed to read channels CSV",
                detail: $"Attempted: parse '{_csvPath}'. Expected: channel list. Found: exception.",
                ex: ex);
        }

        return channels;
    }
}
