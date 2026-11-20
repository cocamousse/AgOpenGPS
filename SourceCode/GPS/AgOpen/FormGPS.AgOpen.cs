using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgLibrary.Settings;
using AgOpenGPS.AgOpen;
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

            // File menu: "AgOpen Hub", right under "AgShare API".
            agOpenHubMenuItem = new ToolStripMenuItem();
            agOpenHubMenuItem.Name = "agOpenHubMenuItem";
            agOpenHubMenuItem.Size = new Size(397, 50);
            agOpenHubMenuItem.Text = "AgOpen Hub";
            agOpenHubMenuItem.Click += AgOpenHubMenuItem_Click;
            int index = fileToolStripMenuItem.DropDownItems.IndexOf(AgShareApiMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(index + 1, agOpenHubMenuItem);
            fileToolStripMenuItem.DropDownOpening += AgOpenHubMenu_DropDownOpening;

            // Bring fields and profiles up to date when AgOpenGPS starts.
            SyncWithAgOpenHub(true);
        }

        /// <summary>
        /// Sync with the hub in the background: send what's waiting, then bring
        /// profiles and fields up to date. quiet: say nothing if nothing changed.
        /// </summary>
        public async void SyncWithAgOpenHub(bool quiet)
        {
            if (agOpenHub == null || !agOpenHub.Settings.IsPaired || agOpenHub.Syncing) return;
            try
            {
                HubSyncContext context = AgOpenSyncContext();
                HubSyncResult result = await Task.Run(() => agOpenHub.Sync(context));
                if (result == null) return;

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
    }
}
