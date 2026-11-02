using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Lumen.Setup
{
    /// <summary>The one setup window: folder, options, prerequisite status and progress. With .NET 4.7 missing, Install is replaced by a link to Microsoft's download page.</summary>
    internal sealed class SetupForm : Form
    {
        private readonly InstallOptions _options;
        private readonly bool _dotNetOk;
        private readonly TextBox _folder = new TextBox { Left = 16, Top = 72, Width = 372 };
        private readonly Button _browse = new Button { Text = "Browse...", Left = 396, Top = 70, Width = 84, Height = 26 };
        private readonly CheckBox _associate = new CheckBox { Text = "Open .lumen files with " + Product.Name, Left = 16, Top = 108, Width = 460 };
        private readonly CheckBox _desktop = new CheckBox { Text = "Add a desktop shortcut", Left = 16, Top = 134, Width = 460 };
        private readonly CheckBox _launch = new CheckBox { Text = "Start " + Product.Name + " now", Left = 16, Top = 134, Width = 460, Checked = true, Visible = false };
        private readonly Label _status = new Label { Left = 16, Top = 168, Width = 464, Height = 36 };
        private readonly ProgressBar _bar = new ProgressBar { Left = 16, Top = 208, Width = 464, Height = 16 };
        private readonly Button _action = new Button { Left = 300, Top = 238, Width = 88, Height = 28 };
        private readonly Button _close = new Button { Text = "Cancel", Left = 392, Top = 238, Width = 88, Height = 28 };
        private bool _finished, _busy;

        /// <summary>Builds the window. <paramref name="release"/> is the .NET Release value found on the machine.</summary>
        public SetupForm(InstallOptions options, int release)
        {
            _options = options;
            _dotNetOk = DotNet.Meets47(release);
            Text = Product.Name + " " + Installer.Version + " Setup";
            ClientSize = new Size(496, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label { Text = Product.Name, Left = 16, Top = 12, Width = 464, Height = 28, Font = new Font("Segoe UI", 14f, FontStyle.Bold) });
            Controls.Add(new Label { Text = "Install to this folder. No administrator rights are needed.", Left = 16, Top = 48, Width = 464 });
            _folder.Text = options.Folder;
            _associate.Checked = options.Associate;
            _desktop.Checked = options.DesktopShortcut;
            Controls.AddRange(new Control[] { _folder, _browse, _associate, _desktop, _launch, _status, _bar, _action, _close });

            _status.Text = _dotNetOk ? ".NET Framework 4.7 or later is installed."
                : Product.Name + " needs .NET Framework 4.7 or later, and this PC does not have it. Install it from Microsoft, restart if asked, then run this setup again.";
            _status.ForeColor = _dotNetOk ? SystemColors.ControlText : Color.Firebrick;
            _action.Text = _dotNetOk ? "Install" : "Get .NET 4.7";
            _folder.Enabled = _browse.Enabled = _associate.Enabled = _desktop.Enabled = _dotNetOk;

            _browse.Click += (s, e) => Browse();
            _action.Click += (s, e) => { if (_dotNetOk) Run(); else Process.Start(DotNet.DownloadUrl); };
            _close.Click += (s, e) => Close();
            FormClosing += (s, e) => { e.Cancel = _busy; };
            FormClosed += (s, e) => { if (_finished && _launch.Checked) Installer.Launch(_options.Folder); };
            AcceptButton = _action;
            ActiveControl = _action;
            // The layout above is in 96 dpi pixels, while fonts in points already follow the display.
            using (var g = CreateGraphics()) Scale(new SizeF(g.DpiX / 96f, g.DpiY / 96f));
        }

        /// <summary>Picks the install folder.</summary>
        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        }

        /// <summary>Installs on a worker thread, so that the window stays live and the bar moves.</summary>
        private void Run()
        {
            _options.Folder = _folder.Text.Trim();
            _options.Associate = _associate.Checked;
            _options.DesktopShortcut = _desktop.Checked;
            string folder;
            try { folder = Path.GetFullPath(_options.Folder); }
            catch (Exception ex) when (Program.IsExpected(ex)) { Report("That folder is not valid: " + ex.Message); return; }
            if (Installer.AppRunning(folder)) { Report("Close " + Product.Name + ", then press Install again."); return; }

            _busy = true;
            _action.Enabled = _close.Enabled = _folder.Enabled = _browse.Enabled = _associate.Enabled = _desktop.Enabled = false;
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "Installing...";
            new Thread(() =>
            {
                try
                {
                    Installer.Install(_options, p => BeginInvoke(new Action(() => _bar.Value = p)));
                    BeginInvoke(new Action(Finished));
                }
                catch (Exception ex) when (Program.IsExpected(ex))
                {
                    BeginInvoke(new Action(() =>
                    {
                        _busy = false;
                        _action.Enabled = _close.Enabled = _folder.Enabled = _browse.Enabled = _associate.Enabled = _desktop.Enabled = true;
                        Report("Setup could not finish: " + ex.Message);
                    }));
                }
            }) { IsBackground = false }.Start();
        }

        /// <summary>Shows a problem in the status line.</summary>
        private void Report(string text)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = text;
        }

        /// <summary>Turns the window into its last page.</summary>
        private void Finished()
        {
            _busy = false;
            _finished = true;
            _status.Text = Product.Name + " is installed. Find it on the Start menu.";
            _desktop.Visible = false;
            _launch.Visible = true;
            _action.Visible = false;
            _close.Text = "Finish";
            _close.Enabled = true;
            AcceptButton = _close;
        }
    }
}
