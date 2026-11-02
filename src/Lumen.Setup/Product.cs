namespace Lumen.Setup
{
    /// <summary>What this installer installs. Each setup project has its own copy of this class and shares the rest of the code, so the two products register under different names and can sit side by side.</summary>
    internal static class Product
    {
        /// <summary>The name people see, and the install folder, shortcut and Settings entry.</summary>
        public const string Name = "Lumen Viewer";
        /// <summary>The application's exe.</summary>
        public const string ExeName = "LumenViewer.exe";
        /// <summary>The shortcut's tooltip.</summary>
        public const string Description = "Read-only viewer for Lumen reports";
        /// <summary>The file type registration for .lumen files.</summary>
        public const string ProgId = "LumenViewer.Report";
        /// <summary>The key under the Windows Uninstall list.</summary>
        public const string RegistryKey = "LumenViewer";
    }
}
