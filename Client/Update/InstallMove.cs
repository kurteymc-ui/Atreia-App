using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using AionDPS.Ui;
using Microsoft.Win32;

namespace AionDPS.Update;

/// <summary>
/// The install moves from %LOCALAPPDATA%\A2HMeter (the meter's first name) to %LOCALAPPDATA%\AtreiaApp (8 November 2026, the owner: "it installs under
/// folder a2hmeter, it should install in the app name"). The updater names the folder after the package id, and an installed copy only ever updates within
/// its own id, so each release is packed twice (.github/workflows/release.yml):
/// <list type="bullet">
/// <item>as "AtreiaApp" (channel "stable", test releases "test"): what the installer puts in, and what every new install updates from;</item>
/// <item>as "A2HMeter" (channels "win" and "alpha", as before): the same build, so an existing install still finds the update. Started from the old
/// folder, it fetches the new installer of the same release, says the meter is moving, and runs it when the meter closes.</item>
/// </list>
/// The new install then tidies up after the old one on its first start (<see cref="CleanUpOldInstall"/>): shortcuts and the "start with Windows" entry
/// that still point into the old folder are pointed at the new one, the old entry in Windows' installed apps goes, and the old folder is deleted.
/// Settings, characters and history are in %APPDATA%\A2HMeter (<see cref="Brand.DataFolder"/>) and stay where they are: both installs read them there.
/// </summary>
public static class InstallMove
{
    public const string OldId = "A2HMeter";
    public const string NewId = "AtreiaApp";
    public const string NewExe = "AtreiaApp.exe";

    private static string? _installer;

    /// <summary>The installer of the new install is downloaded and is run when the meter closes (<see cref="RunIfReady"/>).</summary>
    public static bool Ready => _installer is not null;

