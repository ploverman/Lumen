using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Data;
using Lumen.Charting;
using Lumen.Engine;

namespace Lumen.Reporting
{
    /// <summary>A row of a column store, formatted on demand. No per-row object exists until WPF asks for it.</summary>
    public sealed class RowView
    {
        // Shared with every other RowView of the grid. A view holds only its row number.
        private readonly IList<Column> _cols;
        private readonly IList<string> _formats;
        /// <summary>Row index in the column store.</summary>
        public readonly int Row;

        /// <summary>Creates the view of one row. <paramref name="formats"/> may be null, or hold a format per column.</summary>
        public RowView(IList<Column> cols, IList<string> formats, int row) { _cols = cols; _formats = formats; Row = row; }

        /// <summary>The formatted cell. Grid bindings use the indexer path "[i]", so this runs only for realized cells.</summary>
        public string this[int i]
        {
            get
            {
                var v = _cols[i].Get(Row);
                return v.IsBlank ? "" : PowerBiFormat.Format(v, _formats == null ? null : _formats[i]);
            }
        }

        /// <summary>Equal by row, because <see cref="VirtualRows"/> makes a new view per access and the grid compares them for selection.</summary>
        public override bool Equals(object o) => o is RowView r && r.Row == Row;
        /// <inheritdoc/>
        public override int GetHashCode() => Row;
    }

    /// <summary>A read-only list that makes rows on demand. It is a non-generic IList because that is what WPF's virtualizing collection view needs for random access.
    /// With it, a DataGrid over ten million rows costs no per-row memory.</summary>
    public sealed class VirtualRows : IList
    {
        private readonly IList<Column> _cols;
        private readonly IList<string> _formats;
        private readonly int _count;

        /// <summary>Creates the list.</summary>
        public VirtualRows(IList<Column> cols, IList<string> formats, int count) { _cols = cols; _formats = formats; _count = count; }

        /// <summary>Makes a row view. The setter and other mutators throw: the list is read-only.</summary>
        public object this[int index] { get => new RowView(_cols, _formats, index); set => throw new NotSupportedException(); }
        /// <inheritdoc/>
        public int Count => _count;
        /// <inheritdoc/>
        public bool IsReadOnly => true;
        /// <inheritdoc/>
        public bool IsFixedSize => true;
        /// <inheritdoc/>
        public bool IsSynchronized => false;
        /// <inheritdoc/>
        public object SyncRoot => this;
        /// <inheritdoc/>
        public int Add(object value) => throw new NotSupportedException();
        /// <inheritdoc/>
        public void Clear() => throw new NotSupportedException();
        /// <inheritdoc/>
        public bool Contains(object value) => value is RowView r && r.Row < _count;
        /// <inheritdoc/>
        public int IndexOf(object value) => value is RowView r ? r.Row : -1;
        /// <inheritdoc/>
        public void Insert(int index, object value) => throw new NotSupportedException();
        /// <inheritdoc/>
        public void Remove(object value) => throw new NotSupportedException();
        /// <inheritdoc/>
        public void RemoveAt(int index) => throw new NotSupportedException();
        /// <inheritdoc/>
        public void CopyTo(Array array, int index) { for (int i = 0; i < _count; i++) array.SetValue(this[i], index + i); }
        /// <inheritdoc/>
        public IEnumerator GetEnumerator() { for (int i = 0; i < _count; i++) yield return this[i]; }

        /// <summary>Points a grid at columns: one text column per store column, bound OneTime, since nothing changes under it.</summary>
        public static void Fill(DataGrid grid, IList<Column> cols, IList<string> formats, int rows)
        {
            grid.Columns.Clear();
            for (int i = 0; i < cols.Count; i++)
                grid.Columns.Add(new DataGridTextColumn { Header = cols[i].Name, Binding = new Binding("[" + i + "]") { Mode = BindingMode.OneTime } });
            grid.ItemsSource = new VirtualRows(cols, formats, rows);
        }
    }
}
