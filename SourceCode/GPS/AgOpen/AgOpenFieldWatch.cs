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
        private string masterToPrompt;
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

                // A master field opened with AgOpenGPS's own Field button (not from
                // AgOpen's screens): offer the job prompt once AgOpenGPS's dialogs have
                // closed, so the work goes into a job folder and reaches the hub.
                if (masterToPrompt != null
                    && string.Equals(masterToPrompt, mf.currentFieldDirectory, StringComparison.OrdinalIgnoreCase)
                    && !mf.AgOpenScreenOpen && NativeMethods.IsWindowEnabled(mf.Handle))
                {
                    string folder = masterToPrompt;
                    masterToPrompt = null;
                    mf.AgOpenMasterOpened(folder);
                }
                return;
            }
            // The master opened to edit it has closed: next time it needs a reason again.
            if (openFolder != null && string.Equals(openFolder, mf.AgOpenEditingMaster, StringComparison.OrdinalIgnoreCase))
            {
                mf.AgOpenEditingMaster = null;
            }
            openFolder = null;
            openMaster = null;
            masterToPrompt = null;
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
            masterToPrompt = null;
            AgOpenJob job = AgOpenJobs.Read(System.IO.Path.Combine(RegistrySettings.fieldsDirectory, folder));
            if (job == null)
            {
                // A master field: fine when opened to edit it (Edit paths), otherwise
                // the operator is asked whether to start a job.
                if (!string.Equals(folder, mf.AgOpenEditingMaster, StringComparison.OrdinalIgnoreCase)
                    && !AgOpenJobs.IsJobFolder(System.IO.Path.Combine(RegistrySettings.fieldsDirectory, folder)))
                {
                    masterToPrompt = folder;
                }
                return;
            }
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
