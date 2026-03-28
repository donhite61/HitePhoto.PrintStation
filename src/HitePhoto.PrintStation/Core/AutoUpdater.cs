using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Renci.SshNet;

namespace HitePhoto.PrintStation.Core;

public static class AutoUpdater
{
    private static readonly string _updateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HitePhoto.PrintStation", "Updates");

    private static bool _updating;

    /// <summary>
    /// Check for updates and prompt user. Called manually from Settings or on startup.
    /// If showStatus is true, shows a message even when no update is found.
    /// </summary>
    public static async Task CheckAndPromptAsync(AppSettings settings, bool showStatus = false)
    {
        if (_updating) return;

        try
        {
            bool hasLocal = !string.IsNullOrWhiteSpace(settings.UpdateLocalFolder);
            bool hasSftp = !string.IsNullOrWhiteSpace(settings.UpdateSftpFolder) &&
                           !string.IsNullOrWhiteSpace(settings.UpdateSftpHost);

            if (!hasLocal && !hasSftp)
            {
                AppLog.Info("AutoUpdate: not configured");
                if (showStatus)
                    MessageBox.Show("No update path configured.\n\nSet the NAS Root Folder or SFTP connection in Settings.",
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var remoteVersion = await Task.Run(() => ReadVersionText(settings));
            if (remoteVersion == null)
            {
                if (showStatus)
                    MessageBox.Show($"Could not read version.txt.\n\nLocal path: {(hasLocal ? settings.UpdateLocalFolder : "(not set)")}\nSFTP: {(hasSftp ? $"{settings.UpdateSftpHost}:{settings.UpdateSftpFolder}" : "(not set)")}",
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!DateTime.TryParse(remoteVersion.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var remoteUtc))
            {
                if (showStatus)
                    MessageBox.Show($"Could not parse version.txt: {remoteVersion.Trim()}",
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var localUtc = File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location);

            if (remoteUtc <= localUtc.AddMinutes(2))
            {
                if (showStatus)
                {
                    var currentDisplay = localUtc.ToLocalTime().ToString("yyyy-MM-dd  h:mm tt");
                    MessageBox.Show($"You are running the latest build.\n\nCurrent: {currentDisplay}",
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            var remoteDisplay = remoteUtc.ToLocalTime().ToString("yyyy-MM-dd  h:mm tt");
            var localDisplay = localUtc.ToLocalTime().ToString("yyyy-MM-dd  h:mm tt");
            var result = MessageBox.Show(
                $"A newer build is available.\n\n" +
                $"Current:  {localDisplay}\n" +
                $"Available:  {remoteDisplay}\n\n" +
                $"Download and install the update now?",
                "Update Available",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes) return;

            _updating = true;

            var statusText = new TextBlock
            {
                Text = "Preparing download...",
                Foreground = Brushes.Black,
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var progressWindow = new Window
            {
                Title = "Updating PrintStation",
                Width = 360, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                Content = statusText
            };
            progressWindow.Show();

            Action<string> updateStatus = msg =>
                Application.Current.Dispatcher.BeginInvoke(() => statusText.Text = msg);

            try
            {
                await Task.Run(() => DownloadAndStage(settings, updateStatus));
            }
            catch (Exception ex)
            {
                progressWindow.Close();
                AlertCollector.Error(AlertCategory.Update, "Auto-update download failed",
                    detail: $"Local: {settings.UpdateLocalFolder}, SFTP: {settings.UpdateSftpHost}:{settings.UpdateSftpFolder}",
                    ex: ex);
                MessageBox.Show($"Update failed:\n\n{ex.Message}", "Update Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            progressWindow.Close();
            LaunchBatchAndShutdown();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Update, "Auto-update check failed",
                detail: $"Attempted: check for updates. Found: exception.", ex: ex);
        }
        finally
        {
            _updating = false;
        }
    }

    private static string? ReadVersionText(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.UpdateLocalFolder))
        {
            try
            {
                var localPath = Path.Combine(settings.UpdateLocalFolder, "version.txt");
                if (File.Exists(localPath))
                    return File.ReadAllText(localPath);
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(settings.UpdateSftpFolder) ||
            string.IsNullOrWhiteSpace(settings.UpdateSftpHost))
            return null;

        try
        {
            using var client = CreateClient(settings);
            client.Connect();
            var folder = settings.UpdateSftpFolder.TrimEnd('/');
            var remotePath = $"{folder}/version.txt";

            if (!client.Exists(remotePath))
            {
                client.Disconnect();
                return null;
            }

            using var ms = new MemoryStream();
            client.DownloadFile(remotePath, ms);
            client.Disconnect();
            ms.Position = 0;
            using var reader = new StreamReader(ms);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AlertCollector.Warn(AlertCategory.Update, "Could not check for updates via SFTP",
                detail: $"Host: {settings.UpdateSftpHost}:{settings.UpdateSftpPort}",
                ex: ex);
            return null;
        }
    }

    private static void DownloadAndStage(AppSettings settings, Action<string> updateStatus)
    {
        var stagingDir = Path.Combine(_updateDir, "staged");
        var zipPath = Path.Combine(_updateDir, "PrintStation.zip");

        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(_updateDir);

        bool downloaded = false;
        if (!string.IsNullOrWhiteSpace(settings.UpdateLocalFolder))
        {
            try
            {
                var localZip = Path.Combine(settings.UpdateLocalFolder, "PrintStation.zip");
                if (File.Exists(localZip))
                {
                    updateStatus("Downloading update (local)...");
                    File.Copy(localZip, zipPath, overwrite: true);
                    downloaded = true;
                }
            }
            catch { }
        }

        if (!downloaded)
        {
            if (string.IsNullOrWhiteSpace(settings.UpdateSftpFolder) ||
                string.IsNullOrWhiteSpace(settings.UpdateSftpHost))
                throw new InvalidOperationException("Update files not reachable.");

            updateStatus("Downloading update (SFTP)...");

            using var client = CreateClient(settings);
            client.Connect();
            var folder = settings.UpdateSftpFolder.TrimEnd('/');
            var remotePath = $"{folder}/PrintStation.zip";

            if (!client.Exists(remotePath))
            {
                client.Disconnect();
                throw new FileNotFoundException("PrintStation.zip not found on server.");
            }

            using (var fs = File.Create(zipPath))
            {
                client.DownloadFile(remotePath, fs);
                fs.Flush();
            }
            client.Disconnect();
        }

        updateStatus("Extracting update...");
        ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
        updateStatus("Launching updater...");
    }

    private static void LaunchBatchAndShutdown()
    {
        var stagingDir = Path.Combine(_updateDir, "staged");
        var zipPath = Path.Combine(_updateDir, "PrintStation.zip");
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appExe = Path.Combine(appDir, "HitePhoto.PrintStation.exe");
        var batPath = Path.Combine(_updateDir, "update.bat");

        var batContent = $"""
            @echo off
            echo Waiting for PrintStation to exit...
            :waitloop
            tasklist /FI "IMAGENAME eq HitePhoto.PrintStation.exe" 2>NUL | find /I "HitePhoto.PrintStation.exe" >NUL
            if not errorlevel 1 (
                ping 127.0.0.1 -n 2 >NUL
                goto waitloop
            )
            echo Copying update files...
            xcopy /E /Y /Q "{stagingDir}\*" "{appDir}"
            echo Starting PrintStation...
            start "" "{appExe}"
            echo Cleaning up...
            rmdir /S /Q "{stagingDir}"
            del "{zipPath}"
            (goto) 2>nul & del "%~f0"
            """;

        File.WriteAllText(batPath, batContent);
        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized
        });

        Application.Current.Shutdown();
    }

    public static async Task<string?> UploadUpdateAsync(AppSettings settings, string publishDir)
    {
        return await Task.Run(() =>
        {
            try
            {
                var versionPath = Path.Combine(publishDir, "version.txt");
                var zipPath = Path.Combine(publishDir, "PrintStation.zip");

                if (!File.Exists(versionPath))
                    return "version.txt not found in publish folder.";
                if (!File.Exists(zipPath))
                    return "PrintStation.zip not found in publish folder.";

                if (!string.IsNullOrWhiteSpace(settings.UpdateLocalFolder))
                {
                    var localDir = settings.UpdateLocalFolder;
                    Directory.CreateDirectory(localDir);
                    File.Copy(versionPath, Path.Combine(localDir, "version.txt"), overwrite: true);
                    File.Copy(zipPath, Path.Combine(localDir, "PrintStation.zip"), overwrite: true);
                }

                if (!string.IsNullOrWhiteSpace(settings.UpdateSftpFolder) &&
                    !string.IsNullOrWhiteSpace(settings.UpdateSftpHost))
                {
                    using var client = CreateClient(settings);
                    client.Connect();
                    var folder = settings.UpdateSftpFolder.TrimEnd('/');

                    if (!client.Exists(folder))
                        client.CreateDirectory(folder);

                    using (var fs = File.OpenRead(versionPath))
                        client.UploadFile(fs, $"{folder}/version.txt", true);
                    using (var fs = File.OpenRead(zipPath))
                        client.UploadFile(fs, $"{folder}/PrintStation.zip", true);

                    client.Disconnect();
                }

                return (string?)null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    private static SftpClient CreateClient(AppSettings settings)
    {
        var client = new SftpClient(
            settings.UpdateSftpHost,
            settings.UpdateSftpPort,
            settings.UpdateSftpUsername,
            settings.UpdateSftpPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.OperationTimeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
