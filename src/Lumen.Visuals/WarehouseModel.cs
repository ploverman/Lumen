using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Lumen.Model;

namespace Lumen.Visuals
{
    /// <summary>Maps the field wells of the warehouse visual to the columns of its query result. Every well is a group column, in <see cref="Wells"/> order, skipping empty wells.</summary>
    public sealed class WarehouseFields
    {
        /// <summary>The well names, in query order.</summary>
        public static readonly string[] Wells = { "location", "zone", "aisle", "bayRow", "bay", "level", "position", "status", "sku", "units", "age" };
        /// <summary>The wells that must be filled before anything can be laid out.</summary>
        public static readonly string[] Required = { "location", "zone", "aisle", "bay", "level", "position", "status" };

        private readonly Dictionary<string, int> _columns = new Dictionary<string, int>();

        /// <summary>Numbers the filled wells of a visual.</summary>
        public WarehouseFields(VisualDef def)
        {
            int next = 0;
            foreach (var well in Wells)
                if (def.Role(well).Count > 0) _columns[well] = next++;
        }

        /// <summary>The result column of a well, or -1 when the well is empty.</summary>
        public int this[string well]
        {
            get
            {
                int c;
                return _columns.TryGetValue(well, out c) ? c : -1;
            }
        }

        /// <summary>True when every required well is filled.</summary>
        public bool Complete => Required.All(_columns.ContainsKey);

        /// <summary>The group columns of the query, in result order.</summary>
        public static List<FieldRef> Collect(VisualDef def) => Wells.SelectMany(w => def.Role(w).Take(1)).ToList();
    }

    /// <summary>One pallet slot. <see cref="Slot"/> is the whole space, used for picking; <see cref="Box"/> is the carton drawn in it.</summary>
    public sealed class WarehouseLocation
    {
        /// <summary>Identity and address, as text from the data.</summary>
        public string Id, Zone, Aisle, Sku;
        /// <summary>Numeric address.</summary>
        public int BayRow, Bay, Level, Position;
        /// <summary>True when the status well holds the filled value.</summary>
        public bool Filled;
        /// <summary>Units and stock age. NaN when the well is empty or the cell is blank.</summary>
        public double Units = double.NaN, Age = double.NaN;
        /// <summary>World-space bounds.</summary>
        public Rect3D Slot, Box;
        /// <summary>True when the current filters keep this location.</summary>
        public bool Visible;
        /// <summary>The bay row it belongs to.</summary>
        public WarehouseBayRow Row;
        /// <summary>A stable number in [0, 1) derived from the id, for the variation between cartons.</summary>
        public double Jitter;
    }

    /// <summary>The two bays that face each other across an aisle. The floor tile between them shows this row's aggregate.</summary>
    public sealed class WarehouseBayRow
    {
        /// <summary>The aisle name.</summary>
        public string Aisle;
        /// <summary>The row number in the aisle.</summary>
        public int Number;
        /// <summary>The floor tile, a flat box.</summary>
        public Rect3D Tile;
        /// <summary>Everything in the row, for a quick reject while picking.</summary>
        public Rect3D Bounds;
        /// <summary>All locations in the row.</summary>
        public readonly List<WarehouseLocation> Locations = new List<WarehouseLocation>();
        /// <summary>Visible totals, recomputed by <see cref="WarehouseLayout.ApplyFilter"/>.</summary>
        public int Total, Filled;
        /// <summary>Sum and count of known stock ages among visible filled locations.</summary>
        public double AgeSum;
        /// <summary>See <see cref="AgeSum"/>.</summary>
        public int AgeCount;

        /// <summary>Visible locations with nothing in them.</summary>
        public int Open => Total - Filled;
        /// <summary>Filled share from 0 to 1.</summary>
        public double Fill => Total == 0 ? 0 : (double)Filled / Total;
        /// <summary>Average stock age, or NaN when no filled location has one.</summary>
        public double AverageAge => AgeCount == 0 ? double.NaN : AgeSum / AgeCount;
        /// <summary>The selection key of the row. Location keys are bare ids, so this prefix keeps the two apart.</summary>
        public string Key => "row\u001f" + Aisle + "\u001f" + Number;
    }

