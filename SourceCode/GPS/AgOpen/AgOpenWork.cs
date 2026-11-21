using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Sends the work done in a job folder to the hub as it happens, through the
    /// offline queue (so nothing is lost out of WiFi range):
    /// - coverage, read from the job's own Sections.txt each time AgOpenGPS saves it
    ///   (about every 30 seconds, and when the field closes), as numbered batches of
    ///   strips in lon/lat. The hub ignores a batch it already has.
    /// - the job itself, just before its first coverage (so a job started by mistake
    ///   never shows up empty in farmOS);
    /// - work sessions: a session starts with the first coverage and ends when the
    ///   field closes. No new coverage for 20 minutes (refilling, lunch) ends it too,
    ///   and the next coverage starts a new one. Driving to the field isn't counted.
    /// - "finished" (95% of the field or more, as AgOpenGPS shows it) or "partly done"
    ///   when the field closes. Continue reopens the job at the hub.
    /// - "Delete applied area" in AgOpenGPS clears the job's coverage at the hub too.
    /// Nothing in AgOpenGPS's own drawing or section control changes.
    /// What's been sent is kept in agopen-work.json in the job folder, so restarts and
    /// power cuts never lose or double anything: a job left open is closed properly
    /// the next time AgOpenGPS starts.
    ///
    /// The same check also notes edits to whatever field is open and sends them
    /// when it closes (FieldEdits), and sends tractor and implement profile changes
    /// soon after they're saved (HubProfileSync).
    /// </summary>
    public class AgOpenWork
    {
        public const string FileName = "agopen-work.json";

        private const string OpenJobFileName = "open-job.json";
        private const int CheckEverySeconds = 10;
        private const double SaveEverySeconds = 30;
        private const double PauseMinutes = 20;
        private const double FinishedPercent = 95;
        private const int StripsPerBatch = 150;
        private const int HeadLength = 64;

        private readonly FormGPS mf;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private Tracked current;
        private string notAJob;
        private bool problemLogged;

        // Field edits: the folder open at the last check, and closed folders whose edits still need sending.
        private string lastOpen;
        private readonly List<string> editsToSend = new List<string>();

        // What's been sent for a job, kept in its folder.
        private class WorkState
        {
            /// <summary>How much of Sections.txt has been sent (bytes).</summary>
            [JsonProperty("sections_offset")]
            public long Offset { get; set; }

            /// <summary>The start of Sections.txt, to notice it being rewritten.</summary>
            [JsonProperty("sections_head")]
            public string Head { get; set; }

            [JsonProperty("next_seq")]
            public int NextSeq { get; set; }

            [JsonProperty("job_sent")]
            public bool JobSent { get; set; }

            [JsonProperty("session_id")]
            public string SessionId { get; set; }

            [JsonProperty("session_started_at")]
            public string SessionStartedAt { get; set; }

            [JsonProperty("last_coverage_at")]
            public string LastCoverageAt { get; set; }

            [JsonProperty("any_coverage")]
            public bool AnyCoverage { get; set; }

            /// <summary>The share of the field covered, as AgOpenGPS last showed it.</summary>
            [JsonProperty("percent")]
            public double? Percent { get; set; }

            [JsonProperty("finished_at")]
            public string FinishedAt { get; set; }
        }

        // A job being worked (or one left open, being closed).
        private class Tracked
        {
            public string Folder;
            public string Directory;
            public AgOpenJob Job;
            public WorkState State;
            public LocalPlane Plane;

            /// <summary>The field was opened no earlier than this (MinValue: not known).</summary>
            public DateTime OpenedUtc = DateTime.MinValue;
        }

        // One batch of coverage for POST /jobs/{id}/coverage.
        private class CoverageBatch
        {
            [JsonProperty("seq")]
            public int Seq { get; set; }

            [JsonProperty("session_id")]
            public string SessionId { get; set; }

            [JsonProperty("recorded_at")]
            public string RecordedAt { get; set; }

            [JsonProperty("strips")]
            public List<List<double[]>> Strips { get; set; }
        }

        public AgOpenWork(FormGPS mf)
        {
            this.mf = mf;
            timer.Interval = CheckEverySeconds * 1000;
            timer.Tick += (s, e) => Safely(CheckNow);
        }

        /// <summary>Start following jobs. A job left open last time (power cut) is closed first.</summary>
        public void Start()
        {
            Safely(CloseLeftoverJob);
            timer.Start();
        }

        /// <summary>AgOpenGPS is closing (the field has been saved): send the rest and close the job.</summary>
        public void Stop()
        {
            timer.Stop();
            Tracked closing = current;
            current = null;
            if (closing != null) Close(closing);

            // The field that was open has been saved and closed: send its edits too.
            if (lastOpen != null && !editsToSend.Contains(lastOpen)) editsToSend.Add(lastOpen);
            lastOpen = null;
            Safely(() => SendFieldEdits(null));
        }

        private void Safely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // Sending work must never stop AgOpenGPS: note it once and carry on.
                if (!problemLogged)
                {
                    problemLogged = true;
                    Log.EventWriter("[AgOpen] Sending work to the hub: " + ex);
                }
            }
        }

        private HubConnection Hub
        {
            get
            {
                HubConnection hub = mf.agOpenHub;
                return hub != null && hub.Settings.IsPaired ? hub : null;
            }
        }

        private void CheckNow()
        {
            if (mf.IsDisposed) return;
            string open = mf.isJobStarted ? mf.currentFieldDirectory : null;
            if (string.IsNullOrEmpty(open)) open = null;

            Safely(() => CheckWork(open));
            Safely(() => CheckFieldEdits(open));
            Safely(CheckProfiles);
        }

        // Field edits (any field folder: a field itself or a job): noted while it's
        // open, sent when it closes.
        private void CheckFieldEdits(string open)
        {
            if (lastOpen != null && !string.Equals(open, lastOpen, StringComparison.OrdinalIgnoreCase)
                && !editsToSend.Contains(lastOpen))
            {
                editsToSend.Add(lastOpen);
            }
            lastOpen = open;
            if (open != null)
            {
                FieldEdits.Notice(Path.Combine(RegistrySettings.fieldsDirectory, open), RegistrySettings.toolProfileName,
                    mf.tool != null ? mf.tool.width : (double?)null);
            }
            SendFieldEdits(open);
        }

        private void SendFieldEdits(string open)
        {
            HubConnection hub = Hub;
            // While a sync runs it may be writing field folders: wait for it.
            if (editsToSend.Count == 0 || hub == null || mf.AgOpenSyncing) return;
            foreach (string folder in editsToSend.ToArray())
            {
                if (string.Equals(folder, open, StringComparison.OrdinalIgnoreCase)) continue; // opened again: when it next closes
                bool deferred = false;
                Safely(() => FieldEdits.SendChanges(hub, Path.Combine(RegistrySettings.fieldsDirectory, folder), true, open, out deferred));
                if (!deferred) editsToSend.Remove(folder);
            }
        }

        // A tractor or implement profile saved since the last look goes to the hub.
        private void CheckProfiles()
        {
            HubConnection hub = Hub;
            if (hub == null || mf.AgOpenSyncing) return;
            HubProfileSync.SendIfChanged(hub, "vehicle", RegistrySettings.vehiclesDirectory, RegistrySettings.vehicleProfileName);
            HubProfileSync.SendIfChanged(hub, "tool", RegistrySettings.toolsDirectory, RegistrySettings.toolProfileName);
        }

        // Coverage, sessions and finishing for the job that's open (or just closed).
        private void CheckWork(string open)
        {
            // The job we were following has closed (or another field was opened).
            if (current != null && (open == null || !string.Equals(open, current.Folder, StringComparison.OrdinalIgnoreCase)))
            {
                Tracked closed = current;
                current = null;
                Close(closed);
            }

            if (open == null) notAJob = null;
            HubConnection hub = Hub;
            if (open == null || hub == null) return;

            if (current == null)
            {
                if (string.Equals(open, notAJob, StringComparison.OrdinalIgnoreCase)) return;
                current = Begin(open);
                if (current == null)
                {
                    notAJob = open; // a master field or a plain AgOpenGPS field: nothing to send
                    return;
                }
            }
            Follow(hub, current);
        }

        // A job folder was opened: start following it.
        private Tracked Begin(string folder)
        {
            Tracked tracked;
            try
            {
                tracked = Load(folder);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Can't send the work in '" + folder + "': " + ex.Message);
                return null;
            }
            if (tracked == null) return null;

            // Opened since the last check.
            tracked.OpenedUtc = DateTime.UtcNow.AddSeconds(-CheckEverySeconds);
            NoteOpenJob(folder);
            Log.EventWriter("[AgOpen] Sending the work in job " + folder + " to the hub");
            return tracked;
        }

        private void Follow(HubConnection hub, Tracked tracked)
        {
            // The share of the field done, for "finished" or "partly done".
            CFieldData fd = mf.fd;
            if (fd != null && fd.areaBoundaryOuterLessInner > 1)
            {
                tracked.State.Percent = Math.Round(Math.Max(0, Math.Min(100,
                    100.0 * fd.actualAreaCovered / fd.areaBoundaryOuterLessInner)), 1);
            }

            SendNewCoverage(hub, tracked);

            // No new coverage for a while: that stretch of work is over (refilling, lunch...).
            DateTime last;
            if (tracked.State.SessionId != null && TryTime(tracked.State.LastCoverageAt, out last)
                && (DateTime.UtcNow - last).TotalMinutes >= PauseMinutes)
            {
                EndSession(hub, tracked);
                SaveState(tracked);
            }
        }

        // The field closed (or AgOpenGPS is closing, or it was left open by a power cut):
        // send the last coverage AgOpenGPS saved, end the session, and mark the job
        // finished or partly done.
        private void Close(Tracked tracked)
        {
            HubConnection hub = Hub;
            if (hub == null) return;
            Safely(() =>
            {
                SendNewCoverage(hub, tracked);
                EndSession(hub, tracked);
                WorkState state = tracked.State;
                if (state.JobSent && state.AnyCoverage && state.FinishedAt == null)
                {
                    bool done = !state.Percent.HasValue || state.Percent.Value >= FinishedPercent;
                    DateTime finished;
                    if (!TryTime(state.LastCoverageAt, out finished)) finished = DateTime.UtcNow;
                    state.FinishedAt = AgOpenJobs.HubTime(finished);
                    hub.Enqueue("POST", "/jobs/" + tracked.Job.JobId + "/finish",
                        new JObject { ["finished_at"] = state.FinishedAt, ["status"] = done ? "finished" : "partial" },
                        "finish " + tracked.Folder);
                    Log.EventWriter("[AgOpen] Job " + tracked.Folder + " closed: " + (done ? "finished" : "partly done")
                        + (state.Percent.HasValue ? " (" + state.Percent.Value.ToString("N0", CultureInfo.InvariantCulture) + "% of the field)" : ""));
                }
                SaveState(tracked);
                ClearOpenJob(tracked.Folder);
            });
        }

        // After a power cut the job that was open never closed: close it now.
        private void CloseLeftoverJob()
        {
            if (Hub == null) return;
            string path = Path.Combine(HubSettings.Folder, OpenJobFileName);
            if (!File.Exists(path)) return;
            string folder = (string)HubJson.ParseObject(File.ReadAllText(path))["folder"];
            Tracked tracked = string.IsNullOrEmpty(folder) ? null : Load(folder);
            if (tracked == null)
            {
                File.Delete(path); // the folder is gone or isn't a job any more
                return;
            }
            Log.EventWriter("[AgOpen] Job " + folder + " was left open last time: closing it at the hub");
            Close(tracked);
        }

        #region Coverage

        private void SendNewCoverage(HubConnection hub, Tracked tracked)
        {
            string path = Path.Combine(tracked.Directory, "Sections.txt");
            if (!File.Exists(path)) return;
            WorkState state = tracked.State;

            DateTime recorded = File.GetLastWriteTimeUtc(path);
            string head;
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                head = ReadText(stream, 0, Math.Min(HeadLength, length));

                // "Delete applied area" empties Sections.txt: clear the job's coverage at the hub too.
                bool rewritten = length < state.Offset
                    || (state.Offset > 0 && state.Head != null && !head.StartsWith(state.Head, StringComparison.Ordinal));
                if (rewritten)
                {
                    Log.EventWriter("[AgOpen] The coverage in job " + tracked.Folder + " was deleted: clearing it at the hub");
                    if (state.JobSent && state.AnyCoverage)
                    {
                        hub.Enqueue("POST", "/jobs/" + tracked.Job.JobId + "/coverage/clear",
                            new JObject { ["cleared_at"] = AgOpenJobs.HubTime(DateTime.UtcNow) },
                            "clear coverage " + tracked.Folder);
                    }
                    state.Offset = 0;
                    state.Head = null;
                    SaveState(tracked);
                }
                if (length <= state.Offset) return;
                text = ReadText(stream, state.Offset, length - state.Offset);
            }

            int usedChars;
            List<List<double[]>> patches = ReadPatches(text, out usedChars);
            if (usedChars == 0) return; // a patch still being written: next time
            if (state.Offset == 0) state.Head = head;
            state.Offset += Encoding.UTF8.GetByteCount(text.Substring(0, usedChars));

            var strips = new List<List<double[]>>();
            foreach (List<double[]> patch in patches)
            {
                List<double[]> strip = ToLonLat(tracked.Plane, patch);
                if (strip.Count >= 3) strips.Add(strip);
            }
            if (strips.Count == 0)
            {
                SaveState(tracked);
                return;
            }

            // A long gap since the last coverage: the earlier stretch of work ended then.
            DateTime last;
            if (state.SessionId != null && TryTime(state.LastCoverageAt, out last)
                && (recorded - last).TotalMinutes >= PauseMinutes)
            {
                EndSession(hub, tracked);
            }
            SendJob(hub, tracked);
            if (state.SessionId == null) StartSession(hub, tracked, recorded);

            string recordedAt = AgOpenJobs.HubTime(recorded);
            for (int start = 0; start < strips.Count; start += StripsPerBatch)
            {
                var batch = new CoverageBatch
                {
                    Seq = state.NextSeq++,
                    SessionId = state.SessionId,
                    RecordedAt = recordedAt,
                    Strips = strips.GetRange(start, Math.Min(StripsPerBatch, strips.Count - start))
                };
                hub.Enqueue("POST", "/jobs/" + tracked.Job.JobId + "/coverage",
                    new JObject { ["batches"] = new JArray(JObject.FromObject(batch)) },
                    "coverage " + tracked.Folder + " #" + batch.Seq.ToString(CultureInfo.InvariantCulture));
            }

            state.LastCoverageAt = recordedAt;
            state.AnyCoverage = true;
            state.FinishedAt = null; // more work after a finish: it's finished again when the field closes
            SaveState(tracked);
        }

        private static string ReadText(FileStream stream, long from, long count)
        {
            if (count <= 0) return "";
            stream.Seek(from, SeekOrigin.Begin);
            var bytes = new byte[count];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(bytes, 0, read);
        }

        /// <summary>
        /// The complete patches in a piece of Sections.txt: a line with the number of
        /// points, then one "easting,northing,heading" line per point. A patch still
        /// being written is left for next time. usedChars: how much of the text was read.
        /// </summary>
        private static List<List<double[]>> ReadPatches(string text, out int usedChars)
        {
            var patches = new List<List<double[]>>();
            int position = 0;
            usedChars = 0;
            while (position < text.Length)
            {
                int end = text.IndexOf('\n', position);
                if (end < 0) break;
                string countLine = text.Substring(position, end - position).Trim().Trim('﻿');
                int count;
                if (!int.TryParse(countLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                    || count < 0 || count > 1000000)
                {
                    position = end + 1; // a blank or broken line: skip it
                    usedChars = position;
                    continue;
                }

                var points = new List<double[]>(count);
                int next = end + 1;
                bool complete = true, broken = false;
                for (int i = 0; i < count; i++)
                {
                    int lineEnd = text.IndexOf('\n', next);
                    if (lineEnd < 0)
                    {
                        complete = false;
                        break;
                    }
                    string[] parts = text.Substring(next, lineEnd - next).Trim().Split(',');
                    double east, north;
                    if (parts.Length < 2
                        || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out east)
                        || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out north)
                        || double.IsNaN(east) || double.IsNaN(north) || double.IsInfinity(east) || double.IsInfinity(north))
                    {
                        // Not a point: this patch was cut short (a power cut while saving)
                        // and the next one starts here. Drop the broken one.
                        broken = true;
                        break;
                    }
                    points.Add(new[] { east, north });
                    next = lineEnd + 1;
                }
                if (!complete) break;

                position = next;
                usedChars = position;
                if (broken) continue;
                // A patch starts with its colour, then pairs of left and right edge points
                // (a triangle strip), so it has an odd number of points. An even number
                // means the colour is missing (as AgOpenGPS reads it too).
                if (points.Count % 2 == 1) points.RemoveAt(0);
                if (points.Count >= 3) patches.Add(points);
            }
            return patches;
        }

        private static List<double[]> ToLonLat(LocalPlane plane, List<double[]> patch)
        {
            var strip = new List<double[]>(patch.Count);
            foreach (double[] point in patch)
            {
                Wgs84 p = plane.ConvertGeoCoordToWgs84(new GeoCoord(point[1], point[0]));
                double lon = p.Longitude, lat = p.Latitude;
                // A broken point would make the hub turn the whole batch away: leave it out.
                if (double.IsNaN(lon) || double.IsNaN(lat) || Math.Abs(lon) > 180 || Math.Abs(lat) > 90) continue;
                strip.Add(new[] { Math.Round(lon, 8), Math.Round(lat, 8) });
            }
            return strip;
        }

        #endregion

        #region Job and sessions

        // The job goes to the hub once, just before its first coverage.
        private static void SendJob(HubConnection hub, Tracked tracked)
        {
            if (tracked.State.JobSent) return;
            hub.Enqueue("PUT", "/jobs/" + tracked.Job.JobId, AgOpenJobs.HubBody(tracked.Job), "job " + tracked.Folder);
            tracked.State.JobSent = true;
        }

        private static void StartSession(HubConnection hub, Tracked tracked, DateTime firstCoverage)
        {
            WorkState state = tracked.State;
            DateTime lastSent, jobStart;
            bool sentBefore = TryTime(state.LastCoverageAt, out lastSent);
            bool knowJobStart = TryTime(tracked.Job.StartedAt, out jobStart);

            DateTime started;
            if (!sentBefore && knowJobStart && tracked.OpenedUtc != DateTime.MinValue && firstCoverage < tracked.OpenedUtc)
            {
                // Work done before it could be sent (the tablet wasn't paired yet):
                // all that's known is that it began when the job was started.
                started = jobStart;
            }
            else
            {
                // AgOpenGPS saves coverage about every 30 seconds, so this stretch of work
                // began up to 30 seconds before it was saved: but not before the field was
                // opened, the last coverage already sent, or the job was started.
                started = firstCoverage.AddSeconds(-SaveEverySeconds);
                if (tracked.OpenedUtc != DateTime.MinValue && firstCoverage >= tracked.OpenedUtc && started < tracked.OpenedUtc)
                    started = tracked.OpenedUtc;
                if (sentBefore && started < lastSent) started = lastSent;
                if (knowJobStart && started < jobStart) started = jobStart;
            }
            if (started > firstCoverage) started = firstCoverage;

            state.SessionId = Guid.NewGuid().ToString("N");
            state.SessionStartedAt = AgOpenJobs.HubTime(started);
            hub.Enqueue("PUT", "/jobs/" + tracked.Job.JobId + "/sessions/" + state.SessionId,
                new JObject { ["started_at"] = state.SessionStartedAt },
                "work session " + tracked.Folder);
        }

        // The session ends at its last coverage.
        private static void EndSession(HubConnection hub, Tracked tracked)
        {
            WorkState state = tracked.State;
            if (state.SessionId == null) return;
            string endedAt = state.LastCoverageAt ?? state.SessionStartedAt;
            hub.Enqueue("PUT", "/jobs/" + tracked.Job.JobId + "/sessions/" + state.SessionId,
                new JObject { ["started_at"] = state.SessionStartedAt, ["ended_at"] = endedAt },
                "end of work session " + tracked.Folder);
            state.SessionId = null;
            state.SessionStartedAt = null;
        }

        #endregion

        #region Saved state

        private static Tracked Load(string folder)
        {
            string directory = Path.Combine(RegistrySettings.fieldsDirectory, folder);
            AgOpenJob job = AgOpenJobs.Read(directory);
            if (job == null || string.IsNullOrEmpty(job.JobId)) return null;
            return new Tracked
            {
                Folder = folder,
                Directory = directory,
                Job = job,
                State = LoadState(directory),
                Plane = new LocalPlane(FieldPlaneFiles.LoadOrigin(directory), new SharedFieldProperties())
            };
        }

        private static WorkState LoadState(string directory)
        {
            string path = Path.Combine(directory, FileName);
            try
            {
                if (File.Exists(path)) return JsonConvert.DeserializeObject<WorkState>(File.ReadAllText(path)) ?? new WorkState();
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable " + path + ": " + ex.Message);
            }
            return new WorkState();
        }

        private static void SaveState(Tracked tracked)
        {
            HubFiles.WriteAllTextSafely(Path.Combine(tracked.Directory, FileName),
                JsonConvert.SerializeObject(tracked.State, Formatting.Indented));
        }

        // Which job is open, so a power cut can be tidied up at the next start.
        private static void NoteOpenJob(string folder)
        {
            Directory.CreateDirectory(HubSettings.Folder);
            HubFiles.WriteAllTextSafely(Path.Combine(HubSettings.Folder, OpenJobFileName),
                new JObject { ["folder"] = folder }.ToString(Formatting.Indented));
        }

        private static void ClearOpenJob(string folder)
        {
            string path = Path.Combine(HubSettings.Folder, OpenJobFileName);
            if (!File.Exists(path)) return;
            string open = (string)HubJson.ParseObject(File.ReadAllText(path))["folder"];
            if (string.Equals(open, folder, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }

        private static bool TryTime(string text, out DateTime utc)
        {
            utc = DateTime.MinValue;
            return !string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }

        #endregion
    }
}
