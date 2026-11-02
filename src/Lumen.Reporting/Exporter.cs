using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumen.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Lumen.Reporting
{
    /// <summary>Turns the page into images, PDF, CSV or print output.</summary>
    public static class Exporter
    {
        /// <summary>Renders an element through a VisualBrush, so the element's position and the canvas zoom do not affect the image. 96 * scale makes it sharp at higher scales.</summary>
        public static BitmapSource Render(Visual visual, double width, double height, double scale)
        {
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Encodes a PNG.</summary>
        public static byte[] Png(BitmapSource image)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Writes one PDF page per image. Pages are pictures, so text in the PDF cannot be selected. 0.375 points per pixel maps a 2x render back to about page size.</summary>
        public static void SavePdf(string path, IList<BitmapSource> pages)
        {
            using (var doc = new PdfDocument())
            {
                foreach (var image in pages)
                {
                    using (var ms = new MemoryStream(Png(image)))
                    using (var x = XImage.FromStream(ms))
                    {
                        var page = doc.AddPage();
                        page.Width = XUnit.FromPoint(image.PixelWidth * 0.375);
                        page.Height = XUnit.FromPoint(image.PixelHeight * 0.375);
                        using (var g = XGraphics.FromPdfPage(page))
                            g.DrawImage(x, 0, 0, page.Width.Point, page.Height.Point);
                    }
                }
                doc.Save(path);
            }
        }

        /// <summary>Writes a result as RFC 4180-style CSV. Values use display strings, not the column's format.</summary>
        public static string ToCsv(QueryResult r)
        {
            var sb = new StringBuilder();
            Func<string, string> quote = s => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            sb.AppendLine(string.Join(",", r.Columns.Select(c => quote(c.Name))));
            for (int i = 0; i < r.RowCount; i++)
                sb.AppendLine(string.Join(",", r.Data.Select(col => col[i].IsBlank ? "" : quote(col[i].ToDisplayString()))));
            return sb.ToString();
        }

        /// <summary>Prints the page scaled to fit.</summary>
        public static void Print(Canvas page, string title)
        {
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            double scale = Math.Min(dlg.PrintableAreaWidth / page.Width, dlg.PrintableAreaHeight / page.Height);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(page) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, page.Width, page.Height));
            }
            dlg.PrintVisual(dv, title);
        }
    }
}
