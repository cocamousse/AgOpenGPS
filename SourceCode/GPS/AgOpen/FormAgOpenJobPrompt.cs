using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// The job prompt, shown when a field is loaded from the start screen or from
    /// "Field detected". AgOpenGPS guesses the operation and crop from the loaded
    /// implement, and the operator taps:
    /// - Start: accept the guess; a fresh job folder is made and opened.
    /// - Change: pick the operation and crop, then Start.
    /// - Fix later at home: start anyway; the hub flags the job for review.
    /// A touch anywhere else goes back to the map without opening anything.
    /// </summary>
    public class FormAgOpenJobPrompt : AgOpenPopup
    {
        private const string OtherCrop = "\u0001other";
        private const int MaxCrops = 10;

        private static readonly Color HintColor = Color.FromArgb(160, 72, 0);

        private readonly string masterFolder;
        private readonly AgOpenJobGuess guess;

        private string operation;
        private string crop;
        private bool changing;

        // First page: the guess.
        private readonly PopupLabel labelNew;
        private readonly PopupLabel labelField;
        private readonly PopupLabel labelGuess;
        private readonly PopupLabel labelEquipment;
        private readonly PopupLabel labelFolder;
        private readonly PopupLabel labelHint;
        private readonly FieldPreview preview;
        private readonly PopupTile tileStart;
        private readonly PopupTile tileChange;
        private readonly PopupTile tileFixLater;

        // Change page: pick the operation and crop.
        private readonly PopupLabel labelOperation;
        private readonly PopupLabel labelCrop;
        private readonly PopupLabel labelChangeHint;
        private readonly PopupTile tileBack;
        private readonly PopupTile tileStartChanged;
        private readonly Dictionary<PopupTile, string> operationTiles = new Dictionary<PopupTile, string>();
        private readonly Dictionary<PopupTile, string> cropTiles = new Dictionary<PopupTile, string>();
        private readonly List<PopupTile> operationOrder = new List<PopupTile>();
        private readonly List<PopupTile> cropOrder = new List<PopupTile>();

        private readonly List<PopupElement> guessPage = new List<PopupElement>();
        private readonly List<PopupElement> changePage = new List<PopupElement>();
        private readonly List<Image> pictures = new List<Image>();

        public FormAgOpenJobPrompt(FormGPS mf, string masterFolder, AgOpenJobGuess guess) : base(mf)
        {
            this.masterFolder = masterFolder;
            this.guess = guess;
            operation = guess.Operation;
            crop = guess.Crop;
            EdgeNote = "Touch anywhere else to go to the map";

            // First page
            labelNew = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "New job" });
            labelField = Add(new PopupLabel { Font = HeadingFont, Text = masterFolder });
            labelGuess = Add(new PopupLabel { Font = TitleFont });
            labelEquipment = Add(new PopupLabel { Font = InfoFont, ForeColor = DimText, Text = Equipment() });
            labelFolder = Add(new PopupLabel { Font = InfoFont, ForeColor = DimText });
            labelHint = Add(new PopupLabel { Font = InfoFont, ForeColor = HintColor, Wrap = true });

            AgOpenFieldShape shape = AgOpenFields.Shape(new AgOpenFieldEntry
            {
                Folder = masterFolder,
                FieldDirectory = System.IO.Path.Combine(RegistrySettings.fieldsDirectory, masterFolder)
            });
            preview = Add(new FieldPreview());
            Wgs84? here = mf.AgOpenPosition();
            preview.ShowField(shape, here.HasValue ? AgOpenFields.ToField(shape, here.Value) : null);

            tileStart = Add(new PopupTile { Wide = true, Accent = true, Picture = Keep(Properties.Resources.OK64), Title = "Start" });
            tileStart.Click += (s, e) => Finish(false);
            tileChange = Add(new PopupTile { Wide = true, Centered = true, Title = "Change" });
            tileChange.Click += (s, e) => ShowPage(true);
            tileFixLater = Add(new PopupTile { Wide = true, Centered = true, Title = "Fix later at home" });
            tileFixLater.Click += (s, e) => Finish(true);

            guessPage.AddRange(new PopupElement[]
            {
                labelNew, labelField, labelGuess, labelEquipment, labelFolder, labelHint, preview, tileStart, tileChange, tileFixLater
            });

            // Change page
            labelOperation = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "What are you doing?" });
            foreach (string op in guess.Operations)
            {
                PopupTile tile = Add(new PopupTile { Wide = true, Centered = true, TitleFont = ListFont, Title = Capitalize(op) });
                string value = op;
                tile.Click += (s, e) =>
                {
                    operation = value;
                    ShowChoices();
                };
                operationTiles[tile] = value;
                operationOrder.Add(tile);
                changePage.Add(tile);
            }
            labelCrop = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText, Text = "Crop" });
            labelChangeHint = Add(new PopupLabel { Font = InfoFont, ForeColor = HintColor, Wrap = true });
            tileBack = Add(new PopupTile { Wide = true, Picture = Keep(Properties.Resources.back_button), Title = "Back" });
            tileBack.Click += (s, e) => ShowPage(false);
            tileStartChanged = Add(new PopupTile { Wide = true, Accent = true, Picture = Keep(Properties.Resources.OK64), Title = "Start" });
            tileStartChanged.Click += (s, e) => Finish(false);
            changePage.AddRange(new PopupElement[] { labelOperation, labelCrop, labelChangeHint, tileBack, tileStartChanged });
            BuildCropTiles();

            ShowPage(false);
        }

        /// <summary>What the operator settled on, or null if they went back to the map.</summary>
        public AgOpenJobChoice Choice { get; private set; }

        private Image Keep(Image picture)
        {
            pictures.Add(picture);
            return picture;
        }

        private string Equipment()
        {
            if (guess.Tool.Length > 0 && guess.Vehicle.Length > 0) return guess.Tool + " on " + guess.Vehicle;
            if (guess.Tool.Length > 0) return guess.Tool;
            return guess.Vehicle;
        }

        private static string Capitalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        // One tile per crop (most recent first), plus "No crop" and "Other...".
        private void BuildCropTiles()
        {
            foreach (PopupTile old in cropOrder)
            {
                Elements.Remove(old);
                changePage.Remove(old);
            }
            cropTiles.Clear();
            cropOrder.Clear();

            var values = new List<string>();
            foreach (string name in guess.Crops)
            {
                if (values.Count >= MaxCrops) break;
                values.Add(name);
            }
            if (!string.IsNullOrEmpty(crop) && !values.Exists(v => string.Equals(v, crop, StringComparison.OrdinalIgnoreCase)))
            {
                values.Insert(0, crop);
            }
            values.Add(null);
            values.Add(OtherCrop);

            foreach (string value in values)
            {
                string title = value == null ? "No crop" : value == OtherCrop ? "Other..." : value;
                PopupTile tile = Add(new PopupTile { Wide = true, Centered = true, TitleFont = ListFont, Title = title });
                string picked = value;
                tile.Click += (s, e) => PickCrop(picked);
                cropTiles[tile] = value;
                cropOrder.Add(tile);
                changePage.Add(tile);
            }
        }

        private void PickCrop(string value)
        {
            if (value != OtherCrop)
            {
                crop = value;
                ShowChoices();
                return;
            }
            using (var form = new FormAgOpenText("New crop", "Crop name:", ""))
            {
                if (form.ShowDialog(this) != DialogResult.OK || form.Value.Length == 0) return;
                crop = form.Value;
            }
            guess.Crops.RemoveAll(c => string.Equals(c, crop, StringComparison.OrdinalIgnoreCase));
            guess.Crops.Insert(0, crop);
            BuildCropTiles();
            foreach (PopupTile tile in cropOrder) tile.Visible = changing;
            LayoutElements();
            ShowChoices();
        }

        private bool CanStart
        {
            get { return !string.IsNullOrEmpty(operation) && (!AgOpenJobs.CropRequired(guess, operation) || !string.IsNullOrEmpty(crop)); }
        }

        private string Hint()
        {
            if (string.IsNullOrEmpty(operation))
            {
                return changing ? "Pick what you're doing." : "The implement isn't known yet: tap Change to pick what you're doing.";
            }
            if (AgOpenJobs.CropRequired(guess, operation) && string.IsNullOrEmpty(crop))
            {
                return Capitalize(operation) + " needs a crop" + (changing ? "." : ": tap Change to pick it.");
            }
            return "";
        }

        // Show the current operation and crop on both pages.
        private void ShowChoices()
        {
            labelGuess.Text = string.IsNullOrEmpty(operation)
                ? "Operation not known"
                : Capitalize(operation) + "   ·   " + (string.IsNullOrEmpty(crop) ? "no crop" : crop);
            var choice = new AgOpenJobChoice { Operation = operation, Crop = crop };
            labelFolder.Text = "New folder: " + AgOpenJobs.FolderName(masterFolder, guess, choice, DateTime.Now);
            labelHint.Text = Hint();
            labelChangeHint.Text = Hint();
            tileStart.Enabled = CanStart;
            tileStartChanged.Enabled = CanStart;

            foreach (KeyValuePair<PopupTile, string> pair in operationTiles)
            {
                pair.Key.Accent = string.Equals(pair.Value, operation, StringComparison.OrdinalIgnoreCase);
                pair.Key.Invalidate();
            }
            foreach (KeyValuePair<PopupTile, string> pair in cropTiles)
            {
                bool selected = pair.Value == null ? string.IsNullOrEmpty(crop)
                    : pair.Value != OtherCrop && string.Equals(pair.Value, crop, StringComparison.OrdinalIgnoreCase);
                pair.Key.Accent = selected;
                pair.Key.Invalidate();
            }
        }

        private void ShowPage(bool change)
        {
            changing = change;
            foreach (PopupElement element in guessPage) element.Visible = !change;
            foreach (PopupElement element in changePage) element.Visible = change;
            if (change) PlaceOverMap(0.80, 0.82, new Size(820, 560), new Size(1300, 900));
            else PlaceOverMap(0.66, 0.62, new Size(720, 440), new Size(1120, 700));
            ShowChoices();
            LayoutElements();
            Redraw();
        }

        private void Finish(bool fixLater)
        {
            if (!fixLater && !CanStart) return;
            Choice = new AgOpenJobChoice { Operation = operation, Crop = crop, FixLater = fixLater };
            Close();
        }

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileStartChanged == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;
            const int gap = 16;
            const int buttonHeight = 80;
            int buttonTop = y + h - buttonHeight;

            // First page: what will happen on the left, the field on the right, buttons along the bottom.
            int left = (int)(w * 0.58);
            labelNew.Bounds = new Rectangle(x, y, left, 30);
            labelField.Bounds = new Rectangle(x, y + 30, left, 44);
            labelGuess.Bounds = new Rectangle(x, y + 80, left, 40);
            labelEquipment.Bounds = new Rectangle(x, y + 124, left, 32);
            labelFolder.Bounds = new Rectangle(x, y + 156, left, 32);
            labelHint.Bounds = new Rectangle(x, y + 192, left, Math.Max(0, buttonTop - gap - (y + 192)));
            int previewLeft = x + left + 20;
            preview.Bounds = new Rectangle(previewLeft, y, Math.Max(40, x + w - previewLeft), Math.Max(40, buttonTop - gap - y));
            int startWidth = (int)(w * 0.36);
            int changeWidth = (int)(w * 0.26);
            tileStart.Bounds = new Rectangle(x, buttonTop, startWidth, buttonHeight);
            tileChange.Bounds = new Rectangle(x + startWidth + gap, buttonTop, changeWidth, buttonHeight);
            int fixLeft = x + startWidth + changeWidth + 2 * gap;
            tileFixLater.Bounds = new Rectangle(fixLeft, buttonTop, x + w - fixLeft, buttonHeight);

            // Change page: operation buttons, crop buttons, then Back and Start.
            // The buttons get as tall as there's room for (up to 62).
            const int columns = 4;
            const int tileGap = 10;
            int tileWidth = (w - (columns - 1) * tileGap) / columns;
            int operationRows = (operationOrder.Count + columns - 1) / columns;
            int cropRows = (cropOrder.Count + columns - 1) / columns;
            int rows = Math.Max(1, operationRows + cropRows);
            int room = buttonTop - gap - y - 2 * 34 - 14 - Math.Max(0, operationRows - 1 + cropRows - 1) * tileGap;
            int tileHeight = Math.Max(40, Math.Min(62, room / rows));
            int top = y;
            labelOperation.Bounds = new Rectangle(x, top, w, 30);
            top += 34;
            top = Grid(operationOrder, x, top, tileWidth, tileHeight, tileGap, columns) + 14;
            labelCrop.Bounds = new Rectangle(x, top, w, 30);
            top += 34;
            Grid(cropOrder, x, top, tileWidth, tileHeight, tileGap, columns);

            tileBack.Bounds = new Rectangle(x, buttonTop, 190, buttonHeight);
            int startChangedWidth = Math.Min(320, w / 3);
            tileStartChanged.Bounds = new Rectangle(x + w - startChangedWidth, buttonTop, startChangedWidth, buttonHeight);
            labelChangeHint.Bounds = new Rectangle(x + 190 + gap, buttonTop, Math.Max(0, w - 190 - startChangedWidth - 2 * gap), buttonHeight);
        }

        // Lay tiles out in rows; returns the y just below the last row.
        private static int Grid(List<PopupTile> tiles, int x, int top, int width, int height, int gap, int columns)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                int row = i / columns, column = i % columns;
                tiles[i].Bounds = new Rectangle(x + column * (width + gap), top + row * (height + gap), width, height);
            }
            int rows = (tiles.Count + columns - 1) / columns;
            return top + rows * height + Math.Max(0, rows - 1) * gap;
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
    }
}