    /// <summary>The folder the updater installed this copy into (the one with Update.exe, above "current"); null for a copy it did not install.</summary>
    private static string? InstallRoot()
    {
        try
        {
            string current = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? root = Path.GetDirectoryName(current);
            return root is not null && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsNamed(string root, string id) => string.Equals(Path.GetFileName(root), id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Started from the old folder (%LOCALAPPDATA%\A2HMeter).</summary>
    public static bool IsOldInstall => InstallRoot() is { } root && IsNamed(root, OldId);

    /// <summary>
    /// In the old install, once the meter's window is up: the new installer of this same release is fetched in the background (its SHA-256 checked against
    /// the release's SHA256SUMS.txt), then the player is told the meter moves, with the choice to close it now or keep playing. Either way the installer
    /// runs as the meter closes. Nothing happens in the new install or in a copy the updater did not install; a failure is only logged and tried again at
    /// the next start.
    /// </summary>
    public static async Task OfferAsync(Window owner)
    {
        if (InstallRoot() is not { } root || !IsNamed(root, OldId))
        {
            return;
        }

        try
        {
            string? newRoot = Path.GetDirectoryName(root) is { } parent ? Path.Combine(parent, NewId) : null;
            if (newRoot is not null && File.Exists(Path.Combine(newRoot, "current", NewExe)) && File.Exists(Path.Combine(newRoot, "Update.exe")))
            {
                // already installed there (the installer ran, the old copy was started by an old shortcut): open that one and close this
                _installer = Path.Combine(newRoot, NewExe);
            }
            else
            {
                _installer = await DownloadInstallerAsync();
            }
        }
        catch (Exception ex)
        {
            Log("the new installer could not be fetched: " + ex.Message);
            return;
        }

        var loc = LocalizationManager.Instance;
        Window box = ThemedMessageBox.Build(owner, string.Format(loc["Move.Text"], Brand.AppName), loc["Move.Title"], MessageBoxButton.YesNo,
            MessageBoxImage.Information, null, null, out ThemedMessageBox.Answer answer, (loc["Move.Now"], loc["Move.Later"], ""));
        box.ShowDialog();
        if (answer.Value == MessageBoxResult.Yes)
        {
            owner.Close();
        }
    }

    /// <summary>After the meter's window closed: the new installer (or the new install, when it is there already) is started as the signed-in user, not with
    /// the meter's administrator rights, so it installs for that user; it then opens the meter from its new place.</summary>
    public static void RunIfReady()
    {
        if (_installer is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            // Explorer starts what it is given with the user's ordinary rights (the meter itself runs elevated for the packet capture).
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
            Log("started " + path);
        }
        catch (Exception ex)
        {
            Log("the new installer could not be started: " + ex.Message);
        }
    }

    /// <summary>The version and channel this copy was packed with, from the updater's sq.version next to it ("1.21.0", "win"; a test release "1.21.0-alpha.1", "alpha").</summary>
    private static (string Version, string Channel) Packed()
    {
        string version = AppVersion.Text, channel = "win";
        try
        {
            string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sq.version"));
            if (Regex.Match(text, @"<version>\s*([^<\s]+)\s*</version>") is { Success: true } v)
            {
                version = v.Groups[1].Value;
            }

            if (Regex.Match(text, @"<channel>\s*([^<\s]+)\s*</channel>") is { Success: true } c)
            {
                channel = c.Groups[1].Value;
            }
        }
        catch (Exception)
        {
            // no sq.version: the version of the build, the normal channel
        }

        return (version, channel);
    }

    private static async Task<string> DownloadInstallerAsync()
    {
        (string version, string channel) = Packed();
        bool test = !string.Equals(channel, "win", StringComparison.OrdinalIgnoreCase);
        string name = test ? "Atreia-App-test-Setup.exe" : "Atreia-App-Setup.exe";
        string releases = Brand.RepositoryUrl + "/releases";

        // this release first (the tag is written with or without the "v"), then the newest one
        var bases = new List<string> { $"{releases}/download/v{version}", $"{releases}/download/{version}" };
        if (!test)
        {
            bases.Add($"{releases}/latest/download");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"{NewId}/{AppVersion.Text}");
        Exception? last = null;
        foreach (string at in bases)
        {
            try
            {
                string sums = await http.GetStringAsync($"{at}/SHA256SUMS.txt");
                string? expected = sums.Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length == 2 && p[1].Trim().TrimStart('*') == name).Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
                if (expected is null)
                {
                    throw new InvalidDataException($"{name} is not in SHA256SUMS.txt");
                }

                byte[] bytes = await http.GetByteArrayAsync($"{at}/{name}");
                string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (actual != expected)
                {
                    throw new InvalidDataException($"{name} does not match its SHA-256");
                }

                string folder = Path.Combine(Path.GetTempPath(), NewId);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name);
                await File.WriteAllBytesAsync(path, bytes);
                Log($"fetched {at}/{name}");
                return path;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("no address to fetch from");
    }

    /// <summary>
    /// In the new install, on every start until it is done (in the background): when the old folder is still there next to this one, the shortcuts and
    /// the "start with Windows" entry that point into it are pointed here, its entry in Windows' installed apps is removed and the folder is deleted. Waits
    /// for an old copy that is still closing. Whatever fails is tried again at the next start.
    /// </summary>
    public static void CleanUpOldInstall()
    {
        if (InstallRoot() is not { } root || !IsNamed(root, NewId) || Path.GetDirectoryName(root) is not { } parent)
        {
            return;
        }

        string old = Path.Combine(parent, OldId);
        if (!File.Exists(Path.Combine(old, "Update.exe")))
        {
            return;
        }

        try
        {
            WaitForOldCopies(old, TimeSpan.FromSeconds(60));
            string newStub = File.Exists(Path.Combine(root, NewExe)) ? Path.Combine(root, NewExe) : Environment.ProcessPath ?? Path.Combine(root, "current", NewExe);
            PointShortcuts(old, newStub, Path.Combine(root, "current"));
            StartupRegistration.Repoint(old);
            RemoveUninstallEntry(old);
            Directory.Delete(old, recursive: true);
            Log("removed the old install at " + old);
        }
        catch (Exception ex)
        {
            Log("the old install could not be removed yet: " + ex.Message);
        }
    }

    private static bool Under(string? path, string folder) =>
        path is { Length: > 0 } && Path.GetFullPath(path).StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static void WaitForOldCopies(string old, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            bool running = false;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName is OldId or "AtreiaApp" or "Update" && Under(p.MainModule?.FileName, old))
                    {
                        running = true;
                    }
                }
                catch (Exception)
                {
                    // a process of another user or one that just ended
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (!running)
            {
                return;
            }

            Thread.Sleep(1000);
        }
    }

    /// <summary>Desktop, Start menu and pinned task bar shortcuts into the old folder point into the new one.</summary>
    private static void PointShortcuts(string old, string newStub, string newCurrent)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] folders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch"),
        };

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (string folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string link in links)
            {
                try
                {
                    dynamic shortcut = shell.CreateShortcut(link);
                    string target = (string)shortcut.TargetPath;
                    if (!Under(target, old))
                    {
                        continue;
                    }

                    shortcut.TargetPath = newStub;
                    shortcut.WorkingDirectory = newCurrent;
                    shortcut.IconLocation = newStub + ",0";
                    shortcut.Save();
                    Log("pointed " + link + " at " + newStub);
                }
                catch (Exception)
                {
                    // a shortcut that cannot be read or written: left as it is
                }
            }
        }
    }

    /// <summary>The old install's entry in Windows' installed apps (the updater writes it under the package id), only when it is the one of the old folder.</summary>
    private static void RemoveUninstallEntry(string old)
    {
        const string Apps = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using RegistryKey? apps = Registry.CurrentUser.OpenSubKey(Apps, writable: true);
        using RegistryKey? entry = apps?.OpenSubKey(OldId);
        if (apps is null || entry is null)
        {
            return;
        }

        string where = (entry.GetValue("InstallLocation") as string) ?? (entry.GetValue("UninstallString") as string) ?? "";
        if (where.Contains(old, StringComparison.OrdinalIgnoreCase))
        {
            entry.Dispose();
            apps.DeleteSubKeyTree(OldId, throwOnMissingSubKey: false);
        }
    }

    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(MeterSettings.DataDirectory, "install-move.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // nowhere to write: nothing else to do
        }
    }
}
