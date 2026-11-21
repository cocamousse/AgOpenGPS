using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// The start screen, shown when AgOpenGPS opens:
    /// - Change equipment settings: AgOpenGPS's tractor and implement picker.
    /// - Show fields: the field list, with a drawing of the picked field and
    ///   Load / Edit paths (AgOpenGPS's draw screen: the field outline, where you
    ///   tap points to make AB lines and curves).
    /// - Continue: opens the last field again.
    /// A touch anywhere outside it goes to the map. It closes by itself after
    /// 30 seconds unless it's touched.
    /// </summary>
    public class FormAgOpenStart : AgOpenPopup
    {
        // Start page
        private readonly PopupLabel labelHub;
        private readonly PopupTile tileEquipment;
        private readonly PopupTile tileFields;
        private readonly PopupTile tileContinue;

        // Fields page
        private readonly PopupTile tileBack;
        private readonly PopupLabel labelFieldsTitle;
        private readonly PopupTile tileSort;
        private readonly PopupList listFields;
        private readonly PopupLabel labelFieldName;
        private readonly PopupLabel labelFieldInfo;
        private readonly FieldPreview preview;
        private readonly PopupTile tileLoad;
        private readonly PopupTile tileEdit;

        private readonly List<PopupElement> startPage = new List<PopupElement>();
        private readonly List<PopupElement> fieldsPage = new List<PopupElement>();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly List<Image> pictures = new List<Image>();

        private List<AgOpenFieldEntry> fields = new List<AgOpenFieldEntry>();
        private string continueFolder = "";
        private bool sortNearest;
        private bool showingFields;

        public FormAgOpenStart(FormGPS mf) : base(mf)
        {
            EdgeNote = "Touch anywhere else to go to the map";

            // Start page
            labelHub = Add(new PopupLabel { Font = SmallFont, ForeColor = DimText });

            tileEquipment = Add(new PopupTile
            {
                Picture = Keep(Properties.Resources.vehiclePageTractor),
                Title = "Change equipment settings"
            });
            tileEquipment.Click += TileEquipment_Click;

            tileFields = Add(new PopupTile
            {
                Picture = Keep(Properties.Resources.JobActive),
                Title = "Show fields"
            });
            tileFields.Click += (s, e) => ShowPage(true);

            tileContinue = Add(new PopupTile
            {
                Wide = true,
                Accent = true,
                Picture = Keep(Properties.Resources.Play)
            });
            tileContinue.Click += (s, e) => OpenJob(continueFolder);

            startPage.AddRange(new PopupElement[] { labelHub, tileEquipment, tileFields, tileContinue });

            // Fields page
            tileBack = Add(new PopupTile
            {
                Wide = true,
                Picture = Keep(Properties.Resources.back_button),
                Title = "Back"
            });
            tileBack.Click += (s, e) => ShowPage(false);

            labelFieldsTitle = Add(new PopupLabel { Font = HeadingFont });

            tileSort = Add(new PopupTile
            {
                Wide = true,
                Picture = Keep(Properties.Resources.Sort),
                TitleFont = InfoFont
            });
            tileSort.Click += (s, e) =>
            {
                sortNearest = !sortNearest;
                ReadFields();
            };

            listFields = Add(new PopupList());
            listFields.SelectedIndexChanged += (s, e) => ShowSelectedField();

            labelFieldName = Add(new PopupLabel { Font = HeadingFont });
            labelFieldInfo = Add(new PopupLabel { Font = InfoFont, ForeColor = DimText });

            preview = Add(new FieldPreview { EmptyText = "Tap a field to see it here." });

            tileLoad = Add(new PopupTile
            {
                Wide = true,
                Accent = true,
                Picture = Keep(Properties.Resources.FileOpen),
                Title = "Load"
            });
            tileLoad.Click += (s, e) => OpenSelected(false);

            tileEdit = Add(new PopupTile
            {
                Wide = true,
                Picture = Keep(Properties.Resources.ABDraw),
                Title = "Edit paths",
                Detail = "AB lines and curves"
            });
            tileEdit.Click += (s, e) => OpenSelected(true);

            fieldsPage.AddRange(new PopupElement[]
            {
                tileBack, labelFieldsTitle, tileSort, listFields, labelFieldName, labelFieldInfo, preview, tileLoad, tileEdit
            });

            ShowPage(false);

            refreshTimer.Interval = 1000;
            refreshTimer.Tick += (s, e) => ShowHubLine();
        }

        private Image Keep(Image picture)
        {
            pictures.Add(picture);
            return picture;
        }

        #region Layout

        protected override void LayoutElements()
        {
            // Called from the base constructor's first resize too, before the elements exist.
            if (tileEdit == null) return;

            Rectangle c = ContentRectangle;
            int x = c.Left, y = c.Top, w = c.Width, h = c.Height;
            if (w < 100 || h < 100) return;

            // Start page: hub line on top, two big squares, the Continue bar at the bottom.
            labelHub.Bounds = new Rectangle(x, y, w, 34);
            int continueHeight = Math.Max(78, h / 6);
            tileContinue.Bounds = new Rectangle(x, y + h - continueHeight, w, continueHeight);
            int areaTop = labelHub.Bounds.Bottom + 10;
            int areaBottom = tileContinue.Bounds.Top - 22;
            int gap = Math.Max(22, w / 26);
            int side = Math.Max(80, Math.Min((w - gap) / 2, areaBottom - areaTop));
            int left = x + (w - (2 * side + gap)) / 2;
            int top = areaTop + Math.Max(0, (areaBottom - areaTop - side) / 2);
            tileEquipment.Bounds = new Rectangle(left, top, side, side);
            tileFields.Bounds = new Rectangle(left + side + gap, top, side, side);

            // Fields page: Back, title and sort along the top; the list on the left;
            // the picked field on the right with Load and Edit under it.
            const int barHeight = 68;
            tileBack.Bounds = new Rectangle(x, y, 170, barHeight);
            tileSort.Bounds = new Rectangle(x + w - 250, y, 250, barHeight);
            labelFieldsTitle.Bounds = new Rectangle(tileBack.Bounds.Right + 18, y,
                Math.Max(0, tileSort.Bounds.Left - tileBack.Bounds.Right - 36), barHeight);
            int bodyTop = y + barHeight + 16;
            int listWidth = Math.Max(280, (int)(w * 0.40));
            listFields.Bounds = new Rectangle(x, bodyTop, listWidth, y + h - bodyTop);
            int right = x + listWidth + 22;
            int rightWidth = Math.Max(120, x + w - right);
            labelFieldName.Bounds = new Rectangle(right, bodyTop, rightWidth, 42);
            labelFieldInfo.Bounds = new Rectangle(right, labelFieldName.Bounds.Bottom, rightWidth, 34);
            int buttonHeight = Math.Max(78, h / 7);
            int buttonWidth = (rightWidth - 18) / 2;
            tileLoad.Bounds = new Rectangle(right, y + h - buttonHeight, buttonWidth, buttonHeight);
            tileEdit.Bounds = new Rectangle(right + buttonWidth + 18, y + h - buttonHeight, rightWidth - buttonWidth - 18, buttonHeight);
            int previewTop = labelFieldInfo.Bounds.Bottom + 10;
            preview.Bounds = new Rectangle(right, previewTop, rightWidth, Math.Max(40, tileLoad.Bounds.Top - 16 - previewTop));
        }

        #endregion

        #region Pages

        private void ShowPage(bool showFields)
        {
            showingFields = showFields;
            if (showFields)
            {
                ReadFields();
                PlaceOverMap(0.88, 0.88, new Size(780, 520), new Size(1550, 1000));
            }
            else
            {
                ShowStartPage();
                PlaceOverMap(0.64, 0.70, new Size(660, 480), new Size(1080, 760));
            }
            foreach (PopupElement element in startPage) element.Visible = !showFields;
            foreach (PopupElement element in fieldsPage) element.Visible = showFields;
            LayoutElements();
            Redraw();
        }

        /// <summary>The hub finished a sync: fields or profiles may have changed.</summary>
        public void RefreshAfterSync()
        {
            if (IsDisposed) return;
            if (showingFields) ReadFields();
            else ShowStartPage();
        }

        private void ShowStartPage()
        {
            ShowHubLine();

            string vehicle = RegistrySettings.vehicleProfileName;
            string tool = RegistrySettings.toolProfileName;
            tileEquipment.Detail = "Current: " + (string.IsNullOrEmpty(vehicle) ? "no tractor" : vehicle)
                + " + " + (string.IsNullOrEmpty(tool) ? "no implement" : tool);

            int count = AgOpenFields.Count(RegistrySettings.fieldsDirectory);
            tileFields.Detail = count == 0 ? "No fields yet"
                : count == 1 ? "1 field" : count.ToString(CultureInfo.CurrentCulture) + " fields";

            // Continue: the last job opened on this tablet, e.g. "Smith East spraying".
            AgOpenJob job = AgOpenJobs.LastJob(RegistrySettings.fieldsDirectory);
            bool canContinue = job != null
                && File.Exists(Path.Combine(RegistrySettings.fieldsDirectory, job.Folder, "Field.txt"));
            if (canContinue)
            {
                continueFolder = job.Folder;
                tileContinue.Title = "Continue: " + job.FieldName
                    + (string.IsNullOrEmpty(job.Operation) ? "" : " " + job.Operation);
                tileContinue.Detail = ContinueDetail(job);
            }
            else
            {
                continueFolder = "";
                tileContinue.Title = "Continue";
                tileContinue.Detail = "No recent job yet";
            }
            tileContinue.Enabled = canContinue;
        }

        private static string ContinueDetail(AgOpenJob job)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(job.Crop)) parts.Add(job.Crop);
            DateTime started;
            if (DateTime.TryParse(job.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out started))
            {
                parts.Add("started " + started.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture));
            }
            parts.Add(job.Folder);
            return string.Join("   \u00B7   ", parts);
        }

        private void ShowHubLine()
        {
            labelHub.Text = HubLine();
        }

        private string HubLine()
        {
            HubConnection hub = mf.agOpenHub;
            if (hub == null) return "Hub: not running";
            if (!hub.Settings.IsPaired) return "Hub: not paired (File menu, AgOpen Hub)";
            if (mf.AgOpenSyncing || hub.Syncing) return "Hub: syncing fields and equipment...";
            HubSyncResult last = hub.LastSync;
            if (last != null && last.Completed)
            {
                return "Hub: synced at " + last.FinishedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            }
            if (hub.State == HubState.Offline || (last != null && !last.Completed))
            {
                return "Hub: not reachable, using the fields from the last sync";
            }
            return "Hub: " + hub.StatusText;
        }

        #endregion

        #region Field list

        private void ReadFields()
        {
            AgOpenFieldEntry kept = SelectedEntry();
            string keep = kept != null ? kept.Folder : "";
            Wgs84? here = mf.AgOpenPosition();

            fields = AgOpenFields.List(RegistrySettings.fieldsDirectory, here);
            if (!here.HasValue) sortNearest = false;
            tileSort.Enabled = here.HasValue;
            tileSort.Title = sortNearest ? "Sort: nearest" : "Sort: A to Z";
            labelFieldsTitle.Text = fields.Count == 1 ? "1 field" : fields.Count.ToString(CultureInfo.CurrentCulture) + " fields";

            if (sortNearest)
            {
                fields.Sort((a, b) =>
                {
                    double da = a.DistanceM ?? double.MaxValue, db = b.DistanceM ?? double.MaxValue;
                    int byDistance = da.CompareTo(db);
                    return byDistance != 0 ? byDistance : string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase);
                });
            }
            else
            {
                fields.Sort((a, b) => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase));
            }

            var items = new List<PopupList.Item>();
            int select = -1;
            foreach (AgOpenFieldEntry entry in fields)
            {
                string detail = "";
                if (entry.DistanceM.HasValue)
                {
                    detail = entry.DistanceM.Value <= 0 ? "here" : AgOpenFields.Distance(entry.DistanceM.Value, mf.isMetric);
                }
                if (keep.Length > 0 && string.Equals(entry.Folder, keep, StringComparison.OrdinalIgnoreCase)) select = items.Count;
                items.Add(new PopupList.Item { Text = entry.Folder, Detail = detail, Tag = entry });
            }
            listFields.EmptyText = "No fields on this tablet yet. Fields from the hub show up here after a sync, "
                + "or make one with AgOpenGPS's Field button.";
            listFields.SetItems(items, select);
        }

        private AgOpenFieldEntry SelectedEntry()
        {
            PopupList.Item item = listFields.SelectedItem;
            return item != null ? item.Tag as AgOpenFieldEntry : null;
        }

        private void ShowSelectedField()
        {
            AgOpenFieldEntry entry = SelectedEntry();
            if (entry == null)
            {
                labelFieldName.Text = listFields.Count == 0 ? "No fields yet" : "Pick a field";
                labelFieldInfo.Text = listFields.Count == 0 ? "" : "Tap a field in the list to see it.";
                preview.ShowField(null, null);
                tileLoad.Enabled = false;
                tileEdit.Enabled = false;
                return;
            }

            AgOpenFieldShape shape = AgOpenFields.Shape(entry);
            Wgs84? here = mf.AgOpenPosition();
            GeoCoord? tractor = here.HasValue ? AgOpenFields.ToField(shape, here.Value) : null;

            var info = new List<string>();
            info.Add(shape.Fences.Count > 0 ? AgOpenFields.Area(shape.AreaM2, mf.isMetric) : "No boundary yet");
            if (shape.Tracks.Count > 0)
            {
                info.Add(shape.Tracks.Count == 1 ? "1 line" : shape.Tracks.Count.ToString(CultureInfo.CurrentCulture) + " lines");
            }
            if (here.HasValue)
            {
                double? distance = AgOpenFields.DistanceFrom(shape, here.Value);
                if (distance.HasValue)
                {
                    info.Add(distance.Value <= 0 ? "you're in this field" : AgOpenFields.Distance(distance.Value, mf.isMetric) + " away");
                }
            }

            labelFieldName.Text = entry.Folder;
            labelFieldInfo.Text = string.Join("   ·   ", info);
            preview.ShowField(shape, tractor);
            tileLoad.Enabled = true;
            // AgOpenGPS's draw screen works on the boundary, so it needs one.
            bool hasBoundary = shape.Fences.Count > 0;
            tileEdit.Enabled = hasBoundary;
            tileEdit.Detail = hasBoundary ? "AB lines and curves" : "needs a boundary first";
        }

        #endregion

        #region Buttons

        private void TileEquipment_Click(object sender, EventArgs e)
        {
            if (mf.isJobStarted) return;
            // Step aside while AgOpenGPS's own picker is open, then come back with the new names.
            Hide();
            try
            {
                mf.AgOpenChangeEquipment();
            }
            finally
            {
                if (!IsDisposed)
                {
                    if (mf.isJobStarted)
                    {
                        Close();
                    }
                    else
                    {
                        ShowStartPage();
                        Show();
                    }
                }
            }
        }

        // Load: the job prompt for this field. Edit paths: the master field itself,
        // with AgOpenGPS's draw screen (lines belong to the field, not to a job).
        private void OpenSelected(bool editPaths)
        {
            AgOpenFieldEntry entry = SelectedEntry();
            if (entry == null) return;
            FormGPS main = mf;
            Close();
            if (editPaths) main.AgOpenOpenField(entry.Folder, true);
            else main.AgOpenLoadField(entry.Folder);
        }

        private void OpenJob(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            FormGPS main = mf;
            Close();
            main.AgOpenOpenField(folder, false);
        }

        #endregion

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            refreshTimer.Start();
            StartCountdown(30);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refreshTimer.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                foreach (Image picture in pictures) picture.Dispose();
                pictures.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
