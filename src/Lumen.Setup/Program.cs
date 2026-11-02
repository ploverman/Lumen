using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Lumen.Setup
{
    /// <summary>Entry point. With no arguments it shows the setup window. Run as uninstall.exe with /uninstall it removes the application.
    /// <para>Command line: <c>/quiet</c> shows no window; <c>/dir=path</c>, <c>/desktop</c> and <c>/noassoc</c> set the options;
    /// <c>/assumerelease=n</c> pretends the machine reports .NET Release n, to test the prerequisite check.</para>
    /// <para>Exit codes: 0 done, 1 failed, 2 .NET Framework 4.7 is missing, 3 the application is running.</para></summary>
    internal static class Program
    {
        private const int Failed = 1, MissingDotNet = 2, AppRunning = 3;

        /// <summary>Routes the command line.</summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool quiet = Has(args, "/quiet");
            if (Has(args, "/uninstall")) return Uninstall(quiet);

            string assumed = Value(args, "/assumerelease=");
            int release = assumed != null ? int.Parse(assumed) : DotNet.InstalledRelease();
            if (!Environment.Is64BitOperatingSystem)
            {
                if (!quiet) MessageBox.Show(Product.Name + " needs 64-bit Windows.", Product.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return Failed;
            }

            var options = new InstallOptions
            {
                Folder = Value(args, "/dir=") ?? Installer.DefaultFolder,
                DesktopShortcut = Has(args, "/desktop"),
                Associate = !Has(args, "/noassoc")
            };
            if (!quiet)
            {
                Application.Run(new SetupForm(options, release));
                return 0;
            }

            if (!DotNet.Meets47(release)) return MissingDotNet;
            if (Installer.AppRunning(Path.GetFullPath(options.Folder))) return AppRunning;
            try { Installer.Install(options, p => { }); return 0; }
            catch (Exception ex) when (IsExpected(ex)) { Console.Error.WriteLine(ex.Message); return Failed; }
        }

        /// <summary>Removes the application in the folder this exe runs from.</summary>
        private static int Uninstall(bool quiet)
        {
            string folder = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            if (Installer.AppRunning(folder))
            {
                if (!quiet) MessageBox.Show("Close " + Product.Name + ", then uninstall again.", Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return AppRunning;
            }
            if (!quiet && MessageBox.Show("Remove " + Product.Name + "? Your reports are not touched.", Product.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            try { Installer.Uninstall(folder); return 0; }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (!quiet) MessageBox.Show("Could not remove everything: " + ex.Message, Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return Failed;
            }
        }

        /// <summary>The failures a full disk, a locked file or a bad path can cause.</summary>
        internal static bool IsExpected(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;

        private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static string Value(string[] args, string prefix) =>
            args.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(prefix.Length)).FirstOrDefault();
    }
}
