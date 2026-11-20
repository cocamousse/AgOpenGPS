using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// The look shared by the AgOpen pop-ups: a see-through dark box with white
    /// lettering over the map, with a thin edge line that can carry a short note
    /// (and later a countdown). A touch anywhere outside the box closes it and
    /// goes back to the map; that touch does nothing else.
    /// </summary>
    public class AgOpenPopup : Form, IMessageFilter
    {
        public static readonly Color BoxColor = Color.FromArgb(18, 20, 22);
        public static readonly Color ListColor = Color.FromArgb(32, 35, 38);
        public static readonly Color TileColor = Color.FromArgb(50, 54, 58);
        public static readonly Color TilePressedColor = Color.FromArgb(90, 96, 102);
        public static readonly Color AccentColor = Color.FromArgb(34, 96, 58);
        public static readonly Color SelectedColor = Color.FromArgb(40, 112, 70);
        public static readonly Color EdgeColor = Color.FromArgb(170, 175, 180);
        public static readonly Color DimText = Color.FromArgb(185, 190, 195);
        public static readonly Color DisabledText = Color.FromArgb(105, 110, 115);

        public static readonly Font NoteFont = new Font("Tahoma", 10.5F);
        public static readonly Font SmallFont = new Font("Tahoma", 11.25F);
        public static readonly Font InfoFont = new Font("Tahoma", 12F);
        public static readonly Font ListFont = new Font("Tahoma", 15.75F);
        public static readonly Font TitleFont = new Font("Tahoma", 16F, FontStyle.Bold);
        public static readonly Font HeadingFont = new Font("Tahoma", 18F, FontStyle.Bold);

        /// <summary>How much of the map shows through (1 = solid).</summary>
        public const double BoxOpacity = 0.88;

        /// <summary>Distance from the window edge to the edge line.</summary>
        protected const int EdgeInset = 12;

        protected readonly FormGPS mf;

        private bool filtering;
        private string edgeNote = "";
        private double edgeShare = 1.0;

        protected AgOpenPopup(FormGPS mf)
        {
            this.mf = mf;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = BoxColor;
            ForeColor = Color.White;
            Font = new Font("Tahoma", 14.25F);
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            Opacity = BoxOpacity;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }

        /// <summary>Small text set into the bottom edge line, e.g. "Touch anywhere else to go to the map".</summary>
        public string EdgeNote
        {
            get { return edgeNote; }
            set { edgeNote = value ?? ""; Invalidate(); }
        }

        /// <summary>
        /// How much of the edge line is drawn bright, 0..1 (for a countdown).
        /// The rest is drawn dim.
        /// </summary>
        public double EdgeShare
        {
            get { return edgeShare; }
            set { edgeShare = Math.Max(0.0, Math.Min(1.0, value)); Invalidate(); }
        }

        /// <summary>The inside of the box, where the controls go.</summary>
        protected Rectangle ContentRectangle
        {
            get
            {
                int side = EdgeInset + 16;
                int bottom = EdgeInset + (edgeNote.Length > 0 ? 22 : 16);
                return new Rectangle(side, side, Math.Max(0, ClientSize.Width - 2 * side), Math.Max(0, ClientSize.Height - side - bottom));
            }
        }

        /// <summary>Size the box as a share of the AgOpenGPS window and centre it there.</summary>
        protected void PlaceOverMap(double widthShare, double heightShare, Size smallest, Size largest)
        {
            Rectangle area = mf.RectangleToScreen(mf.ClientRectangle);
            int width = Clamp((int)(area.Width * widthShare), smallest.Width, largest.Width);
            int height = Clamp((int)(area.Height * heightShare), smallest.Height, largest.Height);
            width = Math.Min(width, Math.Max(200, area.Width - 16));
            height = Math.Min(height, Math.Max(150, area.Height - 16));
            Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }

        protected static int Clamp(int value, int low, int high)
        {
            if (value < low) return low;
            if (value > high) return high;
            return value;
        }

        /// <summary>A touch landed on the map outside the box. Closes the box.</summary>
        protected virtual void TouchedOutside()
        {
            Close();
        }

        #region Touch outside the box

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        /// <summary>
        /// Sees every touch and click before it reaches its window. One that lands
        /// on the AgOpenGPS main window (the map, its buttons) closes this box and
        /// is used up, so it doesn't also press whatever was under the finger.
        /// Touches on this box and on other windows (messages, pickers) are left alone.
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_RBUTTONDOWN && m.Msg != WM_MBUTTONDOWN
                && m.Msg != WM_NCLBUTTONDOWN && m.Msg != WM_NCRBUTTONDOWN)
            {
                return false;
            }
            if (mf == null || !Visible || IsDisposed) return false;

            Control target = Control.FromChildHandle(m.HWnd);
            if (target == null) return false;
            Form form = target.FindForm();
            if (form == null || form != mf) return false;

            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) TouchedOutside();
            }));
            return true;
        }

        private void StartFiltering()
        {
            if (filtering) return;
            Application.AddMessageFilter(this);
            filtering = true;
        }

        private void StopFiltering()
        {
            if (!filtering) return;
            Application.RemoveMessageFilter(this);
            filtering = false;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && mf != null) StartFiltering();
            else StopFiltering();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopFiltering();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        #endregion

        #region Drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var edge = new Rectangle(EdgeInset, EdgeInset, ClientSize.Width - 2 * EdgeInset - 1, ClientSize.Height - 2 * EdgeInset - 1);
            if (edge.Width < 10 || edge.Height < 10) return;

            using (var dim = new Pen(Color.FromArgb(70, 74, 78), 3f))
            using (var bright = new Pen(EdgeColor, 3f))
            {
                if (edgeShare >= 1.0)
                {
                    g.DrawRectangle(bright, edge);
                }
                else
                {
                    g.DrawRectangle(dim, edge);
                    DrawEdgeShare(g, bright, edge, edgeShare);
                }
            }

            if (edgeNote.Length > 0)
            {
                Size size = TextRenderer.MeasureText(g, edgeNote, NoteFont);
                var noteBox = new Rectangle((ClientSize.Width - size.Width) / 2 - 10, edge.Bottom - size.Height / 2,
                    size.Width + 20, size.Height);
                using (var brush = new SolidBrush(BackColor))
                {
                    g.FillRectangle(brush, noteBox);
                }
                TextRenderer.DrawText(g, edgeNote, NoteFont, noteBox, DimText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Draw the first share of the edge, clockwise from the top middle.
        private static void DrawEdgeShare(Graphics g, Pen pen, Rectangle r, double share)
        {
            float middle = r.Left + r.Width / 2f;
            var corners = new List<PointF>
            {
                new PointF(middle, r.Top),
                new PointF(r.Right, r.Top),
                new PointF(r.Right, r.Bottom),
                new PointF(r.Left, r.Bottom),
                new PointF(r.Left, r.Top),
                new PointF(middle, r.Top)
            };
            double total = 2.0 * (r.Width + r.Height);
            double left = total * share;
            var points = new List<PointF> { corners[0] };
            for (int i = 1; i < corners.Count && left > 0; i++)
            {
                PointF a = corners[i - 1];
                PointF b = corners[i];
                double length = Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);
                if (length <= left)
                {
                    points.Add(b);
                    left -= length;
                }
                else
                {
                    double t = left / length;
                    points.Add(new PointF((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t)));
                    left = 0;
                }
            }
            if (points.Count > 1) g.DrawLines(pen, points.ToArray());
        }

        /// <summary>A rectangle with rounded corners.</summary>
        public static GraphicsPath RoundedRectangle(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Draw a picture as large as fits in the box (up to twice its size), centred.</summary>
        public static void DrawPicture(Graphics g, Image picture, Rectangle box, bool dim)
        {
            if (picture == null || box.Width <= 0 || box.Height <= 0) return;
            double scale = Math.Min(2.0, Math.Min((double)box.Width / picture.Width, (double)box.Height / picture.Height));
            int width = Math.Max(1, (int)(picture.Width * scale));
            int height = Math.Max(1, (int)(picture.Height * scale));
            var target = new Rectangle(box.Left + (box.Width - width) / 2, box.Top + (box.Height - height) / 2, width, height);

            InterpolationMode before = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (!dim)
            {
                g.DrawImage(picture, target);
            }
            else
            {
                using (var attributes = new ImageAttributes())
                {
                    var matrix = new ColorMatrix { Matrix33 = 0.35f };
                    attributes.SetColorMatrix(matrix);
                    g.DrawImage(picture, target, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            g.InterpolationMode = before;
        }

        #endregion
    }

    /// <summary>
    /// A big touch button for the pop-ups: a picture, a bold title and a smaller
    /// line under it. Square tiles put the picture on top; wide ones (Wide = true)
    /// put the text on the left and the picture on the right.
    /// </summary>
    public class PopupTile : Control
    {
        private Image picture;
        private string title = "";
        private string detail = "";
        private bool pressed;

        public PopupTile()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = AgOpenPopup.BoxColor;
            ForeColor = Color.White;
            TileColor = AgOpenPopup.TileColor;
            TitleFont = AgOpenPopup.TitleFont;
            DetailFont = AgOpenPopup.SmallFont;
            Cursor = Cursors.Hand;
        }

        public Image Picture
        {
            get { return picture; }
            set { picture = value; Invalidate(); }
        }

        public string Title
        {
            get { return title; }
            set { title = value ?? ""; Invalidate(); }
        }

        public string Detail
        {
            get { return detail; }
            set { detail = value ?? ""; Invalidate(); }
        }

        public Color TileColor { get; set; }
        public Font TitleFont { get; set; }
        public Font DetailFont { get; set; }

        /// <summary>Text on the left, picture on the right (for long bars).</summary>
        public bool Wide { get; set; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (Width < 8 || Height < 8) return;

            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = !Enabled ? Color.FromArgb(34, 37, 40) : pressed ? AgOpenPopup.TilePressedColor : TileColor;
            using (GraphicsPath path = AgOpenPopup.RoundedRectangle(r, Math.Min(14, Height / 5)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Enabled ? AgOpenPopup.EdgeColor : Color.FromArgb(80, 84, 88), 1.5f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Color titleColor = Enabled ? ForeColor : AgOpenPopup.DisabledText;
            Color detailColor = Enabled ? AgOpenPopup.DimText : AgOpenPopup.DisabledText;
            if (Wide) PaintWide(g, titleColor, detailColor);
            else PaintSquare(g, titleColor, detailColor);
        }

        private void PaintSquare(Graphics g, Color titleColor, Color detailColor)
        {
            int pad = Math.Max(10, Height / 12);
            int textWidth = Width - 2 * pad;
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            int pictureHeight = picture != null ? (int)(Height * 0.34) : 0;
            Size titleSize = title.Length > 0 ? TextRenderer.MeasureText(g, title, TitleFont, new Size(textWidth, int.MaxValue), flags) : Size.Empty;
            Size detailSize = detail.Length > 0 ? TextRenderer.MeasureText(g, detail, DetailFont, new Size(textWidth, int.MaxValue), flags) : Size.Empty;
            int gap = pad / 2;
            int total = pictureHeight + (pictureHeight > 0 ? gap : 0) + titleSize.Height + (detailSize.Height > 0 ? gap + detailSize.Height : 0);
            int top = Math.Max(pad, (Height - total) / 2);

            if (pictureHeight > 0)
            {
                AgOpenPopup.DrawPicture(g, picture, new Rectangle(pad, top, textWidth, pictureHeight), !Enabled);
                top += pictureHeight + gap;
            }
            if (titleSize.Height > 0)
            {
                TextRenderer.DrawText(g, title, TitleFont, new Rectangle(pad, top, textWidth, titleSize.Height), titleColor, flags);
                top += titleSize.Height + gap;
            }
            if (detailSize.Height > 0)
            {
                int height = Math.Max(0, Math.Min(detailSize.Height, Height - pad - top));
                TextRenderer.DrawText(g, detail, DetailFont, new Rectangle(pad, top, textWidth, height), detailColor, flags);
            }
        }

        private void PaintWide(Graphics g, Color titleColor, Color detailColor)
        {
            int pad = Math.Max(8, Height / 6);
            int pictureSide = picture != null ? Height - 2 * pad : 0;
            if (pictureSide > 0)
            {
                AgOpenPopup.DrawPicture(g, picture, new Rectangle(Width - pad - pictureSide, pad, pictureSide, pictureSide), !Enabled);
            }
            int textLeft = pad + 6;
            int textWidth = Width - textLeft - pad - (pictureSide > 0 ? pictureSide + pad : 0);
            if (textWidth <= 0) return;
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            if (detail.Length == 0)
            {
                TextRenderer.DrawText(g, title, TitleFont, new Rectangle(textLeft, 0, textWidth, Height), titleColor, flags);
                return;
            }
            int half = Height / 2;
            TextRenderer.DrawText(g, title, TitleFont, new Rectangle(textLeft, pad / 2, textWidth, half - pad / 2), titleColor, flags);
            TextRenderer.DrawText(g, detail, DetailFont, new Rectangle(textLeft, half, textWidth, half - pad / 2), detailColor, flags);
        }
    }

    /// <summary>
    /// A touch-friendly list: tall rows, drag up and down to scroll, tap to pick.
    /// Each row has a name on the left and a short note on the right.
    /// </summary>
    public class PopupList : Control
    {
        public class Item
        {
            public string Text = "";
            public string Detail = "";
            public object Tag;
        }

        private const int DragStart = 10;

        private readonly List<Item> items = new List<Item>();
        private int selected = -1;
        private int offset;
        private bool down;
        private bool dragged;
        private int downY;
        private int downOffset;

        public PopupList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = AgOpenPopup.ListColor;
            ForeColor = Color.White;
            Font = AgOpenPopup.ListFont;
            DetailFont = AgOpenPopup.SmallFont;
            ItemHeight = 58;
        }

        public event EventHandler SelectedIndexChanged;

        public int ItemHeight { get; set; }
        public Font DetailFont { get; set; }

        /// <summary>Shown in the middle when the list is empty.</summary>
        public string EmptyText { get; set; } = "";

        public int Count
        {
            get { return items.Count; }
        }

        public Item SelectedItem
        {
            get { return selected >= 0 && selected < items.Count ? items[selected] : null; }
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                int index = value >= 0 && value < items.Count ? value : -1;
                if (index == selected) return;
                selected = index;
                EnsureVisible(index);
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Replace all the rows, then pick the given row (-1 for none).</summary>
        public void SetItems(IEnumerable<Item> newItems, int select)
        {
            items.Clear();
            items.AddRange(newItems);
            selected = -1;
            SetOffset(offset);
            SelectedIndex = select;
            if (select < 0 || select >= items.Count)
            {
                // Nothing picked: still let listeners know the rows changed.
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
            Invalidate();
        }

        public void EnsureVisible(int index)
        {
            if (index < 0) return;
            int top = index * ItemHeight;
            if (top < offset) SetOffset(top);
            else if (top + ItemHeight > offset + Height) SetOffset(top + ItemHeight - Height);
        }

        private int MaxOffset
        {
            get { return Math.Max(0, items.Count * ItemHeight - Height); }
        }

        private void SetOffset(int value)
        {
            int clamped = Math.Max(0, Math.Min(MaxOffset, value));
            if (clamped == offset) return;
            offset = clamped;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetOffset(offset);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                down = true;
                dragged = false;
                downY = e.Y;
                downOffset = offset;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (down)
            {
                int moved = e.Y - downY;
                if (!dragged && Math.Abs(moved) > DragStart) dragged = true;
                if (dragged) SetOffset(downOffset - moved);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (down && !dragged && e.Button == MouseButtons.Left)
            {
                int index = (e.Y + offset) / Math.Max(1, ItemHeight);
                if (e.Y >= 0 && index >= 0 && index < items.Count) SelectedIndex = index;
            }
            down = false;
            dragged = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            SetOffset(offset - e.Delta / 120 * ItemHeight);
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, DetailFont, new Rectangle(16, 0, Width - 32, Height), AgOpenPopup.DimText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            int first = offset / Math.Max(1, ItemHeight);
            const TextFormatFlags nameFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            const TextFormatFlags detailFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            using (var selectedBrush = new SolidBrush(AgOpenPopup.SelectedColor))
            using (var line = new Pen(Color.FromArgb(60, 64, 68)))
            {
                for (int i = first; i < items.Count; i++)
                {
                    int top = i * ItemHeight - offset;
                    if (top > Height) break;
                    var row = new Rectangle(0, top, Width, ItemHeight);
                    if (i == selected) g.FillRectangle(selectedBrush, row);

                    Item item = items[i];
                    int detailWidth = 0;
                    if (item.Detail.Length > 0)
                    {
                        detailWidth = TextRenderer.MeasureText(g, item.Detail, DetailFont).Width + 8;
                        TextRenderer.DrawText(g, item.Detail, DetailFont, new Rectangle(Width - 16 - detailWidth, top, detailWidth, ItemHeight),
                            i == selected ? Color.White : AgOpenPopup.DimText, detailFlags);
                    }
                    TextRenderer.DrawText(g, item.Text, Font, new Rectangle(14, top, Math.Max(0, Width - 34 - detailWidth), ItemHeight),
                        ForeColor, nameFlags);
                    g.DrawLine(line, 0, row.Bottom - 1, Width, row.Bottom - 1);
                }
            }

            // Where we are in the list, when it doesn't all fit.
            int content = items.Count * ItemHeight;
            if (content > Height && Height > 0)
            {
                int thumb = Math.Max(30, Height * Height / content);
                int y = (int)((Height - thumb) * (offset / (double)Math.Max(1, MaxOffset)));
                using (var brush = new SolidBrush(Color.FromArgb(150, 155, 160)))
                {
                    g.FillRectangle(brush, Width - 6, y, 5, thumb);
                }
            }
        }
    }
}
