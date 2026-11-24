using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgLibrary.Settings;
using AgOpenGPS.AgOpen;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Properties;

namespace AgOpenGPS
{
    /// <summary>
    /// The AgOpen additions to the main window. Kept in this one file (plus a
    /// single call in FormGPS_Load) so AgOpenGPS updates stay easy to merge.
    /// </summary>
    public partial class FormGPS
    {
        /// <summary>The link to the AgOpen hub (null if it couldn't start).</summary>
        public HubConnection agOpenHub;

        private ToolStripMenuItem agOpenHubMenuItem;
        private ToolStripMenuItem agOpenStartMenuItem;
        private Button agOpenGoButton;
        private readonly System.Windows.Forms.Timer agOpenGoTimer = new System.Windows.Forms.Timer();
        private FormAgOpenStart agOpenStart;
        private FormAgOpenJobPrompt agOpenJobPrompt;
        private AgOpenFieldWatch agOpenFieldWatch;
        private AgOpenWork agOpenWork;
        private bool agOpenSyncRunning;

        // The field the draw screen was opened for straight away (Show fields: Edit paths).
        private string agOpenEditPathsFolder;

        // AgOpenGPS's screens for making a field, its boundary or its lines: the
        // "Make starting paths?" box waits until they're closed.
        private static readonly HashSet<string> AgOpenFieldEditScreens = new HashSet<string>(StringComparer.Ordinal)
        {
            "FormABDraw", "FormBoundary", "FormBoundaryPlayer", "FormBuildBoundaryFromTracks", "FormBuildTracks",
            "FormCopyTracks", "FormFieldDir", "FormFieldExisting", "FormFieldIsoXml", "FormFieldKML",
            "FormHeadAche", "FormHeadLine", "FormMap", "FormTram", "FormTramLine"
        };

        // Field folders the hub sync renamed this session: old name -> new name.
        private readonly Dictionary<string, string> agOpenRenamedFolders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while a sync with the hub is running (field folders may still change).</summary>
        internal bool AgOpenSyncing
        {
            get { return agOpenSyncRunning; }
        }

        /// <summary>The start screen or the job prompt is up ("Field detected" waits for them).</summary>
        internal bool AgOpenScreenOpen
        {
            get
            {
                return (agOpenStart != null && !agOpenStart.IsDisposed)
                    || (agOpenJobPrompt != null && !agOpenJobPrompt.IsDisposed);
            }
        }

