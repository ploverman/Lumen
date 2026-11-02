using System;
using System.Collections.Generic;

namespace Lumen.Model
{
    /// <summary>One undoable change to a <see cref="ReportDocument"/>.</summary>
    public interface IReportCommand
    {
        /// <summary>Text after "Undo" in the tooltip.</summary>
        string Label { get; }
        /// <summary>Applies the change.</summary>
        void Do(ReportDocument doc);
        /// <summary>Reverts the change.</summary>
        void Undo(ReportDocument doc);
        /// <summary>Absorbs a command that was just executed, so both undo as one step. Returns true if it did.</summary>
        bool TryMerge(IReportCommand next);
    }

    /// <summary>Move or resize, merged while dragging so that one drag is one undo step.</summary>
    public sealed class SetBoundsCommand : IReportCommand
    {
        /// <summary>A visual's rectangle on the page.</summary>
        /// <summary>Position and size in page units.</summary>
        public struct Bounds { public double X, Y, W, H; }

        private readonly string _pageId, _visualId, _gesture;
        private readonly Bounds _from;
        private Bounds _to;

        /// <summary>Moves a visual from one rectangle to another.</summary>
        /// <param name="label">Undo label.</param>
        /// <param name="gesture">Commands sharing a non-null gesture merge. Null never merges.</param>
        /// <param name="pageId">Page of the visual.</param>
        /// <param name="visualId">The visual.</param>
        /// <param name="from">Rectangle to restore on undo.</param>
        /// <param name="to">Rectangle to apply.</param>
        public SetBoundsCommand(string label, string gesture, string pageId, string visualId, Bounds from, Bounds to)
        {
            Label = label; _gesture = gesture; _pageId = pageId; _visualId = visualId; _from = from; _to = to;
        }

        /// <inheritdoc/>
        public string Label { get; }
        /// <inheritdoc/>
        public void Do(ReportDocument d) => Apply(d, _to);
        /// <inheritdoc/>
        public void Undo(ReportDocument d) => Apply(d, _from);

        private void Apply(ReportDocument d, Bounds b)
        {
            var v = d.Find(_pageId, _visualId);
            if (v == null) return;
            v.X = b.X; v.Y = b.Y; v.Width = b.W; v.Height = b.H;
        }

        /// <inheritdoc/>
        public bool TryMerge(IReportCommand next)
        {
            if (next is SetBoundsCommand m && m._visualId == _visualId && m._gesture == _gesture && _gesture != null)
            {
                _to = m._to;
                return true;
            }
            return false;
        }
    }

    /// <summary>Any other change: the caller supplies both directions as closures.</summary>
    public sealed class DelegateReportCommand : IReportCommand
    {
        private readonly Action<ReportDocument> _do, _undo;

        /// <summary>Wraps two closures as a command.</summary>
        public DelegateReportCommand(string label, Action<ReportDocument> doIt, Action<ReportDocument> undo)
        {
            Label = label; _do = doIt; _undo = undo;
        }

        /// <inheritdoc/>
        public string Label { get; }
        /// <inheritdoc/>
        public void Do(ReportDocument d) => _do(d);
        /// <inheritdoc/>
        public void Undo(ReportDocument d) => _undo(d);
        /// <inheritdoc/>
        public bool TryMerge(IReportCommand next) => false;
    }

    /// <summary>Undo and redo stacks over the document. Executing a new command clears redo.</summary>
    public sealed class CommandStack
    {
        private readonly Stack<IReportCommand> _undo = new Stack<IReportCommand>();
        private readonly Stack<IReportCommand> _redo = new Stack<IReportCommand>();
        private readonly Func<ReportDocument> _doc;

        /// <summary>Takes a getter rather than a document, because opening a file swaps the document.</summary>
        public CommandStack(Func<ReportDocument> doc) { _doc = doc; }

        /// <summary>Raised after every execute, undo, redo and clear.</summary>
        public event EventHandler Changed;
        /// <summary>True when there is something to undo.</summary>
        public bool CanUndo => _undo.Count > 0;
        /// <summary>True when there is something to redo.</summary>
        public bool CanRedo => _redo.Count > 0;
        /// <summary>Label of the next undo, or null.</summary>
        public string UndoLabel => _undo.Count > 0 ? _undo.Peek().Label : null;
        /// <summary>Label of the next redo, or null.</summary>
        public string RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;
        /// <summary>True when the document changed since the last save. Undoing back to the saved state still counts as dirty.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>Runs a command and records it, merging it into the previous one when that one accepts.</summary>
        public void Execute(IReportCommand cmd)
        {
            cmd.Do(_doc());
            if (!(_undo.Count > 0 && _undo.Peek().TryMerge(cmd))) _undo.Push(cmd);
            _redo.Clear();
            IsDirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Reverts the most recent command. Does nothing when there is none.</summary>
        public void Undo()
        {
            if (_undo.Count == 0) return;
            var c = _undo.Pop();
            c.Undo(_doc());
            _redo.Push(c);
            IsDirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Reapplies the most recently undone command. Does nothing when there is none.</summary>
        public void Redo()
        {
            if (_redo.Count == 0) return;
            var c = _redo.Pop();
            c.Do(_doc());
            _undo.Push(c);
            IsDirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forgets all history and clears the dirty flag. Used when a document is loaded.</summary>
        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            IsDirty = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Clears the dirty flag and keeps the history.</summary>
        public void MarkSaved() => IsDirty = false;
    }
}
