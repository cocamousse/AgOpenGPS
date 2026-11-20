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
        private FormAgOpenStart agOpenStart;
        private bool agOpenSyncRunning;

        // Field folders the hub sync renamed this session: old name -> new name.
        private readonly Dictionary<string, string> agOpenRenamedFolders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while a sync with the hub is running (field folders may still change).</summary>
        internal bool AgOpenSyncing
        {
            get { return agOpenSyncRunning; }
        }

        private void InitAgOpenHub()
        {
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

            // The start screen comes up once the main window is on screen.
            Shown += AgOpen_Shown;

            // Bring fields and profiles up to date when AgOpenGPS starts.
            SyncWithAgOpenHub(true);
        }

        #region Start screen

        private void AgOpen_Shown(object sender, EventArgs e)
        {
            // Give the map a moment to draw first.
            var delay = new System.Windows.Forms.Timer { Interval = 700 };
            delay.Tick += (s, args) =>
            {
                delay.Stop();
                delay.Dispose();
                ShowAgOpenStartScreen(false);
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
        /// editPaths, AgOpenGPS's Tracks screen (AB lines and curves) opens once the
        /// field is loaded.
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

                // A sync may be writing field folders right now: let it finish.
                if (agOpenSyncRunning)
                {
                    TimedMessageBox(2000, "AgOpen Hub", "Finishing the sync with the hub...");
                    DateTime giveUp = DateTime.UtcNow.AddSeconds(45);
                    while (agOpenSyncRunning && DateTime.UtcNow < giveUp)
                    {
                        await Task.Delay(200);
                    }
                }

                string renamed;
                if (agOpenRenamedFolders.TryGetValue(folder, out renamed)) folder = renamed;

                string fieldFile = Path.Combine(RegistrySettings.fieldsDirectory, folder, "Field.txt");
                if (!File.Exists(fieldFile))
                {
                    TimedMessageBox(3000, "Field not found", "'" + folder + "' isn't on this tablet any more.");
                    return;
                }

                bool wasJobStarted = isJobStarted;
                string previousFolder = currentFieldDirectory;
                await FileOpenField(fieldFile);
                AgOpenAfterFieldOpened(wasJobStarted, previousFolder);

                if (editPaths && isJobStarted)
                {
                    btnBuildTracks_Click(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Opening field '" + folder + "' failed: " + ex);
                TimedMessageBox(4000, "AgOpen", "The field couldn't be opened: " + ex.Message);
            }
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
                    TimedMessageBox(3000, "AgOpen Hub", result.Summary());
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
            }
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
            using (var form = new FormHubSettings(agOpenHub, () => SyncWithAgOpenHub(false)))
            {
                form.ShowDialog(this);
            }
        }

        #endregion
    }
}
