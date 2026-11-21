using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml;
using AgLibrary.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Keeps tractor (vehicle) and implement (tool) profiles the same on every
    /// tablet. A profile changed on this tablet goes to the hub; one changed
    /// elsewhere comes down. If both changed, the newest edit wins.
    ///
    /// AgOpenGPS re-saves profiles when it closes even if nothing changed, so a
    /// profile only counts as changed here when its settings differ from what was
    /// last synced, not just because the file was saved again.
    /// </summary>
    public static class HubProfileSync
    {
        internal class ProfileState
        {
            /// <summary>Fingerprint of the settings as last synced.</summary>
            [JsonProperty("settings_hash")]
            public string SettingsHash { get; set; }

            /// <summary>The hub's edit stamp as last synced.</summary>
            [JsonProperty("hub_stamp")]
            public string HubStamp { get; set; }
        }

        private static string StatePath
        {
            get { return Path.Combine(HubSettings.Folder, "profiles.json"); }
        }

        public static void Run(HubSettings settings, string key, HubSyncContext context, HubSyncResult result)
        {
            HubResponse response = HubClient.Send(settings.ServerUrl, key, HttpMethod.Get, "/profiles", null, 30);
            if (!response.Ok)
            {
                result.Problems.Add("Profiles: " + response.Error);
                return;
            }
            JObject hub = response.Json() as JObject;
            if (hub == null)
            {
                result.Problems.Add("Profiles: the hub's answer couldn't be read.");
                return;
            }

            // Keep the hub's hints for the job prompt (what each implement is and last did).
            AgOpenJobs.SaveHubHints(hub);

            Dictionary<string, ProfileState> state = LoadState();
            JArray deleted = hub["deleted"] as JArray ?? new JArray();
            SyncKind(settings, key, "vehicle", "/vehicles/", context.VehiclesDirectory, hub["vehicles"] as JArray,
                deleted, context.LoadedVehicle, context.FieldOpen, state, result);
            SyncKind(settings, key, "tool", "/tools/", context.ToolsDirectory, hub["tools"] as JArray,
                deleted, context.LoadedTool, context.FieldOpen, state, result);
            SaveState(state);
        }

        private static void SyncKind(HubSettings settings, string key, string kind, string apiPath, string folder,
            JArray hubProfiles, JArray deleted, string loadedName, bool fieldOpen,
            Dictionary<string, ProfileState> state, HubSyncResult result)
        {
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);

            var onHub = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken item in hubProfiles ?? new JArray())
            {
                JObject profile = item as JObject;
                string name = profile != null ? (string)profile["name"] : null;
                if (!string.IsNullOrEmpty(name)) onHub[name] = profile;
            }
            var removedOnHub = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken item in deleted)
            {
                JObject profile = item as JObject;
                if (profile == null || (string)profile["kind"] != kind) continue;
                string name = (string)profile["name"];
                if (!string.IsNullOrEmpty(name)) removedOnHub[name] = profile;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder, "*.xml"))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
            names.UnionWith(onHub.Keys);
            names.UnionWith(removedOnHub.Keys);
            foreach (string stateKey in state.Keys)
            {
                if (stateKey.StartsWith(kind + ":", StringComparison.Ordinal)) names.Add(stateKey.Substring(kind.Length + 1));
            }

            foreach (string name in names)
            {
                if (HubFieldWriter.SafeName(name) != name) continue; // not usable as a file name here
                try
                {
                    SyncOne(settings, key, kind, apiPath, folder, name, onHub, removedOnHub, loadedName, fieldOpen, state, result);
                }
                catch (Exception ex)
                {
                    result.Problems.Add(kind + " profile '" + name + "': " + ex.Message);
                    Log.EventWriter("[AgOpen] Profile sync, " + kind + " '" + name + "': " + ex);
                }
            }
        }

        private static void SyncOne(HubSettings settings, string key, string kind, string apiPath, string folder, string name,
            Dictionary<string, JObject> onHub, Dictionary<string, JObject> removedOnHub, string loadedName, bool fieldOpen,
            Dictionary<string, ProfileState> state, HubSyncResult result)
        {
            string stateKey = kind + ":" + name;
            string file = Path.Combine(folder, name + ".xml");
            bool hasLocal = File.Exists(file);
            string localText = hasLocal ? File.ReadAllText(file) : null;
            string localHash = hasLocal ? SettingsHash(localText) : null;
            DateTime localTime = hasLocal ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            bool isLoaded = string.Equals(name, loadedName, StringComparison.OrdinalIgnoreCase);

            ProfileState known;
            state.TryGetValue(stateKey, out known);

            JObject hubProfile;
            onHub.TryGetValue(name, out hubProfile);
            JObject hubRemoved;
            removedOnHub.TryGetValue(name, out hubRemoved);
            string hubXml = hubProfile != null ? (string)hubProfile["profile_xml"] : null;
            string hubEdited = hubProfile != null ? (string)hubProfile["edited_at"] : null;
            string hubStamp = null;
            if (hubXml != null) hubStamp = hubEdited ?? "";
            else if (hubRemoved != null) hubStamp = "removed " + (string)hubRemoved["edited_at"];

            bool localChanged = hasLocal ? (known == null || known.SettingsHash != localHash) : (known != null && known.SettingsHash != null);
            bool hubChanged = hubStamp != null && (known == null || known.HubStamp != hubStamp);
            if (!localChanged && !hubChanged) return;

            bool keepLocal = localChanged;
            if (localChanged && hubChanged)
            {
                // Both changed since the last sync: the newest edit wins.
                DateTime hubTime = ParseTime(hubRemoved != null && hubXml == null ? (string)hubRemoved["edited_at"] : hubEdited);
                keepLocal = hasLocal ? localTime > hubTime : DateTime.UtcNow > hubTime;
            }

            if (keepLocal)
            {
                if (hasLocal)
                {
                    string editedAt = HubTime(localTime);
                    var body = new JObject();
                    body["profile_xml"] = localText;
                    body["edited_at"] = editedAt;
                    if (kind == "tool")
                    {
                        double? width = SettingNumber(localText, "setVehicle_toolWidth");
                        double? sections = SettingNumber(localText, "setVehicle_numSections");
                        if (width.HasValue) body["width_m"] = width.Value;
                        if (sections.HasValue) body["sections"] = (int)sections.Value;
                    }
                    HubResponse sent = HubClient.Send(settings.ServerUrl, key, HttpMethod.Put, apiPath + Uri.EscapeDataString(name), body.ToString(Newtonsoft.Json.Formatting.None), 30);
                    if (!sent.Ok)
                    {
                        result.Problems.Add(kind + " profile '" + name + "': " + sent.Error);
                        return;
                    }
                    JObject answer = sent.Json() as JObject;
                    bool keptNewer = answer != null && answer["kept_newer"] != null && (bool)answer["kept_newer"];
                    // If the hub kept a newer one, record nothing: the next sync brings it down.
                    if (!keptNewer)
                    {
                        state[stateKey] = new ProfileState { SettingsHash = localHash, HubStamp = editedAt };
                        result.ProfilesSent++;
                    }
                }
                else
                {
                    // Removed on this tablet since the last sync: remove it everywhere.
                    var body = new JObject();
                    body["deleted"] = true;
                    body["edited_at"] = HubTime(DateTime.UtcNow);
                    HubResponse sent = HubClient.Send(settings.ServerUrl, key, HttpMethod.Put, apiPath + Uri.EscapeDataString(name), body.ToString(Newtonsoft.Json.Formatting.None), 30);
                    if (!sent.Ok)
                    {
                        result.Problems.Add(kind + " profile '" + name + "': " + sent.Error);
                        return;
                    }
                    state.Remove(stateKey);
                    result.ProfilesSent++;
                }
                return;
            }

            // The hub's version wins.
            if (isLoaded && fieldOpen)
            {
                // Don't swap the profile in use while a field is open; next time.
                result.Notes.Add("The " + kind + " profile in use ('" + name + "') changed on the hub; it updates next time no field is open.");
                return;
            }

            if (hubXml != null)
            {
                HubFiles.WriteAllTextSafely(file, hubXml);
                state[stateKey] = new ProfileState { SettingsHash = SettingsHash(hubXml), HubStamp = hubStamp };
                result.ProfilesReceived++;
                if (isLoaded)
                {
                    if (kind == "vehicle") result.LoadedVehicleChanged = true;
                    else result.LoadedToolChanged = true;
                }
            }
            else if (hubRemoved != null)
            {
                if (isLoaded)
                {
                    result.Notes.Add("The " + kind + " profile in use ('" + name + "') was removed on another tablet; it's kept here while in use.");
                    return;
                }
                if (hasLocal) File.Delete(file);
                string backup = Path.ChangeExtension(file, ".last");
                if (File.Exists(backup)) File.Delete(backup);
                state[stateKey] = new ProfileState { SettingsHash = null, HubStamp = hubStamp };
                result.ProfilesRemoved++;
            }
        }

        /// <summary>
        /// A fingerprint of a profile's settings (names and values), ignoring how
        /// the file happens to be laid out.
        /// </summary>
        public static string SettingsHash(string xml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var sb = new StringBuilder();
                foreach (XmlNode setting in doc.GetElementsByTagName("setting"))
                {
                    XmlAttribute nameAttribute = setting.Attributes != null ? setting.Attributes["name"] : null;
                    XmlNode value = setting["value"];
                    sb.Append(nameAttribute != null ? nameAttribute.Value : "");
                    sb.Append('=');
                    sb.Append(value != null ? value.InnerXml.Trim() : setting.InnerXml.Trim());
                    sb.Append('\n');
                }
                return HubFieldWriter.TextHash(sb.ToString());
            }
            catch (XmlException)
            {
                return HubFieldWriter.TextHash((xml ?? "").Replace("\r\n", "\n").Trim());
            }
        }

        private static double? SettingNumber(string xml, string settingName)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                foreach (XmlNode setting in doc.GetElementsByTagName("setting"))
                {
                    XmlAttribute nameAttribute = setting.Attributes != null ? setting.Attributes["name"] : null;
                    if (nameAttribute == null || nameAttribute.Value != settingName) continue;
                    XmlNode value = setting["value"];
                    double number;
                    if (value != null && double.TryParse(value.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
                }
            }
            catch (XmlException)
            {
                // not readable: leave it out
            }
            return null;
        }

        // The same text the hub uses for edit times, so the stamps compare equal.
        private static string HubTime(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";
        }

        private static DateTime ParseTime(string text)
        {
            DateTimeOffset time;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) return time.UtcDateTime;
            return DateTime.MinValue;
        }

        private static Dictionary<string, ProfileState> LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var state = JsonConvert.DeserializeObject<Dictionary<string, ProfileState>>(File.ReadAllText(StatePath));
                    if (state != null) return new Dictionary<string, ProfileState>(state, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] profiles.json can't be read: " + ex.Message);
            }
            return new Dictionary<string, ProfileState>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveState(Dictionary<string, ProfileState> state)
        {
            Directory.CreateDirectory(HubSettings.Folder);
            HubFiles.WriteAllTextSafely(StatePath, JsonConvert.SerializeObject(state, Newtonsoft.Json.Formatting.Indented));
        }
    }
}
