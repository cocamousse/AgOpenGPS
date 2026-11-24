using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Watches where the tractor is and shows "Field detected" when it drives up
    /// to or into a field, on the map with no field open. Works offline, on the
    /// field folders already on the tablet.
    ///
    /// When it shows:
    /// - inside a field's boundary, at any speed, or
    /// - within ShowWithinM of the boundary while slowing down to turn in (at or
    ///   under ShowMaxSpeedKmh), so driving past fields on the road doesn't set it off.
    /// After Load, Ignore or the 30 seconds run out, that field stays quiet until
    /// the tractor has been more than QuietUntilM away from it.
    ///
    /// With a field open it also asks "Start a job?" when a field itself (not a job
    /// folder) opens, and "Make starting paths?" when a field that had no lines when
    /// it opened has a boundary (straight away, or once the boundary is made).
    /// </summary>
    public class AgOpenFieldWatch
    {
        private const double ShowWithinM = 25.0;
        private const double ShowMaxSpeedKmh = 15.0;
        private const double QuietUntilM = 150.0;
        private const double SecondFieldWithinM = 25.0;
        private const int CheckEveryMs = 2000;
        private const int ReadFieldsEverySeconds = 30;

        private readonly FormGPS mf;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        // Field folders that stay quiet until the tractor leaves them behind.
        private readonly HashSet<string> quiet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private List<FieldOnTablet> fields = new List<FieldOnTablet>();
        private string openFolder;
        private string openMaster;
        private bool openIsMaster;
        private bool askedInMaster;
        private FormAgOpenStartJobAsk askBox;
        private bool pathsWanted;
        private bool pathsAsked;
        private FormAgOpenStartingPaths pathsBox;
        private FormAgOpenHeadlandAsk headlandBox;
        private DateTime fieldsReadUtc = DateTime.MinValue;
        private bool firstLook = true;
        private bool problemLogged;
        private FormAgOpenFieldDetected popup;

        private class FieldOnTablet
        {
            public AgOpenFieldEntry Entry;
            public AgOpenFieldShape Shape;
        }

        public AgOpenFieldWatch(FormGPS mf)
        {
            this.mf = mf;
            timer.Interval = CheckEveryMs;
            timer.Tick += (s, e) => Check();
        }

        public void Start()
        {
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            ClosePopup();
            CloseAsk();
            ClosePaths();
        }

        /// <summary>Field folders changed (e.g. after a hub sync): read them again on the next check.</summary>
        public void FieldsChanged()
        {
            fieldsReadUtc = DateTime.MinValue;
        }

        private void Check()
        {
            try
            {
                CheckNow();
            }
            catch (Exception ex)
            {
                // A bad field file must never stop AgOpenGPS: note it once and carry on.
                if (!problemLogged)
                {
                    problemLogged = true;
                    Log.EventWriter("[AgOpen] Field detection problem: " + ex);
                }
            }
        }

        private void CheckNow()
        {
            if (mf.IsDisposed || mf.AgOpenShuttingDown) return;

            if (mf.isJobStarted)
            {
                // A field is open: close the pop-up, and keep the open field (and, for
                // a job, its master field) quiet so closing it later doesn't set it off
                // straight away.
                ClosePopup();
                NoteOpenField(mf.currentFieldDirectory);
                if (!string.IsNullOrEmpty(openFolder)) quiet.Add(openFolder);
                if (!string.IsNullOrEmpty(openMaster)) quiet.Add(openMaster);

                // A field itself (not a job folder) was loaded, however it was opened:
                // ask "Start a job?" once, after AgOpenGPS's own dialogs (e.g. the draw
                // screen) have closed. Work done without a job never reaches the hub.
                if (openIsMaster && !askedInMaster
                    && !mf.AgOpenScreenOpen && NativeMethods.IsWindowEnabled(mf.Handle))
                {
                    askedInMaster = true;
                    ShowAsk(mf.currentFieldDirectory);
                }

                // A field that had no lines when it opened: once it has a boundary (it may
                // just have been made), offer starting paths, once. Not on top of "Start a
                // job?", the AgOpen screens, or AgOpenGPS's boundary and line screens.
                if (pathsWanted && !pathsAsked && askBox == null && pathsBox == null
                    && !mf.AgOpenScreenOpen && !mf.AgOpenFieldEditScreenOpen && NativeMethods.IsWindowEnabled(mf.Handle))
                {
                    if (mf.trk.gArr.Count > 0) pathsWanted = false; // lines were made another way: don't ask
                    else if (mf.bnd.bndList.Count > 0) ShowPaths(mf.currentFieldDirectory);
                }
                return;
            }
            openFolder = null;
            openMaster = null;
            openIsMaster = false;
            askedInMaster = false;
            pathsWanted = false;
            CloseAsk();
            ClosePaths();
            if (popup != null) return;
            if (mf.isEasyDriveMode || mf.AgOpenScreenOpen) return;
            if (!NativeMethods.IsWindowEnabled(mf.Handle)) return; // an AgOpenGPS dialog is open

            Wgs84? here = mf.AgOpenPosition();
            if (!here.HasValue) return;

            if ((DateTime.UtcNow - fieldsReadUtc).TotalSeconds >= ReadFieldsEverySeconds) ReadFields();

            // How far is each field? Fields without a boundary can't be detected.
            var near = new List<FormAgOpenFieldDetected.Offer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldOnTablet field in fields)
            {
                if (field.Shape.Fences.Count == 0) continue;
                double? distance = AgOpenFields.DistanceFrom(field.Shape, here.Value);
                if (!distance.HasValue) continue;
                seen.Add(field.Entry.Folder);

                if (distance.Value > QuietUntilM)
                {
                    quiet.Remove(field.Entry.Folder);
                    continue;
                }
                if (distance.Value <= ShowWithinM)
                {
                    near.Add(new FormAgOpenFieldDetected.Offer { Entry = field.Entry, Shape = field.Shape, DistanceM = distance.Value });
                }
            }
            quiet.RemoveWhere(folder => !seen.Contains(folder) && !string.Equals(folder, mf.currentFieldDirectory, StringComparison.OrdinalIgnoreCase));

            // Fields the tractor is already in or beside when AgOpenGPS starts don't
            // pop up: the start screen covers those (Continue, Show fields).
            if (firstLook)
            {
                firstLook = false;
                foreach (FormAgOpenFieldDetected.Offer offer in near) quiet.Add(offer.Entry.Folder);
                return;
            }

            bool slow = mf.avgSpeed <= ShowMaxSpeedKmh;
            var candidates = near.FindAll(o => !quiet.Contains(o.Entry.Folder) && (o.DistanceM <= 0 || slow));
            if (candidates.Count == 0) return;
            candidates.Sort((a, b) => a.DistanceM.CompareTo(b.DistanceM));

            FormAgOpenFieldDetected.Offer main = candidates[0];
            FormAgOpenFieldDetected.Offer second = null;
            if (candidates.Count > 1 && candidates[1].DistanceM <= SecondFieldWithinM) second = candidates[1];
            Show(main, second);
        }

        // A field was opened (by AgOpen or AgOpenGPS's own Field button). For a job
        // folder: note its master, and remember it for Continue.
        private void NoteOpenField(string folder)
        {
            if (string.IsNullOrEmpty(folder) || string.Equals(folder, openFolder, StringComparison.OrdinalIgnoreCase)) return;
            openFolder = folder;
            openMaster = null;
            askedInMaster = false;

            // Starting paths are offered only to a field that has no lines as it opens,
            // and not when it was opened straight into the draw screen (Edit paths).
            bool editPaths = mf.AgOpenTookEditPaths(folder);
            pathsWanted = mf.trk.gArr.Count == 0 && !editPaths;
            pathsAsked = false;
            ClosePaths();
            AgOpenJob job = AgOpenJobs.Read(System.IO.Path.Combine(RegistrySettings.fieldsDirectory, folder));
            openIsMaster = job == null
                && !AgOpenJobs.IsJobFolder(System.IO.Path.Combine(RegistrySettings.fieldsDirectory, folder));
            if (job == null) return;
            openMaster = job.MasterFolder;
            AgOpenJobs.RememberOpened(folder);
        }

        private void ReadFields()
        {
            var list = new List<FieldOnTablet>();
            foreach (AgOpenFieldEntry entry in AgOpenFields.List(RegistrySettings.fieldsDirectory, null))
            {
                list.Add(new FieldOnTablet { Entry = entry, Shape = AgOpenFields.Shape(entry) });
            }
            fields = list;
            fieldsReadUtc = DateTime.UtcNow;
        }

        private void Show(FormAgOpenFieldDetected.Offer main, FormAgOpenFieldDetected.Offer second)
        {
            var shown = new FormAgOpenFieldDetected(mf, main, second);
            popup = shown;
            shown.FormClosed += (s, e) =>
            {
                if (popup == shown) popup = null;
                // Whatever the answer, these fields stay quiet until the tractor leaves them.
                quiet.Add(main.Entry.Folder);
                if (second != null) quiet.Add(second.Entry.Folder);
                string chosen = shown.ChosenFolder;
                if (!string.IsNullOrEmpty(chosen) && !mf.IsDisposed) mf.AgOpenLoadField(chosen);
            };
            Log.EventWriter("[AgOpen] Field detected: " + main.Entry.Folder
                + (second != null ? " (also close: " + second.Entry.Folder + ")" : ""));
            shown.Show(mf);
        }

        private void ShowAsk(string folder)
        {
            CloseAsk();
            var shown = new FormAgOpenStartJobAsk(mf, folder);
            askBox = shown;
            shown.FormClosed += (s, e) =>
            {
                if (askBox == shown) askBox = null;
                if (shown.StartJob && !mf.IsDisposed && mf.isJobStarted
                    && string.Equals(mf.currentFieldDirectory, folder, StringComparison.OrdinalIgnoreCase))
                {
                    mf.AgOpenWorkInMaster(folder);
                }
            };
            shown.Show(mf);
        }

        private void ShowPaths(string folder)
        {
            pathsAsked = true;
            ClosePaths();
            AgOpenStartingPaths.Plan plan = mf.AgOpenPlanStartingPaths();
            if (plan == null) return;

            var fences = new List<List<vec3>>();
            foreach (CBoundaryList boundary in mf.bnd.bndList) fences.Add(new List<vec3>(boundary.fenceLine));

            var shown = new FormAgOpenStartingPaths(mf, folder, plan, fences);
            pathsBox = shown;
            shown.FormClosed += (s, e) =>
            {
                if (pathsBox == shown) pathsBox = null;
                if (mf.IsDisposed || !mf.isJobStarted
                    || !string.Equals(mf.currentFieldDirectory, folder, StringComparison.OrdinalIgnoreCase)) return;
                if (shown.Choice == FormAgOpenStartingPaths.Answer.Make)
                {
                    // Then, for a field without a headland: "Would you like to create
                    // headlands?", once this box has gone.
                    bool askHeadland = !mf.AgOpenHasHeadland;
                    string made = mf.AgOpenAddStartingPaths(!askHeadland);
                    if (made != null && askHeadland) mf.BeginInvoke((Action)(() => ShowHeadland(folder, made)));
                }
                // The draw screen waits until this box has gone.
                else if (shown.Choice == FormAgOpenStartingPaths.Answer.Draw) mf.BeginInvoke((Action)mf.AgOpenDrawPaths);
            };
            Log.EventWriter("[AgOpen] Offering starting paths for '" + folder + "'"
                + (plan.Side != null ? "" : " (border path only: no clear side)"));
            shown.Show(mf);
        }

        private void ShowHeadland(string folder, string made)
        {
            if (mf.IsDisposed || !mf.isJobStarted || mf.bnd.bndList.Count == 0 || mf.AgOpenHasHeadland
                || !string.Equals(mf.currentFieldDirectory, folder, StringComparison.OrdinalIgnoreCase)) return;

            if (headlandBox != null && !headlandBox.IsDisposed) headlandBox.Close();
            var shown = new FormAgOpenHeadlandAsk(mf, made);
            headlandBox = shown;
            shown.FormClosed += (s, e) =>
            {
                if (headlandBox == shown) headlandBox = null;
                if (shown.Passes < 1 || mf.IsDisposed || !mf.isJobStarted
                    || !string.Equals(mf.currentFieldDirectory, folder, StringComparison.OrdinalIgnoreCase)) return;
                mf.AgOpenMakeHeadland(shown.Passes);
            };
            Log.EventWriter("[AgOpen] Offering a headland for '" + folder + "'");
            shown.Show(mf);
        }

        // Closes "Make starting paths?" and "Would you like to create headlands?".
        private void ClosePaths()
        {
            if (pathsBox != null && !pathsBox.IsDisposed) pathsBox.Close();
            pathsBox = null;
            if (headlandBox != null && !headlandBox.IsDisposed) headlandBox.Close();
            headlandBox = null;
        }

        private void CloseAsk()
        {
            if (askBox != null && !askBox.IsDisposed) askBox.Close();
            askBox = null;
        }

        private void ClosePopup()
        {
            if (popup != null && !popup.IsDisposed) popup.Close();
            popup = null;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsWindowEnabled(IntPtr hWnd);
        }
    }
}