        /// <summary>One of AgOpenGPS's screens for making a field's boundary or lines is open.</summary>
        internal bool AgOpenFieldEditScreenOpen
        {
            get
            {
                foreach (Form form in Application.OpenForms)
                {
                    if (form.Visible && AgOpenFieldEditScreens.Contains(form.GetType().Name)) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// True (once) if this field was opened straight into the draw screen with
        /// Edit paths: then it isn't offered starting paths.
        /// </summary>
        internal bool AgOpenTookEditPaths(string folder)
        {
            bool took = agOpenEditPathsFolder != null
                && string.Equals(agOpenEditPathsFolder, folder, StringComparison.OrdinalIgnoreCase);
            agOpenEditPathsFolder = null;
            return took;
        }

        /// <summary>AgOpenGPS is closing down.</summary>
        internal bool AgOpenShuttingDown
        {
            get { return isShuttingDown; }
        }

        private void InitAgOpenHub()
        {
            // The terms were accepted at the start of FormGPS_Load (otherwise
            // AgOpenGPS has exited). 6.8.6 never records that, so the terms screen
            // came back on every start: remember it, so it shows only once.
            if (!Properties.Settings.Default.setDisplay_isTermsAccepted)
            {
                Properties.Settings.Default.setDisplay_isTermsAccepted = true;
                Properties.Settings.Default.Save();
                Log.EventWriter("[AgOpen] Terms accepted: remembered, so they won't be shown again");
            }

            try
            {
                agOpenHub = new HubConnection();
                agOpenHub.Start();
            }
            catch (Exception ex)
            {
                agOpenHub = null;
                Log.EventWriter("[AgOpen] The hub connection couldn't start: " + ex);
            }

            // File menu: "AgOpen Hub" and "AgOpen start screen", right under "AgShare API".
            agOpenHubMenuItem = new ToolStripMenuItem();
            agOpenHubMenuItem.Name = "agOpenHubMenuItem";
            agOpenHubMenuItem.Size = new Size(397, 50);
            agOpenHubMenuItem.Text = "AgOpen Hub";
            agOpenHubMenuItem.Click += AgOpenHubMenuItem_Click;

            agOpenStartMenuItem = new ToolStripMenuItem();
            agOpenStartMenuItem.Name = "agOpenStartMenuItem";
            agOpenStartMenuItem.Size = new Size(397, 50);
            agOpenStartMenuItem.Text = "AgOpen start screen";
            agOpenStartMenuItem.Click += (s, e) => ShowAgOpenStartScreen(true);

            int index = fileToolStripMenuItem.DropDownItems.IndexOf(AgShareApiMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(index + 1, agOpenHubMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(index + 2, agOpenStartMenuItem);
            fileToolStripMenuItem.DropDownOpening += AgOpenHubMenu_DropDownOpening;

            // GO: a main-screen button for the start screen, under AgIO.
            AddAgOpenGoButton();

            // The work done in job folders goes to the hub as it happens (a job left
            // open by a power cut is closed first).
            agOpenWork = new AgOpenWork(this);
            agOpenWork.Start();

            // The start screen comes up once the main window is on screen, and
            // "Field detected" starts watching then too.
            agOpenFieldWatch = new AgOpenFieldWatch(this);
            Shown += AgOpen_Shown;
            FormClosing += (s, e) =>
            {
                // AgOpenGPS cancels the first close to save the field, then closes again.
                if (e.Cancel) return;
                agOpenFieldWatch.Stop();
                agOpenGoTimer.Stop();
                agOpenWork.Stop();
            };

            // Bring fields and profiles up to date when AgOpenGPS starts.
            SyncWithAgOpenHub(true);
        }

        #region Start screen

        // GO sits in the bottom-left corner, under the AgIO button, as wide as that
        // column. It's dimmed while a field is open (the start screen is for
        // choosing one), and steps aside if AgOpenGPS's bottom button bar ever
        // reaches that corner (narrow screens), so it never covers a button.
        private void AddAgOpenGoButton()
        {
            try
            {
                agOpenGoButton = new Button
                {
                    Name = "btnAgOpenGo",
                    BackColor = Color.Transparent,
                    FlatStyle = FlatStyle.Flat,
                    Image = AgOpenPopup.GoPicture(56),
                    Margin = new Padding(0),
                    TabStop = false,
                    UseVisualStyleBackColor = false
                };
                agOpenGoButton.FlatAppearance.BorderSize = 0;
                agOpenGoButton.FlatAppearance.CheckedBackColor = Color.Transparent;
                agOpenGoButton.FlatAppearance.MouseDownBackColor = Color.Transparent;
                agOpenGoButton.FlatAppearance.MouseOverBackColor = Color.Transparent;

                int top = panelLeft.Bottom + 2;
                agOpenGoButton.Bounds = new Rectangle(panelLeft.Left, top, panelLeft.Width, Math.Max(44, ClientSize.Height - top - 2));
                agOpenGoButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                agOpenGoButton.Click += (s, e) => ShowAgOpenStartScreen(true);
                Controls.Add(agOpenGoButton);
                agOpenGoButton.BringToFront();

                agOpenGoTimer.Interval = 500;
                agOpenGoTimer.Tick += (s, e) => UpdateAgOpenGoButton();
                agOpenGoTimer.Start();
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] The GO button couldn't be added: " + ex);
            }
        }

        private void UpdateAgOpenGoButton()
        {
            if (agOpenGoButton == null || agOpenGoButton.IsDisposed) return;

            bool usable = !isJobStarted && !isEasyDriveMode;
            if (agOpenGoButton.Enabled != usable) agOpenGoButton.Enabled = usable;

            bool clash = false;
            if (panelBottom.Visible)
            {
                foreach (Control button in panelBottom.Controls)
                {
                    if (!button.Visible) continue;
                    Rectangle where = button.Bounds;
                    where.Offset(panelBottom.Location);
                    if (where.IntersectsWith(agOpenGoButton.Bounds))
                    {
                        clash = true;
                        break;
                    }
                }
            }
            if (agOpenGoButton.Visible == clash) agOpenGoButton.Visible = !clash;
        }

        private void AgOpen_Shown(object sender, EventArgs e)
        {
            // Give the map a moment to draw first.
            var delay = new System.Windows.Forms.Timer { Interval = 700 };
            delay.Tick += (s, args) =>
            {
                delay.Stop();
                delay.Dispose();
                ShowAgOpenStartScreen(false);
                agOpenFieldWatch.Start();
            };
            delay.Start();
        }

        /// <summary>
        /// Show the start screen. asked: the user picked it from the menu, so say
        /// why if it can't show right now.
        /// </summary>
        public void ShowAgOpenStartScreen(bool asked)
        {
            if (IsDisposed || isShuttingDown) return;
            if (isJobStarted || isEasyDriveMode)
            {
                if (asked) TimedMessageBox(2500, "AgOpen", "Close the field first.");
                return;
            }
            if (agOpenStart != null && !agOpenStart.IsDisposed)
            {
                if (!agOpenStart.Visible) return; // it's stepped aside for the equipment picker
                agOpenStart.Activate();
                return;
            }
            try
            {
                var screen = new FormAgOpenStart(this);
                screen.FormClosed += (s, e) =>
                {
                    if (agOpenStart == screen) agOpenStart = null;
                };
                agOpenStart = screen;
                screen.Show(this);
            }
            catch (Exception ex)
            {
                agOpenStart = null;
                Log.EventWriter("[AgOpen] The start screen couldn't open: " + ex);
            }
        }

        /// <summary>The tractor's position, or null without a GPS fix.</summary>
        internal Wgs84? AgOpenPosition()
        {
            if (!isGPSPositionInitialized) return null;
            Wgs84 position = AppModel.CurrentLatLon;
            if (!position.IsValid) return null;
            return position;
        }

        /// <summary>AgOpenGPS's tractor and implement picker (returns when it closes).</summary>
        internal void AgOpenChangeEquipment()
        {
            loadVehicleToolToolStripMenuItem_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Open a field folder the same way AgOpenGPS's Field button does. With
        /// editPaths, AgOpenGPS's draw screen (the field outline, where you tap
        /// points to make AB lines and curves) opens once the field is loaded.
        /// </summary>
        internal async void AgOpenOpenField(string folder, bool editPaths)
        {
            try
            {
                if (isJobStarted)
                {
                    TimedMessageBox(2500, "AgOpen", "Close the field first.");
                    return;
                }

                await AgOpenWaitForSync();
                folder = AgOpenCurrentName(folder);

                string fieldFile = Path.Combine(RegistrySettings.fieldsDirectory, folder, "Field.txt");
                if (!File.Exists(fieldFile))
                {
                    TimedMessageBox(3000, "Field not found", "'" + folder + "' isn't on this tablet any more.");
                    return;
                }

                bool wasJobStarted = isJobStarted;
                string previousFolder = currentFieldDirectory;
                agOpenEditPathsFolder = editPaths ? folder : null;
                await FileOpenField(fieldFile);
                AgOpenAfterFieldOpened(wasJobStarted, previousFolder);
                if (isJobStarted && AgOpenJobs.IsJobFolder(Path.Combine(RegistrySettings.fieldsDirectory, folder)))
                {
                    AgOpenJobs.RememberOpened(folder);
                }

                if (editPaths && isJobStarted)
                {
                    btnABDraw_Click(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Opening field '" + folder + "' failed: " + ex);
                TimedMessageBox(4000, "AgOpen", "The field couldn't be opened: " + ex.Message);
            }
        }

        /// <summary>
        /// The starting paths for the open field (a path round the border and a curve
        /// along the longest side), worked out but not added. Null without a boundary.
        /// </summary>
        internal AgOpenStartingPaths.Plan AgOpenPlanStartingPaths()
        {
            if (!isJobStarted || bnd.bndList.Count == 0) return null;
            return AgOpenStartingPaths.Make(bnd.bndList[0].fenceLine, points => curve.AddFirstLastPoints(ref points));
        }

        /// <summary>
        /// Yes on "Make starting paths?": add the path round the border and the curve
        /// along the longest side to the open field, as if made on the draw screen, and
        /// select the curve. They reach the hub like any other line. Returns what was
        /// made (for the headland question), or null; tell also shows it in a message.
        /// </summary>
        internal string AgOpenAddStartingPaths(bool tell)
        {
            try
            {
                if (!isJobStarted || trk.gArr.Count > 0) return null; // lines were made meanwhile: leave them be
                AgOpenStartingPaths.Plan plan = AgOpenPlanStartingPaths();
                if (plan == null) return null;

                trk.gArr.Add(plan.Border);
                if (plan.Side != null) trk.gArr.Add(plan.Side);
                trk.idx = trk.gArr.Count - 1;
                FileSaveTracks();

                // As after the draw screen: guidance works its lines out again.
                curve.isCurveValid = false;
                ABLine.isABValid = false;
                twoSecondCounter = 100;
                PanelUpdateRightAndBottom();

                string made = "'" + plan.Border.name + "'" + (plan.Side != null ? " and '" + plan.Side.name + "'" : "");
                Log.EventWriter("[AgOpen] Starting paths made in '" + currentFieldDirectory + "': " + made
                    + (plan.Side != null ? " (side " + Math.Round(plan.SideLengthM) + " m)" : " (no clear side for a curve)"));
                string text = "Made " + made + "." + (plan.Side != null ? " The curve is selected." : "");
                if (tell) TimedMessageBox(AgOpenReadingTime(text), "Starting paths", text);
                return text;
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Starting paths couldn't be made: " + ex);
                TimedMessageBox(4000, "AgOpen", "The starting paths couldn't be made: " + ex.Message);
                return null;
            }
        }

        /// <summary>The open field already has a headland.</summary>
        internal bool AgOpenHasHeadland
        {
            get { return bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0; }
        }

        /// <summary>
        /// How wide a headland this many passes is, as AgOpenGPS shows it, e.g. "120 ft".
        /// A pass is the implement's width less its overlap, as on the headland screen.
        /// </summary>
        internal string AgOpenHeadlandWidthText(int passes)
        {
            return AgOpenDistanceText(passes * (tool.width - tool.overlap));
        }

        private string AgOpenDistanceText(double metres)
        {
            return (metres * m2FtOrM).ToString("0.#", CultureInfo.CurrentCulture) + unitsFtM;
        }

        /// <summary>
        /// Yes on "Would you like to create headlands?": a headland this many passes
        /// (implement widths less overlap) in from the outer boundary, made and saved
        /// as AgOpenGPS's headland screen would, with the Headland button on after.
        /// It reaches the hub like any other headland.
        /// </summary>
        internal void AgOpenMakeHeadland(int passes)
        {
            try
            {
                if (!isJobStarted || bnd.bndList.Count == 0 || passes < 1) return;
                if (AgOpenHasHeadland) return; // one was made meanwhile: leave it be

                double distance = passes * (tool.width - tool.overlap);
                string wide = passes + (passes == 1 ? " pass" : " passes") + " (" + AgOpenHeadlandWidthText(passes) + ")";
                List<vec3> line = AgOpenHeadland.Make(bnd.bndList[0].fenceLine, distance);
                if (line == null)
                {
                    Log.EventWriter("[AgOpen] No headland in '" + currentFieldDirectory + "': too small for " + wide);
                    TimedMessageBox(4000, "Headland", "This field is too small for a headland " + wide + " wide.");
                    return;
                }

                bnd.bndList[0].hdLine.Clear();
                bnd.bndList[0].hdLine.AddRange(line);
                FileSaveHeadland();

                // As after the headland screen.
                bnd.isHeadlandOn = AgOpenHasHeadland;
                PanelsAndOGLSize();
                PanelUpdateRightAndBottom();
                SetZoom();

                Log.EventWriter("[AgOpen] Headland made in '" + currentFieldDirectory + "': " + wide + ", " + line.Count + " points");
                string text = "Made a headland " + wide + " wide.";
                TimedMessageBox(AgOpenReadingTime(text), "Headland", text);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] The headland couldn't be made: " + ex);
                TimedMessageBox(4000, "AgOpen", "The headland couldn't be made: " + ex.Message);
            }
        }

        /// <summary>Create paths on "Make starting paths?": AgOpenGPS's draw screen for the open field.</summary>
        internal void AgOpenDrawPaths()
        {
            if (isJobStarted && !IsDisposed) btnABDraw_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Load a field from the AgOpen screens. A master field gets the job prompt
        /// (each job works in its own fresh folder); a job folder just opens.
        /// </summary>
        internal void AgOpenLoadField(string folder)
        {
            try
            {
                if (isJobStarted)
                {
                    TimedMessageBox(2500, "AgOpen", "Close the field first.");
                    return;
                }
                folder = AgOpenCurrentName(folder);
                string directory = Path.Combine(RegistrySettings.fieldsDirectory, folder);
                if (AgOpenJobs.IsJobFolder(directory))
                {
                    AgOpenOpenField(folder, false);
                    return;
                }
                if (!File.Exists(Path.Combine(directory, "Field.txt")))
                {
                    TimedMessageBox(3000, "Field not found", "'" + folder + "' isn't on this tablet any more.");
                    return;
                }

                if (agOpenJobPrompt != null && !agOpenJobPrompt.IsDisposed) agOpenJobPrompt.Close();
                AgOpenJobGuess guess = AgOpenJobs.Guess(RegistrySettings.toolProfileName, RegistrySettings.vehicleProfileName,
                    tool != null ? tool.width : (double?)null);
                var prompt = new FormAgOpenJobPrompt(this, folder, guess);
                prompt.FormClosed += (s, e) =>
                {
                    if (agOpenJobPrompt == prompt) agOpenJobPrompt = null;
                    if (prompt.Choice != null) AgOpenStartJob(folder, guess, prompt.Choice);
                };
                agOpenJobPrompt = prompt;
                prompt.Show(this);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] The job prompt couldn't open for '" + folder + "': " + ex);
                TimedMessageBox(4000, "AgOpen", "The job prompt couldn't open: " + ex.Message);
            }
        }

        /// <summary>
        /// "Yes, start a job" was tapped after a field itself (not a job folder) was
        /// loaded. Show the job prompt: Start turns the sections off, closes the field
        /// and opens a fresh job folder; a touch on the map keeps the field open.
        /// </summary>
        internal void AgOpenWorkInMaster(string folder)
        {
            try
            {
                if (agOpenJobPrompt != null && !agOpenJobPrompt.IsDisposed) return;
                Log.EventWriter("[AgOpen] Starting a job from the open field '" + folder + "'");
                AgOpenJobGuess guess = AgOpenJobs.Guess(RegistrySettings.toolProfileName, RegistrySettings.vehicleProfileName,
                    tool != null ? tool.width : (double?)null);
                var prompt = new FormAgOpenJobPrompt(this, folder, guess, true);
                prompt.FormClosed += async (s, e) =>
                {
                    if (agOpenJobPrompt == prompt) agOpenJobPrompt = null;
                    if (prompt.Choice == null)
                    {
                        Log.EventWriter("[AgOpen] Kept the field '" + folder + "' open without a job");
                        return;
                    }
                    try
                    {
                        if (isJobStarted && string.Equals(currentFieldDirectory, folder, StringComparison.OrdinalIgnoreCase))
                        {
                            if (autoBtnState == btnStates.Auto) btnSectionMasterAuto.PerformClick();
                            if (manualBtnState == btnStates.On) btnSectionMasterManual.PerformClick();
                            await FileSaveEverythingBeforeClosingField();
                        }
                        AgOpenStartJob(folder, guess, prompt.Choice);
                    }
                    catch (Exception ex)
                    {
                        Log.EventWriter("[AgOpen] Closing '" + folder + "' to start a job failed: " + ex);
                        TimedMessageBox(4000, "AgOpen", "The job couldn't be started: " + ex.Message);
                    }
                };
                agOpenJobPrompt = prompt;
                prompt.Show(this);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] The job prompt couldn't open for '" + folder + "': " + ex);
            }
        }

        // Start was tapped: make the job folder from the master and open it. The job
        // goes to the hub with its first coverage (AgOpenWork), so a job started by
        // mistake never shows up empty in farmOS.
        private async void AgOpenStartJob(string masterFolder, AgOpenJobGuess guess, AgOpenJobChoice choice)
        {
            try
            {
                if (isJobStarted)
                {
                    TimedMessageBox(2500, "AgOpen", "Close the field first.");
                    return;
                }
                await AgOpenWaitForSync();
                masterFolder = AgOpenCurrentName(masterFolder);

                // A field not on the hub yet goes up first, so the job has a field to belong to.
                string masterDir = Path.Combine(RegistrySettings.fieldsDirectory, masterFolder);
                if (agOpenHub != null && agOpenHub.Settings.IsPaired && HubFieldState.Load(masterDir) == null)
                {
                    bool deferred;
                    FieldEdits.SendChanges(agOpenHub, masterDir, true, null, out deferred);
                }

                AgOpenJob job = AgOpenJobs.Create(RegistrySettings.fieldsDirectory, masterFolder, guess, choice);
                // Edits made in the job folder (new lines...) are counted from here.
                FieldEdits.SetBase(Path.Combine(RegistrySettings.fieldsDirectory, job.Folder));
                AgOpenJobs.Remember(guess.Tool, choice);
                Log.EventWriter("[AgOpen] Job started: " + job.Folder + " (" + (job.Operation ?? "operation not known")
                    + (job.Crop != null ? ", " + job.Crop : "") + (job.FixLater ? ", fix later" : "") + ")");

                AgOpenOpenField(job.Folder, false);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Starting a job in '" + masterFolder + "' failed: " + ex);
                TimedMessageBox(4000, "AgOpen", "The job couldn't be started: " + ex.Message);
            }
        }

        // A sync may be writing field folders right now: let it finish first.
        private async Task AgOpenWaitForSync()
        {
            if (!agOpenSyncRunning) return;
            TimedMessageBox(2000, "AgOpen Hub", "Finishing the sync with the hub...");
            DateTime giveUp = DateTime.UtcNow.AddSeconds(45);
            while (agOpenSyncRunning && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(200);
            }
        }

        // The folder's name now, if the hub sync renamed it this session.
        private string AgOpenCurrentName(string folder)
        {
            string renamed;
            return folder != null && agOpenRenamedFolders.TryGetValue(folder, out renamed) ? renamed : folder;
        }

        // The steps AgOpenGPS takes after its Field button has opened a field
        // (the end of btnJobMenu_Click in Forms/Controls.Designer.cs).
        private void AgOpenAfterFieldOpened(bool wasJobStarted, string previousFolder)
        {
            if (isJobStarted)
            {
                if (autoBtnState == btnStates.Auto) btnSectionMasterAuto.PerformClick();
                if (manualBtnState == btnStates.On) btnSectionMasterManual.PerformClick();

                bool openedNewOrChanged = !wasJobStarted
                    || !string.Equals(currentFieldDirectory, previousFolder, StringComparison.OrdinalIgnoreCase);
                if (openedNewOrChanged)
                {
                    double distance = AppModel.CurrentLatLon.DistanceInKiloMeters(AppModel.LocalPlane.Origin);
                    if (distance > 10)
                    {
                        TimedMessageBox(2500, "High Field Start Distance Warning",
                            "Field Start is " + distance.ToString("N1") + " km From current position");
                        Log.EventWriter("High Field Start Distance Warning");
                    }

                    Log.EventWriter("** Opened **  " + currentFieldDirectory + "   "
                        + DateTime.Now.ToString("f", CultureInfo.InvariantCulture));

                    if (!isEasyDriveMode)
                    {
                        Properties.Settings.Default.setF_CurrentDir = currentFieldDirectory;
                        Properties.Settings.Default.Save();
                    }

                    isobus.SendFieldName(currentFieldDirectory);
                }
            }

            FieldMenuButtonEnableDisable(isJobStarted);
            toolStripBtnFieldTools.Enabled = isJobStarted;
            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);
            trk.idx = -1;
            PanelUpdateRightAndBottom();
        }

        #endregion

        #region Sync

        /// <summary>
        /// Sync with the hub in the background: send what's waiting, then bring
        /// profiles and fields up to date. quiet: say nothing if nothing changed.
        /// </summary>
        public async void SyncWithAgOpenHub(bool quiet)
        {
            if (agOpenHub == null || !agOpenHub.Settings.IsPaired || agOpenHub.Syncing || agOpenSyncRunning) return;
            agOpenSyncRunning = true;
            try
            {
                HubSyncContext context = AgOpenSyncContext();
                HubSyncResult result = await Task.Run(() => agOpenHub.Sync(context));
                if (result == null) return;

                AgOpenNoteRenames(result);
                if ((result.LoadedVehicleChanged || result.LoadedToolChanged) && !isJobStarted)
                {
                    ReloadProfilesFromHub(result.LoadedVehicleChanged, result.LoadedToolChanged);
                }
                if (!quiet || result.ChangedAnything || result.Problems.Count > 0)
                {
                    string summary = result.Summary();
                    TimedMessageBox(AgOpenReadingTime(summary), "AgOpen Hub", summary);
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Hub sync failed: " + ex);
            }
            finally
            {
                agOpenSyncRunning = false;
                if (agOpenStart != null && !agOpenStart.IsDisposed) agOpenStart.RefreshAfterSync();
                if (agOpenFieldWatch != null) agOpenFieldWatch.FieldsChanged();
            }
        }

        // How long a message stays up: long enough to read in the cab (it can't be
        // tapped away), so a longer one stays longer.
        private static int AgOpenReadingTime(string text)
        {
            return Math.Min(4000 + 60 * (text ?? "").Length, 12000);
        }

        // Folders renamed by the sync: remember the new names, and keep AgOpenGPS's
        // "last field" pointing at the right folder.
        private void AgOpenNoteRenames(HubSyncResult result)
        {
            foreach (KeyValuePair<string, string> rename in result.RenamedFolders)
            {
                foreach (string older in new List<string>(agOpenRenamedFolders.Keys))
                {
                    if (string.Equals(agOpenRenamedFolders[older], rename.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        agOpenRenamedFolders[older] = rename.Value;
                    }
                }
                agOpenRenamedFolders[rename.Key] = rename.Value;

                if (!isJobStarted && string.Equals(currentFieldDirectory, rename.Key, StringComparison.OrdinalIgnoreCase))
                {
                    currentFieldDirectory = rename.Value;
                    Properties.Settings.Default.setF_CurrentDir = rename.Value;
                    Properties.Settings.Default.Save();
                    Log.EventWriter("[AgOpen] Last field renamed on the hub: " + rename.Key + " -> " + rename.Value);
                }
            }
        }

        private HubSyncContext AgOpenSyncContext()
        {
            var context = new HubSyncContext();
            context.FieldsDirectory = RegistrySettings.fieldsDirectory ?? "";
            context.VehiclesDirectory = RegistrySettings.vehiclesDirectory ?? "";
            context.ToolsDirectory = RegistrySettings.toolsDirectory ?? "";
            context.FieldOpen = isJobStarted;
            context.OpenFieldFolder = isJobStarted ? (currentFieldDirectory ?? "") : "";
            context.LoadedVehicle = RegistrySettings.vehicleProfileName ?? "";
            context.LoadedTool = RegistrySettings.toolProfileName ?? "";
            return context;
        }

        // The profile in use was updated from the hub: load it again, the same way
        // the profile picker does.
        private void ReloadProfilesFromHub(bool vehicleChanged, bool toolChanged)
        {
            if (vehicleChanged)
            {
                LoadResult result = VehicleSettings.Default.Load(RegistrySettings.vehicleProfileName);
                if (result != LoadResult.Ok)
                {
                    Log.EventWriter("[AgOpen] Reloading vehicle profile from the hub failed: " + result);
                    return;
                }
            }
            if (toolChanged)
            {
                LoadResult result = ToolSettings.Default.Load(RegistrySettings.toolProfileName);
                if (result != LoadResult.Ok)
                {
                    Log.EventWriter("[AgOpen] Reloading tool profile from the hub failed: " + result);
                    return;
                }
            }

            vehicle = new CVehicle(this);
            tool = new CTool(this);
            LoadSettings();
            SetVehicleTextures();
            SendSettings();
            SendRelaySettingsToMachineModule();
            Log.EventWriter("[AgOpen] Reloaded the profile in use after an update from the hub");
        }

        #endregion

        #region Menu

        // Show the hub's state right in the menu.
        private void AgOpenHubMenu_DropDownOpening(object sender, EventArgs e)
        {
            if (agOpenHub == null) return;
            int waiting = agOpenHub.Outbox.Count;
            string text = "AgOpen Hub";
            if (agOpenHub.State == HubState.NotPaired) text += " (not paired)";
            else if (waiting > 0) text += " (" + waiting.ToString(CultureInfo.InvariantCulture) + " waiting)";
            agOpenHubMenuItem.Text = text;
        }

        private void AgOpenHubMenuItem_Click(object sender, EventArgs e)
        {
            if (agOpenHub == null)
            {
                TimedMessageBox(4000, "AgOpen Hub", "The hub connection didn't start. See the event log.");
                return;
            }
            using (var form = new FormHubSettings(agOpenHub, () => SyncWithAgOpenHub(false), () => AgOpenSyncing))
            {
                form.ShowDialog(this);
            }
        }

        #endregion
    }
}
