using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// The look shared by the AgOpen pop-ups, made for a tablet in a tractor cab:
    /// a light, see-through box with rounded corners over the map, solid buttons,
    /// and large dark lettering that stays readable in bright sun.
    ///
    /// The whole pop-up is drawn as one picture that has its own see-through
    /// amount for every pixel (a Windows "layered window"). That's what lets the
    /// box be see-through while the buttons and text stay solid. The buttons and
    /// lists are drawn by the pop-up itself (PopupElement), not Windows controls.
    ///
    /// A touch anywhere outside the box closes it and goes back to the map; that
    /// touch does nothing else.
    /// </summary>
    public class AgOpenPopup : Form, IMessageFilter
    {
        // Light colours with strong contrast, for use in bright sun.
        public static readonly Color BoxColor = Color.FromArgb(168, 234, 237, 240);
        public static readonly Color BoxEdgeColor = Color.FromArgb(120, 126, 132);
        public static readonly Color BoxEdgeDim = Color.FromArgb(200, 204, 208);
        public static readonly Color CountdownColor = Color.FromArgb(30, 115, 55);
        public static readonly Color TextColor = Color.FromArgb(18, 22, 26);
        public static readonly Color DimText = Color.FromArgb(70, 76, 82);
        public static readonly Color DisabledText = Color.FromArgb(150, 155, 160);
        public static readonly Color TileColor = Color.White;
        public static readonly Color TileEdge = Color.FromArgb(150, 156, 162);
        public static readonly Color TilePressedColor = Color.FromArgb(200, 207, 214);
        public static readonly Color TileDisabledColor = Color.FromArgb(232, 235, 238);
        public static readonly Color AccentColor = Color.FromArgb(30, 115, 55);
        public static readonly Color AccentPressedColor = Color.FromArgb(18, 78, 36);
        public static readonly Color AccentText = Color.White;
        public static readonly Color PanelColor = Color.White;
        public static readonly Color PanelLine = Color.FromArgb(218, 222, 226);

        // About a quarter bigger than AgOpenGPS's usual lettering: the tablet is
        // further from your face in a cab dock.
        public static readonly Font NoteFont = new Font("Tahoma", 12.75F);
        public static readonly Font SmallFont = new Font("Tahoma", 14F);
        public static readonly Font InfoFont = new Font("Tahoma", 15F);
        public static readonly Font ListFont = new Font("Tahoma", 18F);
        public static readonly Font TitleFont = new Font("Tahoma", 20F, FontStyle.Bold);
        public static readonly Font HeadingFont = new Font("Tahoma", 22F, FontStyle.Bold);

        /// <summary>Room around the box for its soft shadow.</summary>
        protected const int ShadowSize = 12;

        /// <summary>How round the box corners are.</summary>
        protected const int BoxRadius = 24;

        protected readonly FormGPS mf;
        protected readonly List<PopupElement> Elements = new List<PopupElement>();

        private PopupElement touched;
        private Bitmap canvas;
        private bool redrawPending;
        private bool filtering;
        private string edgeNote = "";
        private double edgeShare = 1.0;

        protected AgOpenPopup(FormGPS mf)
        {
            this.mf = mf;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "AgOpen";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED;
                return cp;
            }
        }

        /// <summary>Small text set into the bottom edge of the box, e.g. "Touch anywhere else to go to the map".</summary>
        public string EdgeNote
        {
            get { return edgeNote; }
            set { edgeNote = value ?? ""; Redraw(); }
        }

        /// <summary>
        /// How much of the box edge is drawn in green, 0..1, going round clockwise
        /// from the top middle (for a countdown). 1 = a plain grey edge.
        /// </summary>
        public double EdgeShare
        {
            get { return edgeShare; }
            set { edgeShare = Math.Max(0.0, Math.Min(1.0, value)); Redraw(); }
        }

        /// <summary>The box itself (inside the shadow).</summary>
        protected Rectangle BoxRectangle
        {
            get { return new Rectangle(ShadowSize, ShadowSize, Math.Max(0, Width - 2 * ShadowSize - 1), Math.Max(0, Height - 2 * ShadowSize - 1)); }
        }

        /// <summary>The inside of the box, where the buttons and text go.</summary>
        protected Rectangle ContentRectangle
        {
            get
            {
                Rectangle box = BoxRectangle;
                const int pad = 24;
                int bottom = pad + (edgeNote.Length > 0 ? 14 : 0);
                return new Rectangle(box.Left + pad, box.Top + pad, Math.Max(0, box.Width - 2 * pad), Math.Max(0, box.Height - pad - bottom));
            }
        }

        /// <summary>Add a button, list or text to the pop-up.</summary>
        protected T Add<T>(T element) where T : PopupElement
        {
            element.Popup = this;
            Elements.Add(element);
            Redraw();
            return element;
        }

        /// <summary>Put the buttons and text in place for the current size.</summary>
        protected virtual void LayoutElements()
        {
        }

        /// <summary>Size the pop-up as a share of the AgOpenGPS window and centre it there.</summary>
        protected void PlaceOverMap(double widthShare, double heightShare, Size smallest, Size largest)
        {
            Rectangle area = mf.RectangleToScreen(mf.ClientRectangle);
            int width = Clamp((int)(area.Width * widthShare), smallest.Width, largest.Width);
            int height = Clamp((int)(area.Height * heightShare), smallest.Height, largest.Height);
            width = Math.Min(width, Math.Max(200, area.Width - 8));
            height = Math.Min(height, Math.Max(150, area.Height - 8));
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutElements();
            Redraw();
        }

        #region Drawing

        /// <summary>Draw the pop-up again soon (several requests make one drawing).</summary>
        public void Redraw()
        {
            if (redrawPending || IsDisposed) return;
            redrawPending = true;
            if (IsHandleCreated) BeginInvoke(new Action(DrawNow));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DrawNow();
        }

        private void DrawNow()
        {
            redrawPending = false;
            if (IsDisposed || !IsHandleCreated) return;
            int width = Width, height = Height;
            if (width < 4 || height < 4) return;

            if (canvas == null || canvas.Width != width || canvas.Height != height)
            {
                if (canvas != null) canvas.Dispose();
                canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            }

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                PaintBox(g);
                foreach (PopupElement element in Elements)
                {
                    if (!element.Visible || element.Bounds.Width <= 0 || element.Bounds.Height <= 0) continue;
                    GraphicsState state = g.Save();
                    g.SetClip(element.Bounds);
                    element.Paint(g);
                    g.Restore(state);
                }
                PaintEdgeNote(g);
            }
            Present(canvas);
        }

        private void PaintBox(Graphics g)
        {
            Rectangle box = BoxRectangle;
            if (box.Width < 2 * BoxRadius || box.Height < 2 * BoxRadius) return;

            // Soft shadow: rings outside the box, darker close to it.
            using (GraphicsPath inner = RoundedRectangle(box, BoxRadius))
            {
                for (int grow = 2; grow <= ShadowSize; grow += 2)
                {
                    Rectangle outer = Rectangle.Inflate(box, grow, grow);
                    using (GraphicsPath ring = RoundedRectangle(outer, BoxRadius + grow))
                    using (var brush = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                    {
                        ring.AddPath(inner, false);
                        ring.FillMode = FillMode.Alternate;
                        g.FillPath(brush, ring);
                    }
                }

                using (var fill = new SolidBrush(BoxColor))
                {
                    g.FillPath(fill, inner);
                }

                if (edgeShare >= 1.0)
                {
                    using (var pen = new Pen(BoxEdgeColor, 2.5f))
                    {
                        g.DrawPath(pen, inner);
                    }
                }
                else
                {
                    using (var dim = new Pen(BoxEdgeDim, 3f))
                    using (var bright = new Pen(CountdownColor, 5f))
                    {
                        g.DrawPath(dim, inner);
                        PointF[] part = EdgePart(box, BoxRadius, edgeShare);
                        if (part.Length > 1) g.DrawLines(bright, part);
                    }
                }
            }
        }

        // The note sits on the bottom edge, on a small solid label so it's easy to read.
        private void PaintEdgeNote(Graphics g)
        {
            if (edgeNote.Length == 0) return;
            Rectangle box = BoxRectangle;
            Size size = MeasureText(g, edgeNote, NoteFont, Math.Max(10, box.Width - 40), false);
            int width = Math.Min(box.Width - 40, size.Width + 28);
            int height = size.Height + 10;
            var label = new Rectangle(box.Left + (box.Width - width) / 2, box.Bottom - height / 2, width, height);
            using (GraphicsPath path = RoundedRectangle(label, height / 2))
            using (var fill = new SolidBrush(Color.FromArgb(245, 250, 251, 252)))
            using (var pen = new Pen(BoxEdgeColor, 1.5f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            DrawText(g, edgeNote, NoteFont, DimText, label, StringAlignment.Center, StringAlignment.Center, false);
        }

        // The first share of the box edge, clockwise from the top middle.
        private static PointF[] EdgePart(Rectangle r, int radius, double share)
        {
            PointF[] points;
            using (var path = new GraphicsPath())
            {
                int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
                float middle = r.Left + r.Width / 2f;
                path.AddLine(middle, r.Top, r.Right - d / 2f, r.Top);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddLine(r.Right, r.Top + d / 2f, r.Right, r.Bottom - d / 2f);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddLine(r.Right - d / 2f, r.Bottom, r.Left + d / 2f, r.Bottom);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.AddLine(r.Left, r.Bottom - d / 2f, r.Left, r.Top + d / 2f);
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddLine(r.Left + d / 2f, r.Top, middle, r.Top);
                path.Flatten();
                points = path.PathPoints;
            }

            double total = 0;
            for (int i = 1; i < points.Length; i++) total += Length(points[i - 1], points[i]);
            double left = total * share;
            var part = new List<PointF> { points[0] };
            for (int i = 1; i < points.Length && left > 0; i++)
            {
                double length = Length(points[i - 1], points[i]);
                if (length <= left)
                {
                    part.Add(points[i]);
                    left -= length;
                }
                else
                {
                    double t = left / length;
                    part.Add(new PointF((float)(points[i - 1].X + (points[i].X - points[i - 1].X) * t),
                        (float)(points[i - 1].Y + (points[i].Y - points[i - 1].Y) * t)));
                    left = 0;
                }
            }
            return part.ToArray();
        }

        private static double Length(PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Hand the finished picture to Windows, with its see-through amounts.
        private void Present(Bitmap bitmap)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr memory = Native.CreateCompatibleDC(screen);
            IntPtr picture = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                picture = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = Native.SelectObject(memory, picture);
                var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                var source = new Native.POINT(0, 0);
                var position = new Native.POINT(Left, Top);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
                if (picture != IntPtr.Zero)
                {
                    Native.SelectObject(memory, previous);
                    Native.DeleteObject(picture);
                }
                Native.DeleteDC(memory);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && canvas != null)
            {
                canvas.Dispose();
                canvas = null;
            }
            base.Dispose(disposing);
        }

        #endregion

        #region Drawing helpers

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

        private static StringFormat TextFormat(StringAlignment horizontal, StringAlignment vertical, bool wrap)
        {
            var format = new StringFormat
            {
                Alignment = horizontal,
                LineAlignment = vertical,
                Trimming = StringTrimming.EllipsisCharacter
            };
            // Wrapped text shows whole lines only; a single line is never hidden, at worst trimmed.
            format.FormatFlags = wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap;
            return format;
        }

        /// <summary>Draw text in a box, cut short with "..." if it doesn't fit.</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle box,
            StringAlignment horizontal, StringAlignment vertical, bool wrap)
        {
            if (string.IsNullOrEmpty(text) || box.Width <= 0 || box.Height <= 0) return;
            using (StringFormat format = TextFormat(horizontal, vertical, wrap))
            using (var brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, box, format);
            }
        }

        /// <summary>How much room text needs, up to the given width.</summary>
        public static Size MeasureText(Graphics g, string text, Font font, int width, bool wrap)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return Size.Empty;
            using (StringFormat format = TextFormat(StringAlignment.Near, StringAlignment.Near, wrap))
            {
                return Size.Ceiling(g.MeasureString(text, font, width, format));
            }
        }

        /// <summary>Draw a picture as large as fits in the box (up to twice its size), centred.</summary>
        public static void DrawPicture(Graphics g, Image picture, Rectangle box, bool dim)
        {
            if (picture == null || box.Width <= 0 || box.Height <= 0) return;
            double scale = Math.Min(2.0, Math.Min((double)box.Width / picture.Width, (double)box.Height / picture.Height));
            int width = Math.Max(1, (int)(picture.Width * scale));
            int height = Math.Max(1, (int)(picture.Height * scale));
            var target = new Rectangle(box.Left + (box.Width - width) / 2, box.Top + (box.Height - height) / 2, width, height);
            if (!dim)
            {
                g.DrawImage(picture, target);
                return;
            }
            using (var attributes = new ImageAttributes())
            {
                var matrix = new ColorMatrix { Matrix33 = 0.35f };
                attributes.SetColorMatrix(matrix);
                g.DrawImage(picture, target, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        #endregion

        #region Touch

        private PopupElement ElementAt(Point point)
        {
            for (int i = Elements.Count - 1; i >= 0; i--)
            {
                PopupElement element = Elements[i];
                if (element.Visible && element.Enabled && element.Bounds.Contains(point)) return element;
            }
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            touched = ElementAt(e.Location);
            if (touched != null) touched.TouchDown(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (touched != null) touched.TouchMove(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || touched == null) return;
            PopupElement element = touched;
            touched = null;
            element.TouchUp(e.Location, element.Bounds.Contains(e.Location));
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (touched == null) return;
            // Lost the finger without a release (e.g. another window opened): cancel.
            PopupElement element = touched;
            touched = null;
            element.TouchUp(new Point(-10000, -10000), false);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            PopupElement element = ElementAt(e.Location);
            if (element != null) element.Wheel(e.Delta);
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

        #endregion

        private static class Native
        {
            public const int WS_EX_LAYERED = 0x00080000;
            public const int ULW_ALPHA = 0x00000002;
            public const byte AC_SRC_OVER = 0x00;
            public const byte AC_SRC_ALPHA = 0x01;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;

                public POINT(int x, int y)
                {
                    X = x;
                    Y = y;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SIZE
            {
                public int CX;
                public int CY;

                public SIZE(int cx, int cy)
                {
                    CX = cx;
                    CY = cy;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            public struct BLENDFUNCTION
            {
                public byte BlendOp;
                public byte BlendFlags;
                public byte SourceConstantAlpha;
                public byte AlphaFormat;
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

            [DllImport("user32.dll")]
            public static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

            [DllImport("gdi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

            [DllImport("gdi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteObject(IntPtr hObject);
        }
    }

    /// <summary>
    /// Something drawn inside a pop-up that may react to touch: a button, a list,
    /// a line of text. Positions are in pop-up pixels.
    /// </summary>
    public abstract class PopupElement
    {
        private Rectangle bounds;
        private bool visible = true;
        private bool enabled = true;

        internal AgOpenPopup Popup { get; set; }

        public Rectangle Bounds
        {
            get { return bounds; }
            set
            {
                if (bounds == value) return;
                bounds = value;
                Invalidate();
            }
        }

        public bool Visible
        {
            get { return visible; }
            set
            {
                if (visible == value) return;
                visible = value;
                Invalidate();
            }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value) return;
                enabled = value;
                Invalidate();
            }
        }

        /// <summary>Ask for the pop-up to be drawn again.</summary>
        public void Invalidate()
        {
            if (Popup != null) Popup.Redraw();
        }

        /// <summary>Draw inside Bounds.</summary>
        public abstract void Paint(Graphics g);

        /// <summary>A finger went down on this element.</summary>
        public virtual void TouchDown(Point point)
        {
        }

        /// <summary>The finger that went down on this element moved (it may be outside it now).</summary>
        public virtual void TouchMove(Point point)
        {
        }

        /// <summary>The finger lifted. inside: it was still on this element.</summary>
        public virtual void TouchUp(Point point, bool inside)
        {
        }

        public virtual void Wheel(int delta)
        {
        }
    }

    /// <summary>A line (or a few lines) of text.</summary>
    public class PopupLabel : PopupElement
    {
        private string text = "";

        public string Text
        {
            get { return text; }
            set
            {
                string newText = value ?? "";
                if (text == newText) return;
                text = newText;
                Invalidate();
            }
        }

        public Font Font { get; set; } = AgOpenPopup.InfoFont;
        public Color ForeColor { get; set; } = AgOpenPopup.TextColor;
        public StringAlignment Alignment { get; set; } = StringAlignment.Near;
        public bool Wrap { get; set; }

        public override void Paint(Graphics g)
        {
            AgOpenPopup.DrawText(g, text, Font, ForeColor, Bounds, Alignment, StringAlignment.Center, Wrap);
        }
    }

    /// <summary>
    /// A big solid touch button: a picture, a bold title and a smaller line under
    /// it. Square tiles put the picture on top; wide ones (Wide = true) put the text
    /// on the left and the picture on the right. Accent = the green main action.
    /// </summary>
    public class PopupTile : PopupElement
    {
        private Image picture;
        private string title = "";
        private string detail = "";
        private bool pressed;

        public event EventHandler Click;

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

        public bool Accent { get; set; }
        public bool Wide { get; set; }
        public Font TitleFont { get; set; } = AgOpenPopup.TitleFont;
        public Font DetailFont { get; set; } = AgOpenPopup.SmallFont;

        public override void TouchDown(Point point)
        {
            pressed = true;
            Invalidate();
        }

        public override void TouchMove(Point point)
        {
            bool inside = Bounds.Contains(point);
            if (inside == pressed) return;
            pressed = inside;
            Invalidate();
        }

        public override void TouchUp(Point point, bool inside)
        {
            bool wasPressed = pressed;
            pressed = false;
            Invalidate();
            if (inside && wasPressed && Enabled && Visible) Click?.Invoke(this, EventArgs.Empty);
        }

        public override void Paint(Graphics g)
        {
            Rectangle r = Bounds;
            var shape = new Rectangle(r.Left + 1, r.Top + 1, r.Width - 3, r.Height - 3);
            if (shape.Width < 8 || shape.Height < 8) return;

            Color fill;
            if (!Enabled) fill = AgOpenPopup.TileDisabledColor;
            else if (Accent) fill = pressed ? AgOpenPopup.AccentPressedColor : AgOpenPopup.AccentColor;
            else fill = pressed ? AgOpenPopup.TilePressedColor : AgOpenPopup.TileColor;

            using (GraphicsPath path = AgOpenPopup.RoundedRectangle(shape, Math.Min(16, shape.Height / 4)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Enabled && Accent ? AgOpenPopup.AccentPressedColor : AgOpenPopup.TileEdge, 1.5f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Color titleColor = !Enabled ? AgOpenPopup.DisabledText : Accent ? AgOpenPopup.AccentText : AgOpenPopup.TextColor;
            Color detailColor = !Enabled ? AgOpenPopup.DisabledText : Accent ? AgOpenPopup.AccentText : AgOpenPopup.DimText;
            if (Wide) PaintWide(g, shape, titleColor, detailColor);
            else PaintSquare(g, shape, titleColor, detailColor);
        }

        private void PaintSquare(Graphics g, Rectangle r, Color titleColor, Color detailColor)
        {
            int pad = Math.Max(12, r.Height / 12);
            int textWidth = r.Width - 2 * pad;
            int pictureHeight = picture != null ? (int)(r.Height * 0.30) : 0;
            Size titleSize = AgOpenPopup.MeasureText(g, title, TitleFont, textWidth, true);
            Size detailSize = AgOpenPopup.MeasureText(g, detail, DetailFont, textWidth, true);
            int gap = pad / 2;
            int total = pictureHeight + (pictureHeight > 0 ? gap : 0) + titleSize.Height + (detailSize.Height > 0 ? gap + detailSize.Height : 0);
            int top = r.Top + Math.Max(pad, (r.Height - total) / 2);

            if (pictureHeight > 0)
            {
                AgOpenPopup.DrawPicture(g, picture, new Rectangle(r.Left + pad, top, textWidth, pictureHeight), !Enabled);
                top += pictureHeight + gap;
            }
            if (titleSize.Height > 0)
            {
                AgOpenPopup.DrawText(g, title, TitleFont, titleColor, new Rectangle(r.Left + pad, top, textWidth, titleSize.Height),
                    StringAlignment.Center, StringAlignment.Near, true);
                top += titleSize.Height + gap;
            }
            if (detailSize.Height > 0)
            {
                int height = Math.Max(0, Math.Min(detailSize.Height, r.Bottom - pad / 2 - top));
                AgOpenPopup.DrawText(g, detail, DetailFont, detailColor, new Rectangle(r.Left + pad, top, textWidth, height),
                    StringAlignment.Center, StringAlignment.Near, true);
            }
        }

        private void PaintWide(Graphics g, Rectangle r, Color titleColor, Color detailColor)
        {
            int pad = Math.Max(8, r.Height / 6);
            int pictureSide = picture != null ? r.Height - 2 * pad : 0;
            if (pictureSide > 0)
            {
                AgOpenPopup.DrawPicture(g, picture, new Rectangle(r.Right - pad - pictureSide, r.Top + pad, pictureSide, pictureSide), !Enabled);
            }
            int textLeft = r.Left + pad + 6;
            int textWidth = r.Right - textLeft - pad - (pictureSide > 0 ? pictureSide + pad : 0);
            if (textWidth <= 0) return;

            if (detail.Length == 0)
            {
                AgOpenPopup.DrawText(g, title, TitleFont, titleColor, new Rectangle(textLeft, r.Top, textWidth, r.Height),
                    StringAlignment.Near, StringAlignment.Center, false);
                return;
            }
            int half = r.Height / 2;
            AgOpenPopup.DrawText(g, title, TitleFont, titleColor, new Rectangle(textLeft, r.Top + pad / 3, textWidth, half - pad / 3),
                StringAlignment.Near, StringAlignment.Far, false);
            AgOpenPopup.DrawText(g, detail, DetailFont, detailColor, new Rectangle(textLeft, r.Top + half, textWidth, half - pad / 3),
                StringAlignment.Near, StringAlignment.Near, false);
        }
    }

    /// <summary>
    /// A touch-friendly list: tall rows, drag up and down to scroll, tap to pick.
    /// Each row has a name on the left and a short note on the right.
    /// </summary>
    public class PopupList : PopupElement
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

        public event EventHandler SelectedIndexChanged;

        public int ItemHeight { get; set; } = 66;
        public Font Font { get; set; } = AgOpenPopup.ListFont;
        public Font DetailFont { get; set; } = AgOpenPopup.SmallFont;

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
            if (select >= 0 && select < items.Count)
            {
                SelectedIndex = select;
            }
            else
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
            else if (top + ItemHeight > offset + Bounds.Height) SetOffset(top + ItemHeight - Bounds.Height);
        }

        private int MaxOffset
        {
            get { return Math.Max(0, items.Count * ItemHeight - Bounds.Height); }
        }

        private void SetOffset(int value)
        {
            int clamped = Math.Max(0, Math.Min(MaxOffset, value));
            if (clamped == offset) return;
            offset = clamped;
            Invalidate();
        }

        public override void TouchDown(Point point)
        {
            down = true;
            dragged = false;
            downY = point.Y;
            downOffset = offset;
        }

        public override void TouchMove(Point point)
        {
            if (!down) return;
            int moved = point.Y - downY;
            if (!dragged && Math.Abs(moved) > DragStart) dragged = true;
            if (dragged) SetOffset(downOffset - moved);
        }

        public override void TouchUp(Point point, bool inside)
        {
            if (down && !dragged && inside)
            {
                int index = (point.Y - Bounds.Top + offset) / Math.Max(1, ItemHeight);
                if (index >= 0 && index < items.Count) SelectedIndex = index;
            }
            down = false;
            dragged = false;
        }

        public override void Wheel(int delta)
        {
            SetOffset(offset - delta / 120 * ItemHeight);
        }

        public override void Paint(Graphics g)
        {
            Rectangle r = Bounds;
            // The list may have been filled before it got its size.
            if (offset > MaxOffset) offset = MaxOffset;
            var shape = new Rectangle(r.Left, r.Top, r.Width - 1, r.Height - 1);
            using (GraphicsPath path = AgOpenPopup.RoundedRectangle(shape, 14))
            {
                using (var fill = new SolidBrush(AgOpenPopup.PanelColor))
                {
                    g.FillPath(fill, path);
                }

                GraphicsState state = g.Save();
                g.SetClip(path, CombineMode.Intersect);

                if (items.Count == 0)
                {
                    AgOpenPopup.DrawText(g, EmptyText, DetailFont, AgOpenPopup.DimText, Rectangle.Inflate(r, -18, -18),
                        StringAlignment.Center, StringAlignment.Center, true);
                }
                else
                {
                    PaintRows(g, r);
                }

                g.Restore(state);
                using (var pen = new Pen(AgOpenPopup.TileEdge, 1.5f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        private void PaintRows(Graphics g, Rectangle r)
        {
            int first = offset / Math.Max(1, ItemHeight);
            using (var selectedBrush = new SolidBrush(AgOpenPopup.AccentColor))
            using (var line = new Pen(AgOpenPopup.PanelLine, 1f))
            {
                for (int i = first; i < items.Count; i++)
                {
                    int top = r.Top + i * ItemHeight - offset;
                    if (top > r.Bottom) break;
                    var row = new Rectangle(r.Left, top, r.Width, ItemHeight);
                    bool isSelected = i == selected;
                    if (isSelected) g.FillRectangle(selectedBrush, row);

                    Item item = items[i];
                    int detailWidth = 0;
                    if (item.Detail.Length > 0)
                    {
                        detailWidth = AgOpenPopup.MeasureText(g, item.Detail, DetailFont, r.Width / 2, false).Width + 6;
                        AgOpenPopup.DrawText(g, item.Detail, DetailFont, isSelected ? AgOpenPopup.AccentText : AgOpenPopup.DimText,
                            new Rectangle(r.Right - 18 - detailWidth, top, detailWidth, ItemHeight),
                            StringAlignment.Far, StringAlignment.Center, false);
                    }
                    AgOpenPopup.DrawText(g, item.Text, Font, isSelected ? AgOpenPopup.AccentText : AgOpenPopup.TextColor,
                        new Rectangle(r.Left + 16, top, Math.Max(0, r.Width - 40 - detailWidth), ItemHeight),
                        StringAlignment.Near, StringAlignment.Center, false);
                    if (!isSelected) g.DrawLine(line, r.Left + 10, row.Bottom - 1, r.Right - 10, row.Bottom - 1);
                }
            }

            // Where we are in the list, when it doesn't all fit.
            int content = items.Count * ItemHeight;
            if (content > r.Height && r.Height > 0)
            {
                int thumb = Math.Max(36, r.Height * r.Height / content);
                int y = r.Top + (int)((r.Height - thumb) * (offset / (double)Math.Max(1, MaxOffset)));
                using (var brush = new SolidBrush(Color.FromArgb(150, 120, 126, 132)))
                {
                    g.FillRectangle(brush, r.Right - 9, y + 4, 6, Math.Max(10, thumb - 8));
                }
            }
        }
    }
}
