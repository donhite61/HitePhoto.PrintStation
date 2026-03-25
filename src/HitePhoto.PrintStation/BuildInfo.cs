using System;
using System.IO;
using System.Reflection;

namespace HitePhoto.PrintStation
{
    public static class BuildInfo
    {
        private static readonly DateTime _buildUtc =
            File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location);

        public static string BuildTimestamp { get; } =
            _buildUtc.ToLocalTime().ToString("yyyy-MM-dd  h:mm tt");

        /// <summary>
        /// UTC build time for version comparison with remote version.txt.
        /// </summary>
        public static DateTime BuildDateTimeUtc => _buildUtc;
    }
}
