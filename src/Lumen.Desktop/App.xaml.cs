using System;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lumen.Desktop
{
    /// <summary>Entry point. Parses the command line: --bench, --screenshot and --warehouse run a check and exit; otherwise a .lumen argument opens, or a recovery file is offered.</summary>
    public partial class App : Application
    {
        /// <summary>True during --bench, --screenshot and --warehouse: unhandled errors are collected, not shown, so nothing waits on a message box.</summary>
        internal static bool SelfCheck;
        /// <summary>Errors collected during a check, which then reports failure.</summary>
        internal static readonly System.Collections.Generic.List<string> Failures = new System.Collections.Generic.List<string>();

        /// <summary>Creates the main window and routes the command line.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;

            var window = new MainWindow();
            MainWindow = window;

            int bench = Array.IndexOf(e.Args, "--bench");
            if (bench >= 0 && bench + 2 < e.Args.Length)
            {
                SelfCheck = true;
                window.RunBench(int.Parse(e.Args[bench + 1]), e.Args[bench + 2]);
                return;
            }

            int shot = Array.IndexOf(e.Args, "--screenshot");
            if (shot >= 0 && shot + 1 < e.Args.Length)
            {
                SelfCheck = true;
                window.RunScreenshot(e.Args[shot + 1]);
                return;
            }

            int warehouse = Array.IndexOf(e.Args, "--warehouse");
            if (warehouse >= 0 && warehouse + 1 < e.Args.Length)
            {
                SelfCheck = true;
                window.RunWarehouseCheck(e.Args[warehouse + 1]);
                return;
            }

            window.Show();
            var file = e.Args.FirstOrDefault(a => a.EndsWith(".lumen", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (file != null) window.OpenFile(file);
            else window.OfferRecovery();
        }

        /// <summary>Last resort for UI-thread exceptions: the app shows the message and keeps running, because losing a report is worse than a failed click.</summary>
        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (SelfCheck) { Failures.Add(e.Exception.ToString()); return; }
            MessageBox.Show(e.Exception.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
