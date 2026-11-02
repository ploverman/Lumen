using System;
using System.Collections.Generic;
using System.Linq;
using Lumen.Engine;
using Lumen.Model;

namespace Lumen.Connectors
{
    /// <summary>A small deterministic star schema: Sales, Product, Date.</summary>
    public static class SampleData
    {
        private static readonly string[] Regions = { "North", "South", "East", "West" };
        private static readonly string[] Colors = { "Red", "Black", "Silver", "Blue" };
        private static readonly string[] Categories = { "Bikes", "Components", "Clothing", "Accessories" };
        private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        private const int ProductCount = 40;
        private static readonly DateTime FirstDate = new DateTime(2024, 1, 1);
        // Sales stop here while the date table runs to year end, so the sample has a current-year YTD with an empty tail.
        private static readonly DateTime LastDate = new DateTime(2026, 9, 30);

        /// <summary>Generates one sample table by name.</summary>
        public static ColumnBatch Batch(string table, int salesRows)
        {
            switch (table)
            {
                case "Product": return Product();
                case "Date": return Dates();
                case "Locations": return WarehouseSample.Locations();
                default: return Sales(salesRows);
            }
        }

        /// <summary>Generates the fact table. The fixed seed makes every run identical, which the tests rely on.</summary>
        private static ColumnBatch Sales(int rows)
        {
            var rnd = new Random(42);
            int days = (LastDate - FirstDate).Days + 1;
            var date = new DateBuilder(rows);
            var key = new Int64Builder(rows);
            var region = new DictBuilder();
            var qty = new Int64Builder(rows);
            var price = new DoubleBuilder(rows);
            var cost = new DoubleBuilder(rows);
            for (int r = 0; r < rows; r++)
            {
                int product = rnd.Next(1, ProductCount + 1);
                double basePrice = BasePrice(product);
                date.Add(FirstDate.AddDays(rnd.Next(days)).Ticks);
                key.Add(product);
                region.Add(Regions[rnd.Next(Regions.Length)]);
                qty.Add(rnd.Next(1, 6));
                double unit = Math.Round(basePrice * (0.9 + rnd.NextDouble() * 0.2), 2);
                price.Add(unit);
                cost.Add(Math.Round(unit * (0.48 + product % 9 * 0.04), 2));
            }
            var b = new ColumnBatch();
            b.Columns.Add(date.Finish("OrderDate"));
            b.Columns.Add(key.Finish("ProductKey"));
            b.Columns.Add(region.Finish("Region"));
            b.Columns.Add(qty.Finish("Qty"));
            b.Columns.Add(price.Finish("UnitPrice"));
            b.Columns.Add(cost.Finish("UnitCost"));
            return b;
        }

        /// <summary>List price by product. Categories differ widely, so that charts have some shape.</summary>
        private static double BasePrice(int product)
        {
            switch ((product - 1) % Categories.Length)
            {
                case 0: return 650 + product * 25;
                case 1: return 120 + product * 6;
                case 2: return 35 + product;
                default: return 12 + product * 0.5;
            }
        }

        /// <summary>Generates the product dimension.</summary>
        private static ColumnBatch Product()
        {
            var key = new Int64Builder(ProductCount);
            var name = new DictBuilder();
            var cat = new DictBuilder();
            var color = new DictBuilder();
            for (int p = 1; p <= ProductCount; p++)
            {
                key.Add(p);
                string category = Categories[(p - 1) % Categories.Length];
                name.Add(category.TrimEnd('s') + " " + p.ToString("00"));
                cat.Add(category);
                // Multiplicative hash, so colors do not line up with the category cycle.
                color.Add(Colors[(int)(((uint)p * 2654435761u) >> 16) % Colors.Length]);
            }
            var b = new ColumnBatch();
            b.Columns.Add(key.Finish("ProductKey"));
            b.Columns.Add(name.Finish("Product"));
            b.Columns.Add(cat.Finish("Category"));
            b.Columns.Add(color.Finish("Color"));
            return b;
        }

