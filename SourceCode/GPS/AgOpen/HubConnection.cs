using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgLibrary.Logging;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    public enum HubState
    {
        NotPaired,
        Connecting,
        Online,
        Offline,
        KeyRejected
    }

    /// <summary>
    /// This tablet's link to the AgOpen hub: its settings, the outbox, and a
    /// background sender that delivers the outbox whenever the hub is reachable.
    /// Out of WiFi range nothing is lost: requests wait in the outbox, and the
    /// sender keeps trying, waiting a little longer each time (up to 5 minutes).
    /// </summary>
    public class HubConnection
    {
        private const int IdleCheckSeconds = 30;
        private const int FirstRetrySeconds = 15;
        private const int LongestRetrySeconds = 300;

        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly object stateLock = new object();
        private Thread worker;
        private volatile bool stopping;
        private volatile HubSettings settings;

        private readonly object syncLock = new object();
        private volatile bool syncing;

        private HubState state = HubState.NotPaired;
        private string lastProblem = "";
        private DateTime lastContactUtc = DateTime.MinValue;
        private HubSyncResult lastSync;

        public HubConnection()
        {
            settings = HubSettings.Load();
            Outbox = new HubOutbox(Path.Combine(HubSettings.Folder, "Outbox"));
            state = settings.IsPaired ? HubState.Connecting : HubState.NotPaired;
        }

        public HubOutbox Outbox { get; private set; }

        public HubSettings Settings
        {
            get { return settings; }
        }

        public HubState State
        {
            get { lock (stateLock) return state; }
        }

        public string LastProblem
        {
            get { lock (stateLock) return lastProblem; }
        }

        public DateTime LastContactUtc
        {
            get { lock (stateLock) return lastContactUtc; }
        }

        /// <summary>The last finished sync (null if none yet).</summary>
        public HubSyncResult LastSync
        {
            get { lock (stateLock) return lastSync; }
        }

        public bool Syncing
        {
            get { return syncing; }
        }

        /// <summary>
        /// Bring this tablet up to date with the hub (see HubSync). Runs on the
        /// calling thread: call it from a background task. Returns null if a sync
        /// is already running.
        /// </summary>
        public HubSyncResult Sync(HubSyncContext context)
        {
            if (!Monitor.TryEnter(syncLock)) return null;
            try
            {
                syncing = true;
                HubSyncResult result = HubSync.Run(this, context);
                lock (stateLock) lastSync = result;
                return result;
            }
            finally
            {
                syncing = false;
                Monitor.Exit(syncLock);
            }
        }

        /// <summary>One line for the screen, e.g. "Connected to the hub".</summary>
        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case HubState.NotPaired:
                        return "Not paired with a hub yet";
                    case HubState.Connecting:
                        return "Connecting to the hub...";
                    case HubState.Online:
                        return "Connected to the hub";
                    case HubState.KeyRejected:
                        return "The hub no longer accepts this tablet: pair it again";
                    default:
                        return "Hub not reachable right now (data is kept and sent later)";
                }
            }
        }

        public void Start()
        {
            if (worker != null) return;
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "AgOpen hub sender";
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
        }

        /// <summary>Send now instead of waiting for the next try.</summary>
        public void Kick()
        {
            wake.Set();
        }

        /// <summary>Queue a request for the hub and start sending it. Returns its outbox file name ("" if it couldn't be queued).</summary>
        public string Enqueue(string method, string path, object body, string what)
        {
            try
            {
                string name = Outbox.Add(method, path, body, what);
                Kick();
                return name;
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Couldn't queue " + what + " for the hub: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// Pair this tablet with the hub, using a code from the server
        /// (aog-hub tablet pair "Tablet 1"). Returns "" on success, or what went wrong.
        /// </summary>
        public async Task<string> PairAsync(string serverText, string code)
        {
            string url = HubClient.NormalizeUrl(serverText);
            if (url.Length == 0) return "Enter the hub's address, for example 192.168.1.20:8642";
            code = (code ?? "").Trim();
            if (code.Length == 0) return "Enter the pairing code shown on the server.";

            HubResponse response = await HubClient.PairAsync(url, code);
            if (!response.Ok)
            {
                if (response.Status == 403) return "The hub didn't accept that code. Codes work once and expire after about 10 minutes: make a new one on the server.";
                return response.Error;
            }

            JObject json = response.Json() as JObject;
            string key = json != null ? (string)json["key"] : null;
            if (string.IsNullOrEmpty(key)) return "The hub's answer had no key in it.";

            var paired = new HubSettings
            {
                ServerUrl = url,
                TabletId = json["tablet_id"] != null ? (string)json["tablet_id"] : "",
                TabletLabel = json["label"] != null ? (string)json["label"] : "",
                PairedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            paired.SetKey(key);
            paired.Save();
            settings = paired;
            Log.EventWriter("[AgOpen] Paired with the hub at " + url + " as '" + paired.TabletLabel + "'");
            SetState(HubState.Connecting, "", false);
            Kick();
            return "";
        }

        /// <summary>Forget this tablet's key (the hub address is kept).</summary>
        public void Unpair()
        {
            var unpaired = new HubSettings { ServerUrl = settings.ServerUrl };
            unpaired.Save();
            settings = unpaired;
            SetState(HubState.NotPaired, "", false);
            Log.EventWriter("[AgOpen] Unpaired from the hub");
        }

        /// <summary>Check the hub can be reached (and, when paired, still accepts this tablet).</summary>
        public async Task<string> TestAsync(string serverText)
        {
            string url = HubClient.NormalizeUrl(serverText);
            if (url.Length == 0) return "Enter the hub's address, for example 192.168.1.20:8642";

            HubResponse health = await HubClient.HealthAsync(url);
            if (!health.Ok) return health.Error;

            HubSettings current = settings;
            if (!current.IsPaired || current.ServerUrl != url) return "The hub is there. Now pair this tablet with a code.";

            HubResponse me = await HubClient.MeAsync(url, current.GetKey());
            if (me.Ok) return "Connected. This tablet is '" + current.TabletLabel + "' on the hub.";
            if (me.Status == 401) return "The hub is there, but no longer accepts this tablet. Pair it again.";
            return me.Error;
        }

        private void SetState(HubState newState, string problem, bool contacted)
        {
            lock (stateLock)
            {
                state = newState;
                lastProblem = problem ?? "";
                if (contacted) lastContactUtc = DateTime.UtcNow;
            }
        }

        private void Run()
        {
            int retrySeconds = FirstRetrySeconds;
            while (!stopping)
            {
                bool allSent = false;
                try
                {
                    allSent = SendWaiting();
                }
                catch (Exception ex)
                {
                    Log.EventWriter("[AgOpen] Hub sender problem: " + ex);
                }

                int waitSeconds;
                if (allSent)
                {
                    retrySeconds = FirstRetrySeconds;
                    waitSeconds = IdleCheckSeconds;
                }
                else
                {
                    waitSeconds = retrySeconds;
                    retrySeconds = Math.Min(retrySeconds * 2, LongestRetrySeconds);
                }
                wake.WaitOne(TimeSpan.FromSeconds(waitSeconds));
            }
        }

        /// <summary>Send everything waiting, oldest first. True if nothing is left to retry.</summary>
        private bool SendWaiting()
        {
            HubSettings current = settings;
            if (!current.IsPaired)
            {
                SetState(HubState.NotPaired, "", false);
                return true;
            }
            string key = current.GetKey();
            if (key.Length == 0)
            {
                SetState(HubState.KeyRejected, "This tablet's key can't be read on this computer.", false);
                return true;
            }

            string[] files = Outbox.Pending();
            if (files.Length == 0)
            {
                // Nothing to send: just check that the hub is still there.
                HubResponse me = HubClient.Send(current.ServerUrl, key, HttpMethod.Get, "/me", null, 10);
                return Record(me);
            }

            foreach (string file in files)
            {
                if (stopping || settings != current) return false;
                HubOutboxItem item = Outbox.Read(file);
                if (item == null)
                {
                    Outbox.SetAside(file, "The file couldn't be read.");
                    continue;
                }

                string body = item.Body != null ? item.Body.ToString(Newtonsoft.Json.Formatting.None) : null;
                HubResponse response = HubClient.Send(current.ServerUrl, key, new HttpMethod(item.Method), item.Path, body, 60);
                if (response.Ok)
                {
                    Outbox.Remove(file);
                    Record(response);
                    continue;
                }
                if (!Record(response)) return false;

                // The hub answered but refused this one request. Keep it aside so
                // the requests behind it can still go.
                Log.EventWriter("[AgOpen] Hub refused " + item.Method + " " + item.Path + ": " + response.Error);
                Outbox.SetAside(file, response.Error);
            }
            return true;
        }

        /// <summary>
        /// Note what an answer says about the connection. False means stop and try
        /// later: the hub couldn't be reached, was busy, or rejected the key.
        /// </summary>
        private bool Record(HubResponse response)
        {
            if (response.Ok)
            {
                SetState(HubState.Online, "", true);
                return true;
            }
            if (!response.Reached || response.Status >= 500 || response.Status == 408 || response.Status == 429)
            {
                SetState(HubState.Offline, response.Error, false);
                return false;
            }
            if (response.Status == 401)
            {
                SetState(HubState.KeyRejected, response.Error, true);
                return false;
            }
            SetState(HubState.Online, response.Error, true);
            return true;
        }
    }
}
