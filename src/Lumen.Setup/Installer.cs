using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lumen.Setup
{
    /// <summary>What a run of the installer should do. The form and the command line both fill one of these.</summary>
    internal sealed class InstallOptions
    {
        /// <summary>Install folder.</summary>
        public string Folder = Installer.DefaultFolder;
        /// <summary>Add a desktop shortcut.</summary>
        public bool DesktopShortcut;
        /// <summary>Make the application open .lumen files.</summary>
        public bool Associate = true;
    }

    /// <summary>The prerequisite check. .NET Framework 4.x reports its version as a number in the registry; Microsoft documents 460798 as the lowest value for 4.7.</summary>
    internal static class DotNet
    {
        /// <summary>The lowest Release value of .NET Framework 4.7.</summary>
        public const int Net47Release = 460798;
        /// <summary>Where to get it. The installer opens this page; it never downloads anything itself.</summary>
        public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net47";

        /// <summary>The installed Release value, or 0 if .NET 4.5 or later is absent. Reads the 64-bit view, which a 32-bit process would otherwise not see.</summary>
        public static int InstalledRelease()
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                return key?.GetValue("Release") is int release ? release : 0;
        }

        /// <summary>True when the machine can run the application. Pure, so that the self-test can drive it with any number.</summary>
        public static bool Meets47(int release) => release >= Net47Release;
    }

    /// <summary>Copies the embedded application and registers it for the current user. Everything lands under the user's profile and HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    internal static class Installer
    {
        private const string AppName = Product.Name;
        private const string ExeName = Product.ExeName;
        private const string UninstallerName = "uninstall.exe";
        // Lists the installed files, one relative path per line. The uninstaller deletes these and nothing else, so a folder the user chose is safe.
        private const string ManifestName = "install.manifest";
        private const string PayloadPrefix = "payload/";
        private const string ProgId = Product.ProgId;
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.RegistryKey;
        private const string ClassesKey = @"Software\Classes\";

        /// <summary>The usual place for a per-user application.</summary>
        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>The installer's version, which the build takes from Directory.Build.props.</summary>
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        /// <summary>The embedded files.</summary>
        private static List<string> Payload() =>
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith(PayloadPrefix, StringComparison.Ordinal)).ToList();

        /// <summary>True if the application started from this folder is running. Windows would refuse to overwrite or delete its files.</summary>
        public static bool AppRunning(string folder)
        {
            string exe = Path.Combine(folder, ExeName);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                using (p)
                {
                    try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
                }
            }
            return false;
        }

        /// <summary>Installs or upgrades. <paramref name="progress"/> gets a percentage. A failed first install removes what it wrote; a failed upgrade leaves a mix, and running setup again repairs it.</summary>
        public static void Install(InstallOptions o, Action<int> progress)
        {
            string folder = Path.GetFullPath(o.Folder);
            string manifest = Path.Combine(folder, ManifestName);
            bool upgrade = File.Exists(manifest);
            var previous = upgrade ? File.ReadAllLines(manifest).ToList() : new List<string>();
            var written = new List<string>();
            var payload = Payload();
            try
            {
                Directory.CreateDirectory(folder);
                var assembly = Assembly.GetExecutingAssembly();
                for (int i = 0; i < payload.Count; i++)
                {
                    string relative = payload[i].Substring(PayloadPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(folder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var from = assembly.GetManifestResourceStream(payload[i]))
                    using (var to = new FileStream(target, FileMode.Create, FileAccess.Write))
                        from.CopyTo(to);
                    written.Add(relative);
                    progress((i + 1) * 90 / payload.Count);
                }
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(folder, UninstallerName), true);
                written.Add(UninstallerName);
                written.Add(ManifestName);
                File.WriteAllLines(manifest, written);

                // Files of an older version that this one no longer ships.
                foreach (var stale in previous.Except(written, StringComparer.OrdinalIgnoreCase))
                    DeleteFile(Path.Combine(folder, stale));

                string exe = Path.Combine(folder, ExeName);
                CreateShortcut(StartMenuLink, exe, folder);
                if (o.DesktopShortcut) CreateShortcut(DesktopLink, exe, folder);
                if (o.Associate) Associate(exe);
                Register(folder, exe);
                progress(100);
            }
            catch (Exception) when (!upgrade)
            {
                foreach (var f in written) DeleteFile(Path.Combine(folder, f));
                RemoveEmptyFolders(folder, written);
                throw;
            }
        }

        /// <summary>Removes what <see cref="Install"/> added, running from the installed uninstall.exe. That exe cannot delete itself while it runs, so a hidden command finishes the job after it exits.</summary>
        public static void Uninstall(string folder)
        {
            var listed = File.ReadAllLines(Path.Combine(folder, ManifestName)).ToList();
            DeleteFile(StartMenuLink);
            DeleteFile(DesktopLink);
            Unassociate();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            foreach (var f in listed.Where(x => !x.Equals(UninstallerName, StringComparison.OrdinalIgnoreCase)))
                DeleteFile(Path.Combine(folder, f));
            RemoveEmptyFolders(folder, listed);

            string me = Path.Combine(folder, UninstallerName);
            string command = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + me + "\" & rmdir \"" + folder + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false });
        }

        /// <summary>Starts the installed application.</summary>
        public static void Launch(string folder) => Process.Start(Path.Combine(folder, ExeName));

        /// <summary>Deletes a file that may already be gone.</summary>
        private static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Removes the subfolders of the listed files, deepest first, then the install folder, if each is empty. Never recursive: a file the user put there stays.</summary>
        private static void RemoveEmptyFolders(string folder, IEnumerable<string> files)
        {
            var folders = files.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct().OrderByDescending(d => d.Length)
                .Select(d => Path.Combine(folder, d)).Concat(new[] { folder });
            foreach (var d in folders)
                if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        }

        /// <summary>Makes a .lnk through the shell's scripting object, which is the one way to write one from managed code without a type library reference.</summary>
        private static void CreateShortcut(string link, string target, string folder)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = folder;
            shortcut.Description = Product.Description;
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }

        /// <summary>Registers the file type. If something else already owns .lumen it stays the default, and this application joins Open with.</summary>
        private static void Associate(string exe)
        {
            var hkcu = Registry.CurrentUser;
            using (var command = hkcu.CreateSubKey(ClassesKey + ProgId + @"\shell\open\command"))
                command.SetValue("", "\"" + exe + "\" \"%1\"");
            using (var type = hkcu.CreateSubKey(ClassesKey + ProgId))
                type.SetValue("", "Lumen report");
            using (var icon = hkcu.CreateSubKey(ClassesKey + ProgId + @"\DefaultIcon"))
                icon.SetValue("", exe + ",0");
            using (var ext = hkcu.CreateSubKey(ClassesKey + ".lumen"))
            {
                if (string.IsNullOrEmpty(ext.GetValue("") as string)) ext.SetValue("", ProgId);
                using (var with = ext.CreateSubKey("OpenWithProgids")) with.SetValue(ProgId, "");
            }
            NotifyShell();
        }

        /// <summary>Undoes <see cref="Associate"/>, leaving anything another program registered.</summary>
        private static void Unassociate()
        {
            var hkcu = Registry.CurrentUser;
            hkcu.DeleteSubKeyTree(ClassesKey + ProgId, false);
            bool remove = false;
            using (var ext = hkcu.OpenSubKey(ClassesKey + ".lumen", true))
            {
                if (ext != null)
                {
                    if (ext.GetValue("") as string == ProgId) ext.DeleteValue("");
                    using (var with = ext.OpenSubKey("OpenWithProgids", true))
                    {
                        with?.DeleteValue(ProgId, false);
                        if (with != null && with.ValueCount == 0) ext.DeleteSubKey("OpenWithProgids");
                    }
                    remove = ext.ValueCount == 0 && ext.SubKeyCount == 0;
                }
            }
            if (remove) hkcu.DeleteSubKey(ClassesKey + ".lumen");
            NotifyShell();
        }

        /// <summary>Adds the entry in Settings, Installed apps.</summary>
        private static void Register(string folder, string exe)
        {
            string uninstaller = "\"" + Path.Combine(folder, UninstallerName) + "\" /uninstall";
            long kb = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Lumen");
                key.SetValue("InstallLocation", folder);
                key.SetValue("DisplayIcon", exe);
                key.SetValue("UninstallString", uninstaller);
                key.SetValue("QuietUninstallString", uninstaller + " /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }
        }

        /// <summary>Tells Explorer that file associations changed, so icons update without a restart.</summary>
        private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

        [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
