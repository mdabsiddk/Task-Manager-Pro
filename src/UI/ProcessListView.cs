using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskManagerPro
{
    internal enum RowKind { Section, Group, Process }

    /// <summary>One line of the list, prepared by the form (all text already formatted).</summary>
    internal sealed class ListRow
    {
        public RowKind Kind;
        public string Key;              // stable identity: selection and expansion survive refreshes
        public string Text;
        public int Depth;
        public bool Expandable, Expanded, InExpandedBlock;
        public Image Icon;
        public string Status;
        public Color StatusColor;
        public string[] Cells;          // display text per column (Name/Status use Text/Status)
        public double[] Heat;           // 0..1 per column, NaN = no heat colouring
        public string[] EditValues;     // raw values of the limit columns, used when editing starts
        public bool LimitsEditable;
        public ProcessGroup Group;
        public ProcInfo Proc;
    }

    internal sealed class ListColumn
    {
        public string Key, Title, TopText, Tooltip;
        public int Width, MinWidth;
        public bool RightAlign, IsLimit, Fill, TopMuted;
    }

    internal sealed class LimitEditEventArgs : EventArgs
    {
        public ListRow Row;
        public ListColumn Column;
        public string Text;
        public string Error;   // set by the handler to reject the value
    }

    internal sealed class RowEventArgs : EventArgs
    {
        public ListRow Row;
        public ListColumn Column;
        public Point ScreenLocation;
    }

    /// <summary>
    /// Owner-drawn process list in the style of the Windows 11 Task Manager: two-line headers with the
    /// total usage on top, expandable app groups with icons, heat-map value cells, and limit cells that
    /// become editable with a single click. Only visible rows are painted.
    /// </summary>
    internal sealed class ProcessListView : Control
    {
        public event EventHandler<RowEventArgs> HeaderClicked;
        public event EventHandler<RowEventArgs> ExpandToggled;
        public event EventHandler<RowEventArgs> ContextMenuRequested;
        public event EventHandler<RowEventArgs> EndTaskRequested;
        public event EventHandler<RowEventArgs> EditBlocked;
        public event EventHandler<LimitEditEventArgs> LimitEdited;
        public event EventHandler<RowEventArgs> EditStarted;
        public event EventHandler SelectionChanged;
        public event EventHandler ColumnsResized;

        public readonly List<ListColumn> Columns = new List<ListColumn>();
        public string SortKey;
        public bool SortAscending;

        private readonly float scale;
        private readonly VScrollBar scrollBar;
        private readonly TextBox editor;
        private readonly ToolTip tip;
        private List<ListRow> rows = new List<ListRow>();
        private List<ListRow> pendingRows;
        private string selectedKey;
        private int topIndex;
        private int hoverRow = -1, hoverHeader = -1, pressedHeader = -1;
        private int resizeColumn = -1, resizeStartX, resizeStartWidth;
        private ListRow editRow;
        private ListColumn editColumn;
        private bool committing;
        private Font headerTopFont, boldFont, glyphFont, smallGlyphFont;

        public ProcessListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            // Screen DPI without creating our window handle before the child controls exist.
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            scrollBar = new VScrollBar { Dock = DockStyle.Right, Visible = false, SmallChange = 1 };
            scrollBar.ValueChanged += delegate
            {
                topIndex = scrollBar.Value;
                MoveEditorOrCancel();
                Invalidate();
            };
            Controls.Add(scrollBar);

            editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, TextAlign = HorizontalAlignment.Right, MaxLength = 9 };
            editor.KeyDown += Editor_KeyDown;
            editor.LostFocus += delegate { if (editor.Visible && !committing) CommitEdit(false); };
            Controls.Add(editor);

            tip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 6000 };
            RowHeight = S(28);
            HeaderHeight = S(56);
            Theme.Changed += OnThemeChanged;
        }

        public int RowHeight { get; private set; }
        public int HeaderHeight { get; private set; }
        public bool IsEditing { get { return editor.Visible; } }

        public int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            DisposeFonts();
            headerTopFont = new Font(Font.FontFamily, 13f, FontStyle.Regular);
            boldFont = new Font(Font, FontStyle.Bold);
            glyphFont = Theme.IconFont(8f);
            smallGlyphFont = Theme.IconFont(6.5f);
            if (editor != null) editor.Font = Font;
            RowHeight = Math.Max(S(28), Font.Height + S(10));
            Invalidate();
        }

        private void DisposeFonts()
        {
            foreach (Font f in new[] { headerTopFont, boldFont, glyphFont, smallGlyphFont })
            {
                if (f != null) f.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                DisposeFonts();
                tip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            Theme.ApplyControl(scrollBar);
            StyleEditor();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (headerTopFont == null) OnFontChanged(EventArgs.Empty);
            Theme.ApplyControl(scrollBar);
            StyleEditor();
        }

        private void StyleEditor()
        {
            editor.BackColor = Theme.Current.InputBack;
            editor.ForeColor = Theme.Current.Text;
        }

        // =====================================================================
        // Data
        // =====================================================================

        public ListRow SelectedRow
        {
            get
            {
                int i = IndexOfKey(selectedKey);
                return i >= 0 ? rows[i] : null;
            }
        }

        public IList<ListRow> Rows { get { return rows; } }

        /// <summary>Replaces the rows, keeping selection and scroll position (deferred while editing).</summary>
        public void SetRows(List<ListRow> newRows)
        {
            if (IsEditing)
            {
                pendingRows = newRows;
                return;
            }
            rows = newRows;
            if (selectedKey != null && IndexOfKey(selectedKey) < 0) selectedKey = null;
            if (hoverRow >= rows.Count) hoverRow = -1;
            UpdateScrollBar();
            Invalidate();
        }

        public void SelectKey(string key, bool ensureVisible)
        {
            if (selectedKey == key) return;
            selectedKey = key;
            if (ensureVisible) EnsureVisible(IndexOfKey(key));
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private int IndexOfKey(string key)
        {
            if (key == null) return -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Key == key) return i;
            }
            return -1;
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private Rectangle ContentRect
        {
            get
            {
                int w = ClientSize.Width - (scrollBar.Visible ? scrollBar.Width : 0);
                return new Rectangle(0, HeaderHeight, Math.Max(0, w), Math.Max(0, ClientSize.Height - HeaderHeight));
            }
        }

        private int VisibleRowCount
        {
            get { return Math.Max(1, ContentRect.Height / RowHeight); }
        }

        /// <summary>Left edge and width of every column; the Fill column takes the remaining space.</summary>
        private int[] ColumnLefts(out int[] widths)
        {
            int available = ContentRect.Width;
            int fixedTotal = 0;
            foreach (ListColumn c in Columns)
            {
                if (!c.Fill) fixedTotal += S(c.Width);
            }
            widths = new int[Columns.Count];
            var lefts = new int[Columns.Count];
            int x = 0;
            for (int i = 0; i < Columns.Count; i++)
            {
                ListColumn c = Columns[i];
                widths[i] = c.Fill ? Math.Max(S(c.MinWidth), available - fixedTotal) : S(c.Width);
                lefts[i] = x;
                x += widths[i];
            }
            return lefts;
        }

        private Rectangle CellRect(int rowIndex, int col)
        {
            int[] widths;
            int[] lefts = ColumnLefts(out widths);
            int y = HeaderHeight + (rowIndex - topIndex) * RowHeight;
            return new Rectangle(lefts[col], y, widths[col], RowHeight);
        }

        private Rectangle LimitBox(Rectangle cell)
        {
            int padX = S(6), padY = S(4);
            return new Rectangle(cell.X + padX, cell.Y + padY, cell.Width - 2 * padX, cell.Height - 2 * padY);
        }

        private void UpdateScrollBar()
        {
            int visible = VisibleRowCount;
            bool needed = rows.Count > visible;
            if (scrollBar.Visible != needed) scrollBar.Visible = needed;
            int maxTop = Math.Max(0, rows.Count - visible);
            if (topIndex > maxTop) topIndex = maxTop;
            if (needed)
            {
                scrollBar.Minimum = 0;
                scrollBar.Maximum = Math.Max(0, rows.Count - 1);
                scrollBar.LargeChange = visible;
                if (scrollBar.Value != topIndex) scrollBar.Value = Math.Min(topIndex, scrollBar.Maximum);
            }
            else
            {
                topIndex = 0;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollBar();
            MoveEditorOrCancel();
        }

        private void EnsureVisible(int index)
        {
            if (index < 0) return;
            int visible = VisibleRowCount;
            if (index < topIndex) topIndex = index;
            else if (index >= topIndex + visible) topIndex = index - visible + 1;
            UpdateScrollBar();
            Invalidate();
        }

        // =====================================================================
        // Painting
        // =====================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Surface);
            if (headerTopFont == null) OnFontChanged(EventArgs.Empty);

            int[] widths;
            int[] lefts = ColumnLefts(out widths);
            PaintHeader(g, p, lefts, widths);

            Rectangle content = ContentRect;
            g.SetClip(content);
            int last = Math.Min(rows.Count, topIndex + VisibleRowCount + 1);
            for (int i = topIndex; i < last; i++)
            {
                PaintRow(g, p, rows[i], i, lefts, widths);
            }
            g.ResetClip();

            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "No processes match your search.", Font, content, p.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }
        }

        private void PaintHeader(Graphics g, Palette p, int[] lefts, int[] widths)
        {
            int h = HeaderHeight;
            for (int i = 0; i < Columns.Count; i++)
            {
                ListColumn c = Columns[i];
                var cell = new Rectangle(lefts[i], 0, widths[i], h);
                if (i == hoverHeader || i == pressedHeader)
                {
                    using (var b = new SolidBrush(p.Hover)) g.FillRectangle(b, cell);
                }

                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                            | (c.RightAlign ? TextFormatFlags.Right : TextFormatFlags.Left);
                var inner = new Rectangle(cell.X + S(10), cell.Y, cell.Width - S(20), cell.Height);

                if (!string.IsNullOrEmpty(c.TopText))
                {
                    var top = new Rectangle(inner.X, inner.Y + S(9), inner.Width, S(24));
                    if (c.TopMuted) TextRenderer.DrawText(g, c.TopText, Font, top, p.SubText, flags | TextFormatFlags.Bottom);
                    else TextRenderer.DrawText(g, c.TopText, headerTopFont, top, p.Text, flags | TextFormatFlags.Bottom);
                }
                var bottom = new Rectangle(inner.X, h - S(25), inner.Width, S(20));
                TextRenderer.DrawText(g, c.Title, Font, bottom, p.SubText, flags | TextFormatFlags.VerticalCenter);

                if (c.Key == SortKey)
                {
                    var glyph = new Rectangle(cell.X, S(1), cell.Width, S(12));
                    TextRenderer.DrawText(g, SortAscending ? "" : "", smallGlyphFont, glyph, p.SubText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                if (i > 0)
                {
                    using (var pen = new Pen(p.Separator)) g.DrawLine(pen, cell.X, 0, cell.X, h - 1);
                }
            }
            using (var pen = new Pen(p.Separator)) g.DrawLine(pen, 0, h - 1, ClientSize.Width, h - 1);
        }

        private void PaintRow(Graphics g, Palette p, ListRow row, int index, int[] lefts, int[] widths)
        {
            int y = HeaderHeight + (index - topIndex) * RowHeight;
            var rowRect = new Rectangle(0, y, ContentRect.Width, RowHeight);
            bool selected = row.Key == selectedKey;

            Color back = selected ? p.Selected : index == hoverRow ? p.Hover : row.InExpandedBlock ? p.Expanded : p.Surface;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, rowRect);

            Color fore = p.Text;
            var textFlags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

            if (row.Kind == RowKind.Section)
            {
                var r = new Rectangle(S(12), y, rowRect.Width - S(24), RowHeight);
                TextRenderer.DrawText(g, row.Text, boldFont, r, p.Text, textFlags);
                return;
            }

            for (int c = 0; c < Columns.Count; c++)
            {
                ListColumn col = Columns[c];
                var cell = new Rectangle(lefts[c], y, widths[c], RowHeight);

                double heat = row.Heat != null && c < row.Heat.Length ? row.Heat[c] : double.NaN;
                if (!double.IsNaN(heat))
                {
                    using (var b = new SolidBrush(p.Heat(heat))) g.FillRectangle(b, cell.X, cell.Y, cell.Width, cell.Height);
                }

                if (col.Key == "Name")
                {
                    PaintName(g, p, row, cell, fore, selected);
                }
                else if (col.Key == "Status")
                {
                    var r = new Rectangle(cell.X + S(10), cell.Y, cell.Width - S(14), cell.Height);
                    Color sc = row.StatusColor.IsEmpty ? p.SubText : row.StatusColor;
                    TextRenderer.DrawText(g, row.Status ?? "", Font, r, sc, textFlags);
                }
                else if (col.IsLimit)
                {
                    PaintLimit(g, p, row, c, cell, index, fore);
                }
                else
                {
                    string text = row.Cells != null && c < row.Cells.Length ? row.Cells[c] : "";
                    var r = new Rectangle(cell.X + S(6), cell.Y, cell.Width - S(14), cell.Height);
                    TextRenderer.DrawText(g, text, Font, r, fore, textFlags | (col.RightAlign ? TextFormatFlags.Right : TextFormatFlags.Left));
                }
            }

            if (selected)
            {
                using (var pen = new Pen(p.SelectedBorder)) g.DrawRectangle(pen, 0, y, rowRect.Width - 1, RowHeight - 1);
            }
        }

        private void PaintName(Graphics g, Palette p, ListRow row, Rectangle cell, Color fore, bool selected)
        {
            int x = cell.X + S(8) + row.Depth * S(30);
            var chevron = new Rectangle(x, cell.Y, S(18), cell.Height);
            if (row.Expandable)
            {
                TextRenderer.DrawText(g, row.Expanded ? "" : "", glyphFont, chevron, p.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            x += S(22);

            int icon = S(16);
            if (row.Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(row.Icon, new Rectangle(x, cell.Y + (cell.Height - icon) / 2, icon, icon));
            }
            x += icon + S(10);

            var r = new Rectangle(x, cell.Y, Math.Max(0, cell.Right - x - S(6)), cell.Height);
            TextRenderer.DrawText(g, row.Text, Font, r, fore,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        }

        private void PaintLimit(Graphics g, Palette p, ListRow row, int c, Rectangle cell, int index, Color fore)
        {
            string text = row.Cells != null && c < row.Cells.Length ? row.Cells[c] : "";
            if (!row.LimitsEditable)
            {
                if (string.IsNullOrEmpty(text)) return;
                var plain = new Rectangle(cell.X + S(6), cell.Y, cell.Width - S(16), cell.Height);
                TextRenderer.DrawText(g, text, Font, plain, p.SubText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                return;
            }

            Rectangle box = LimitBox(cell);
            bool hot = index == hoverRow && box.Contains(PointToClient(MousePosition));
            using (var path = RoundRect(box, S(4)))
            using (var b = new SolidBrush(p.InputBack))
            using (var pen = new Pen(hot ? p.InputHover : p.InputBorder))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
                g.DrawPath(pen, path);
                g.SmoothingMode = SmoothingMode.None;
            }
            var r = new Rectangle(box.X + S(4), box.Y, box.Width - S(10), box.Height);
            bool empty = string.IsNullOrEmpty(text);
            TextRenderer.DrawText(g, empty ? "—" : text, Font, r, empty ? p.Disabled : p.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // =====================================================================
        // Mouse
        // =====================================================================

        private int HitRow(Point pt)
        {
            if (pt.Y < HeaderHeight || pt.X >= ContentRect.Width) return -1;
            int i = topIndex + (pt.Y - HeaderHeight) / RowHeight;
            return i < rows.Count ? i : -1;
        }

        private int HitColumn(int x)
        {
            int[] widths;
            int[] lefts = ColumnLefts(out widths);
            for (int i = 0; i < Columns.Count; i++)
            {
                if (x >= lefts[i] && x < lefts[i] + widths[i]) return i;
            }
            return -1;
        }

        /// <summary>Column whose right border is under the mouse (resizable, non-fill columns only).</summary>
        private int HitResizeBorder(Point pt)
        {
            if (pt.Y >= HeaderHeight) return -1;
            int[] widths;
            int[] lefts = ColumnLefts(out widths);
            for (int i = 0; i < Columns.Count; i++)
            {
                int right = lefts[i] + widths[i];
                if (Math.Abs(pt.X - right) <= S(4) && !Columns[i].Fill) return i;
                // The border between a Fill column and the next one resizes the next one from its left edge.
                if (i + 1 < Columns.Count && Columns[i].Fill && Math.Abs(pt.X - right) <= S(4)) return -(i + 2);
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (resizeColumn >= 0)
            {
                ListColumn c = Columns[resizeColumn];
                int delta = (int)Math.Round((e.X - resizeStartX) / scale);
                c.Width = Math.Max(c.MinWidth, resizeStartWidth + (resizeLeftEdge ? -delta : delta));
                Invalidate();
                return;
            }

            int border = HitResizeBorder(e.Location);
            Cursor = border != -1 ? Cursors.SizeWE : Cursors.Default;

            int newHoverRow = HitRow(e.Location);
            int newHoverHeader = e.Y < HeaderHeight && border == -1 ? HitColumn(e.X) : -1;
            int hoverCol = newHoverRow >= 0 ? HitColumn(e.X) : -1;
            bool overLimit = hoverCol >= 0 && Columns[hoverCol].IsLimit; // limit boxes highlight under the mouse
            if (newHoverRow != hoverRow || newHoverHeader != hoverHeader || overLimit)
            {
                hoverRow = newHoverRow;
                hoverHeader = newHoverHeader;
                Invalidate();
            }

            if (newHoverHeader >= 0 && !string.IsNullOrEmpty(Columns[newHoverHeader].Tooltip))
            {
                if (tip.GetToolTip(this) != Columns[newHoverHeader].Tooltip) tip.SetToolTip(this, Columns[newHoverHeader].Tooltip);
            }
            else if (tip.GetToolTip(this) != "")
            {
                tip.SetToolTip(this, "");
            }
        }

        private bool resizeLeftEdge;

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = hoverHeader = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Y < HeaderHeight)
            {
                if (e.Button != MouseButtons.Left) return;
                int border = HitResizeBorder(e.Location);
                if (border != -1)
                {
                    resizeLeftEdge = border < -1;
                    resizeColumn = border < -1 ? -border - 1 : border;
                    resizeStartX = e.X;
                    resizeStartWidth = Columns[resizeColumn].Width;
                    return;
                }
                pressedHeader = HitColumn(e.X);
                Invalidate();
                return;
            }

            int index = HitRow(e.Location);
            if (index < 0) return;
            ListRow row = rows[index];
            if (row.Kind != RowKind.Section) SelectKey(row.Key, false);
            int col = HitColumn(e.X);

            if (e.Button == MouseButtons.Right)
            {
                if (row.Kind != RowKind.Section && ContextMenuRequested != null)
                    ContextMenuRequested(this, new RowEventArgs { Row = row, ScreenLocation = PointToScreen(e.Location) });
                return;
            }
            if (e.Button != MouseButtons.Left || col < 0) return;

            ListColumn column = Columns[col];
            if (column.Key == "Name" && row.Expandable && e.X < CellRect(index, col).X + S(8) + row.Depth * S(30) + S(22))
            {
                RaiseToggle(row);
                return;
            }
            if (column.IsLimit && row.Kind == RowKind.Group)
            {
                if (row.LimitsEditable) BeginEdit(row, column);
                else if (EditBlocked != null) EditBlocked(this, new RowEventArgs { Row = row, Column = column, ScreenLocation = PointToScreen(e.Location) });
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (resizeColumn >= 0)
            {
                resizeColumn = -1;
                if (ColumnsResized != null) ColumnsResized(this, EventArgs.Empty);
                return;
            }
            if (pressedHeader >= 0)
            {
                int col = HitColumn(e.X);
                if (col == pressedHeader && e.Y < HeaderHeight && HeaderClicked != null)
                    HeaderClicked(this, new RowEventArgs { Column = Columns[col] });
                pressedHeader = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int index = HitRow(e.Location);
            int col = HitColumn(e.X);
            if (index < 0 || col < 0 || Columns[col].IsLimit) return;
            if (rows[index].Expandable) RaiseToggle(rows[index]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-Math.Sign(e.Delta) * 3);
        }

        private void ScrollBy(int delta)
        {
            int maxTop = Math.Max(0, rows.Count - VisibleRowCount);
            int next = Math.Max(0, Math.Min(maxTop, topIndex + delta));
            if (next == topIndex) return;
            topIndex = next;
            UpdateScrollBar();
            MoveEditorOrCancel();
            Invalidate();
        }

        private void RaiseToggle(ListRow row)
        {
            if (ExpandToggled != null) ExpandToggled(this, new RowEventArgs { Row = row });
        }

        // =====================================================================
        // Keyboard
        // =====================================================================

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int index = IndexOfKey(selectedKey);
            ListRow row = index >= 0 ? rows[index] : null;
            switch (e.KeyCode)
            {
                case Keys.Up: MoveSelection(index - 1, -1); break;
                case Keys.Down: MoveSelection(index + 1, 1); break;
                case Keys.PageUp: MoveSelection(index - VisibleRowCount, -1); break;
                case Keys.PageDown: MoveSelection(index + VisibleRowCount, 1); break;
                case Keys.Home: MoveSelection(0, 1); break;
                case Keys.End: MoveSelection(rows.Count - 1, -1); break;
                case Keys.Right:
                    if (row != null && row.Expandable && !row.Expanded) RaiseToggle(row);
                    break;
                case Keys.Left:
                    if (row != null && row.Expandable && row.Expanded) RaiseToggle(row);
                    else if (row != null && row.Depth > 0) MoveToParent(index);
                    break;
                case Keys.Enter:
                    if (row != null && row.Expandable) RaiseToggle(row);
                    break;
                case Keys.Delete:
                    if (row != null && EndTaskRequested != null) EndTaskRequested(this, new RowEventArgs { Row = row });
                    break;
                case Keys.Apps:
                    if (row != null && ContextMenuRequested != null)
                    {
                        Rectangle r = CellRect(index, 0);
                        ContextMenuRequested(this, new RowEventArgs { Row = row, ScreenLocation = PointToScreen(new Point(r.X + S(40), r.Bottom)) });
                    }
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void MoveSelection(int target, int direction)
        {
            if (rows.Count == 0) return;
            target = Math.Max(0, Math.Min(rows.Count - 1, target));
            while (target >= 0 && target < rows.Count && rows[target].Kind == RowKind.Section) target += direction;
            if (target < 0 || target >= rows.Count) return;
            SelectKey(rows[target].Key, true);
            EnsureVisible(target);
        }

        private void MoveToParent(int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (rows[i].Depth < rows[index].Depth && rows[i].Kind != RowKind.Section)
                {
                    SelectKey(rows[i].Key, true);
                    return;
                }
            }
        }

        // =====================================================================
        // Editing limits
        // =====================================================================

        public void BeginEdit(ListRow row, ListColumn column)
        {
            if (IsEditing) CommitEdit(false);
            int index = rows.IndexOf(row);
            int col = Columns.IndexOf(column);
            if (index < 0 || col < 0) return;
            EnsureVisible(index);

            editRow = row;
            editColumn = column;
            editor.Text = row.EditValues != null && col < row.EditValues.Length ? row.EditValues[col] : "";
            StyleEditor();
            PlaceEditor();
            editor.Visible = true;
            editor.Focus();
            editor.SelectAll();
            if (EditStarted != null)
                EditStarted(this, new RowEventArgs { Row = row, Column = column, ScreenLocation = PointToScreen(new Point(editor.Left, editor.Bottom)) });
        }

        private void PlaceEditor()
        {
            int index = rows.IndexOf(editRow);
            int col = Columns.IndexOf(editColumn);
            Rectangle box = LimitBox(CellRect(index, col));
            int h = editor.PreferredHeight;
            editor.Bounds = new Rectangle(box.X + S(5), box.Y + (box.Height - h) / 2, box.Width - S(12), h);
        }

        private void MoveEditorOrCancel()
        {
            if (!IsEditing) return;
            int index = rows.IndexOf(editRow);
            if (index < topIndex || index >= topIndex + VisibleRowCount) CommitEdit(false);
            else PlaceEditor();
        }

        private void Editor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitEdit(false);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndEdit();
            }
            else if (e.KeyCode == Keys.Tab)
            {
                e.SuppressKeyPress = true;
                CommitEdit(true, e.Shift);
            }
        }

        private void CommitEdit(bool moveNext, bool backwards = false)
        {
            if (!IsEditing || committing) return;
            committing = true;
            try
            {
                var args = new LimitEditEventArgs { Row = editRow, Column = editColumn, Text = editor.Text.Trim() };
                if (LimitEdited != null) LimitEdited(this, args);
                if (args.Error != null)
                {
                    Toast.Show("Check this limit", args.Error, ToastKind.Warning, "limit", null);
                    editor.Focus();
                    editor.SelectAll();
                    return;
                }

                ListRow row = editRow;
                int col = Columns.IndexOf(editColumn);
                EndEdit();
                if (moveNext)
                {
                    for (int c = col + (backwards ? -1 : 1); c >= 0 && c < Columns.Count; c += backwards ? -1 : 1)
                    {
                        if (!Columns[c].IsLimit) continue;
                        ListRow fresh = FindRow(row.Key) ?? row;
                        BeginEdit(fresh, Columns[c]);
                        break;
                    }
                }
            }
            finally
            {
                committing = false;
            }
        }

        public void CancelEdit()
        {
            if (IsEditing) EndEdit();
        }

        private void EndEdit()
        {
            editor.Visible = false;
            editRow = null;
            editColumn = null;
            Focus();
            if (pendingRows != null)
            {
                List<ListRow> next = pendingRows;
                pendingRows = null;
                SetRows(next);
            }
            Invalidate();
        }

        private ListRow FindRow(string key)
        {
            int i = IndexOfKey(key);
            return i >= 0 ? rows[i] : null;
        }

        /// <summary>Shows a short notice about the limit being edited (one card at a time).</summary>
        public void ShowHint(string title, string text, ToastKind kind)
        {
            Toast.Show(title, text, kind, "limit", null);
        }
    }
}
