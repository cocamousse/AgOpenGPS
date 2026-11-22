using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AgOpenGPS.Controls;
using AgOpenGPS.Forms;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Pair this tablet with the AgOpen hub and see how the connection is doing.
    /// Built in code (no designer file) to keep the AgOpen changes in one place.
    /// </summary>
    public class FormHubSettings : Form
    {
        private readonly HubConnection hub;
        private readonly Action syncNow;
        private readonly Func<bool> syncRunning;
        private readonly TextBox textServer = new TextBox();
        private readonly TextBox textCode = new TextBox();
        private readonly Button buttonPair = new Button();
        private readonly Button buttonTest = new Button();
        private readonly Button buttonUnpair = new Button();
        private readonly Button buttonSync = new Button();
        private readonly Button buttonClose = new Button();
        private readonly Label labelMessage = new Label();
        private readonly Label labelStatus = new Label();
        private readonly Timer refreshTimer = new Timer();

        // A sync started from this window, and the last sync before it, so the
        // message can say when it's done.
        private bool syncAsked;
        private HubSyncResult syncBefore;

        /// <param name="syncRunning">True while AgOpenGPS is running a sync (it may not have reached the hub yet).</param>
        public FormHubSettings(HubConnection hub, Action syncNow, Func<bool> syncRunning)
        {
            this.hub = hub;
            this.syncNow = syncNow;
            this.syncRunning = syncRunning;
            BuildLayout();

            textServer.Text = hub.Settings.ServerUrl;
            refreshTimer.Interval = 1000;
            refreshTimer.Tick += (s, e) => ShowStatus();
            ShowStatus();
        }

        private void BuildLayout()
        {
            SuspendLayout();

            var labelTitle = new Label
            {
                Text = "AgOpen Hub",
                Font = new Font("Tahoma", 18F, FontStyle.Bold),
                Location = new Point(16, 10),
                AutoSize = true
            };
            var labelIntro = new Label
            {
                Text = "Connects this tablet to your hub server, which keeps fields, jobs and equipment "
                    + "in step on every tablet and in farmOS.",
                Font = new Font("Tahoma", 11F),
                Location = new Point(16, 50),
                Size = new Size(608, 44)
            };
            var labelServer = new Label
            {
                Text = "Hub address:",
                Font = new Font("Tahoma", 14.25F, FontStyle.Bold),
                Location = new Point(16, 100),
                AutoSize = true
            };
            textServer.Location = new Point(16, 130);
            textServer.Size = new Size(400, 30);
            textServer.Click += TextBox_Click;

            buttonTest.Text = "Test connection";
            buttonTest.Location = new Point(432, 122);
            buttonTest.Size = new Size(192, 46);
            buttonTest.Click += ButtonTest_Click;

            var labelCode = new Label
            {
                Text = "Pairing code:",
                Font = new Font("Tahoma", 14.25F, FontStyle.Bold),
                Location = new Point(16, 176),
                AutoSize = true
            };
            textCode.Location = new Point(16, 206);
            textCode.Size = new Size(200, 30);
            textCode.Click += TextBox_Click;

            buttonPair.Text = "Pair this tablet";
            buttonPair.Location = new Point(232, 198);
            buttonPair.Size = new Size(184, 46);
            buttonPair.Click += ButtonPair_Click;

            var labelCodeHint = new Label
            {
                Text = "Get a code on the server:  aog-hub tablet pair \"Tablet 1\"",
                Font = new Font("Tahoma", 9.75F),
                ForeColor = Color.DimGray,
                Location = new Point(16, 248),
                AutoSize = true
            };

            labelMessage.Font = new Font("Tahoma", 12F, FontStyle.Bold);
            labelMessage.Location = new Point(16, 274);
            labelMessage.Size = new Size(608, 52);

            labelStatus.Font = new Font("Tahoma", 10F);
            labelStatus.Location = new Point(16, 330);
            labelStatus.Size = new Size(500, 106);

            buttonUnpair.Text = "Unpair";
            buttonUnpair.Location = new Point(16, 440);
            buttonUnpair.Size = new Size(150, 46);
            buttonUnpair.Click += ButtonUnpair_Click;

            buttonSync.Text = "Sync now";
            buttonSync.Location = new Point(182, 440);
            buttonSync.Size = new Size(150, 46);
            buttonSync.Click += ButtonSync_Click;

            buttonClose.Image = Properties.Resources.OK64;
            buttonClose.FlatStyle = FlatStyle.Flat;
            buttonClose.FlatAppearance.BorderSize = 0;
            buttonClose.Location = new Point(536, 400);
            buttonClose.Size = new Size(88, 88);
            buttonClose.DialogResult = DialogResult.OK;

            AutoScaleDimensions = new SizeF(10F, 23F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.WhiteSmoke;
            ClientSize = new Size(640, 500);
            ControlBox = false;
            Font = new Font("Tahoma", 14.25F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "AgOpen Hub";
            AcceptButton = buttonClose;

            Controls.Add(labelTitle);
            Controls.Add(labelIntro);
            Controls.Add(labelServer);
            Controls.Add(textServer);
            Controls.Add(buttonTest);
            Controls.Add(labelCode);
            Controls.Add(textCode);
            Controls.Add(buttonPair);
            Controls.Add(labelCodeHint);
            Controls.Add(labelMessage);
            Controls.Add(labelStatus);
            Controls.Add(buttonUnpair);
            Controls.Add(buttonSync);
            Controls.Add(buttonClose);

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            refreshTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            base.OnFormClosed(e);
        }

        private void ShowStatus()
        {
            HubSettings settings = hub.Settings;
            HubState state = hub.State;
            string text = hub.StatusText;

            if (settings.IsPaired)
            {
                text += "\r\nThis tablet: '" + settings.TabletLabel + "' on " + settings.ServerUrl;
            }

            int waiting = hub.Outbox.Count;
            int setAside = hub.Outbox.SetAsideCount;
            text += "\r\nWaiting to send: " + waiting.ToString(CultureInfo.InvariantCulture);
            if (setAside > 0)
            {
                text += "   Refused by the hub: " + setAside.ToString(CultureInfo.InvariantCulture);
            }

            DateTime contact = hub.LastContactUtc;
            if (contact != DateTime.MinValue)
            {
                text += "   Last heard from the hub: " + contact.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            HubSyncResult sync = hub.LastSync;
            bool syncing = hub.Syncing || (syncRunning != null && syncRunning());
            if (syncing)
            {
                text += "\r\nSyncing fields and profiles...";
            }
            else if (sync != null)
            {
                text += "\r\nLast sync " + sync.FinishedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ": " + sync.Summary();
            }
            if (state != HubState.Online && hub.LastProblem.Length > 0)
            {
                text += "\r\n" + hub.LastProblem;
            }

            labelStatus.Text = text;
            switch (state)
            {
                case HubState.Online:
                    labelStatus.ForeColor = Color.DarkGreen;
                    break;
                case HubState.NotPaired:
                case HubState.Connecting:
                    labelStatus.ForeColor = Color.DimGray;
                    break;
                default:
                    labelStatus.ForeColor = Color.DarkRed;
                    break;
            }
            buttonUnpair.Enabled = settings.IsPaired;
            buttonSync.Enabled = settings.IsPaired && !syncing;

            if (syncAsked && !syncing)
            {
                syncAsked = false;
                ShowSyncDone(sync);
            }
        }

        // The sync started here is over: say how it went (the details are in the status below).
        private void ShowSyncDone(HubSyncResult sync)
        {
            if (sync == null || ReferenceEquals(sync, syncBefore))
            {
                ShowMessage("The sync stopped before it finished. See the event log.", false);
                return;
            }
            string at = sync.FinishedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            if (!sync.Completed)
            {
                ShowMessage("The sync didn't finish: see below.", false);
            }
            else if (sync.Problems.Count > 0)
            {
                ShowMessage("Sync finished at " + at + ", with a problem: see below.", false);
            }
            else
            {
                ShowMessage("Sync finished at " + at + ".", true);
            }
        }

        private void StartSync(string message)
        {
            if (syncNow == null) return;
            syncBefore = hub.LastSync;
            syncAsked = true;
            ShowMessage(message, true);
            syncNow();
        }

        private void ShowMessage(string message, bool good)
        {
            labelMessage.Text = message;
            labelMessage.ForeColor = good ? Color.DarkGreen : Color.DarkRed;
        }

        private void SetBusy(bool busy)
        {
            buttonPair.Enabled = !busy;
            buttonTest.Enabled = !busy;
            buttonClose.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private void TextBox_Click(object sender, EventArgs e)
        {
            if (Properties.Settings.Default.setDisplay_isKeyboardOn)
            {
                ((TextBox)sender).ShowKeyboard(this);
                buttonClose.Focus();
            }
        }

        private async void ButtonPair_Click(object sender, EventArgs e)
        {
            SetBusy(true);
            ShowMessage("Pairing...", true);
            try
            {
                string problem = await hub.PairAsync(textServer.Text, textCode.Text);
                if (problem.Length == 0)
                {
                    textCode.Text = "";
                    textServer.Text = hub.Settings.ServerUrl;
                    StartSync("Paired. This tablet is '" + hub.Settings.TabletLabel + "' on the hub. Getting fields and profiles...");
                }
                else
                {
                    ShowMessage(problem, false);
                }
            }
            catch (Exception ex)
            {
                ShowMessage("Pairing failed: " + ex.Message, false);
            }
            finally
            {
                SetBusy(false);
                ShowStatus();
            }
        }

        private async void ButtonTest_Click(object sender, EventArgs e)
        {
            SetBusy(true);
            ShowMessage("Checking...", true);
            try
            {
                string result = await hub.TestAsync(textServer.Text);
                ShowMessage(result, result.StartsWith("Connected", StringComparison.Ordinal) || result.StartsWith("The hub is there.", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                ShowMessage("Check failed: " + ex.Message, false);
            }
            finally
            {
                SetBusy(false);
                ShowStatus();
            }
        }

        private void ButtonSync_Click(object sender, EventArgs e)
        {
            StartSync("Syncing with the hub...");
            ShowStatus();
        }

        private void ButtonUnpair_Click(object sender, EventArgs e)
        {
            DialogResult answer = FormDialog.ShowQuestion(
                "Unpair this tablet?",
                "It stops sending to the hub until it's paired again. Anything waiting to send is kept.",
                DialogSeverity.Warning);
            if (answer != DialogResult.OK) return;
            hub.Unpair();
            ShowMessage("Unpaired.", true);
            ShowStatus();
        }
    }
}
