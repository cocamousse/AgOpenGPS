using System;
using System.Collections.Generic;
using System.Drawing;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// "Field loaded. Start a job?": a small box shown whenever a field itself (not
    /// a job folder) opens, however it was opened. Yes opens the job prompt; a touch
    /// on the map or 30 seconds closes it, and the field stays open with no job
    /// (for editing paths or the boundary). AgOpenFieldWatch decides when to show it.
    /// </summary>
    public class FormAgOpenStartJobAsk : AgOpenPopup
    {
        private readonly PopupLabel labelTitle;
        private readonly PopupLabel labelName;
        private readonly PopupLabel labelInfo;
        private readonly PopupTile tileYes;
        private readonly List<Image> pictures = new List<Image>();

        public FormAgOpenStartJobAsk(FormGPS mf, string folder) : base(mf)
        {
            EdgeNote = "Touch anywhere else to keep the field open without a job";

            labelTitle = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "Field loaded" });
            labelName = Add(new PopupLabel { Font = HeadingFont, Text = folder });
            labelInfo = Add(new PopupLabel
            {
                Font = InfoFont,
                ForeColor = DimText,
                Wrap = true,
                Text = "Start a job? Work done without one isn't sent to the hub."
            });

            Image ok = Properties.Resources.OK64;
            pictures.Add(ok);
            tileYes = Add(new PopupTile { Wide = true, Accent = true, Picture = ok, Title = "Yes, start a job" });
            tileYes.Click += (s, e) =>
            {
                StartJob = true;
                Close();
            };

            // Near the top of the map, out of the way of the tractor.
            PlaceOverMap(0.42, 0.26, new Size(560, 230), new Size(700, 280), 0.06);
        }

        /// <summary>Yes was tapped.</summary>
        public bool StartJob { get; private set; }

        // Don't take the keyboard away from AgOpenGPS.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Image picture in pictures) picture.Dispose();
                pictures.Clear();
            }
            base.Dispose(disposing);
        }

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileYes == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;

            labelTitle.Bounds = new Rectangle(x, y, w, 30);
            labelName.Bounds = new Rectangle(x, y + 30, w, 44);
            int buttonHeight = 74;
            labelInfo.Bounds = new Rectangle(x, y + 76, w, Math.Max(0, h - 76 - buttonHeight - 10));
            tileYes.Bounds = new Rectangle(x, y + h - buttonHeight, w, buttonHeight);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartCountdown(30);
        }
    }
}
