using System;
using System.Drawing;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// "Would you like to create headlands?": shown straight after Yes on "Make
    /// starting paths?" when the field has no headland yet. Yes, 1, 2 or 3 passes
    /// makes a headland that many implement widths wide, as AgOpenGPS's headland
    /// screen would; No, a touch on the map or 10 seconds closes it.
    /// AgOpenFieldWatch decides when to show it.
    /// </summary>
    public class FormAgOpenHeadlandAsk : AgOpenPopup
    {
        /// <summary>The choices offered, in passes.</summary>
        public const int MostPasses = 3;

        // Bold lettering for the Yes buttons, biggest first: the biggest that fits is used.
        private static readonly Font[] YesFonts =
        {
            TitleFont,
            new Font("Tahoma", 18F, FontStyle.Bold),
            new Font("Tahoma", 16F, FontStyle.Bold),
            new Font("Tahoma", 14F, FontStyle.Bold)
        };

        private readonly PopupLabel labelTitle;
        private readonly PopupLabel labelQuestion;
        private readonly PopupLabel labelInfo;
        private readonly PopupTile[] tilePasses = new PopupTile[MostPasses];
        private readonly PopupTile tileNo;

        /// <param name="made">What the starting paths box just made, e.g. "Made 'Border path' and 'Long side 87.3°'."</param>
        public FormAgOpenHeadlandAsk(FormGPS mf, string made) : base(mf)
        {
            EdgeNote = "Touch anywhere else to go to the map";

            labelTitle = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = made });
            labelQuestion = Add(new PopupLabel { Font = HeadingFont, Text = "Would you like to create headlands?" });
            labelInfo = Add(new PopupLabel
            {
                Font = InfoFont,
                ForeColor = DimText,
                Wrap = true,
                Text = "All around the field, one implement width per pass."
            });

            // Each Yes shows how wide that headland is, e.g. "120 ft".
            for (int i = 0; i < MostPasses; i++)
            {
                int passes = i + 1;
                tilePasses[i] = Add(new PopupTile
                {
                    Wide = true,
                    Centered = true,
                    Accent = true,
                    Title = passes == 1 ? "Yes, 1 pass" : "Yes, " + passes + " passes",
                    Detail = mf.AgOpenHeadlandWidthText(passes)
                });
                tilePasses[i].Click += (s, e) => Choose(passes);
            }

            tileNo = Add(new PopupTile { Wide = true, Centered = true, Title = "No" });
            tileNo.Click += (s, e) => Close();

            // In the same place as "Make starting paths?", near the top of the map.
            PlaceOverMap(0.62, 0.42, new Size(800, 300), new Size(1060, 330), 0.06);
        }

        /// <summary>How many passes wide the headland should be; 0 for none.</summary>
        public int Passes { get; private set; }

        // Don't take the keyboard away from AgOpenGPS.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private void Choose(int passes)
        {
            Passes = passes;
            Close();
        }

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileNo == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;

            // The text at the top; the four buttons across the bottom, No narrower.
            const int gap = 16;
            int buttonHeight = 78;
            int buttonTop = y + h - buttonHeight;
            int yesWidth = (int)((w - MostPasses * gap) * 0.28);
            Font yesFont = FittingFont(yesWidth, buttonHeight);
            int left = x;
            foreach (PopupTile tile in tilePasses)
            {
                tile.TitleFont = yesFont;
                tile.Bounds = new Rectangle(left, buttonTop, yesWidth, buttonHeight);
                left += yesWidth + gap;
            }
            tileNo.Bounds = new Rectangle(left, buttonTop, x + w - left, buttonHeight);

            labelTitle.Bounds = new Rectangle(x, y, w, 30);
            labelQuestion.Bounds = new Rectangle(x, y + 30, w, 44);
            labelInfo.Bounds = new Rectangle(x, y + 80, w, Math.Max(0, buttonTop - gap - (y + 80)));
        }

        // The biggest Yes lettering the longest Yes fits on one line in (as PopupTile lays out a wide button).
        private Font FittingFont(int tileWidth, int tileHeight)
        {
            int pad = Math.Max(8, (tileHeight - 3) / 6);
            int textWidth = tileWidth - 3 - 2 * pad - 6 - 4;
            string longest = tilePasses[MostPasses - 1].Title;
            using (var bitmap = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                foreach (Font font in YesFonts)
                {
                    if (MeasureText(g, longest, font, 10000, false).Width <= textWidth) return font;
                }
            }
            return YesFonts[YesFonts.Length - 1];
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartCountdown(10);
        }
    }
}
