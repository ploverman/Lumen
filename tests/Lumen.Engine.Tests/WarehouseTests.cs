using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Media3D;
using Lumen.Connectors;
using Lumen.Model;
using Lumen.Visuals;
using Xunit;

namespace Lumen.Engine.Tests
{
    /// <summary>The warehouse sample, floor plan, picking and camera math. No window is needed: all of this is plain computation.</summary>
    public class WarehouseTests
    {
        private static readonly Lazy<Fixture> F = new Lazy<Fixture>(() => new Fixture());

        /// <summary>The sample run through the engine, as the visual would query it.</summary>
        private sealed class Fixture
        {
            public readonly SemanticModel Model;
            public readonly InProcessEngine Engine = new InProcessEngine();
            public readonly VisualDef Def = new VisualDef { Type = "warehouse3d" };
            public readonly WarehouseFields Fields;
            public readonly QueryResult All;

            public Fixture()
            {
                var ds = WarehouseSample.BuildDataset(out Model);
                Engine.Load(ds);
                var wells = new Dictionary<string, string>
                {
                    { "location", "Location ID" }, { "zone", "Zone" }, { "aisle", "Aisle" }, { "bayRow", "Bay Row" }, { "bay", "Bay" }, { "level", "Level" },
                    { "position", "Position" }, { "status", "Status" }, { "sku", "SKU" }, { "units", "Units" }, { "age", "Stock Age Days" }
                };
                foreach (var w in wells) Def.Roles[w.Key] = new List<FieldRef> { Col(w.Value) };
                Fields = new WarehouseFields(Def);
                All = Query();
            }

            public FieldRef Col(string name)
            {
                var t = Model.TableByName("Locations");
                return FieldRef.ForColumn(t.Id, t.ColumnByName(name).Id);
            }

            public QueryResult Query(params FilterDef[] filters)
            {
                var spec = new QuerySpec { Limit = 150000 };
                spec.GroupBy.AddRange(WarehouseFields.Collect(Def));
                spec.Filters.AddRange(filters);
                return Engine.Execute(spec, CancellationToken.None);
            }

            public WarehouseLayout Layout(params FilterDef[] filters)
            {
                var layout = WarehouseLayout.Build(All, Fields, new WarehouseFormat());
                layout.ApplyFilter(Query(filters), Fields);
                return layout;
            }
        }

        [Fact]
        public void SampleHasTheAdvertisedShape()
        {
            var f = F.Value;
            Assert.Null(f.All.Error);
            Assert.Equal(14000, f.All.RowCount);
            var w = f.Layout();
            Assert.Equal(14000, w.Locations.Count);
            Assert.Equal(250, w.BayRows.Count);
            Assert.Equal(25, w.Aisles.Count);
            Assert.Equal(4, w.Zones.Count);
            Assert.All(w.BayRows, r => Assert.Equal(56, r.Locations.Count));
        }

        [Fact]
        public void MeasuresAgreeWithAPlainCount()
        {
            var f = F.Value;
            var w = f.Layout();
            var spec = new QuerySpec();
            foreach (var name in new[] { "Total Locations", "Filled", "Open Slots", "Rows Over 70%" })
                spec.Measures.Add(FieldRef.ForMeasure(f.Model.MeasureByName(name).Id));
            var r = f.Engine.Execute(spec, CancellationToken.None);
            Assert.Null(r.Error);
            Assert.Equal(14000, r.Measure(0, 0).AsDouble());
            Assert.Equal(w.Locations.Count(l => l.Filled), r.Measure(1, 0).AsDouble());
            Assert.Equal(w.Locations.Count(l => !l.Filled), r.Measure(2, 0).AsDouble());
            Assert.Equal(w.BayRows.Count(x => x.Fill > 0.7), r.Measure(3, 0).AsDouble());
        }

        [Fact]
        public void SlotsNeverOverlap()
        {
            var w = F.Value.Layout();
            foreach (var row in w.BayRows)
                for (int i = 0; i < row.Locations.Count; i++)
                    for (int j = i + 1; j < row.Locations.Count; j++)
                        Assert.False(Rect3D.Intersect(row.Locations[i].Slot, row.Locations[j].Slot).Volume() > 1e-9, row.Locations[i].Id + " overlaps " + row.Locations[j].Id);
        }

        [Fact]
        public void FacingBaysSitOnOppositeSidesOfTheAisle()
        {
            var w = F.Value.Layout();
            var aisle = w.Aisles.First(a => a.Name == "B-03");
            Assert.True(w.Find("B-03-19-20-1").Slot.X < aisle.CenterX);
            Assert.True(w.Find("B-03-20-20-1").Slot.X > aisle.CenterX);
        }

        [Fact]
        public void FilteringHidesLocationsWithoutMovingThem()
        {
            var f = F.Value;
            var whole = f.Layout();
            var one = f.Layout(FilterDef.Basic(f.Col("Level"), new object[] { 60L }));
            Assert.Equal(2000, one.Locations.Count(l => l.Visible));
            Assert.All(one.Locations.Where(l => l.Visible), l => Assert.Equal(whole.Find(l.Id).Slot, l.Slot));
            Assert.Equal(whole.Bounds, one.Bounds);
        }

