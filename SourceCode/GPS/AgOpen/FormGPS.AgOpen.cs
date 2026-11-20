using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgOpenGPS.AgOpen;

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
        }

        // Show how many requests are waiting, right in the menu.
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
            using (var form = new FormHubSettings(agOpenHub))
            {
                form.ShowDialog(this);
            }
        }
    }
}
