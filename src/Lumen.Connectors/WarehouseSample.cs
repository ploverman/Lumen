using System;
using System.Collections.Generic;
using Lumen.Engine;
using Lumen.Model;

namespace Lumen.Connectors
{
    /// <summary>A deterministic distribution center: four zones, 25 aisles and 14,000 pallet locations, with one table in a single row per location.
    /// Each aisle has 20 bays, 7 levels and 4 positions per bay. Bays face each other in pairs across the aisle, and each pair is a bay row of 56 locations.</summary>
    public static class WarehouseSample
    {
        private static readonly string[] Zones = { "Zone A", "Zone B", "Zone C", "Zone D" };
        private static readonly int[] AislesPerZone = { 6, 6, 7, 6 };
        private static readonly double[] ZoneFill = { 0.45, 0.70, 0.13, 0.71 };
        private static readonly int[] Levels = { 0, 10, 20, 30, 40, 50, 60 };
        private static readonly double[] LevelEffect = { 0.26, 0.24, 0.12, 0.02, -0.08, -0.16, -0.24 };
        private const int Bays = 20, Positions = 4;

        /// <summary>Generates the table. The fixed seed makes every run identical.</summary>
        public static ColumnBatch Locations()
        {
            var rnd = new Random(2026);
            var id = new DictBuilder();
            var zone = new DictBuilder();
            var aisle = new DictBuilder();
            var rowKey = new DictBuilder();
            var bayRow = new Int64Builder(14000);
            var bay = new Int64Builder(14000);
            var level = new Int64Builder(14000);
            var position = new Int64Builder(14000);
            var status = new DictBuilder();
            var sku = new DictBuilder();
            var units = new Int64Builder(14000);
            var age = new Int64Builder(14000);
            var band = new DictBuilder();
            var rowKeys = new List<string>(14000);
            var rowFilled = new Dictionary<string, int>();

            for (int z = 0; z < Zones.Length; z++)
            {
                char letter = Zones[z][5];
                for (int a = 1; a <= AislesPerZone[z]; a++)
                {
                    string aisleName = letter + "-" + a.ToString("00");
                    double aisleFill = ZoneFill[z] + (rnd.NextDouble() - 0.5) * 0.14;
                    double aisleAge = 60 + rnd.NextDouble() * 90;
                    // Each bay row gets its own lean, so the floor tiles are not one flat color.
                    var rowFill = new double[Bays / 2 + 1];
                    for (int r = 1; r <= Bays / 2; r++) rowFill[r] = aisleFill + (rnd.NextDouble() - 0.5) * 0.3;

                    for (int b = 1; b <= Bays; b++)
                    {
                        int row = (b + 1) / 2;
                        for (int l = 0; l < Levels.Length; l++)
                        {
                            for (int p = 1; p <= Positions; p++)
                            {
                                bool filled = rnd.NextDouble() < rowFill[row] + LevelEffect[l];
                                int days = (int)(aisleAge * -Math.Log(1 - rnd.NextDouble() * 0.98));
                                id.Add(aisleName + "-" + b.ToString("00") + "-" + Levels[l].ToString("00") + "-" + p);
                                zone.Add(Zones[z]);
                                aisle.Add(aisleName);
                                string key = aisleName + "-" + row.ToString("00");
                                rowKey.Add(key);
                                rowKeys.Add(key);
                                int soFar;
                                rowFilled.TryGetValue(key, out soFar);
                                rowFilled[key] = soFar + (filled ? 1 : 0);
                                bayRow.Add(row);
                                bay.Add(b);
                                level.Add(Levels[l]);
                                position.Add(p);
                                status.Add(filled ? "Filled" : "Open");
                                if (filled)
                                {
                                    sku.Add("SKU-" + rnd.Next(10000, 11000));
                                    units.Add(rnd.Next(12, 121));
                                    age.Add(days);
                                    band.Add(days <= 30 ? "0-30 days" : days <= 90 ? "31-90 days" : days <= 180 ? "91-180 days" : "Over 180 days");
                                }
                                else
                                {
                                    sku.Add(null);
                                    units.AddNull();
                                    age.AddNull();
                                    band.Add("Empty");
                                }
                            }
                        }
                    }
                }
            }

            // LQL has no context transition, so a per-row threshold is a stored column rather than a measure.
            var rowBand = new DictBuilder();
            foreach (var key in rowKeys) rowBand.Add(rowFilled[key] / 56.0 > 0.7 ? "Over 70%" : "70% or less");

            var batch = new ColumnBatch();
            batch.Columns.Add(id.Finish("Location ID"));
            batch.Columns.Add(zone.Finish("Zone"));
            batch.Columns.Add(aisle.Finish("Aisle"));
            batch.Columns.Add(rowKey.Finish("Bay Row Key"));
            batch.Columns.Add(rowBand.Finish("Bay Row Band"));
            batch.Columns.Add(bayRow.Finish("Bay Row"));
            batch.Columns.Add(bay.Finish("Bay"));
            batch.Columns.Add(level.Finish("Level"));
            batch.Columns.Add(position.Finish("Position"));
            batch.Columns.Add(status.Finish("Status"));
            batch.Columns.Add(sku.Finish("SKU"));
            batch.Columns.Add(units.Finish("Units"));
            batch.Columns.Add(age.Finish("Stock Age Days"));
            batch.Columns.Add(band.Finish("Stock Age Band"));
            return batch;
        }

        /// <summary>Builds the one-table model and its measures.</summary>
        public static Dataset BuildDataset(out SemanticModel model)
        {
            model = new SemanticModel();
            var def = new TableDef { Id = "locations", Name = "Locations", Source = new DataSourceRef { Kind = "sample", Query = "Locations" } };
            var data = new List<TableData> { TableLoader.Bind(def, Locations()) };
            var measures = new List<MeasureDef>();
            model.Tables.Add(def);
            def.DiagramX = 60;
            def.DiagramY = 60;
            foreach (var column in new[] { "Bay Row", "Bay", "Level", "Position" }) def.ColumnByName(column).SummarizeBy = SummarizeBy.None;
            def.ColumnByName("Bay Row Key").IsHidden = true;
            def.ColumnByName("Bay Row Band").IsHidden = true;

            Action<string, string, string> measure = (name, expr, fmt) => measures.Add(new MeasureDef
            {
                Id = "m-" + name.ToLowerInvariant().Replace(' ', '-').Replace("%", "pct"),
                Name = name, HomeTableId = def.Id, Expression = expr, FormatString = fmt
            });
            measure("Total Locations", "COUNTROWS(Locations)", "#,0");
            measure("Filled", "CALCULATE(COUNTROWS(Locations), Locations[Status] = \"Filled\")", "#,0");
            measure("Open Slots", "CALCULATE(COUNTROWS(Locations), Locations[Status] = \"Open\")", "#,0");
            measure("Fill Rate", "DIVIDE([Filled], [Total Locations])", "0%");
            measure("Avg Stock Age", "AVERAGE(Locations[Stock Age Days])", "0");
            measure("Rows Over 70%", "CALCULATE(DISTINCTCOUNT(Locations[Bay Row Key]), Locations[Bay Row Band] = \"Over 70%\")", "#,0");
            model.Measures.AddRange(measures);
            return new Dataset(model, data);
        }
    }
}
