using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// A small drawing of a field, north up: the boundary filled in, inner
    /// boundaries cut out, guidance lines in orange, and a blue dot where the
    /// tractor is when it's in or near the field.
    /// </summary>
    public class FieldPreview : Control
    {
        private static readonly Color FieldFill = Color.FromArgb(70, 120, 190, 110);
        private static readonly Color FieldEdge = Color.FromArgb(235, 240, 235);
        private static readonly Color LineColor = Color.FromArgb(255, 170, 60);
        private static readonly Color HereColor = Color.FromArgb(40, 160, 255);

        private AgOpenFieldShape shape;
        private GeoCoord? here;

        public FieldPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = AgOpenPopup.ListColor;
            ForeColor = AgOpenPopup.DimText;
            Font = AgOpenPopup.InfoFont;
        }

        /// <summary>Shown when there's nothing to draw.</summary>
        public string EmptyText { get; set; } = "";

        /// <summary>Draw this field (null to clear). here: the tractor in the field's metres, if known.</summary>
        public void ShowField(AgOpenFieldShape field, GeoCoord? tractor)
        {
            shape = field;
            here = tractor;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool hasFence = shape != null && shape.Fences.Count > 0;
            bool hasLines = shape != null && shape.Tracks.Count > 0;
            if (!hasFence && !hasLines)
            {
                string text = shape == null ? EmptyText : "No boundary or lines in this field yet";
                TextRenderer.DrawText(g, text, Font, new Rectangle(16, 0, Width - 32, Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            // What to fit in the drawing: the boundaries, or the lines if there's no boundary.
            double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
            if (hasFence)
            {
                foreach (List<vec3> fence in shape.Fences)
                {
                    foreach (vec3 p in fence) Grow(p.easting, p.northing, ref minE, ref minN, ref maxE, ref maxN);
                }
            }
            else
            {
                foreach (CTrk track in shape.Tracks)
                {
                    Grow(track.ptA.easting, track.ptA.northing, ref minE, ref minN, ref maxE, ref maxN);
                    Grow(track.ptB.easting, track.ptB.northing, ref minE, ref minN, ref maxE, ref maxN);
                    foreach (vec3 p in track.curvePts) Grow(p.easting, p.northing, ref minE, ref minN, ref maxE, ref maxN);
                }
            }

            // Show the tractor only when it's near: far away it would shrink the field to a dot.
            double spanE = Math.Max(1.0, maxE - minE), spanN = Math.Max(1.0, maxN - minN);
            bool showHere = false;
            if (here.HasValue)
            {
                double padE = spanE * 0.3, padN = spanN * 0.3;
                double hereE = here.Value.Easting, hereN = here.Value.Northing;
                if (hereE > minE - padE && hereE < maxE + padE && hereN > minN - padN && hereN < maxN + padN)
                {
                    showHere = true;
                    Grow(hereE, hereN, ref minE, ref minN, ref maxE, ref maxN);
                    spanE = Math.Max(1.0, maxE - minE);
                    spanN = Math.Max(1.0, maxN - minN);
                }
            }

            const float margin = 18f;
            double scale = Math.Min((Width - 2 * margin) / spanE, (Height - 2 * margin) / spanN);
            if (scale <= 0) return;
            double midE = (minE + maxE) / 2.0, midN = (minN + maxN) / 2.0;
            double centreX = Width / 2.0, centreY = Height / 2.0;

            PointF Map(double east, double north)
            {
                return new PointF((float)(centreX + (east - midE) * scale), (float)(centreY - (north - midN) * scale));
            }

            if (hasFence)
            {
                using (var path = new GraphicsPath(FillMode.Alternate))
                {
                    foreach (List<vec3> fence in shape.Fences)
                    {
                        var points = new PointF[fence.Count];
                        for (int i = 0; i < fence.Count; i++) points[i] = Map(fence[i].easting, fence[i].northing);
                        path.AddPolygon(points);
                    }
                    using (var fill = new SolidBrush(FieldFill))
                    using (var edge = new Pen(FieldEdge, 2f))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                }
            }

            using (var line = new Pen(LineColor, 2f))
            {
                double reach = (spanE + spanN) * 2.0;
                foreach (CTrk track in shape.Tracks)
                {
                    if (track.mode == TrackMode.AB)
                    {
                        // AB lines go on forever; draw them across the whole picture.
                        double dirE = Math.Sin(track.heading), dirN = Math.Cos(track.heading);
                        PointF a = Map(track.ptA.easting - dirE * reach, track.ptA.northing - dirN * reach);
                        PointF b = Map(track.ptA.easting + dirE * reach, track.ptA.northing + dirN * reach);
                        g.DrawLine(line, a, b);
                    }
                    else if (track.curvePts.Count > 1)
                    {
                        var points = new PointF[track.curvePts.Count];
                        for (int i = 0; i < track.curvePts.Count; i++) points[i] = Map(track.curvePts[i].easting, track.curvePts[i].northing);
                        g.DrawLines(line, points);
                    }
                }
            }

            if (showHere)
            {
                PointF p = Map(here.Value.Easting, here.Value.Northing);
                const float radius = 8f;
                using (var fill = new SolidBrush(HereColor))
                using (var edge = new Pen(Color.White, 2f))
                {
                    g.FillEllipse(fill, p.X - radius, p.Y - radius, 2 * radius, 2 * radius);
                    g.DrawEllipse(edge, p.X - radius, p.Y - radius, 2 * radius, 2 * radius);
                }
            }
        }

        private static void Grow(double east, double north, ref double minE, ref double minN, ref double maxE, ref double maxN)
        {
            if (east < minE) minE = east;
            if (east > maxE) maxE = east;
            if (north < minN) minN = north;
            if (north > maxN) maxN = north;
        }
    }
}
