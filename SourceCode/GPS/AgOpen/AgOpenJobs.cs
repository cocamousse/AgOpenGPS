using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// A job: one operation on one field, worked in its own AgOpenGPS folder so
    /// section control starts with no coverage. Saved as agopen-job.json in that
    /// folder, which is also how a job folder is told apart from a master field.
    /// </summary>
    public class AgOpenJob
    {
        public const string FileName = "agopen-job.json";

        [JsonProperty("job_id")]
        public string JobId { get; set; } = "";

        /// <summary>The hub's id for the field, when the master came from the hub.</summary>
        [JsonProperty("field_id")]
        public string FieldId { get; set; }

        [JsonProperty("field_name")]
        public string FieldName { get; set; } = "";

        /// <summary>The master field's folder this job was copied from.</summary>
        [JsonProperty("master_folder")]
        public string MasterFolder { get; set; } = "";

        /// <summary>This job's own folder.</summary>
        [JsonProperty("folder")]
        public string Folder { get; set; } = "";

        [JsonProperty("operation")]
        public string Operation { get; set; }

        [JsonProperty("crop")]
        public string Crop { get; set; }

        [JsonProperty("implement_type")]
        public string ImplementType { get; set; }

        [JsonProperty("vehicle")]
        public string Vehicle { get; set; }

        [JsonProperty("tool")]
        public string Tool { get; set; }

        /// <summary>The operator chose "Fix later at home": the hub flags it for review.</summary>
        [JsonProperty("fix_later")]
        public bool FixLater { get; set; }

        /// <summary>When Start was tapped, UTC (the hub's format).</summary>
        [JsonProperty("started_at")]
        public string StartedAt { get; set; } = "";
    }

    /// <summary>What the job prompt suggests, and the lists to choose from.</summary>
    public class AgOpenJobGuess
    {
        public string Tool = "";
        public string Vehicle = "";
        public double? WidthM;

        /// <summary>What the implement is (sprayer, planter...), or null if unknown.</summary>
        public string ImplementType;

        /// <summary>What this implement normally does, from its type.</summary>
        public string NaturalOperation;

        /// <summary>The guess: what it did last time, else what it normally does. Null if unknown.</summary>
        public string Operation;

        /// <summary>The guessed crop, or null for none.</summary>
        public string Crop;

        public List<string> Operations = new List<string>();
        public List<string> Crops = new List<string>();
        public List<string> CropRequiredFor = new List<string>();
    }

    /// <summary>What the operator settled on in the job prompt.</summary>
    public class AgOpenJobChoice
    {
        public string Operation;
        public string Crop;
        public bool FixLater;
    }

    /// <summary>
    /// Job prompt guesses, job folders and the last job. Guesses come from what
    /// each implement last did (this tablet's memory and, through the hub, any
    /// tablet's jobs), then from what the implement is. Everything works offline.
    /// </summary>
    public static class AgOpenJobs
    {
        // The same starting rules as the hub (aog_hub/config.py), for when the hub
        // hasn't said yet. The hub's answer wins once a sync has run.
        private static readonly Dictionary<string, string[]> TypeKeywords = new Dictionary<string, string[]>
        {
            { "sprayer", new[] { "sprayer", "spray" } },
            { "planter", new[] { "planter", "plant", "seeder", "drill" } },
            { "plow", new[] { "plow", "plough", "moldboard" } },
            { "disc", new[] { "disc", "disk" } },
            { "harrow", new[] { "harrow" } },
            { "cultivator", new[] { "cultivator", "field cultivator", "cult" } },
            { "chisel", new[] { "chisel", "ripper" } },
            { "mower", new[] { "mower", "mow", "chopper", "shredder" } },
            { "spreader", new[] { "spreader" } },
            { "rake", new[] { "rake" } },
        };

        // On this farm, implement profiles are named by type (Disk, Plow, Sprayer,
        // Planter, Drill), or by brand when there are several of one kind (Hardi,
        // Gator: both sprayers).
        private static readonly Dictionary<string, string> Brands = new Dictionary<string, string>
        {
            { "hardi", "sprayer" },
            { "hardy", "sprayer" },
            { "gator", "sprayer" },
            { "white", "planter" },
            { "kinze", "planter" },
        };

        private const double SprayerFromWidthM = 15.0;

        // What each implement type is doing: the verb on the record. The implement
        // itself is the tool profile (Disk, Plow, Drill...), kept on every job.
        // Planters and drills both plant: for farmOS what matters is the crop.
        private static readonly Dictionary<string, string> Operations = new Dictionary<string, string>
        {
            { "sprayer", "spraying" },
            { "planter", "planting" },
            { "plow", "plowing" },
            { "disc", "disking" },
            { "harrow", "harrowing" },
            { "cultivator", "cultivating" },
            { "chisel", "chisel plowing" },
            { "mower", "mowing" },
            { "spreader", "spreading" },
            { "rake", "raking" },
        };

        // Order the operations are offered in.
        private static readonly string[] OperationOrder =
        {
            "planting", "spraying", "disking", "plowing", "harrowing", "cultivating", "chisel plowing",
            "mowing", "spreading", "raking", "harvest"
        };

        private static readonly string[] DefaultCrops = { "Corn", "Soybeans", "Wheat", "Oats", "Hay", "Cover crop" };
        private static readonly string[] DefaultCropRequiredFor = { "planting", "harvest" };

        // A word for the job folder name when no implement profile is loaded.
        private static readonly Dictionary<string, string> OperationWords = new Dictionary<string, string>
        {
            { "spraying", "SPRAY" }, { "planting", "PLANT" }, { "disking", "DISK" }, { "plowing", "PLOW" },
            { "harrowing", "HARROW" }, { "cultivating", "CULTIVATE" }, { "chisel plowing", "CHISEL" },
            { "tillage", "TILL" }, { "mowing", "MOW" }, { "spreading", "SPREAD" }, { "raking", "RAKE" },
            { "harvest", "HARVEST" }, { "seeding", "SEED" },
        };

        // Files copied from the master field into a new job folder (as AgOpenGPS's
        // own "New from existing" does), plus every saved recorded path (*.rec).
        private static readonly string[] CopiedFiles =
        {
            "Boundary.txt", "Headland.txt", "Headlines.txt", "TrackLines.txt", "ABLines.txt", "CurveLines.txt",
            "Flags.txt", "Tram.txt", "RecPath.txt", "BackPic.txt", "BackPic.png", "Elevation.txt",
        };

        private static readonly object FileLock = new object();

        private static string HintsPath
        {
            get { return Path.Combine(HubSettings.Folder, "equipment.json"); }
        }

        private static string MemoryPath
        {
            get { return Path.Combine(HubSettings.Folder, "job-memory.json"); }
        }

        private static string LastJobPath
        {
            get { return Path.Combine(HubSettings.Folder, "last-job.json"); }
        }

        #region Job folders

        public static bool IsJobFolder(string fieldDirectory)
        {
            return !string.IsNullOrEmpty(fieldDirectory) && File.Exists(Path.Combine(fieldDirectory, AgOpenJob.FileName));
        }

        /// <summary>The job saved in a folder, or null if it isn't a job folder.</summary>
        public static AgOpenJob Read(string fieldDirectory)
        {
            string path = Path.Combine(fieldDirectory, AgOpenJob.FileName);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<AgOpenJob>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Make a new job folder from a master field: its boundary, headland,
        /// lines, recorded paths, flags and tramlines, with no coverage. Returns the job.
        /// </summary>
        public static AgOpenJob Create(string fieldsDirectory, string masterFolder, AgOpenJobGuess guess, AgOpenJobChoice choice)
        {
            string masterDir = Path.Combine(fieldsDirectory, masterFolder);
            if (!File.Exists(Path.Combine(masterDir, "Field.txt")))
            {
                throw new InvalidOperationException("'" + masterFolder + "' has no Field.txt.");
            }
            Wgs84 origin = FieldPlaneFiles.LoadOrigin(masterDir);

            string name = UniqueFolderName(fieldsDirectory, FolderName(masterFolder, guess, choice, DateTime.Now));
            string jobDir = Path.Combine(fieldsDirectory, name);
            Directory.CreateDirectory(jobDir);
            try
            {
                FieldPlaneFiles.Save(jobDir, DateTime.Now, origin);
                foreach (string file in CopiedFiles) CopyIfThere(masterDir, jobDir, file);
                foreach (string path in Directory.GetFiles(masterDir, "*.rec")) CopyIfThere(masterDir, jobDir, Path.GetFileName(path));

                // Blank coverage, and the files AgOpenGPS expects, as "New from existing" makes them.
                File.WriteAllText(Path.Combine(jobDir, "Sections.txt"), "");
                File.WriteAllText(Path.Combine(jobDir, "Contour.txt"), "$Contour" + Environment.NewLine);
                WriteIfMissing(jobDir, "Flags.txt", "$Flags" + Environment.NewLine + "0" + Environment.NewLine);
                WriteIfMissing(jobDir, "RecPath.txt", "$RecPath" + Environment.NewLine + "0" + Environment.NewLine);
                WriteIfMissing(jobDir, "Headlines.txt", "$Headlines" + Environment.NewLine);

                HubFieldState master = HubFieldState.Load(masterDir);
                var job = new AgOpenJob
                {
                    JobId = Guid.NewGuid().ToString(),
                    FieldId = master != null && !string.IsNullOrEmpty(master.FieldId) ? master.FieldId : null,
                    FieldName = masterFolder,
                    MasterFolder = masterFolder,
                    Folder = name,
                    Operation = choice.Operation,
                    Crop = string.IsNullOrEmpty(choice.Crop) ? null : choice.Crop,
                    ImplementType = guess.ImplementType,
                    Vehicle = guess.Vehicle,
                    Tool = guess.Tool,
                    FixLater = choice.FixLater,
                    StartedAt = HubTime(DateTime.UtcNow)
                };
                HubFiles.WriteAllTextSafely(Path.Combine(jobDir, AgOpenJob.FileName),
                    JsonConvert.SerializeObject(job, Formatting.Indented));
                return job;
            }
            catch
            {
                // Don't leave a half-made job folder behind.
                try
                {
                    Directory.Delete(jobDir, true);
                }
                catch (Exception cleanup)
                {
                    Log.EventWriter("[AgOpen] Couldn't remove half-made job folder " + jobDir + ": " + cleanup.Message);
                }
                throw;
            }
        }

        /// <summary>What to send the hub when a job starts (PUT /jobs/{id}).</summary>
        public static JObject HubBody(AgOpenJob job)
        {
            var body = new JObject
            {
                ["started_at"] = job.StartedAt,
                ["folder_name"] = job.Folder,
                ["fix_later"] = job.FixLater
            };
            if (!string.IsNullOrEmpty(job.FieldId)) body["field_id"] = job.FieldId;
            if (!string.IsNullOrEmpty(job.Vehicle)) body["vehicle"] = job.Vehicle;
            if (!string.IsNullOrEmpty(job.Tool)) body["tool"] = job.Tool;
            if (!string.IsNullOrEmpty(job.Operation)) body["operation"] = job.Operation;
            if (!string.IsNullOrEmpty(job.Crop)) body["crop"] = job.Crop;
            return body;
        }

        /// <summary>
        /// The farm's name for a job folder: field, crop, implement and the date,
        /// e.g. "SMITH EAST CORN SPRAYER 10-19-26". Capitals tell job folders apart
        /// from master fields in AgOpenGPS's own field list.
        /// </summary>
        public static string FolderName(string fieldName, AgOpenJobGuess guess, AgOpenJobChoice choice, DateTime date)
        {
            var parts = new List<string> { fieldName };
            if (!string.IsNullOrEmpty(choice.Crop)) parts.Add(choice.Crop);

            string implement = guess.Tool;
            if (string.IsNullOrWhiteSpace(implement))
            {
                implement = "JOB";
                if (!string.IsNullOrEmpty(choice.Operation))
                {
                    implement = OperationWords.TryGetValue(choice.Operation, out string word) ? word : choice.Operation;
                }
            }
            parts.Add(implement);
            parts.Add(date.ToString("MM-dd-yy", CultureInfo.InvariantCulture));
            return SafeFolderName(string.Join(" ", parts).ToUpperInvariant());
        }

        private static string UniqueFolderName(string fieldsDirectory, string name)
        {
            if (!Directory.Exists(Path.Combine(fieldsDirectory, name))) return name;
            for (int n = 2; n < 1000; n++)
            {
                string numbered = name + " " + n.ToString(CultureInfo.InvariantCulture);
                if (!Directory.Exists(Path.Combine(fieldsDirectory, numbered))) return numbered;
            }
            return name + " " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        }

        private static string SafeFolderName(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad.ToString(), "");
            name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.');
            if (name.Length > 100) name = name.Substring(0, 100).TrimEnd();
            return name.Length > 0 ? name : "JOB";
        }

        private static void CopyIfThere(string fromDir, string toDir, string file)
        {
            string from = Path.Combine(fromDir, file);
            if (File.Exists(from)) File.Copy(from, Path.Combine(toDir, file), true);
        }

        private static void WriteIfMissing(string dir, string file, string text)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) File.WriteAllText(path, text);
        }

        /// <summary>The hub's time format: UTC, to the second.</summary>
        public static string HubTime(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";
        }

        #endregion

        #region Last job (for Continue)

        /// <summary>Note the job folder that was just opened, for Continue.</summary>
        public static void RememberOpened(string folder)
        {
            try
            {
                lock (FileLock)
                {
                    var state = new JObject { ["folder"] = folder, ["opened_at"] = HubTime(DateTime.UtcNow) };
                    Directory.CreateDirectory(HubSettings.Folder);
                    HubFiles.WriteAllTextSafely(LastJobPath, state.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Couldn't note the last job: " + ex.Message);
            }
        }

        /// <summary>The last job opened on this tablet, if its folder is still there.</summary>
        public static AgOpenJob LastJob(string fieldsDirectory)
        {
            try
            {
                if (!File.Exists(LastJobPath)) return null;
                JObject state = JObject.Parse(File.ReadAllText(LastJobPath));
                string folder = (string)state["folder"];
                if (string.IsNullOrEmpty(folder)) return null;
                AgOpenJob job = Read(Path.Combine(fieldsDirectory, folder));
                if (job == null) return null;
                job.Folder = folder;
                return job;
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Couldn't read the last job: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Guessing

        /// <summary>The hub's equipment answer (GET /profiles), kept for when the tablet is offline.</summary>
        public static void SaveHubHints(JObject profiles)
        {
            try
            {
                var hints = new JObject
                {
                    ["tools"] = new JObject(),
                    ["crops"] = profiles["crops"] as JArray ?? new JArray(),
                    ["operations"] = profiles["operations"] as JArray ?? new JArray(),
                    ["crop_required_for"] = profiles["crop_required_for"] as JArray ?? new JArray(),
                    ["saved_at"] = HubTime(DateTime.UtcNow)
                };
                foreach (JToken item in profiles["tools"] as JArray ?? new JArray())
                {
                    if (!(item is JObject tool) || string.IsNullOrEmpty((string)tool["name"])) continue;
                    hints["tools"][(string)tool["name"]] = new JObject
                    {
                        ["implement_type"] = tool["implement_type"],
                        ["operation"] = tool["operation"],
                        ["last_job"] = tool["last_job"]
                    };
                }
                lock (FileLock)
                {
                    Directory.CreateDirectory(HubSettings.Folder);
                    HubFiles.WriteAllTextSafely(HintsPath, hints.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Couldn't keep the hub's equipment hints: " + ex.Message);
            }
        }

        /// <summary>Remember what this implement did, for next time's guess.</summary>
        public static void Remember(string tool, AgOpenJobChoice choice)
        {
            if (string.IsNullOrEmpty(tool)) return;
            try
            {
                lock (FileLock)
                {
                    JObject memory = ReadJson(MemoryPath) ?? new JObject();
                    JObject tools = memory["tools"] as JObject ?? new JObject();
                    tools[tool] = new JObject
                    {
                        ["operation"] = choice.Operation,
                        ["crop"] = choice.Crop,
                        ["at"] = HubTime(DateTime.UtcNow)
                    };
                    memory["tools"] = tools;

                    if (!string.IsNullOrEmpty(choice.Crop))
                    {
                        var crops = new List<string> { choice.Crop };
                        foreach (JToken crop in memory["crops"] as JArray ?? new JArray())
                        {
                            string name = (string)crop;
                            if (!string.IsNullOrEmpty(name) && !Contains(crops, name)) crops.Add(name);
                        }
                        memory["crops"] = new JArray(crops.ToArray());
                    }
                    Directory.CreateDirectory(HubSettings.Folder);
                    HubFiles.WriteAllTextSafely(MemoryPath, memory.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Couldn't remember the job choice: " + ex.Message);
            }
        }

        /// <summary>Guess the operation and crop for the loaded implement.</summary>
        public static AgOpenJobGuess Guess(string tool, string vehicle, double? widthM)
        {
            var guess = new AgOpenJobGuess { Tool = tool ?? "", Vehicle = vehicle ?? "", WidthM = widthM };
            JObject hints;
            JObject memory;
            lock (FileLock)
            {
                hints = ReadJson(HintsPath) ?? new JObject();
                memory = ReadJson(MemoryPath) ?? new JObject();
            }
            JObject hubTool = (hints["tools"] as JObject)?[guess.Tool] as JObject;

            // What the implement is, and what it normally does.
            guess.ImplementType = (string)hubTool?["implement_type"] ?? GuessImplementType(guess.Tool, widthM);
            guess.NaturalOperation = (string)hubTool?["operation"];
            if (guess.NaturalOperation == null && guess.ImplementType != null)
            {
                Operations.TryGetValue(guess.ImplementType, out guess.NaturalOperation);
            }

            // What it did last time: this tablet's memory or the hub's (any tablet), whichever is newer.
            JObject mine = (memory["tools"] as JObject)?[guess.Tool] as JObject;
            JObject hubLast = hubTool?["last_job"] as JObject;
            JObject last = Newer(mine, (string)mine?["at"], hubLast, (string)hubLast?["started_at"]);
            guess.Operation = (string)last?["operation"] ?? guess.NaturalOperation;
            if (last != null && (string)last["operation"] == guess.Operation)
            {
                string crop = (string)last["crop"];
                guess.Crop = string.IsNullOrEmpty(crop) ? null : crop;
            }

            // Lists to choose from.
            foreach (string op in OperationOrder) AddOnce(guess.Operations, op);
            foreach (JToken op in hints["operations"] as JArray ?? new JArray()) AddOnce(guess.Operations, (string)op);
            AddOnce(guess.Operations, guess.NaturalOperation);
            AddOnce(guess.Operations, guess.Operation);

            foreach (JToken crop in memory["crops"] as JArray ?? new JArray()) AddOnce(guess.Crops, (string)crop);
            foreach (JToken crop in hints["crops"] as JArray ?? new JArray()) AddOnce(guess.Crops, (string)crop);
            foreach (string crop in DefaultCrops) AddOnce(guess.Crops, crop);
            AddOnce(guess.Crops, guess.Crop);

            JArray required = hints["crop_required_for"] as JArray;
            if (required != null && required.Count > 0)
            {
                foreach (JToken op in required) AddOnce(guess.CropRequiredFor, (string)op);
            }
            else
            {
                guess.CropRequiredFor.AddRange(DefaultCropRequiredFor);
            }
            return guess;
        }

        /// <summary>Does this operation need a crop for the record to be complete?</summary>
        public static bool CropRequired(AgOpenJobGuess guess, string operation)
        {
            return !string.IsNullOrEmpty(operation) && Contains(guess.CropRequiredFor, operation);
        }

        /// <summary>The hub's starting rules, for when the hub hasn't said what an implement is.</summary>
        public static string GuessImplementType(string name, double? widthM)
        {
            string joined = string.Join(" ", Words(name ?? ""));
            foreach (KeyValuePair<string, string[]> type in TypeKeywords)
            {
                foreach (string keyword in type.Value)
                {
                    string phrase = string.Join(" ", Words(keyword));
                    if (phrase.Length > 0 && Regex.IsMatch(joined, @"\b" + Regex.Escape(phrase) + @"\b")) return type.Key;
                }
            }
            List<string> words = Words(name ?? "");
            foreach (KeyValuePair<string, string> brand in Brands)
            {
                if (words.Contains(brand.Key)) return brand.Value;
            }
            if (widthM.HasValue && widthM.Value >= SprayerFromWidthM) return "sprayer";
            return null;
        }

        private static List<string> Words(string text)
        {
            var words = new List<string>();
            foreach (Match match in Regex.Matches(text.ToLowerInvariant(), "[a-z]+")) words.Add(match.Value);
            return words;
        }

        private static JObject Newer(JObject a, string aTime, JObject b, string bTime)
        {
            if (a == null) return b;
            if (b == null) return a;
            DateTime at, bt;
            bool aOk = DateTime.TryParse(aTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out at);
            bool bOk = DateTime.TryParse(bTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out bt);
            if (!bOk) return a;
            if (!aOk) return b;
            return bt > at ? b : a;
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !Contains(list, value.Trim())) list.Add(value.Trim());
        }

        private static bool Contains(List<string> list, string value)
        {
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static JObject ReadJson(string path)
        {
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable " + path + ": " + ex.Message);
                return null;
            }
        }

        #endregion
    }
}
