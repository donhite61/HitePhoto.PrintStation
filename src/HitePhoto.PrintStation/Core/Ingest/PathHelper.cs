namespace HitePhoto.PrintStation.Core.Ingest;

public static class PathHelper
{
    /// <summary>
    /// Convert a folder_path to an SFTP remote path.
    /// Handles: SFTP paths (start with /), Windows paths (drive letter), and bare paths.
    /// </summary>
    public static string ToRemotePath(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return "";
        if (rawPath.StartsWith("/")) return rawPath;
        if (rawPath.Length >= 2 && rawPath[1] == ':')
            return rawPath[2..].Replace('\\', '/');
        return rawPath.Replace('\\', '/');
    }
}
