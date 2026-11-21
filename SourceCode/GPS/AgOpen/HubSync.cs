using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using AgLibrary.Logging;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>What a sync needs to know about AgOpenGPS at the moment it starts.</summary>
    public class HubSyncContext
    {
        public string FieldsDirectory = "";
        public string VehiclesDirectory = "";
        public string ToolsDirectory = "";

        /// <summary>The field folder open in AgOpenGPS ("" if none): never written while open.</summary>
        public string OpenFieldFolder = "";
        public bool FieldOpen;

        public string LoadedVehicle = "";
        public string LoadedTool = "";
    }

    /// <summary>What a sync did, for the screen and the event log.</summary>
    public class HubSyncResult
    {
        public DateTime FinishedUtc;
        public bool Completed;

        public int FieldsOnHub;
        public int FieldsWritten;
        public int FieldsUnchanged;
        public int FieldsSkipped;

        public int ProfilesReceived;
        public int ProfilesSent;
        public int ProfilesRemoved;

        /// <summary>Fields whose edits on this tablet were sent to the hub.</summary>
        public int FieldEditsSent;

        public bool LoadedVehicleChanged;
        public bool LoadedToolChanged;

        public List<string> Notes = new List<string>();
        public List<string> Problems = new List<string>();

        /// <summary>Field folders renamed because the field was renamed on the hub: old name, new name.</summary>
        public List<KeyValuePair<string, string>> RenamedFolders = new List<KeyValuePair<string, string>>();

        public bool ChangedAnything
        {
            get { return FieldsWritten > 0 || ProfilesReceived > 0 || ProfilesSent > 0 || ProfilesRemoved > 0 || FieldEditsSent > 0; }
        }

        public string Summary()
        {
            if (!Completed)
            {
                return Problems.Count > 0 ? "Sync didn't finish: " + Problems[0] : "Sync didn't finish.";
            }
            string text = FieldsOnHub.ToString(CultureInfo.InvariantCulture) + " fields";
            if (FieldsWritten > 0) text += " (" + FieldsWritten.ToString(CultureInfo.InvariantCulture) + " updated)";
            if (FieldEditsSent > 0) text += ", edits sent from " + FieldEditsSent.ToString(CultureInfo.InvariantCulture) + (FieldEditsSent == 1 ? " field" : " fields");
            int profiles = ProfilesReceived + ProfilesSent + ProfilesRemoved;
            if (profiles > 0) text += ", " + profiles.ToString(CultureInfo.InvariantCulture) + " profile changes";
            if (FieldsSkipped > 0) text += ", " + FieldsSkipped.ToString(CultureInfo.InvariantCulture) + " fields skipped";
            if (Problems.Count > 0) text += ". Problem: " + Problems[0];
            return text;
        }
    }

    /// <summary>
    /// Brings this tablet up to date with the hub: first queues the field edits
    /// made on this tablet and sends everything waiting in the outbox, then syncs
    /// tractor and implement profiles, then writes every field from the hub into
    /// the AgOpenGPS Fields folder (never one holding edits the hub hasn't got).
    /// </summary>
    public static class HubSync
    {
        private const int OutboxWaitSeconds = 30;

        public static HubSyncResult Run(HubConnection hub, HubSyncContext context)
        {
            var result = new HubSyncResult();
            HubSettings settings = hub.Settings;
            string key = settings.GetKey();
            if (!settings.IsPaired || key.Length == 0)
            {
                result.Problems.Add("This tablet isn't paired with a hub.");
                return Finish(result);
            }

            // Changes made on this tablet go up first, so what comes down includes them.
            // (The open field's edits go when it closes.)
            result.FieldEditsSent = FieldEdits.SendAll(hub, context.FieldsDirectory, context.FieldOpen ? context.OpenFieldFolder : "", result);
            hub.Kick();
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(OutboxWaitSeconds);
            while (hub.Outbox.Count > 0 && DateTime.UtcNow < waitUntil && hub.State != HubState.Offline && hub.State != HubState.KeyRejected)
            {
                System.Threading.Thread.Sleep(500);
            }

            HubResponse health = HubClient.Send(settings.ServerUrl, key, HttpMethod.Get, "/me", null, 10);
            if (!health.Ok)
            {
                result.Problems.Add(health.Error);
                return Finish(result);
            }

            HubProfileSync.Run(settings, key, context, result);
            SyncFields(hub, settings, key, context, result);
            result.Completed = true;
            return Finish(result);
        }

        private static HubSyncResult Finish(HubSyncResult result)
        {
            result.FinishedUtc = DateTime.UtcNow;
            Log.EventWriter("[AgOpen] Hub sync: " + result.Summary());
            foreach (string note in result.Notes)
            {
                Log.EventWriter("[AgOpen]   " + note);
            }
            for (int i = 1; i < result.Problems.Count; i++)
            {
                Log.EventWriter("[AgOpen]   Problem: " + result.Problems[i]);
            }
            return result;
        }

        private static void SyncFields(HubConnection hub, HubSettings settings, string key, HubSyncContext context, HubSyncResult result)
        {
            HubResponse response = HubClient.Send(settings.ServerUrl, key, HttpMethod.Get, "/fields", null, 120);
            JObject json = response.Ok ? response.Json() as JObject : null;
            JArray fields = json != null ? json["fields"] as JArray : null;
            if (fields == null)
            {
                result.Problems.Add("Fields: " + (response.Ok ? "the hub's answer couldn't be read." : response.Error));
                return;
            }
            result.FieldsOnHub = fields.Count;

            if (string.IsNullOrEmpty(context.FieldsDirectory)) return;
            Directory.CreateDirectory(context.FieldsDirectory);
            string tempRoot = Path.Combine(HubSettings.Folder, "Temp");
            Directory.CreateDirectory(tempRoot);

            // Which folders already hold which hub field.
            var folderOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string folder in Directory.GetDirectories(context.FieldsDirectory))
            {
                HubFieldState existing = HubFieldState.Load(folder);
                if (existing != null && existing.FieldId.Length > 0 && !folderOf.ContainsKey(existing.FieldId))
                {
                    folderOf[existing.FieldId] = folder;
                }
            }

            // A field made on this tablet that the hub found it already had: its
            // folder takes the real id (or, if that field has a folder here already,
            // stays as it is; its lines and paths were added to that field).
            foreach (JToken item in fields)
            {
                JObject field = item as JObject;
                string id = field != null ? (string)field["id"] ?? "" : "";
                JArray merged = field != null ? field["merged_ids"] as JArray : null;
                if (id.Length == 0 || merged == null) continue;
                foreach (JToken oldId in merged)
                {
                    string old = (string)oldId ?? "";
                    string mergedFolder;
                    if (old.Length == 0 || !folderOf.TryGetValue(old, out mergedFolder)) continue;
                    folderOf.Remove(old);
                    if (folderOf.ContainsKey(id))
                    {
                        result.Notes.Add("'" + Path.GetFileName(mergedFolder) + "' is the same field as '" + Path.GetFileName(folderOf[id])
                            + "' on the hub; its lines and paths were added there. You can delete '" + Path.GetFileName(mergedFolder) + "'.");
                        continue;
                    }
                    HubFieldState state = HubFieldState.Load(mergedFolder);
                    if (state == null) continue;
                    state.FieldId = id;
                    state.HubHash = "";
                    state.Save(mergedFolder);
                    folderOf[id] = mergedFolder;
                    Log.EventWriter("[AgOpen] '" + Path.GetFileName(mergedFolder) + "' is a field the hub already had: now linked to it");
                }
            }

            foreach (JToken item in fields)
            {
                JObject field = item as JObject;
                if (field == null) continue;
                string id = (string)field["id"] ?? "";
                string name = (string)field["name"] ?? "";
                if (id.Length == 0) continue;
                try
                {
                    SyncField(hub, field, id, name, folderOf, context, tempRoot, result);
                }
                catch (Exception ex)
                {
                    result.FieldsSkipped++;
                    result.Problems.Add("Field '" + name + "': " + ex.Message);
                    Log.EventWriter("[AgOpen] Writing field '" + name + "' failed: " + ex);
                }
            }
        }

        private static void SyncField(HubConnection hub, JObject field, string id, string name, Dictionary<string, string> folderOf,
            HubSyncContext context, string tempRoot, HubSyncResult result)
        {
            string hubHash = HubFieldWriter.TextHash(field.ToString(Newtonsoft.Json.Formatting.None));
            string wanted = Path.Combine(context.FieldsDirectory, HubFieldWriter.SafeName(name));

            string folder;
            HubFieldState previous = null;
            if (folderOf.TryGetValue(id, out folder))
            {
                previous = HubFieldState.Load(folder);
                if (IsOpen(folder, context))
                {
                    result.FieldsSkipped++;
                    result.Notes.Add("'" + name + "' is open, so it updates next time.");
                    return;
                }
                string why;
                if (FieldEdits.HasUnsent(hub, folder, out why))
                {
                    // Holds edits the hub hasn't got yet: don't overwrite.
                    result.FieldsSkipped++;
                    result.Notes.Add("'" + Path.GetFileName(folder) + "' " + why + "; it isn't overwritten from the hub.");
                    return;
                }
                // Renamed on the hub: rename the folder too, if the new name is free.
                bool moved = false;
                if (!string.Equals(folder, wanted, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(wanted))
                {
                    Directory.Move(folder, wanted);
                    result.RenamedFolders.Add(new KeyValuePair<string, string>(Path.GetFileName(folder), Path.GetFileName(wanted)));
                    folder = wanted;
                    folderOf[id] = folder;
                    moved = true;
                }
                if (previous != null && (moved || string.IsNullOrEmpty(previous.Name)))
                {
                    // The folder's name as it matches the hub: renaming it on this tablet later is a rename to send.
                    previous.Name = Path.GetFileName(folder);
                    previous.Save(folder);
                }
                if (previous != null && previous.HubHash == hubHash)
                {
                    result.FieldsUnchanged++;
                    return;
                }
            }
            else
            {
                folder = wanted;
                if (Directory.Exists(folder))
                {
                    HubFieldState other = HubFieldState.Load(folder);
                    if (other == null)
                    {
                        // A folder of the same name that didn't come from the hub: leave it alone.
                        result.FieldsSkipped++;
                        result.Notes.Add("'" + name + "': a field folder with that name already exists here, so the hub's copy isn't written.");
                        return;
                    }
                    // Another hub field already has this name: keep both.
                    folder = wanted + " (" + id.Substring(0, Math.Min(4, id.Length)) + ")";
                }
                folderOf[id] = folder;
            }

            HubFieldState written = HubFieldWriter.Write(field, folder, previous, hubHash, tempRoot);
            if (written == null)
            {
                result.FieldsSkipped++;
                result.Notes.Add("'" + name + "' has no boundary or lines yet, so there's nothing to load.");
                return;
            }
            result.FieldsWritten++;
        }

        private static bool IsOpen(string folder, HubSyncContext context)
        {
            return context.FieldOpen && context.OpenFieldFolder.Length > 0
                && string.Equals(Path.GetFileName(folder), context.OpenFieldFolder, StringComparison.OrdinalIgnoreCase);
        }
    }
}
