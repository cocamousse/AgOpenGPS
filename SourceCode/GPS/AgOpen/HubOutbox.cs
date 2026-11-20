using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AgLibrary.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>One request waiting to go to the hub.</summary>
    public class HubOutboxItem
    {
        [JsonProperty("method")]
        public string Method { get; set; } = "POST";

        /// <summary>The part after /api/v1, e.g. "/jobs/1234/coverage".</summary>
        [JsonProperty("path")]
        public string Path { get; set; } = "";

        [JsonProperty("body")]
        public JToken Body { get; set; }

        /// <summary>When the request was made on this tablet (UTC).</summary>
        [JsonProperty("created_at")]
        public string CreatedAt { get; set; } = "";

        /// <summary>A few words for the status screen, e.g. "coverage, North 80".</summary>
        [JsonProperty("what")]
        public string What { get; set; } = "";
    }

    /// <summary>
    /// Requests waiting to go to the hub, kept as files so nothing is lost when the
    /// tablet is out of WiFi range or switched off. They are sent oldest first.
    /// The hub is built so that sending the same request twice does no harm, so
    /// a request whose answer got lost is simply sent again.
    /// </summary>
    public class HubOutbox
    {
        private static long sequence;
        private readonly string folder;
        private readonly string setAsideFolder;

        public HubOutbox(string folder)
        {
            this.folder = folder;
            setAsideFolder = System.IO.Path.Combine(folder, "SetAside");
            Directory.CreateDirectory(folder);
        }

        /// <summary>Queue a request. body is turned into JSON (null for none).</summary>
        public void Add(string method, string path, object body, string what)
        {
            var item = new HubOutboxItem
            {
                Method = method,
                Path = path,
                Body = body == null ? null : (body as JToken ?? JToken.FromObject(body)),
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                What = what ?? ""
            };
            // Names sort in the order requests were made.
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Interlocked.Increment(ref sequence).ToString("D6", CultureInfo.InvariantCulture) + ".json";
            HubFiles.WriteAllTextSafely(System.IO.Path.Combine(folder, name), JsonConvert.SerializeObject(item));
        }

        /// <summary>The waiting requests' files, oldest first.</summary>
        public string[] Pending()
        {
            try
            {
                return Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Can't list the outbox: " + ex.Message);
                return new string[0];
            }
        }

        public int Count
        {
            get { return Pending().Length; }
        }

        /// <summary>How many requests the hub refused and were set aside.</summary>
        public int SetAsideCount
        {
            get
            {
                return Directory.Exists(setAsideFolder) ? Directory.GetFiles(setAsideFolder, "*.json").Length : 0;
            }
        }

        public HubOutboxItem Read(string file)
        {
            try
            {
                return JsonConvert.DeserializeObject<HubOutboxItem>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable outbox file " + System.IO.Path.GetFileName(file) + ": " + ex.Message);
                return null;
            }
        }

        public void Remove(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Can't remove sent outbox file: " + ex.Message);
            }
        }

        /// <summary>
        /// The hub refused this request (for example, bad data). Keep it, with the
        /// reason, in the SetAside folder so the requests behind it can still go.
        /// </summary>
        public void SetAside(string file, string reason)
        {
            try
            {
                Directory.CreateDirectory(setAsideFolder);
                string target = System.IO.Path.Combine(setAsideFolder, System.IO.Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                File.Move(file, target);
                File.WriteAllText(System.IO.Path.ChangeExtension(target, ".reason.txt"), reason ?? "");
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Can't set aside outbox file: " + ex.Message);
                Remove(file);
            }
        }
    }
}
