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
    /// <summary>Which side of a button its label goes on.</summary>
    public enum LabelSide { Right, Left, Above, Below, OnButton }

    /// <summary>One label: a small light box with dark words, and a point toward its button.</summary>
    public sealed class AgOpenLabelChip
    {
        /// <summary>The box, in the overlay's own pixels.</summary>
        public Rectangle Box;
        public string Text = "";
        public Font Font;

        /// <summary>The side of the button the label is on (OnButton: drawn over the button, no point).</summary>
        public LabelSide Side;

        /// <summary>Where the point touches the button's edge, in the overlay's pixels.</summary>
        public Point Tip;

        /// <summary>The button (or row of buttons) the label is for, on screen; not drawn.</summary>
        public Rectangle Button;
    }

    /// <summary>
    /// A see-through layer over a window that shows button labels. Touches go
    /// straight through it to the buttons and map underneath, and it never takes
    /// the keyboard. Drawn as one picture with its own see-through amount per pixel
    /// (a layered window), like the AgOpen pop-ups.
    /// </summary>
    public sealed class AgOpenLabelOverlay : Form
    {
        // Light boxes with dark lettering, as on the AgOpen pop-ups (readable in sun).
        private static readonly Color ChipFill = Color.FromArgb(238, 250, 251, 252);
        private static readonly Color ChipEdge = Color.FromArgb(150, 156, 162);
        private static readonly Color ChipText = Color.FromArgb(18, 22, 26);
        private static readonly Color ShadowColor = Color.FromArgb(70, 0, 0, 0);

        private Bitmap canvas;
        private byte alpha = 255;

        public AgOpenLabelOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "AgOpen labels";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>How solid the labels are, 0..255 (for fading out).</summary>
        public byte Opacity255
        {
            get { return alpha; }
            set
            {
                if (alpha == value) return;
                alpha = value;
                if (canvas != null && IsHandleCreated && Visible) Present();
            }
        }

        /// <summary>Show these labels over a screen area, staying just above owner.</summary>
        public void ShowLabels(Form owner, Rectangle screenArea, List<AgOpenLabelChip> chips)
        {
            if (IsDisposed) return;
            if (Bounds != screenArea) Bounds = screenArea;
            if (!Visible)
            {
                if (owner != null && !owner.IsDisposed) Show(owner);
                else Show();
            }
            alpha = 255;
            Render(chips);
        }

        private void Render(List<AgOpenLabelChip> chips)
        {
            int width = Math.Max(1, Width), height = Math.Max(1, Height);
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
                foreach (AgOpenLabelChip chip in chips) DrawChip(g, chip);
            }
            Present();
        }

        /// <summary>Draw one label: a rounded box, a small point toward its button, the words.</summary>
        public static void DrawChip(Graphics g, AgOpenLabelChip chip)
        {
            Rectangle box = chip.Box;
            if (box.Width < 6 || box.Height < 6) return;
            int radius = Math.Min(10, box.Height / 3);

            using (GraphicsPath path = AgOpenPopup.RoundedRectangle(box, radius))
            {
                // Soft shadow just below and right.
                using (var shadow = new GraphicsPath())
                {
                    shadow.AddPath(path, false);
                    using (var move = new Matrix())
                    {
                        move.Translate(2, 3);
                        shadow.Transform(move);
                    }
                    using (var brush = new SolidBrush(ShadowColor)) g.FillPath(brush, shadow);
                }

                PointF[] point = Pointer(chip);
                using (var fill = new SolidBrush(ChipFill))
                using (var edge = new Pen(ChipEdge, 1.5f))
                {
                    if (point != null && Length(point[1], chip.Tip) > 1.5)
                    {
                        // The button is further away: a thin line from the point to it.
                        using (var lead = new Pen(ChipFill, 4f))
                        using (var leadEdge = new Pen(ChipEdge, 6f))
                        {
                            g.DrawLine(leadEdge, point[1], chip.Tip);
                            g.DrawLine(lead, point[1], chip.Tip);
                        }
                    }
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                    if (point != null)
                    {
                        g.FillPolygon(fill, point);
                        g.DrawLine(edge, point[0], point[1]);
                        g.DrawLine(edge, point[1], point[2]);
                    }
                }
            }

            Rectangle textBox = Rectangle.Inflate(box, -6, -3);
            AgOpenPopup.DrawText(g, chip.Text, chip.Font, ChipText, textBox, StringAlignment.Center, StringAlignment.Center, true);
        }

        /// <summary>How far a label's point reaches out of its box; past that, a line carries on to the button.</summary>
        public const int PointerReach = 10;

        // A small triangle out of the box edge, on the side facing the button; it
        // reaches the button if that's close, otherwise a line carries on from it.
        private static PointF[] Pointer(AgOpenLabelChip chip)
        {
            Rectangle b = chip.Box;
            const float half = 7f, reach = PointerReach;
            switch (chip.Side)
            {
                case LabelSide.Right: // label right of the button: point out of its left edge
                    {
                        float y = Math.Max(b.Top + half + 2, Math.Min(b.Bottom - half - 2, chip.Tip.Y));
                        return new[] { new PointF(b.Left + 1, y - half), new PointF(Math.Max(b.Left - reach, chip.Tip.X), y), new PointF(b.Left + 1, y + half) };
                    }
                case LabelSide.Left:
                    {
                        float y = Math.Max(b.Top + half + 2, Math.Min(b.Bottom - half - 2, chip.Tip.Y));
                        return new[] { new PointF(b.Right - 1, y - half), new PointF(Math.Min(b.Right + reach, chip.Tip.X), y), new PointF(b.Right - 1, y + half) };
                    }
                case LabelSide.Above:
                    {
                        float x = Math.Max(b.Left + half + 2, Math.Min(b.Right - half - 2, chip.Tip.X));
                        return new[] { new PointF(x - half, b.Bottom - 1), new PointF(x, Math.Min(b.Bottom + reach, chip.Tip.Y)), new PointF(x + half, b.Bottom - 1) };
                    }
                case LabelSide.Below:
                    {
                        float x = Math.Max(b.Left + half + 2, Math.Min(b.Right - half - 2, chip.Tip.X));
                        return new[] { new PointF(x - half, b.Top + 1), new PointF(x, Math.Max(b.Top - reach, chip.Tip.Y)), new PointF(x + half, b.Top + 1) };
                    }
                default:
                    return null;
            }
        }

        private static double Length(PointF a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Hand the picture to Windows, with its see-through amounts.
        private void Present()
        {
            if (canvas == null || !IsHandleCreated) return;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr memory = Native.CreateCompatibleDC(screen);
            IntPtr picture = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                picture = canvas.GetHbitmap(Color.FromArgb(0));
                previous = Native.SelectObject(memory, picture);
                var size = new Native.SIZE(canvas.Width, canvas.Height);
                var source = new Native.POINT(0, 0);
                var position = new Native.POINT(Left, Top);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = alpha,
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

        private static class Native
        {
            public const int WS_EX_LAYERED = 0x00080000;
            public const int WS_EX_TRANSPARENT = 0x00000020;
            public const int WS_EX_NOACTIVATE = 0x08000000;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
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
                IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

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

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteObject(IntPtr hObject);
        }
    }
}
