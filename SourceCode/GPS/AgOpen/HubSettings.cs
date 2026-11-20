using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgLibrary.Logging;
using Newtonsoft.Json;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// How this tablet reaches the AgOpen hub: the hub's address, and the key the
    /// hub gave this tablet when it was paired.
    /// Kept in AgOpenHub\hub.json inside the AgOpenGPS folder. The key is
    /// encrypted for this Windows user (DPAPI), so a copy of the file on another
    /// computer is useless; a lost key is simply replaced by pairing again.
    /// </summary>
    public class HubSettings
    {
        private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("AgOpen hub tablet key");

        [JsonProperty("server_url")]
        public string ServerUrl { get; set; } = "";

        [JsonProperty("tablet_id")]
        public string TabletId { get; set; } = "";

        [JsonProperty("tablet_label")]
        public string TabletLabel { get; set; } = "";

        [JsonProperty("paired_at")]
        public string PairedAt { get; set; } = "";

        [JsonProperty("key_protected")]
        public string KeyProtected { get; set; } = "";

        [JsonIgnore]
        public bool IsPaired
        {
            get { return !string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(KeyProtected); }
        }

        /// <summary>The folder for everything the hub connection keeps on this tablet.</summary>
        public static string Folder
        {
            get { return Path.Combine(RegistrySettings.baseDirectory, "AgOpenHub"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(Folder, "hub.json"); }
        }

        /// <summary>The tablet key, or "" if there is none or it can't be read on this computer.</summary>
        public string GetKey()
        {
            if (string.IsNullOrEmpty(KeyProtected)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), KeyEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Tablet key can't be read: " + ex.Message);
                return "";
            }
        }

        public void SetKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                KeyProtected = "";
                return;
            }
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.CurrentUser);
            KeyProtected = Convert.ToBase64String(data);
        }

        public static HubSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    HubSettings settings = JsonConvert.DeserializeObject<HubSettings>(File.ReadAllText(FilePath));
                    if (settings != null) return settings;
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] hub.json can't be read, starting unpaired: " + ex.Message);
            }
            return new HubSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            HubFiles.WriteAllTextSafely(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    /// <summary>File helpers for the hub connection.</summary>
    public static class HubFiles
    {
        /// <summary>
        /// Write a file so it is either the old version or the new one, never half
        /// written, even if the tablet loses power in the middle.
        /// </summary>
        public static void WriteAllTextSafely(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
