using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Lumen.Viewer
{
    /// <summary>Entry point. Parses the command line: <c>--check report.lumen out.png</c> runs the self-check and exits; otherwise a .lumen argument opens.</summary>
    public partial class App : Application
    {
        /// <summary>True during --check: unhandled errors are collected, not shown, so nothing waits on a message box.</summary>
        internal static bool SelfCheck;
        /// <summary>Errors collected during a check, which then reports failure.</summary>
        internal static readonly System.Collections.Generic.List<string> Failures = new System.Collections.Generic.List<string>();

        /// <summary>Creates the window and routes the command line.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;

            var window = new ViewerWindow();
            MainWindow = window;

            int check = Array.IndexOf(e.Args, "--check");
            if (check >= 0 && check + 2 < e.Args.Length)
            {
                SelfCheck = true;
                new ViewerCheck(window).Run(e.Args[check + 1], e.Args[check + 2]);
                return;
            }

            window.Show();
            var file = e.Args.FirstOrDefault(a => a.EndsWith(".lumen", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (file != null) window.Open(file);
        }

        /// <summary>Last resort for UI-thread exceptions: the viewer shows the message and keeps the report on screen.</summary>
        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (SelfCheck) { Failures.Add(e.Exception.ToString()); return; }
            MessageBox.Show(e.Exception.Message, "Lumen Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