        /// <summary>Generates the date dimension.</summary>
        private static ColumnBatch Dates()
        {
            var date = new DateBuilder(1000);
            var year = new Int64Builder(1000);
            var quarter = new DictBuilder();
            var monthNo = new Int64Builder(1000);
            var month = new DictBuilder();
            for (var d = FirstDate; d <= new DateTime(2026, 12, 31); d = d.AddDays(1))
            {
                date.Add(d.Ticks);
                year.Add(d.Year);
                quarter.Add("Q" + ((d.Month - 1) / 3 + 1));
                monthNo.Add(d.Month);
                month.Add(Months[d.Month - 1]);
            }
            var b = new ColumnBatch();
            b.Columns.Add(date.Finish("Date"));
            b.Columns.Add(year.Finish("Year"));
            b.Columns.Add(quarter.Finish("Quarter"));
            b.Columns.Add(monthNo.Finish("MonthNo"));
            b.Columns.Add(month.Finish("Month"));
            return b;
        }

        /// <summary>Builds the model: tables, keys, relationships, marked date table and the PRD's golden measures.</summary>
        public static Dataset BuildDataset(int salesRows, out SemanticModel model)
        {
            model = new SemanticModel();
            var data = new List<TableData>();

            Func<string, TableDef> add = name =>
            {
                var def = new TableDef { Id = name.ToLowerInvariant(), Name = name, Source = new DataSourceRef { Kind = "sample", Query = name } };
                data.Add(TableLoader.Bind(def, Batch(name, salesRows)));
                return def;
            };

            var sales = add("Sales");
            var product = add("Product");
            var date = add("Date");
            model.Tables.AddRange(new[] { sales, product, date });

            sales.DiagramX = 60; sales.DiagramY = 60;
            product.DiagramX = 420; product.DiagramY = 40;
            date.DiagramX = 420; date.DiagramY = 300;

            date.DateTableColumnId = date.ColumnByName("Date").Id;
            date.ColumnByName("Date").FormatString = "yyyy-MM-dd";
            date.ColumnByName("Month").SortByColumnId = date.ColumnByName("MonthNo").Id;
            date.ColumnByName("MonthNo").IsHidden = true;
            date.ColumnByName("Year").SummarizeBy = SummarizeBy.None;
            date.ColumnByName("MonthNo").SummarizeBy = SummarizeBy.None;
            sales.ColumnByName("OrderDate").FormatString = "yyyy-MM-dd";
            sales.ColumnByName("UnitPrice").FormatString = "#,0.00";
            sales.ColumnByName("UnitCost").FormatString = "#,0.00";
            sales.ColumnByName("ProductKey").IsHidden = true;
            sales.ColumnByName("ProductKey").SummarizeBy = SummarizeBy.None;
            product.ColumnByName("ProductKey").SummarizeBy = SummarizeBy.None;

            model.Relationships.Add(new RelationshipDef
            {
                Id = "rel-product", FromTableId = sales.Id, FromColumnId = sales.ColumnByName("ProductKey").Id,
                ToTableId = product.Id, ToColumnId = product.ColumnByName("ProductKey").Id
            });
            model.Relationships.Add(new RelationshipDef
            {
                Id = "rel-date", FromTableId = sales.Id, FromColumnId = sales.ColumnByName("OrderDate").Id,
                ToTableId = date.Id, ToColumnId = date.ColumnByName("Date").Id
            });

            var measures = model.Measures;
            Action<string, string, string> measure = (name, expr, fmt) => measures.Add(new MeasureDef
            {
                Id = "m-" + name.ToLowerInvariant().Replace(' ', '-').Replace("%", "pct"),
                Name = name, HomeTableId = sales.Id, Expression = expr, FormatString = fmt
            });
            measure("Sales Amount", "SUMX(Sales, Sales[Qty] * Sales[UnitPrice])", "#,0");
            measure("Total Cost", "SUMX(Sales, Sales[Qty] * Sales[UnitCost])", "#,0");
            measure("Margin %", "DIVIDE([Sales Amount] - [Total Cost], [Sales Amount])", "0.0%");
            measure("Sales YTD", "TOTALYTD([Sales Amount], 'Date'[Date])", "#,0");
            measure("Sales LY", "CALCULATE([Sales Amount], SAMEPERIODLASTYEAR('Date'[Date]))", "#,0");
            measure("Red Sales", "CALCULATE([Sales Amount], Product[Color] = \"Red\")", "#,0");
            measure("All-Product Share", "DIVIDE([Sales Amount], CALCULATE([Sales Amount], ALL(Product)))", "0.0%");
            measure("Orders", "COUNTROWS(Sales)", "#,0");

            return new Dataset(model, data);
        }
    }
}
