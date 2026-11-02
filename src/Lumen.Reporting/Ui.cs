using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumen.Reporting
{
    /// <summary>Small builders for code-only UI. There are no XAML views: everything except App.xaml is constructed in C#,
    /// and these helpers keep that construction short and theme-aware.</summary>
    public static class Ui
    {
        /// <summary>16 by 16 pictograms for the visual types.</summary>
        public static readonly Dictionary<string, Geometry> VisualIcons = new Dictionary<string, string>
        {
            { "card", "M1,4h14v8H1z M3,7h6v2H3z" },
            { "multiRowCard", "M1,2h14v5H1z M1,9h14v5H1z M3,4h6v1H3z M3,11h6v1H3z" },
            { "table", "M1,2h14v12H1z M2,6h6v3H2z M9,6h5v3H9z M2,10h6v3H2z M9,10h5v3H9z" },
            { "matrix", "M1,2h14v12H1z M6,3h3v2H6z M10,3h4v2h-4z M2,6h3v3H2z M6,6h3v3H6z M10,6h4v3h-4z M2,10h3v3H2z M6,10h3v3H6z M10,10h4v3h-4z" },
            { "clusteredBar", "M1,2h1v12H1z M3,3h11v2H3z M3,6h7v2H3z M3,10h9v2H3z" },
            { "stackedBar", "M1,2h1v12H1z M3,3h6v3H3z M10,3h4v3h-4z M3,8h4v3H3z M8,8h3v3H8z" },
            { "clusteredColumn", "M2,7h3v7H2z M6,3h3v11H6z M10,9h3v5h-3z" },
            { "stackedColumn", "M2,9h4v5H2z M2,5h4v3H2z M8,7h4v7H8z M8,2h4v4H8z" },
            { "line", "M1,12L5,6L9,9L15,2V4L9,11L5,8L1,14z" },
            { "area", "M1,14V10L5,5L9,8L15,3V14z" },
            { "pie", "M8,8V1A7,7 0 1 0 15,8z M9,0A7,7 0 0 1 16,7H9z" },
            { "donut", "M8,1A7,7 0 1 1 7.99,1z M8,5A3,3 0 1 0 8.01,5z" },
            { "scatter", "M3,10h2v2H3z M7,6h2v2H7z M10,9h2v2h-2z M12,3h2v2h-2z M5,4h2v2H5z" },
            { "slicer", "M1,2h14L10,8v6L6,12V8z" }
        }.ToDictionary(kv => kv.Key, kv => (Geometry)Freeze(Geometry.Parse(kv.Value)));

        private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

        /// <summary>A bold section heading.</summary>
        public static TextBlock Heading(string text) => new TextBlock
        {
            Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 6, 0, 4)
        }.Fg("ChromeFg");

        /// <summary>A small secondary label.</summary>
        public static TextBlock Label(string text) => new TextBlock
        {
            Text = text, FontSize = 12, Margin = new Thickness(0, 4, 0, 2), TextWrapping = TextWrapping.Wrap
        }.Fg("ChromeFg2");

        /// <summary>Binds the foreground to a theme resource. SetResourceReference is the code form of DynamicResource, so the element follows theme changes.</summary>
        public static T Fg<T>(this T e, string key) where T : FrameworkElement
        {
            e.SetResourceReference(TextBlock.ForegroundProperty, key);
            return e;
        }

        /// <summary>Binds the background to a theme resource. Background has no common base, hence the type tests.</summary>
        public static T Bg<T>(this T e, string key) where T : FrameworkElement
        {
            if (e is Panel p) p.SetResourceReference(Panel.BackgroundProperty, key);
            else if (e is Border b) b.SetResourceReference(Border.BackgroundProperty, key);
            else if (e is Control c) c.SetResourceReference(Control.BackgroundProperty, key);
            return e;
        }

        /// <summary>A compact button with a click action.</summary>
        public static Button Button(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 4, 2) };
            b.Click += (s, e) => click();
            return b;
        }

        /// <summary>Wraps content in a titled pane.</summary>
        public static Grid Pane(string title, UIElement content)
        {
            var g = new Grid().Bg("PanelBg");
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(10, 8, 8, 6) }.Fg("ChromeFg");
            g.Children.Add(t);
            Grid.SetRow(content, 1);
            g.Children.Add(content);
            return g;
        }
    }
}
