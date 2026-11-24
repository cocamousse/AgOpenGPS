using System;
using System.Collections.Generic;
using System.Drawing;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// "Make starting paths?": shown when an open field has a boundary but no lines
    /// (it was loaded without any, or its boundary was just made). Yes makes a path
    /// round the border and a curve along the longest side, then (for a field with
    /// no headland) asks "Would you like to create headlands?"; Create paths opens
    /// AgOpenGPS's draw screen; No, a touch on the map or 10 seconds closes it.
    /// The drawing shows what Yes makes. AgOpenFieldWatch decides when to show it.
    /// </summary>
    public class FormAgOpenStartingPaths : AgOpenPopup
    {
        /// <summary>What the operator chose.</summary>
        public enum Answer { None, Make, Draw }

        private readonly PopupLabel labelTitle;
        private readonly PopupLabel labelQuestion;
        private readonly PopupLabel labelInfo;
        private readonly FieldPreview preview;
        private readonly PopupTile tileYes;
        private readonly PopupTile tileDraw;
        private readonly PopupTile tileNo;

        /// <param name="fences">The field's boundaries (outer first), for the drawing.</param>
        public FormAgOpenStartingPaths(FormGPS mf, string folder, AgOpenStartingPaths.Plan plan, List<List<vec3>> fences)
            : base(mf)
        {
            EdgeNote = "Touch anywhere else to go to the map";
            bool hasSide = plan.Side != null;

            labelTitle = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "No paths in " + folder + " yet" });
            labelQuestion = Add(new PopupLabel { Font = HeadingFont, Text = "Make starting paths?" });
            labelInfo = Add(new PopupLabel
            {
                Font = InfoFont,
                ForeColor = DimText,
                Wrap = true,
                Text = hasSide
                    ? "A path around the border (orange) and a curve along the longest side (blue)."
                    : "A path around the border (orange). This field has no clear side for a curve: Create paths lets you draw one."
            });

            preview = Add(new FieldPreview());
            var shape = new AgOpenFieldShape();
            shape.Fences.AddRange(fences);
            shape.Tracks.Add(plan.Border);
            preview.ShowField(shape, null);
            preview.ShowMark(hasSide ? plan.SidePoints : null);

            tileYes = Add(new PopupTile { Wide = true, Centered = true, Accent = true, Title = hasSide ? "Yes, make them" : "Yes, make it" });
            tileYes.Click += (s, e) => Choose(Answer.Make);

            tileDraw = Add(new PopupTile { Wide = true, Centered = true, Title = "Create paths" });
            tileDraw.Click += (s, e) => Choose(Answer.Draw);

            tileNo = Add(new PopupTile { Wide = true, Centered = true, Title = "No" });
            tileNo.Click += (s, e) => Close();

            // Near the top of the map, out of the way of the tractor.
            PlaceOverMap(0.62, 0.46, new Size(800, 380), new Size(1060, 500), 0.06);
        }

        /// <summary>Yes (Make), Create paths (Draw), or None.</summary>
        public Answer Choice { get; private set; }

        // Don't take the keyboard away from AgOpenGPS.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private void Choose(Answer answer)
        {
            Choice = answer;
            Close();
        }

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileNo == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;

            // The three buttons across the bottom; text on the left and the drawing on the right above them.
            const int gap = 16;
            int buttonHeight = 78;
            int buttonTop = y + h - buttonHeight;
            int yesWidth = (int)((w - 2 * gap) * 0.40);
            int drawWidth = (int)((w - 2 * gap) * 0.35);
            tileYes.Bounds = new Rectangle(x, buttonTop, yesWidth, buttonHeight);
            tileDraw.Bounds = new Rectangle(x + yesWidth + gap, buttonTop, drawWidth, buttonHeight);
            tileNo.Bounds = new Rectangle(x + yesWidth + drawWidth + 2 * gap, buttonTop, w - yesWidth - drawWidth - 2 * gap, buttonHeight);

            int upper = Math.Max(40, h - buttonHeight - gap);
            int leftWidth = (int)(w * 0.58);
            labelTitle.Bounds = new Rectangle(x, y, leftWidth, 30);
            labelQuestion.Bounds = new Rectangle(x, y + 30, leftWidth, 44);
            labelInfo.Bounds = new Rectangle(x, y + 80, leftWidth, Math.Max(0, upper - 80));

            int previewLeft = x + leftWidth + 20;
            preview.Bounds = new Rectangle(previewLeft, y, Math.Max(40, x + w - previewLeft), upper);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartCountdown(10);
        }
    }
}
