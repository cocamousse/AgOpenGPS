using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgLibrary.Logging;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// The words shown next to AgOpenGPS's buttons, kept in a plain text file
    /// ("AgOpen button labels.txt" in the AgOpenGPS folder) so they can be changed
    /// in Notepad without a new build. The file is written with the main screen's
    /// names the first time; a button on another screen gets a name made from its
    /// control name the first time that screen opens, added at the bottom.
    ///
    /// File layout: [Screen] headings, then one "control name = label" per line.
    /// An empty label shows nothing for that button; # starts a note.
    /// </summary>
    public static class AgOpenButtonNames
    {
        /// <summary>The heading for AgOpenGPS's main screen and its pop-out menus.</summary>
        public const string MainScreen = "Main screen";

        private static readonly object Gate = new object();

        // Screen -> control name -> label.
        private static Dictionary<string, Dictionary<string, string>> names;
        private static DateTime fileStampUtc = DateTime.MinValue;
        private static DateTime lookedUtc = DateTime.MinValue;
        private static bool problemLogged;

        // The main screen's names, grouped as they are on screen. A line starting
        // with # becomes a note in the file.
        private static readonly string[] MainDefaults =
        {
            "# Top left",
            "fileToolStripMenuItem = Menu",
            "# Left column",
            "btnNavigationSettings = View",
            "toolStripDropDownButton4 = Tools",
            "toolStripDropDownButton1 = Settings",
            "btnJobMenu = Field",
            "toolStripBtnFieldTools = Field tools",
            "btnAutoSteerConfig = Steer setup",
            "btnStartAgIO = AgIO",
            "btnAgOpenGo = Start screen",
            "# Right column",
            "btnAutoSteer = Auto steer",
            "btnAutoYouTurn = Auto U-turn",
            "btnSectionMasterAuto = Sections auto",
            "btnSectionMasterManual = Sections manual",
            "btnIsobusSectionControl = ISOBUS sections",
            "btnAutoTrack = Auto track",
            "btnCycleLinesBk = Previous line",
            "btnCycleLines = Next line",
            "btnContour = Contour",
            "btnContourLock = Contour lock",
            "# Bottom row",
            "btnTrack = Tracks",
            "btnSnapToPivot = Snap to tractor",
            "btnAdjRight = Nudge right",
            "btnAdjLeft = Nudge left",
            "btnFlag = Flag",
            "btnHeadlandOnOff = Headland",
            "cboxIsSectionControlled = Headland sections",
            "btnHydLift = Hyd lift",
            "btnTramDisplayMode = Tramlines",
            "btnResetToolHeading = Reset tool",
            "btnChangeMappingColor = Coverage color",
            "btnYouSkipEnable = Skip rows",
            "cboxpRowWidth = Rows to skip",
            "# Top right",
            "btnFieldStats = Field info",
            "btnGPSData = GPS info",
            "btnChargeStatus = Battery",
            "btnMinimizeMainForm = Minimize",
            "btnMaximizeMainForm = Full screen",
            "btnShutdown = Close",
            "# Tracks menu (opened with Tracks)",
            "btnNudge = Nudge",
            "btnABDraw = Draw lines",
            "btnPlusAB = New AB line",
            "btnBuildTracks = Tracks list",
            "btnTracksOff = Lines off",
            "cboxAutoSnapToPivot = Auto snap",
            "btnRefNudge = Move line",
            "# View menu (opened with View)",
            "btnTiltDn = Tilt down",
            "btnTiltUp = Tilt up",
            "btn2D = 2D",
            "btn3D = 3D",
            "btnN2D = North up",
            "btnGrid = Grid",
            "btnDayNightMode = Day / night",
            "btnBrightnessDn = Dimmer",
            "btnBrightnessUp = Brighter",
            "# Recorded path buttons",
            "btnPathGoStop = Drive path",
            "btnResumePath = Resume path",
            "btnPathRecordStop = Record path",
            "btnPickPath = Pick path",
            "btnSwapABRecordedPath = Reverse path",
            "# Simulator",
            "btnResetSim = Reset sim",
            "btnResetSteerAngle = Center steering",
            "btnSpeedDn = Slower",
            "btnSimSpeedUp = Faster",
            "btnSimSetSpeedToZero = Stop",
            "btnSimReverseDirection = Turn around",
        };

        private static readonly string[] Header =
        {
            "# AgOpen button labels",
            "# The words shown next to AgOpenGPS's buttons. Change the words after an",
            "# = sign and save this file: AgOpenGPS uses them the next time labels show.",
            "# Leave the words after = empty to show no label for that button.",
            "# Other screens are added at the bottom the first time they open, with",
            "# names made from the button's name in the program: change them as you like.",
        };

        /// <summary>The labels file, in the AgOpenGPS folder.</summary>
        public static string FilePath
        {
            get { return Path.Combine(RegistrySettings.baseDirectory, "AgOpen button labels.txt"); }
        }

        /// <summary>
        /// The label for a button on a screen ("" for none). A button the file
        /// doesn't have yet is added with a name made from its control name.
        /// screenTitle is noted next to a new screen's heading.
        /// </summary>
        public static string Get(string screen, string key, string screenTitle)
        {
            if (string.IsNullOrEmpty(key)) return "";
            lock (Gate)
            {
                try
                {
                    Load();
                    Dictionary<string, string> list;
                    string label;
                    if (names.TryGetValue(screen, out list) && list.TryGetValue(key, out label)) return label;

                    label = AutoName(key);
                    Remember(screen, key, label, screenTitle);
                    return label;
                }
                catch (Exception ex)
                {
                    NoteProblem(ex);
                    return AutoName(key);
                }
            }
        }

        /// <summary>
        /// A label made from a control's name: "btnMakeABLine" becomes "Make AB line",
        /// "cboxIsZoom" becomes "Zoom".
        /// </summary>
        public static string AutoName(string controlName)
        {
            if (string.IsNullOrEmpty(controlName)) return "";
            string name = controlName;
            foreach (string end in new[] { "ToolStripMenuItem", "MenuItem", "Button" })
            {
                if (name.Length > end.Length && name.EndsWith(end, StringComparison.Ordinal)) name = name.Substring(0, name.Length - end.Length);
            }
            foreach (string start in new[] { "toolStrip", "button", "cbox", "chk", "btn", "cb", "rb", "nud", "lbl" })
            {
                if (name.Length > start.Length && name.StartsWith(start, StringComparison.Ordinal)
                    && (char.IsUpper(name[start.Length]) || char.IsDigit(name[start.Length])))
                {
                    name = name.Substring(start.Length);
                    break;
                }
            }

            // Split at changes of case and at numbers, keeping runs of capitals (AB, KML) together.
            var words = new List<string>();
            foreach (Match m in Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+[A-Za-z]?"))
            {
                words.Add(m.Value);
            }
            if (words.Count == 0) return controlName;
            if (words.Count > 1 && words[0] == "Is") words.RemoveAt(0);

            var text = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i];
                if (w == "Bk") w = "back";
                else if (w == "Dn") w = "down";
                bool acronym = w.Length > 1 && w.ToUpperInvariant() == w && !char.IsDigit(w[0]);
                if (!acronym && !char.IsDigit(w[0])) w = i == 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant() : w.ToLowerInvariant();
                if (text.Length > 0) text.Append(' ');
                text.Append(w);
            }
            return text.ToString().Replace("You turn", "U-turn").Replace("you turn", "U-turn");
        }

        // Read the file again if it changed since last time (looked at once a second at
        // most); write it first if it isn't there.
        private static void Load()
        {
            if (names != null && (DateTime.UtcNow - lookedUtc).TotalSeconds < 1) return;
            lookedUtc = DateTime.UtcNow;
            string path = FilePath;
            if (!File.Exists(path)) WriteNew(path);

            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (names != null && stamp == fileStampUtc) return;

            var read = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string screen = MainScreen;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = line.IndexOf(']');
                    if (close > 1) screen = line.Substring(1, close - 1).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string label = line.Substring(eq + 1).Trim();
                Dictionary<string, string> list;
                if (!read.TryGetValue(screen, out list))
                {
                    list = new Dictionary<string, string>(StringComparer.Ordinal);
                    read[screen] = list;
                }
                list[key] = label;
            }
            names = read;
            fileStampUtc = stamp;
        }

        private static void WriteNew(string path)
        {
            var lines = new List<string>(Header);
            lines.Add("");
            lines.Add("[" + MainScreen + "]");
            foreach (string line in MainDefaults)
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) lines.Add("");
                lines.Add(line);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines, Encoding.UTF8);
            Log.EventWriter("[AgOpen] Button labels file written: " + path);
        }

        // Add a button the file doesn't have yet, at the bottom, under its screen.
        private static void Remember(string screen, string key, string label, string screenTitle)
        {
            Dictionary<string, string> list;
            bool newScreen = !names.TryGetValue(screen, out list);
            if (newScreen)
            {
                list = new Dictionary<string, string>(StringComparer.Ordinal);
                names[screen] = list;
            }
            list[key] = label;

            var text = new StringBuilder();
            if (newScreen || lastAppendedScreen != screen)
            {
                text.AppendLine();
                if (newScreen && !string.IsNullOrWhiteSpace(screenTitle)) text.AppendLine("# " + screenTitle.Trim());
                text.AppendLine("[" + screen + "]");
            }
            text.AppendLine(key + " = " + label);
            string path = FilePath;
            File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            fileStampUtc = File.GetLastWriteTimeUtc(path);
            lastAppendedScreen = screen;
        }

        private static string lastAppendedScreen;

        private static void NoteProblem(Exception ex)
        {
            if (problemLogged) return;
            problemLogged = true;
            Log.EventWriter("[AgOpen] Button labels file problem: " + ex.Message);
        }
    }
}