    /// <summary>One aisle: its center line and extent.</summary>
    public sealed class WarehouseAisle
    {
        /// <summary>Aisle and zone names.</summary>
        public string Name, Zone;
        /// <summary>Everything in the aisle.</summary>
        public Rect3D Bounds;
        /// <summary>The X of the center line.</summary>
        public double CenterX;
    }

    /// <summary>A zone with its bounds, for the floor label.</summary>
    public sealed class WarehouseZone
    {
        /// <summary>Zone name.</summary>
        public string Name;
        /// <summary>Everything in the zone.</summary>
        public Rect3D Bounds;
    }

    /// <summary>The floor plan: where every location sits, and the running totals of what the filters keep.
    /// World axes: X runs east, Y up, Z south, and aisles run along Z. Zones sit in a grid of <see cref="WarehouseFormat.ZonesAcross"/> columns.
    /// The layout is computed from the unfiltered rows, so that filtering hides locations and never moves them.</summary>
    public sealed class WarehouseLayout
    {
        private const double Flue = 0.4;         // gap between back-to-back racks
        private const double UprightGap = 0.3;   // upright width allowance per bay
        private const double TileInset = 0.08;   // bare floor between tiles

        /// <summary>All locations, in result order.</summary>
        public readonly List<WarehouseLocation> Locations = new List<WarehouseLocation>();
        /// <summary>All bay rows.</summary>
        public readonly List<WarehouseBayRow> BayRows = new List<WarehouseBayRow>();
        /// <summary>All aisles, zone by zone.</summary>
        public readonly List<WarehouseAisle> Aisles = new List<WarehouseAisle>();
        /// <summary>All zones.</summary>
        public readonly List<WarehouseZone> Zones = new List<WarehouseZone>();
        /// <summary>Distinct level values, ascending. The index is the height in levels.</summary>
        public readonly List<int> Levels = new List<int>();
        /// <summary>Everything.</summary>
        public Rect3D Bounds = Rect3D.Empty;
        /// <summary>What the filters keep, or everything when they keep nothing.</summary>
        public Rect3D VisibleBounds = Rect3D.Empty;
        /// <summary>Slots per bay along the aisle.</summary>
        public int PositionsPerBay;
        /// <summary>Bay length along the aisle, in meters.</summary>
        public double BayLength;

        private readonly Dictionary<string, WarehouseLocation> _byId = new Dictionary<string, WarehouseLocation>();
        private readonly Dictionary<string, WarehouseBayRow> _rows = new Dictionary<string, WarehouseBayRow>();
        private readonly WarehouseFormat _format;

        private WarehouseLayout(WarehouseFormat format) { _format = format; }

        /// <summary>The location with an id, or null.</summary>
        public WarehouseLocation Find(string id)
        {
            WarehouseLocation l;
            return id != null && _byId.TryGetValue(id, out l) ? l : null;
        }

        /// <summary>The bay row with a selection key, or null.</summary>
        public WarehouseBayRow FindRow(string key)
        {
            WarehouseBayRow r;
            return key != null && _rows.TryGetValue(key, out r) ? r : null;
        }

        /// <summary>Lays out the unfiltered rows. Rows repeating an id are ignored, because each id is one physical slot.</summary>
        public static WarehouseLayout Build(QueryResult rows, WarehouseFields f, WarehouseFormat format)
        {
            var layout = new WarehouseLayout(format);
            layout.ReadRows(rows, f, format);
            layout.Place(format);
            return layout;
        }