        [Fact]
        public void ApplyFilterReportsWhetherTheVisibleSetChanged()
        {
            var f = F.Value;
            var w = WarehouseLayout.Build(f.All, f.Fields, new WarehouseFormat());
            Assert.True(w.ApplyFilter(f.All, f.Fields));
            Assert.False(w.ApplyFilter(f.All, f.Fields));
            Assert.True(w.ApplyFilter(f.Query(FilterDef.Basic(f.Col("Zone"), new object[] { "Zone A" })), f.Fields));
            Assert.Equal(new[] { 3360 }, new[] { w.Locations.Count(l => l.Visible) });
        }

        [Fact]
        public void RayDownOntoASlotPicksIt()
        {
            var w = F.Value.Layout();
            var target = w.Find("C-02-07-60-3");
            var center = new Point3D(target.Slot.X + target.Slot.SizeX / 2, 100, target.Slot.Z + target.Slot.SizeZ / 2);
            WarehouseLocation hit;
            WarehouseBayRow tile;
            w.Pick(center, new Vector3D(0, -1, 0), out hit, out tile);
            Assert.Equal(target.Id, hit?.Id);
            Assert.Null(tile);
        }

        [Fact]
        public void RayIntoAnAisleLandsOnItsTile()
        {
            var w = F.Value.Layout();
            var row = w.BayRows[40];
            var above = new Point3D(row.Tile.X + row.Tile.SizeX / 2, 100, row.Tile.Z + row.Tile.SizeZ / 2);
            WarehouseLocation hit;
            WarehouseBayRow tile;
            w.Pick(above, new Vector3D(0, -1, 0), out hit, out tile);
            Assert.Null(hit);
            Assert.Same(row, tile);
        }

        [Fact]
        public void RayIgnoresFilteredLocations()
        {
            var f = F.Value;
            var w = f.Layout(FilterDef.Basic(f.Col("Level"), new object[] { 0L }));
            var top = w.Find("C-02-07-60-3");
            var above = new Point3D(top.Slot.X + top.Slot.SizeX / 2, 100, top.Slot.Z + top.Slot.SizeZ / 2);
            WarehouseLocation hit;
            WarehouseBayRow tile;
            w.Pick(above, new Vector3D(0, -1, 0), out hit, out tile);
            Assert.Equal(0, hit.Level);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(30, 0)]
        [InlineData(31, 1)]
        [InlineData(90, 1)]
        [InlineData(91, 2)]
        [InlineData(180, 2)]
        [InlineData(181, 3)]
        public void AgeBandsIncludeTheirUpperLimit(double days, int band) =>
            Assert.Equal(band, WarehousePalette.Band(days, 30, 90, 180));

        [Fact]
        public void CameraRayThroughTheCenterIsTheViewDirection()
        {
            var cam = new WarehouseCamera { Target = new Point3D(10, 2, 20), Yaw = 0.7, Pitch = 0.5, Distance = 30, Fov = 40 };
            Point3D origin;
            Vector3D direction;
            cam.Ray(new Point(400, 300), new Size(800, 600), out origin, out direction);
            Assert.Equal(0, (origin - cam.Position).Length, 9);
            Assert.Equal(0, (direction - cam.Forward).Length, 9);
            Assert.Equal(30, (cam.Target - cam.Position).Length, 9);
            Assert.Equal(0, Vector3D.DotProduct(cam.Forward, cam.Up), 9);
        }

        [Fact]
        public void CameraRayMatchesTheFieldOfViewAtTheEdge()
        {
            var cam = new WarehouseCamera { Target = new Point3D(0, 0, 0), Yaw = 0, Pitch = 0, Distance = 10, Fov = 90 };
            Point3D origin;
            Vector3D direction;
            cam.Ray(new Point(800, 300), new Size(800, 600), out origin, out direction);
            direction.Normalize();
            Assert.Equal(45, Vector3D.AngleBetween(cam.Forward, direction), 6);
        }

        [Fact]
        public void FlightStartsAndEndsOnItsCamerasAndTurnsTheShortWay()
        {
            var a = new WarehouseCamera { Target = new Point3D(0, 0, 0), Yaw = 3.0, Pitch = 0.2, Distance = 100, Fov = 40 };
            var b = new WarehouseCamera { Target = new Point3D(10, 0, 0), Yaw = -3.0, Pitch = 1.2, Distance = 10, Fov = 70 };
            Assert.Equal(100, WarehouseCamera.Lerp(a, b, 0).Distance, 9);
            Assert.Equal(10, WarehouseCamera.Lerp(a, b, 1).Distance, 9);
            Assert.Equal(Math.Sqrt(1000), WarehouseCamera.Lerp(a, b, 0.5).Distance, 9);
            // From 3.0 to -3.0 is 0.28 radians through pi, not 6 back through zero.
            Assert.True(WarehouseCamera.Lerp(a, b, 0.5).Yaw > 3.0);
        }
    }

    internal static class Rect3DExtensions
    {
        /// <summary>Volume, or zero for an empty box.</summary>
        public static double Volume(this Rect3D r) => r.IsEmpty ? 0 : r.SizeX * r.SizeY * r.SizeZ;
    }
}
