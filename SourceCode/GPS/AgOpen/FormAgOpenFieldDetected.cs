using System;
using System.Collections.Generic;
using System.Drawing;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// "Field detected": shown on the map, with no field open, when the tractor
    /// drives up to or into a field. Load opens the field; Ignore, a touch on the
    /// map or 30 seconds closes it. When a second field is close too, it's offered
    /// underneath. AgOpenFieldWatch decides when to show it.
    /// </summary>
    public class FormAgOpenFieldDetected : AgOpenPopup
    {
        /// <summary>A field to offer, with how far the tractor is from it.</summary>
        public class Offer
        {
            public AgOpenFieldEntry Entry;
            public AgOpenFieldShape Shape;
            public double DistanceM;
        }

        private readonly Offer main;
        private readonly Offer second;

        private readonly PopupLabel labelTitle;
        private readonly PopupLabel labelName;
        private readonly PopupLabel labelInfo;
        private readonly FieldPreview preview;
        private readonly PopupTile tileLoad;
        private readonly PopupTile tileIgnore;
        private readonly PopupTile tileSecond;
        private readonly List<Image> pictures = new List<Image>();

        public FormAgOpenFieldDetected(FormGPS mf, Offer main, Offer second) : base(mf)
        {
            this.main = main;
            this.second = second;
            EdgeNote = "Touch anywhere else to go to the map";

            labelTitle = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "Field detected" });
            labelName = Add(new PopupLabel { Font = HeadingFont, Text = main.Entry.Folder });
            labelInfo = Add(new PopupLabel { Font = InfoFont, ForeColor = DimText, Text = Describe(main) });

            preview = Add(new FieldPreview());
            Wgs84? here = mf.AgOpenPosition();
            preview.ShowField(main.Shape, here.HasValue ? AgOpenFields.ToField(main.Shape, here.Value) : null);

            tileLoad = Add(new PopupTile { Wide = true, Accent = true, Picture = Keep(Properties.Resources.FileOpen), Title = "Load" });
            tileLoad.Click += (s, e) => Choose(main.Entry.Folder);

            tileIgnore = Add(new PopupTile { Wide = true, Picture = Keep(Properties.Resources.Cancel64), Title = "Ignore" });
            tileIgnore.Click += (s, e) => Close();

            if (second != null)
            {
                tileSecond = Add(new PopupTile
                {
                    Wide = true,
                    Picture = Keep(Properties.Resources.FileOpen),
                    Title = "Load " + second.Entry.Folder + " instead",
                    Detail = Describe(second)
                });
                tileSecond.Click += (s, e) => Choose(second.Entry.Folder);
            }

            // Near the top of the map, out of the way of the tractor.
            PlaceOverMap(0.60, 0.40, new Size(780, 340), new Size(1040, 480), 0.06, second != null ? 100 : 0);
        }

        /// <summary>The field folder the operator chose to load, or null (ignored).</summary>
        public string ChosenFolder { get; private set; }

        // Don't take the keyboard away from AgOpenGPS when it pops up while driving.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private Image Keep(Image picture)
        {
            pictures.Add(picture);
            return picture;
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

        private string Describe(Offer offer)
        {
            string where = offer.DistanceM <= 0 ? "you're in this field"
                : AgOpenFields.Distance(offer.DistanceM, mf.isMetric) + " away";
            return offer.Shape.Fences.Count > 0
                ? AgOpenFields.Area(offer.Shape.AreaM2, mf.isMetric) + "   ·   " + where
                : where;
        }

        private void Choose(string folder)
        {
            ChosenFolder = folder;
            Close();
        }

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileIgnore == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;

            int secondHeight = tileSecond != null ? 78 : 0;
            int mainHeight = h - (secondHeight > 0 ? secondHeight + 18 : 0);
            int leftWidth = (int)(w * 0.60);

            labelTitle.Bounds = new Rectangle(x, y, leftWidth, 30);
            labelName.Bounds = new Rectangle(x, y + 30, leftWidth, 44);
            labelInfo.Bounds = new Rectangle(x, y + 74, leftWidth, 34);

            int buttonHeight = 78;
            int buttonWidth = (leftWidth - 16) / 2;
            int buttonTop = y + mainHeight - buttonHeight;
            tileLoad.Bounds = new Rectangle(x, buttonTop, buttonWidth, buttonHeight);
            tileIgnore.Bounds = new Rectangle(x + buttonWidth + 16, buttonTop, leftWidth - buttonWidth - 16, buttonHeight);

            int previewLeft = x + leftWidth + 20;
            preview.Bounds = new Rectangle(previewLeft, y, Math.Max(40, x + w - previewLeft), mainHeight);

            if (tileSecond != null)
            {
                tileSecond.Bounds = new Rectangle(x, y + h - secondHeight, w, secondHeight);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartCountdown(30);
        }
    }
}