        /// <summary>Reads the result into locations.</summary>
        private void ReadRows(QueryResult r, WarehouseFields f, WarehouseFormat format)
        {
            int cId = f["location"], cZone = f["zone"], cAisle = f["aisle"], cRow = f["bayRow"], cBay = f["bay"], cLevel = f["level"],
                cPos = f["position"], cStatus = f["status"], cSku = f["sku"], cUnits = f["units"], cAge = f["age"];
            string filled = format.FilledValue ?? "";
            for (int i = 0; i < r.RowCount; i++)
            {
                string id = r.Data[cId][i].ToDisplayString();
                if (_byId.ContainsKey(id)) continue;
                int bay = (int)r.Data[cBay][i].AsDouble();
                var l = new WarehouseLocation
                {
                    Id = id,
                    Zone = r.Data[cZone][i].ToDisplayString(),
                    Aisle = r.Data[cAisle][i].ToDisplayString(),
                    Bay = bay,
                    // Without a bay row well, bays 1 and 2 face each other, then 3 and 4, and so on.
                    BayRow = cRow >= 0 ? (int)r.Data[cRow][i].AsDouble() : (bay + 1) / 2,
                    Level = (int)r.Data[cLevel][i].AsDouble(),
                    Position = (int)r.Data[cPos][i].AsDouble(),
                    Filled = string.Equals(r.Data[cStatus][i].ToDisplayString(), filled, StringComparison.OrdinalIgnoreCase),
                    Sku = cSku >= 0 && !r.Data[cSku][i].IsBlank ? r.Data[cSku][i].ToDisplayString() : null,
                    Units = cUnits >= 0 && !r.Data[cUnits][i].IsBlank ? r.Data[cUnits][i].AsDouble() : double.NaN,
                    Age = cAge >= 0 && !r.Data[cAge][i].IsBlank ? r.Data[cAge][i].AsDouble() : double.NaN,
                    Jitter = Hash(id)
                };
                _byId[id] = l;
                Locations.Add(l);
            }
        }

        /// <summary>FNV-1a of the id, scaled to [0, 1). Cartons keep their size across redraws and runs.</summary>
        private static double Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return (h % 100000) / 100000.0;
        }

