using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Sends edits made to fields on this tablet to the hub, each stamped with the
    /// time it was made, so "newest edit wins" works across tablets, the office
    /// and farmOS:
    /// - while a field is open, each check notes which items changed, when, and
    ///   which implement was loaded (lines and recorded paths are tagged with it);
    /// - when the field closes, and at every sync, whatever differs from what the
    ///   hub has goes up through the offline queue;
    /// - a job folder's edits go to its field on the hub, and are copied into the
    ///   master field on this tablet straight away;
    /// - a field that isn't on the hub yet gets an id and goes up the first time
    ///   it's closed after use (the hub may find it's a field it already has);
    /// - renaming a field's folder renames the field.
    /// What the hub has is kept in agopen-edits.json in each field folder, so
    /// nothing is sent twice and a folder is never overwritten from the hub while
    /// it holds edits the hub hasn't got.
    /// </summary>
    internal static class FieldEdits
    {
        public const string FileName = "agopen-edits.json";

        // The sync (on its own thread) and the main window both come here.
        private static readonly object Gate = new object();

        internal class State
        {
            /// <summary>Fingerprints of what the hub has, as far as this tablet knows (null: not worked out yet).</summary>
            [JsonProperty("base")]
            public Dictionary<string, string> Base { get; set; }

            /// <summary>Fingerprints last noticed here, to tell when an item changes.</summary>
            [JsonProperty("seen")]
            public Dictionary<string, string> Seen { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>When each item last changed here (UTC).</summary>
            [JsonProperty("edited_at")]
            public Dictionary<string, string> EditedAt { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The implement loaded when a line or path was made, and its width.</summary>
            [JsonProperty("tool")]
            public Dictionary<string, string> Tool { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

            [JsonProperty("width_m")]
            public Dictionary<string, double> Width { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);

            /// <summary>The files' save times when last looked at, to skip folders that haven't changed.</summary>
            [JsonProperty("files")]
            public Dictionary<string, long> Files { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The outbox request with edits not yet delivered: the hub's copy mustn't overwrite them until it's gone.</summary>
            [JsonProperty("waiting")]
            public string Waiting { get; set; }
        }

        #region Noting edits

        /// <summary>The folder now matches what the hub has (just written from it, or a job folder just made).</summary>
        public static void SetBase(string folder)
        {
            lock (Gate)
            {
                FieldSnapshot snap = FieldSnapshot.Read(folder);
                var state = new State
                {
                    Base = new Dictionary<string, string>(snap.Fingerprints, StringComparer.Ordinal),
                    Seen = new Dictionary<string, string>(snap.Fingerprints, StringComparer.Ordinal),
                    Files = FileStamps(folder)
                };
                Save(folder, state);
            }
        }

        /// <summary>
        /// While a field is open: note which items changed since the last look,
        /// when, and with which implement loaded. Cheap when nothing was saved.
        /// </summary>
        public static void Notice(string folder, string tool, double? width)
        {
            lock (Gate)
            {
                if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "Field.txt"))) return;
                Dictionary<string, long> stamps = FileStamps(folder);
                State state = Load(folder);
                if (state != null && state.Base != null && SameStamps(stamps, state.Files)) return;

                // A file saved in the last couple of seconds may still be being written.
                long recent = DateTime.UtcNow.AddSeconds(-2).Ticks;
                foreach (long stamp in stamps.Values)
                {
                    if (stamp > recent) return;
                }

                FieldSnapshot snap = FieldSnapshot.Read(folder);
                if (!snap.Complete) return;
                state = LoadOrStart(folder, snap);
                Record(state, snap, tool, width);
                state.Files = stamps;
                Save(folder, state);
            }
        }

        // Note items that changed since last noticed, with their save time and the
        // implement loaded now (null: not known, e.g. noticed only at a sync).
        private static void Record(State state, FieldSnapshot snap, string tool, double? width)
        {
            foreach (KeyValuePair<string, string> item in snap.Fingerprints)
            {
                string seen;
                if (state.Seen.TryGetValue(item.Key, out seen) && seen == item.Value) continue;
                state.Seen[item.Key] = item.Value;
                state.EditedAt[item.Key] = AgOpenJobs.HubTime(snap.SavedAt[item.Key]);
                Tag(state, item.Key, tool, width);
            }
            foreach (string key in state.Seen.Keys.ToList())
            {
                if (snap.Fingerprints.ContainsKey(key)) continue;
                state.Seen.Remove(key);
                state.EditedAt[key] = AgOpenJobs.HubTime(RemovedAt(snap.Folder, key));
            }
        }

        private static void Tag(State state, string key, string tool, double? width)
        {
            if (string.IsNullOrEmpty(tool)) return;
            if (!key.StartsWith(FieldSnapshot.LinePrefix, StringComparison.Ordinal)
                && !key.StartsWith(FieldSnapshot.PathPrefix, StringComparison.Ordinal)) return;
            state.Tool[key] = tool;
            if (width.HasValue && width.Value > 0) state.Width[key] = Math.Round(width.Value, 3);
        }

        // A removed line counts from when TrackLines.txt was saved without it; a
        // removed path (its file is gone) from now.
        private static DateTime RemovedAt(string folder, string key)
        {
            string file = FieldSnapshot.FileOf(key);
            string path = file != null ? Path.Combine(folder, file) : null;
            return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
        }

        #endregion

        #region Sending

        /// <summary>
        /// Send what changed in this field folder to the hub, through the outbox.
        /// allowNew: a field that isn't on the hub yet gets an id and goes up (it
        /// was just used); otherwise only fields the hub knows, and jobs, are sent.
        /// openFolder: the field open in AgOpenGPS (never touched). deferred: a job
        /// whose master field is open waits until that closes.
        /// Returns a note on what was sent, or null if nothing was.
        /// </summary>
        public static string SendChanges(HubConnection hub, string folder, bool allowNew, string openFolder, out bool deferred)
        {
            deferred = false;
            lock (Gate)
            {
                if (hub == null || !Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "Field.txt"))) return null;
                string folderName = Path.GetFileName(folder);
                if (IsOpen(folderName, openFolder))
                {
                    deferred = true;
                    return null;
                }

                AgOpenJob job = AgOpenJobs.Read(folder);
                string master = job != null ? MasterFolder(folder, job) : null;
                if (master != null && IsOpen(Path.GetFileName(master), openFolder))
                {
                    deferred = true;
                    return null;
                }
                HubFieldState field = job == null ? HubFieldState.Load(folder) : null;

                FieldSnapshot snap = FieldSnapshot.Read(folder);
                if (!snap.Complete) return null;
                if (job == null && field == null)
                {
                    if (!allowNew || !snap.HasContent) return null;
                    field = NewField(folder);
                }

                State state = LoadOrStart(folder, snap);
                Record(state, snap, null, null);

                var changed = new List<string>();
                foreach (KeyValuePair<string, string> item in snap.Fingerprints)
                {
                    string had;
                    if (!state.Base.TryGetValue(item.Key, out had) || had != item.Value) changed.Add(item.Key);
                }
                // A removed boundary isn't sent: the hub keeps it, and it comes back.
                List<string> removed = state.Base.Keys
                    .Where(k => !snap.Fingerprints.ContainsKey(k) && k != FieldSnapshot.BoundaryKey).ToList();
                // A new boundary from a job brings its headland along to the master.
                if (job != null && changed.Contains(FieldSnapshot.BoundaryKey)
                    && snap.Fingerprints.ContainsKey(FieldSnapshot.HeadlandKey) && !changed.Contains(FieldSnapshot.HeadlandKey))
                {
                    changed.Add(FieldSnapshot.HeadlandKey);
                }
                bool renamed = field != null && !string.IsNullOrEmpty(field.Name) && field.Name != folderName;

                if (changed.Count == 0 && removed.Count == 0 && !renamed)
                {
                    state.Files = FileStamps(folder);
                    Save(folder, state);
                    return null;
                }

                // Where the edits go on the hub.
                string fieldId;
                string fieldName;
                if (job != null)
                {
                    HubFieldState masterState = master != null ? HubFieldState.Load(master) : null;
                    if (masterState == null && master != null && allowNew)
                    {
                        // The master isn't on the hub yet: send it first, so the job's edits have a field to go to.
                        bool unused;
                        SendChanges(hub, master, true, openFolder, out unused);
                        masterState = HubFieldState.Load(master);
                    }
                    fieldId = masterState != null && !string.IsNullOrEmpty(masterState.FieldId) ? masterState.FieldId : job.FieldId;
                    fieldName = masterState != null && !string.IsNullOrEmpty(masterState.Name) ? masterState.Name
                        : master != null ? Path.GetFileName(master) : job.FieldName;
                }
                else
                {
                    fieldId = field.FieldId;
                    fieldName = renamed || string.IsNullOrEmpty(field.Name) ? folderName : field.Name;
                }

                string waiting = null;
                if (!string.IsNullOrEmpty(fieldId))
                {
                    var plane = new LocalPlane(FieldPlaneFiles.LoadOrigin(folder), new SharedFieldProperties());
                    JObject body = Body(snap, state, plane, fieldId, fieldName, renamed, changed, removed);
                    waiting = hub.Enqueue("POST", "/fields", body, "field edits " + folderName);
                    if (string.IsNullOrEmpty(waiting)) return null; // couldn't be queued: try again later
                }

                foreach (string key in changed.Concat(removed))
                {
                    state.EditedAt.Remove(key);
                    state.Tool.Remove(key);
                    state.Width.Remove(key);
                }
                state.Base = new Dictionary<string, string>(snap.Fingerprints, StringComparer.Ordinal);
                state.Seen = new Dictionary<string, string>(snap.Fingerprints, StringComparer.Ordinal);
                state.Files = FileStamps(folder);
                if (waiting != null) state.Waiting = waiting;
                Save(folder, state);

                if (field != null)
                {
                    field.HubHash = ""; // the hub's copy, with these edits, comes back at the next sync
                    if (renamed) field.Name = folderName;
                    field.Save(folder);
                }
                string note = Describe(folderName, changed, removed, renamed, fieldId != null);
                Log.EventWriter("[AgOpen] " + note);
                if (job != null && master != null) ApplyToMaster(snap, master, changed, removed, waiting);
                return note;
            }
        }

        /// <summary>
        /// At a sync, before anything comes down: send the edits in every field the
        /// hub knows, and every job folder (not the open one). Fields not on the hub
        /// are sent when they're used, not here.
        /// </summary>
        public static int SendAll(HubConnection hub, string fieldsDirectory, string openFolder, HubSyncResult result)
        {
            int sent = 0;
            if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory)) return 0;
            foreach (string folder in Directory.GetDirectories(fieldsDirectory))
            {
                try
                {
                    if (!NeedsLook(folder)) continue;
                    bool deferred;
                    string note = SendChanges(hub, folder, false, openFolder, out deferred);
                    if (note != null)
                    {
                        sent++;
                        result.Notes.Add(note);
                    }
                }
                catch (Exception ex)
                {
                    result.Problems.Add("Edits in '" + Path.GetFileName(folder) + "': " + ex.Message);
                    Log.EventWriter("[AgOpen] Sending edits in '" + Path.GetFileName(folder) + "' failed: " + ex);
                }
            }
            return sent;
        }

        /// <summary>
        /// True if this folder holds edits the hub hasn't got yet (or can't be read),
        /// so it mustn't be overwritten from the hub. why: for the sync's notes.
        /// </summary>
        public static bool HasUnsent(HubConnection hub, string folder, out string why)
        {
            lock (Gate)
            {
                why = null;
                State state = Load(folder);
                if (state != null && hub != null && !string.IsNullOrEmpty(state.Waiting))
                {
                    if (hub.Outbox.IsWaiting(state.Waiting))
                    {
                        why = "has edits from this tablet still waiting to go to the hub";
                        return true;
                    }
                    if (hub.Outbox.IsSetAside(state.Waiting))
                    {
                        Log.EventWriter("[AgOpen] The hub refused edits from '" + Path.GetFileName(folder) + "' (kept in Outbox\\SetAside); the hub's copy replaces them.");
                    }
                }
                FieldSnapshot snap = FieldSnapshot.Read(folder);
                if (!snap.Complete)
                {
                    why = "couldn't be read";
                    return true;
                }
                state = LoadOrStart(folder, snap);
                foreach (KeyValuePair<string, string> item in snap.Fingerprints)
                {
                    string had;
                    if (!state.Base.TryGetValue(item.Key, out had) || had != item.Value)
                    {
                        why = "was changed on this tablet";
                        return true;
                    }
                }
                foreach (string key in state.Base.Keys)
                {
                    if (key != FieldSnapshot.BoundaryKey && !snap.Fingerprints.ContainsKey(key))
                    {
                        why = "was changed on this tablet";
                        return true;
                    }
                }
                return false;
            }
        }

        // Quick check: a folder whose files haven't been saved since last time is skipped.
        private static bool NeedsLook(string folder)
        {
            if (!File.Exists(Path.Combine(folder, "Field.txt"))) return false;
            bool job = File.Exists(Path.Combine(folder, AgOpenJob.FileName));
            HubFieldState field = job ? null : HubFieldState.Load(folder);
            if (!job && field == null) return false; // not on the hub: sent when it's next used
            if (field != null && !string.IsNullOrEmpty(field.Name) && field.Name != Path.GetFileName(folder)) return true; // renamed
            State state = Load(folder);
            return state == null || state.Base == null || !SameStamps(FileStamps(folder), state.Files);
        }

        private static bool IsOpen(string folderName, string openFolder)
        {
            return !string.IsNullOrEmpty(openFolder) && string.Equals(folderName, openFolder, StringComparison.OrdinalIgnoreCase);
        }

        // What the hub takes (POST /fields): only the items that changed.
        private static JObject Body(FieldSnapshot snap, State state, LocalPlane plane, string fieldId, string name, bool renamed,
            List<string> changed, List<string> removed)
        {
            var body = new JObject { ["id"] = fieldId, ["name"] = name };
            if (renamed) body["name_edited_at"] = AgOpenJobs.HubTime(DateTime.UtcNow);
            body["origin"] = new JArray(plane.Origin.Longitude, plane.Origin.Latitude);

            var lines = new JArray();
            var paths = new JArray();
            foreach (string key in changed)
            {
                string at = EditTime(state, snap, key);
                if (key == FieldSnapshot.BoundaryKey)
                {
                    JObject boundary = snap.BoundaryGeoJson(plane);
                    if (boundary == null) continue;
                    body["boundary"] = boundary;
                    body["boundary_source"] = "recorded";
                    body["boundary_edited_at"] = at;
                }
                else if (key == FieldSnapshot.HeadlandKey)
                {
                    body["headland"] = snap.HeadlandGeoJson(plane);
                    body["headland_edited_at"] = at;
                }
                else if (key == FieldSnapshot.FlagsKey)
                {
                    body["flags"] = snap.FlagsJson();
                    body["flags_edited_at"] = at;
                }
                else if (key == FieldSnapshot.TramKey)
                {
                    body["tramlines"] = snap.TramLinesGeoJson(plane);
                    body["tram_boundary"] = snap.TramBoundaryGeoJson(plane);
                    body["tramlines_edited_at"] = at;
                    body["tram_boundary_edited_at"] = at;
                }
                else if (key.StartsWith(FieldSnapshot.LinePrefix, StringComparison.Ordinal))
                {
                    JObject line = snap.LineJson(key.Substring(FieldSnapshot.LinePrefix.Length), plane);
                    if (line == null) continue;
                    AddTool(line, state, key, true);
                    line["edited_at"] = at;
                    lines.Add(line);
                }
                else if (key.StartsWith(FieldSnapshot.PathPrefix, StringComparison.Ordinal))
                {
                    JObject path = snap.PathJson(key.Substring(FieldSnapshot.PathPrefix.Length), plane);
                    if (path == null) continue;
                    AddTool(path, state, key, false);
                    path["edited_at"] = at;
                    paths.Add(path);
                }
            }
            foreach (string key in removed)
            {
                string at = EditTime(state, snap, key);
                var none = new JObject { ["type"] = "MultiLineString", ["coordinates"] = new JArray() };
                if (key.StartsWith(FieldSnapshot.LinePrefix, StringComparison.Ordinal))
                {
                    lines.Add(new JObject { ["name"] = key.Substring(FieldSnapshot.LinePrefix.Length), ["deleted"] = true, ["edited_at"] = at });
                }
                else if (key.StartsWith(FieldSnapshot.PathPrefix, StringComparison.Ordinal))
                {
                    paths.Add(new JObject { ["name"] = key.Substring(FieldSnapshot.PathPrefix.Length), ["deleted"] = true, ["edited_at"] = at });
                }
                else if (key == FieldSnapshot.HeadlandKey)
                {
                    body["headland"] = none;
                    body["headland_edited_at"] = at;
                }
                else if (key == FieldSnapshot.FlagsKey)
                {
                    body["flags"] = new JArray();
                    body["flags_edited_at"] = at;
                }
                else if (key == FieldSnapshot.TramKey)
                {
                    body["tramlines"] = none;
                    body["tram_boundary"] = none.DeepClone();
                    body["tramlines_edited_at"] = at;
                    body["tram_boundary_edited_at"] = at;
                }
            }
            if (lines.Count > 0) body["lines"] = lines;
            if (paths.Count > 0) body["paths"] = paths;
            return body;
        }

        private static void AddTool(JObject item, State state, string key, bool withWidth)
        {
            string tool;
            if (state.Tool.TryGetValue(key, out tool) && !string.IsNullOrEmpty(tool)) item["tool"] = tool;
            double width;
            if (withWidth && state.Width.TryGetValue(key, out width)) item["width_m"] = width;
        }

        // When an item was edited: as noticed while the field was open, otherwise when its file was saved.
        private static string EditTime(State state, FieldSnapshot snap, string key)
        {
            string at;
            if (state.EditedAt.TryGetValue(key, out at) && !string.IsNullOrEmpty(at)) return at;
            DateTime saved;
            if (snap.SavedAt.TryGetValue(key, out saved)) return AgOpenJobs.HubTime(saved);
            return AgOpenJobs.HubTime(RemovedAt(snap.Folder, key));
        }

        private static string Describe(string folder, List<string> changed, List<string> removed, bool renamed, bool toHub)
        {
            int lines = changed.Concat(removed).Count(k => k.StartsWith(FieldSnapshot.LinePrefix, StringComparison.Ordinal));
            int paths = changed.Concat(removed).Count(k => k.StartsWith(FieldSnapshot.PathPrefix, StringComparison.Ordinal));
            var parts = new List<string>();
            if (renamed) parts.Add("new name");
            if (changed.Contains(FieldSnapshot.BoundaryKey)) parts.Add("boundary");
            if (changed.Contains(FieldSnapshot.HeadlandKey) || removed.Contains(FieldSnapshot.HeadlandKey)) parts.Add("headland");
            if (lines > 0) parts.Add(lines + (lines == 1 ? " line" : " lines"));
            if (paths > 0) parts.Add(paths + (paths == 1 ? " recorded path" : " recorded paths"));
            if (changed.Contains(FieldSnapshot.FlagsKey) || removed.Contains(FieldSnapshot.FlagsKey)) parts.Add("flags");
            if (changed.Contains(FieldSnapshot.TramKey) || removed.Contains(FieldSnapshot.TramKey)) parts.Add("tramlines");
            return "'" + folder + "': " + string.Join(", ", parts) + (toHub ? " sent to the hub" : " noted (no field on the hub)");
        }

        #endregion

        #region Fields new to the hub, and jobs' masters

        // A field folder the hub hasn't heard of: give it an id of its own. If the
        // hub finds it's a field it already has, it remembers this id as that field.
        private static HubFieldState NewField(string folder)
        {
            Wgs84 origin = FieldPlaneFiles.LoadOrigin(folder);
            var state = new HubFieldState
            {
                FieldId = Guid.NewGuid().ToString("N"),
                HubHash = "",
                Origin = new[] { origin.Latitude, origin.Longitude },
                SyncedAt = DateTime.UtcNow.ToString("o"),
                Name = Path.GetFileName(folder)
            };
            state.Save(folder);
            Log.EventWriter("[AgOpen] '" + Path.GetFileName(folder) + "' isn't on the hub yet: sending it");
            return state;
        }

        // The master field a job folder was made from (found by its field id if it was renamed).
        private static string MasterFolder(string jobFolder, AgOpenJob job)
        {
            string fields = Path.GetDirectoryName(jobFolder);
            if (!string.IsNullOrEmpty(job.MasterFolder))
            {
                string named = Path.Combine(fields, job.MasterFolder);
                if (Directory.Exists(named) && !File.Exists(Path.Combine(named, AgOpenJob.FileName))) return named;
            }
            if (!string.IsNullOrEmpty(job.FieldId))
            {
                foreach (string folder in Directory.GetDirectories(fields))
                {
                    HubFieldState state = HubFieldState.Load(folder);
                    if (state != null && state.FieldId == job.FieldId) return folder;
                }
            }
            return null;
        }

        // A job's edits go into its master field on this tablet straight away, and
        // count as sent from there too (they went up with the job's edits).
        private static void ApplyToMaster(FieldSnapshot job, string master, List<string> changed, List<string> removed, string waiting)
        {
            try
            {
                FieldSnapshot before = FieldSnapshot.Read(master);
                if (!before.Complete)
                {
                    Log.EventWriter("[AgOpen] '" + Path.GetFileName(master) + "' couldn't be read, so the job's edits weren't copied into it");
                    return;
                }
                State state = LoadOrStart(master, before);
                Record(state, before, null, null); // the master's own edits stay its own

                List<string> keys = changed.Concat(removed).ToList();
                job.CopyItemsTo(master, keys);
                FieldSnapshot after = FieldSnapshot.Read(master);
                foreach (string key in keys)
                {
                    string fingerprint;
                    if (after.Fingerprints.TryGetValue(key, out fingerprint))
                    {
                        state.Base[key] = fingerprint;
                        state.Seen[key] = fingerprint;
                    }
                    else
                    {
                        state.Base.Remove(key);
                        state.Seen.Remove(key);
                    }
                    state.EditedAt.Remove(key);
                    state.Tool.Remove(key);
                    state.Width.Remove(key);
                }
                state.Files = FileStamps(master);
                if (!string.IsNullOrEmpty(waiting)) state.Waiting = waiting;
                Save(master, state);

                HubFieldState field = HubFieldState.Load(master);
                if (field != null && !string.IsNullOrEmpty(waiting))
                {
                    field.HubHash = "";
                    field.Save(master);
                }
                Log.EventWriter("[AgOpen] Copied the job's edits from '" + Path.GetFileName(job.Folder) + "' into '" + Path.GetFileName(master) + "'");
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Copying the job's edits into '" + Path.GetFileName(master) + "' failed: " + ex);
            }
        }

        #endregion

        #region Saved state

        // The state for a folder. For one made before edits were tracked, what the
        // hub has is worked out from what's there.
        private static State LoadOrStart(string folder, FieldSnapshot snap)
        {
            State state = Load(folder);
            if (state != null && state.Base != null) return state;
            state = state ?? new State();
            state.Base = new Dictionary<string, string>(StringComparer.Ordinal);

            HubFieldState hub = HubFieldState.Load(folder);
            AgOpenJob job = hub == null ? AgOpenJobs.Read(folder) : null;
            if (hub != null)
            {
                // Written from the hub: items in files unchanged since then are what the
                // hub has. Items in changed files count as new (none can count as removed).
                var unchanged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> item in snap.Fingerprints)
                {
                    string file = FieldSnapshot.FileOf(item.Key);
                    if (file == null) continue;
                    bool same;
                    if (!unchanged.TryGetValue(file, out same))
                    {
                        string written;
                        string path = Path.Combine(folder, file);
                        same = hub.Files != null && hub.Files.TryGetValue(file, out written)
                            && File.Exists(path) && HubFieldWriter.FileHash(path) == written;
                        unchanged[file] = same;
                    }
                    if (same) state.Base[item.Key] = item.Value;
                }
            }
            else if (job != null)
            {
                string master = MasterFolder(folder, job);
                if (master == null)
                {
                    // Nothing to compare with: count the job folder as unchanged.
                    foreach (KeyValuePair<string, string> item in snap.Fingerprints) state.Base[item.Key] = item.Value;
                }
                else
                {
                    // A job made before edits were tracked: compare with its master field.
                    // Only items the job has count, so lines added to the master since
                    // aren't taken as removed.
                    FieldSnapshot masterSnap = FieldSnapshot.Read(master);
                    foreach (KeyValuePair<string, string> item in snap.Fingerprints)
                    {
                        string theirs;
                        if (masterSnap.Fingerprints.TryGetValue(item.Key, out theirs)) state.Base[item.Key] = theirs;
                    }
                }
            }
            // Otherwise: a field not on the hub, where everything is new.
            state.Seen = new Dictionary<string, string>(state.Base, StringComparer.Ordinal);
            return state;
        }

        private static State Load(string folder)
        {
            string path = Path.Combine(folder, FileName);
            try
            {
                if (File.Exists(path)) return JsonConvert.DeserializeObject<State>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable " + path + ": " + ex.Message);
            }
            return null;
        }

        private static void Save(string folder, State state)
        {
            HubFiles.WriteAllTextSafely(Path.Combine(folder, FileName), JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        // The save times of the files that hold a field's items.
        private static Dictionary<string, long> FileStamps(string folder)
        {
            var stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in FieldSnapshot.ItemFiles)
            {
                string path = Path.Combine(folder, file);
                if (File.Exists(path)) stamps[file] = File.GetLastWriteTimeUtc(path).Ticks;
            }
            foreach (string path in Directory.GetFiles(folder, "*.rec"))
            {
                stamps[Path.GetFileName(path)] = File.GetLastWriteTimeUtc(path).Ticks;
            }
            return stamps;
        }

        private static bool SameStamps(Dictionary<string, long> now, Dictionary<string, long> before)
        {
            if (now == null || before == null || now.Count != before.Count) return false;
            foreach (KeyValuePair<string, long> pair in now)
            {
                long other;
                if (!before.TryGetValue(pair.Key, out other) || other != pair.Value) return false;
            }
            return true;
        }

        #endregion
    }
}
