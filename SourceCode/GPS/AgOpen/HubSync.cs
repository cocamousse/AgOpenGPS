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

        public bool LoadedVehicleChanged;
        public bool LoadedToolChanged;

        public List<string> Notes = new List<string>();
        public List<string> Problems = new List<string>();

        public bool ChangedAnything
        {
            get { return FieldsWritten > 0 || ProfilesReceived > 0 || ProfilesSent > 0 || ProfilesRemoved > 0; }
        }

        public string Summary()
        {
            if (!Completed)
            {
                return Problems.Count > 0 ? "Sync didn't finish: " + Problems[0] : "Sync didn't finish.";
            }
            string text = FieldsOnHub.ToString(CultureInfo.InvariantCulture) + " fields";
            if (FieldsWritten > 0) text += " (" + FieldsWritten.ToString(CultureInfo.InvariantCulture) + " updated)";
            int profiles = ProfilesReceived + ProfilesSent + ProfilesRemoved;
            if (profiles > 0) text += ", " + profiles.ToString(CultureInfo.InvariantCulture) + " profile changes";
            if (FieldsSkipped > 0) text += ", " + FieldsSkipped.ToString(CultureInfo.InvariantCulture) + " fields skipped";
            if (Problems.Count > 0) text += ". Problem: " + Problems[0];
            return text;
        }
    }

    /// <summary>
    /// Brings this tablet up to date with the hub: first sends anything waiting
    /// in the outbox, then syncs tractor and implement profiles, then writes
    /// every field from the hub into the AgOpenGPS Fields folder.
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
            SyncFields(settings, key, context, result);
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

        private static void SyncFields(HubSettings settings, string key, HubSyncContext context, HubSyncResult result)
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

            foreach (JToken item in fields)
            {
                JObject field = item as JObject;
                if (field == null) continue;
                string id = (string)field["id"] ?? "";
                string name = (string)field["name"] ?? "";
                if (id.Length == 0) continue;
                try
                {
                    SyncField(field, id, name, folderOf, context, tempRoot, result);
                }
                catch (Exception ex)
                {
                    result.FieldsSkipped++;
                    result.Problems.Add("Field '" + name + "': " + ex.Message);
                    Log.EventWriter("[AgOpen] Writing field '" + name + "' failed: " + ex);
                }
            }
        }

        private static void SyncField(JObject field, string id, string name, Dictionary<string, string> folderOf,
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
                if (previous != null && previous.HasLocalChanges(folder))
                {
                    // Edited on this tablet and not yet sent to the hub: don't overwrite.
                    result.FieldsSkipped++;
                    result.Notes.Add("'" + Path.GetFileName(folder) + "' was changed on this tablet; it isn't overwritten from the hub.");
                    return;
                }
                // Renamed on the hub: rename the folder too, if the new name is free.
                if (!string.Equals(folder, wanted, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(wanted))
                {
                    Directory.Move(folder, wanted);
                    folder = wanted;
                    folderOf[id] = folder;
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