        /// <summary>Assigns every location its place: zone grid, aisle offset, bay row along the aisle, side, level and position.</summary>
        private void Place(WarehouseFormat format)
        {
            if (Locations.Count == 0) return;
            Levels.AddRange(Locations.Select(l => l.Level).Distinct().OrderBy(x => x));
            var levelIndex = Levels.Select((v, i) => new { v, i }).ToDictionary(x => x.v, x => x.i);
            var positions = Locations.Select(l => l.Position).Distinct().OrderBy(x => x).ToList();
            var positionIndex = positions.Select((v, i) => new { v, i }).ToDictionary(x => x.v, x => x.i);
            PositionsPerBay = positions.Count;
            BayLength = PositionsPerBay * format.LocationWidth + UprightGap;
            double pitch = format.AisleWidth + 2 * format.LocationDepth + Flue;

            var zones = Locations.GroupBy(l => l.Zone, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            var shaped = zones.Select(z => new
            {
                Name = z.Key,
                Aisles = z.GroupBy(l => l.Aisle, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList()
            }).Select(z => new
            {
                z.Name,
                z.Aisles,
                Width = z.Aisles.Count * pitch,
                Depth = z.Aisles.Max(a => a.Select(l => l.BayRow).Distinct().Count()) * BayLength
            }).ToList();

            // Column widths and row depths of the zone grid.
            int across = Math.Max(1, format.ZonesAcross);
            int gridRows = (shaped.Count + across - 1) / across;
            var widths = new double[across];
            var depths = new double[gridRows];
            for (int i = 0; i < shaped.Count; i++)
            {
                widths[i % across] = Math.Max(widths[i % across], shaped[i].Width);
                depths[i / across] = Math.Max(depths[i / across], shaped[i].Depth);
            }

            for (int z = 0; z < shaped.Count; z++)
            {
                double zoneX = widths.Take(z % across).Sum() + (z % across) * format.ZoneGap;
                double zoneZ = depths.Take(z / across).Sum() + (z / across) * format.ZoneGap;
                var zoneBounds = Rect3D.Empty;
                for (int a = 0; a < shaped[z].Aisles.Count; a++)
                {
                    var aisle = shaped[z].Aisles[a];
                    double cx = zoneX + (a + 0.5) * pitch;
                    var rows = aisle.Select(l => l.BayRow).Distinct().OrderBy(x => x).ToList();
                    var aisleBounds = Rect3D.Empty;
                    for (int ri = 0; ri < rows.Count; ri++)
                    {
                        var row = new WarehouseBayRow
                        {
                            Aisle = aisle.Key,
                            Number = rows[ri],
                            Tile = new Rect3D(cx - format.AisleWidth / 2, 0.04, zoneZ + ri * BayLength + TileInset, format.AisleWidth, 0.02, BayLength - 2 * TileInset),
                            Bounds = Rect3D.Empty
                        };
                        var inRow = aisle.Where(l => l.BayRow == rows[ri]).ToList();
                        var bays = inRow.Select(l => l.Bay).Distinct().OrderBy(x => x).ToList();
                        foreach (var l in inRow)
                        {
                            int side = bays.IndexOf(l.Bay) % 2;
                            double slotX = side == 0 ? cx - format.AisleWidth / 2 - format.LocationDepth : cx + format.AisleWidth / 2;
                            double slotZ = zoneZ + ri * BayLength + UprightGap / 2 + positionIndex[l.Position] * format.LocationWidth;
                            double slotY = levelIndex[l.Level] * format.LevelHeight + 0.15;
                            l.Slot = new Rect3D(slotX, slotY, slotZ, format.LocationDepth, format.LevelHeight - 0.15, format.LocationWidth);
                            l.Box = CartonIn(l.Slot, l.Jitter);
                            l.Row = row;
                            row.Locations.Add(l);
                            row.Bounds = Rect3D.Union(row.Bounds, l.Slot);
                        }
                        aisleBounds = Rect3D.Union(aisleBounds, row.Bounds);
                        BayRows.Add(row);
                        _rows[row.Key] = row;
                    }
                    Aisles.Add(new WarehouseAisle { Name = aisle.Key, Zone = shaped[z].Name, Bounds = aisleBounds, CenterX = cx });
                    zoneBounds = Rect3D.Union(zoneBounds, aisleBounds);
                }
                Zones.Add(new WarehouseZone { Name = shaped[z].Name, Bounds = zoneBounds });
                Bounds = Rect3D.Union(Bounds, zoneBounds);
            }
        }

        /// <summary>A carton standing on the shelf, centered, with a size that varies by location.</summary>
        private static Rect3D CartonIn(Rect3D slot, double jitter)
        {
            double wide = 0.8 + 0.15 * jitter;
            double high = 0.5 + 0.35 * ((jitter * 7.13) % 1);
            double deep = 0.78 + 0.17 * ((jitter * 3.71) % 1);
            double dx = slot.SizeX * wide, dy = slot.SizeY * high, dz = slot.SizeZ * deep;
            return new Rect3D(slot.X + (slot.SizeX - dx) / 2, slot.Y, slot.Z + (slot.SizeZ - dz) / 2, dx, dy, dz);
        }

        /// <summary>Marks what the filtered result keeps and recomputes the bay row totals. Returns false when the visible set is unchanged, so the racks can stay as they are.</summary>
        public bool ApplyFilter(QueryResult filtered, WarehouseFields f)
        {
            var keep = new HashSet<string>();
            int cId = f["location"];
            for (int i = 0; i < filtered.RowCount; i++) keep.Add(filtered.Data[cId][i].ToDisplayString());

            bool changed = false;
            foreach (var l in Locations)
            {
                bool now = keep.Contains(l.Id);
                if (now != l.Visible) { changed = true; l.Visible = now; }
            }

            VisibleBounds = Rect3D.Empty;
            foreach (var row in BayRows)
            {
                row.Total = row.Filled = row.AgeCount = 0;
                row.AgeSum = 0;
                foreach (var l in row.Locations)
                {
                    if (!l.Visible) continue;
                    row.Total++;
                    if (!l.Filled) continue;
                    row.Filled++;
                    if (!double.IsNaN(l.Age)) { row.AgeSum += l.Age; row.AgeCount++; }
                }
                if (row.Total > 0) VisibleBounds = Rect3D.Union(VisibleBounds, row.Bounds);
            }
            if (VisibleBounds.IsEmpty) VisibleBounds = Bounds;
            return changed;
        }

        /// <summary>The nearest visible location along a ray, or else the visible floor tile under it. Rows are tested first, so a pick looks at a few hundred boxes rather than every location.</summary>
        public void Pick(Point3D origin, Vector3D direction, out WarehouseLocation location, out WarehouseBayRow tile)
        {
            location = null;
            tile = null;
            double best = double.PositiveInfinity;
            foreach (var row in BayRows)
            {
                double t;
                if (row.Total == 0 || !Hit(row.Bounds, origin, direction, out t) || t >= best) continue;
                foreach (var l in row.Locations)
                    if (l.Visible && Hit(l.Slot, origin, direction, out t) && t < best) { best = t; location = l; }
            }
            if (location != null || direction.Y >= 0) return;

            double toFloor = -origin.Y / direction.Y;
            double x = origin.X + direction.X * toFloor, z = origin.Z + direction.Z * toFloor;
            foreach (var row in BayRows)
                if (row.Total > 0 && x >= row.Tile.X && x <= row.Tile.X + row.Tile.SizeX && z >= row.Tile.Z && z <= row.Tile.Z + row.Tile.SizeZ) { tile = row; return; }
        }

        /// <summary>Slab test of a ray against a box. <paramref name="t"/> is the entry distance, in units of the direction's length.</summary>
        private static bool Hit(Rect3D b, Point3D o, Vector3D d, out double t)
        {
            double near = 0, far = double.PositiveInfinity;
            t = 0;
            if (!Slab(o.X, d.X, b.X, b.X + b.SizeX, ref near, ref far)) return false;
            if (!Slab(o.Y, d.Y, b.Y, b.Y + b.SizeY, ref near, ref far)) return false;
            if (!Slab(o.Z, d.Z, b.Z, b.Z + b.SizeZ, ref near, ref far)) return false;
            t = near;
            return true;
        }

        /// <summary>Narrows the entry and exit distances by one axis. Returns false once they cross.</summary>
        private static bool Slab(double origin, double dir, double min, double max, ref double near, ref double far)
        {
            if (Math.Abs(dir) < 1e-12) return origin >= min && origin <= max;
            double a = (min - origin) / dir, b = (max - origin) / dir;
            if (a > b) { double s = a; a = b; b = s; }
            if (a > near) near = a;
            if (b < far) far = b;
            return near <= far;
        }
    }

    /// <summary>One entry of the legend.</summary>
    public struct WarehouseSwatch
    {
        /// <summary>Swatch color.</summary>
        public Color Color;
        /// <summary>Swatch text.</summary>
        public string Text;
        /// <summary>Makes an entry.</summary>
        public WarehouseSwatch(Color color, string text) { Color = color; Text = text; }
    }

    /// <summary>Turns a location or bay row into a color for a color mode. The thresholds come from the format, so the legend and the scene cannot disagree.</summary>
    public sealed class WarehousePalette
    {
        private static readonly Color[] Age = { C("#A9CDEA"), C("#4F97D8"), C("#1F4FA3"), C("#5A1E87") };
        private static readonly Color[] OpenBand = { C("#D6EEDD"), C("#97DDB0"), C("#2FBF73"), C("#0B8A48") };
        private static readonly Color Grey = C("#D2D2D2");

        private readonly WarehouseFormat _f;
        private readonly Color[] _fill;
        private readonly Color _occupied, _occupiedDark;

        /// <summary>Reads colors and thresholds from a format.</summary>
        public WarehousePalette(WarehouseFormat f)
        {
            _f = f;
            _fill = new[]
            {
                ResolvedTheme.Parse(f.ColorUnder, C("#6FBF5F")),
                ResolvedTheme.Parse(f.ColorMiddle, C("#E8DF4A")),
                ResolvedTheme.Parse(f.ColorOver, C("#EF3A2C"))
            };
            _occupied = ResolvedTheme.Parse(f.ColorOccupied, C("#D8B27C"));
            _occupiedDark = Color.FromRgb((byte)(_occupied.R * 0.88), (byte)(_occupied.G * 0.88), (byte)(_occupied.B * 0.88));
        }

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        /// <summary>The band of a value given ascending upper limits: 0 up to and including the first limit, and so on.</summary>
        public static int Band(double value, params double[] limits)
        {
            int i = 0;
            while (i < limits.Length && value > limits[i]) i++;
            return i;
        }

        private int FillBand(double fill) => fill * 100 < _f.FillLow ? 0 : fill * 100 > _f.FillHigh ? 2 : 1;
        private int AgeBand(double days) => Band(days, _f.AgeFresh, _f.AgeMiddle, _f.AgeOld);
        private int OpenSlotBand(int open) => Band(open, _f.OpenLow - 1, _f.OpenMiddle - 1, _f.OpenHigh - 1);

        /// <summary>The color of the fill-rate band of a share from 0 to 1. The callout's level bars use it too.</summary>
        public Color FillColor(double fill) => _fill[FillBand(fill)];

        /// <summary>The floor tile color of a bay row in a mode.</summary>
        public Color Tile(WarehouseBayRow row, WarehouseColor mode)
        {
            switch (mode)
            {
                case WarehouseColor.StockAge: return double.IsNaN(row.AverageAge) ? Grey : Age[AgeBand(row.AverageAge)];
                case WarehouseColor.OpenSlots: return OpenBand[OpenSlotBand(row.Open)];
                default: return _fill[FillBand(row.Fill)];
            }
        }

        /// <summary>The carton color of a location in a mode, or null to draw nothing. Open slots are drawn only in the Open slots mode, as translucent boxes.</summary>
        public Color? Box(WarehouseLocation l, WarehouseColor mode)
        {
            switch (mode)
            {
                case WarehouseColor.StockAge:
                    if (!l.Filled) return null;
                    return double.IsNaN(l.Age) ? Grey : Age[AgeBand(l.Age)];
                case WarehouseColor.OpenSlots:
                    if (l.Filled) return Grey;
                    var c = OpenBand[OpenSlotBand(l.Row.Open)];
                    c.A = 140;
                    return c;
                default:
                    if (!l.Filled) return null;
                    return l.Jitter < 0.5 ? _occupied : _occupiedDark;
            }
        }

        /// <summary>The caption of the legend in a mode.</summary>
        public static string Caption(WarehouseColor mode)
        {
            switch (mode)
            {
                case WarehouseColor.StockAge: return "Box = its stock age. Tile = bay row average";
                case WarehouseColor.OpenSlots: return "Green box = open slot. Tile = open slots in the bay row";
                default: return "Floor tile = fill rate of one bay row";
            }
        }

        /// <summary>The swatches of the legend in a mode.</summary>
        public IList<WarehouseSwatch> Swatches(WarehouseColor mode)
        {
            switch (mode)
            {
                case WarehouseColor.StockAge:
                    return new[]
                    {
                        new WarehouseSwatch(Age[0], "0-" + _f.AgeFresh + " days"),
                        new WarehouseSwatch(Age[1], (_f.AgeFresh + 1) + "-" + _f.AgeMiddle + " days"),
                        new WarehouseSwatch(Age[2], (_f.AgeMiddle + 1) + "-" + _f.AgeOld + " days"),
                        new WarehouseSwatch(Age[3], "Over " + _f.AgeOld + " days")
                    };
                case WarehouseColor.OpenSlots:
                    return new[]
                    {
                        new WarehouseSwatch(OpenBand[0], "0-" + (_f.OpenLow - 1) + " open"),
                        new WarehouseSwatch(OpenBand[1], _f.OpenLow + "-" + (_f.OpenMiddle - 1) + " open"),
                        new WarehouseSwatch(OpenBand[2], _f.OpenMiddle + "-" + (_f.OpenHigh - 1) + " open"),
                        new WarehouseSwatch(OpenBand[3], _f.OpenHigh + "+ open")
                    };
                default:
                    return new[]
                    {
                        new WarehouseSwatch(_fill[0], "under " + _f.FillLow + "%"),
                        new WarehouseSwatch(_fill[1], _f.FillLow + " to " + _f.FillHigh + "%"),
                        new WarehouseSwatch(_fill[2], "over " + _f.FillHigh + "%"),
                        new WarehouseSwatch(_occupied, "occupied")
                    };
            }
        }
    }
}
