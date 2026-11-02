using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Lumen.Visuals
{
    /// <summary>Collects boxes and quads of one material into meshes. One mesh per material is what keeps thousands of boxes cheap:
    /// WPF draws a mesh in one call, and the camera can move without touching the geometry.
    /// Meshes are capped below 16-bit index range, which is the most every driver is sure to take.</summary>
    internal sealed class MeshBatch
    {
        private const int MaxVertices = 60000;
        private readonly Material _material;
        private readonly List<GeometryModel3D> _done = new List<GeometryModel3D>();
        private Point3DCollection _p;
        private Vector3DCollection _n;
        private Int32Collection _t;

        /// <summary>Starts a batch of one solid color.</summary>
        public MeshBatch(Color color)
        {
            var m = new DiffuseMaterial(new SolidColorBrush(color));
            m.Freeze();
            _material = m;
        }

        /// <summary>Adds a quad. The corners run counterclockwise seen from outside, which is the side WPF draws.</summary>
        public void Quad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
        {
            if (_p == null || _p.Count + 4 > MaxVertices) Flush();
            int i = _p.Count;
            _p.Add(a); _p.Add(b); _p.Add(c); _p.Add(d);
            _n.Add(normal); _n.Add(normal); _n.Add(normal); _n.Add(normal);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        /// <summary>Adds a box without its bottom face, which nobody ever sees.</summary>
        public void Box(Rect3D r)
        {
            double x0 = r.X, x1 = r.X + r.SizeX, y0 = r.Y, y1 = r.Y + r.SizeY, z0 = r.Z, z1 = r.Z + r.SizeZ;
            Quad(new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0), new Vector3D(0, 1, 0));
            Quad(new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1), new Vector3D(1, 0, 0));
            Quad(new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0), new Vector3D(-1, 0, 0));
            Quad(new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1), new Vector3D(0, 0, 1));
            Quad(new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0), new Vector3D(0, 0, -1));
        }

        /// <summary>Closes the current mesh and starts another.</summary>
        private void Flush()
        {
            if (_p != null && _p.Count > 0)
            {
                _p.Freeze(); _n.Freeze(); _t.Freeze();
                var mesh = new MeshGeometry3D { Positions = _p, Normals = _n, TriangleIndices = _t };
                mesh.Freeze();
                var model = new GeometryModel3D(mesh, _material);
                model.Freeze();
                _done.Add(model);
            }
            _p = new Point3DCollection(MaxVertices / 8);
            _n = new Vector3DCollection(MaxVertices / 8);
            _t = new Int32Collection(MaxVertices / 4);
        }

        /// <summary>Adds the finished meshes to a group.</summary>
        public void AddTo(Model3DGroup group)
        {
            Flush();
            foreach (var m in _done) group.Children.Add(m);
        }
    }

    /// <summary>Builds the frozen models of the warehouse from a layout. The visual swaps them whole: the structure changes with the filters, the heat also with the color mode.</summary>
    internal static class WarehouseScene
    {
        private const double UprightSize = 0.14;
        private const double BeamHigh = 0.13, BeamDeep = 0.08;
        private const double FloorMargin = 400, GridStep = 5, GridMargin = 40;

        /// <summary>The lights. One soft key from the northwest and a fill from the southeast keep the box faces apart, since there are no shadows.</summary>
        public static Model3DGroup Lights()
        {
            var g = new Model3DGroup();
            g.Children.Add(new AmbientLight(Color.FromRgb(0x98, 0x98, 0x98)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(0x90, 0x90, 0x90), new Vector3D(0.45, -1, 0.35)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(0x48, 0x48, 0x48), new Vector3D(-0.6, -0.3, -0.7)));
            g.Freeze();
            return g;
        }

        /// <summary>The floor around what is visible, its grid, and the racks of every bay with a visible location.</summary>
        public static Model3DGroup Structure(WarehouseLayout w, WarehouseFormat f, ResolvedTheme theme)
        {
            var g = new Model3DGroup();
            var b = w.VisibleBounds;
            double x0 = b.X - FloorMargin, x1 = b.X + b.SizeX + FloorMargin, z0 = b.Z - FloorMargin, z1 = b.Z + b.SizeZ + FloorMargin;

            double gx0 = b.X - GridMargin, gx1 = b.X + b.SizeX + GridMargin, gz0 = b.Z - GridMargin, gz1 = b.Z + b.SizeZ + GridMargin;

            bool dark = theme.VisualBackground.R + theme.VisualBackground.G + theme.VisualBackground.B < 384;
            var floor = new MeshBatch(dark ? Blend(theme.VisualBackground, Colors.White, 0.08) : Color.FromRgb(0xF4, 0xF6, 0xF7));
            floor.Quad(new Point3D(x0, 0, z1), new Point3D(x1, 0, z1), new Point3D(x1, 0, z0), new Point3D(x0, 0, z0), new Vector3D(0, 1, 0));
            floor.AddTo(g);

            var grid = new MeshBatch(dark ? Blend(theme.VisualBackground, Colors.White, 0.16) : Color.FromRgb(0xE3, 0xE8, 0xEB));
            for (double x = Math.Ceiling(gx0 / GridStep) * GridStep; x <= gx1; x += GridStep)
                grid.Quad(new Point3D(x - 0.03, 0.01, gz1), new Point3D(x + 0.03, 0.01, gz1), new Point3D(x + 0.03, 0.01, gz0), new Point3D(x - 0.03, 0.01, gz0), new Vector3D(0, 1, 0));
            for (double z = Math.Ceiling(gz0 / GridStep) * GridStep; z <= gz1; z += GridStep)
                grid.Quad(new Point3D(gx0, 0.01, z + 0.03), new Point3D(gx1, 0.01, z + 0.03), new Point3D(gx1, 0.01, z - 0.03), new Point3D(gx0, 0.01, z - 0.03), new Vector3D(0, 1, 0));
            grid.AddTo(g);

            var uprights = new MeshBatch(ResolvedTheme.Parse(f.ColorUpright, Color.FromRgb(0x24, 0x4F, 0xB0)));
            var beams = new MeshBatch(ResolvedTheme.Parse(f.ColorBeam, Color.FromRgb(0xE0, 0x7B, 0x2E)));
            double height = w.Levels.Count * f.LevelHeight + 0.2;

            // A rack face is the bay row's locations on one side of the aisle; X separates the sides.
            foreach (var row in w.BayRows)
            {
                if (row.Total == 0) continue;
                foreach (var side in row.Locations.GroupBy(l => l.Slot.X))
                {
                    var r = Rect3D.Empty;
                    foreach (var l in side) r = Rect3D.Union(r, l.Slot);
                    double zStart = r.Z - 0.15, zEnd = r.Z + r.SizeZ + 0.15;
                    foreach (double x in new[] { r.X, r.X + r.SizeX - UprightSize })
                        foreach (double z in new[] { zStart, zEnd - UprightSize })
                            uprights.Box(new Rect3D(x, 0, z, UprightSize, height, UprightSize));
                    for (int level = 0; level < w.Levels.Count; level++)
                    {
                        double y = level * f.LevelHeight + 0.15 - BeamHigh;
                        foreach (double x in new[] { r.X, r.X + r.SizeX - BeamDeep })
                            beams.Box(new Rect3D(x, y, zStart, BeamDeep, BeamHigh, zEnd - zStart));
                    }
                }
            }
            uprights.AddTo(g);
            beams.AddTo(g);
            g.Freeze();
            return g;
        }

        /// <summary>The floor tiles and cartons for a color mode. Translucent boxes go last: WPF does not sort, and whatever is behind them must already be drawn.</summary>
        public static Model3DGroup Heat(WarehouseLayout w, WarehousePalette palette, WarehouseColor mode)
        {
            var g = new Model3DGroup();
            var batches = new Dictionary<Color, MeshBatch>();
            Func<Color, MeshBatch> batch = c =>
            {
                MeshBatch b;
                if (!batches.TryGetValue(c, out b)) batches[c] = b = new MeshBatch(c);
                return b;
            };

            foreach (var row in w.BayRows)
            {
                if (row.Total == 0) continue;
                batch(palette.Tile(row, mode)).Box(row.Tile);
                foreach (var l in row.Locations)
                {
                    if (!l.Visible) continue;
                    var c = palette.Box(l, mode);
                    if (c != null) batch(c.Value).Box(l.Box);
                }
            }
            foreach (var kv in batches.OrderByDescending(kv => kv.Key.A)) kv.Value.AddTo(g);
            g.Freeze();
            return g;
        }

        /// <summary>A translucent shell over the selected things.</summary>
        public static Model3DGroup Highlight(IEnumerable<Rect3D> boxes, Color color)
        {
            var g = new Model3DGroup();
            var c = color;
            c.A = 170;
            var batch = new MeshBatch(c);
            foreach (var r in boxes) batch.Box(new Rect3D(r.X - 0.06, r.Y - 0.03, r.Z - 0.06, r.SizeX + 0.12, r.SizeY + 0.1, r.SizeZ + 0.12));
            batch.AddTo(g);
            g.Freeze();
            return g;
        }

        /// <summary>Zone names floating above the racks and aisle numbers on the floor at each end of the aisle. Drawn last, because a texture's clear pixels still write depth.</summary>
        public static Model3DGroup Labels(WarehouseLayout w, WarehouseFormat f, ResolvedTheme theme)
        {
            var g = new Model3DGroup();
            var textures = new Dictionary<string, ImageBrush>();
            double top = w.Levels.Count * f.LevelHeight + 0.5;

            foreach (var zone in w.Zones)
            {
                var visible = w.BayRows.Where(r => r.Total > 0 && w.Aisles.First(a => a.Name == r.Aisle).Zone == zone.Name).ToList();
                if (visible.Count == 0) continue;
                var b = Rect3D.Empty;
                foreach (var r in visible) b = Rect3D.Union(b, r.Bounds);
                var brush = Texture(textures, zone.Name.ToUpperInvariant(), theme.Foreground);
                double aspect = brush.ImageSource.Width / brush.ImageSource.Height;
                double wide = Math.Min(b.SizeX, b.SizeZ) * 0.72;
                g.Children.Add(FlatQuad(b.X + b.SizeX / 2, top, b.Z + b.SizeZ / 2, wide, wide / aspect, brush));
            }

            foreach (var aisle in w.Aisles)
            {
                if (!w.BayRows.Any(r => r.Aisle == aisle.Name && r.Total > 0)) continue;
                string number = aisle.Name.Substring(aisle.Name.LastIndexOf('-') + 1);
                var brush = Texture(textures, number, theme.Foreground);
                double aspect = brush.ImageSource.Width / brush.ImageSource.Height;
                double high = f.AisleWidth * 0.5, wide = high * aspect;
                g.Children.Add(FlatQuad(aisle.CenterX, 0.07, aisle.Bounds.Z + aisle.Bounds.SizeZ + high, wide, high, brush));
                g.Children.Add(FlatQuad(aisle.CenterX, 0.07, aisle.Bounds.Z - high, wide, high, brush));
            }
            g.Freeze();
            return g;
        }

        /// <summary>A flat textured rectangle facing up, reading north.</summary>
        private static GeometryModel3D FlatQuad(double cx, double y, double cz, double wide, double deep, Brush brush)
        {
            double x0 = cx - wide / 2, x1 = cx + wide / 2, z0 = cz - deep / 2, z1 = cz + deep / 2;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { new Point3D(x0, y, z1), new Point3D(x1, y, z1), new Point3D(x1, y, z0), new Point3D(x0, y, z0) },
                Normals = new Vector3DCollection { new Vector3D(0, 1, 0), new Vector3D(0, 1, 0), new Vector3D(0, 1, 0), new Vector3D(0, 1, 0) },
                TextureCoordinates = new PointCollection { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
            };
            mesh.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            var model = new GeometryModel3D(mesh, material);
            model.Freeze();
            return model;
        }

        /// <summary>Renders text to a transparent bitmap brush, once per text.</summary>
        private static ImageBrush Texture(Dictionary<string, ImageBrush> cache, string text, Color color)
        {
            ImageBrush brush;
            if (cache.TryGetValue(text, out brush)) return brush;
            var format = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Black"), 96, new SolidColorBrush(color), 1);
            int w = (int)Math.Ceiling(format.Width) + 8, h = (int)Math.Ceiling(format.Height);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawText(format, new Point(4, 0));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            brush = new ImageBrush(bitmap);
            brush.Freeze();
            cache[text] = brush;
            return brush;
        }

        /// <summary>Mixes two colors. <paramref name="t"/> runs from all of <paramref name="a"/> to all of <paramref name="b"/>.</summary>
        private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}
