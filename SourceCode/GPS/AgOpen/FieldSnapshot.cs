using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// A field folder as AgOpenGPS reads it, item by item: the boundary, the
    /// headland, the flags and the tramlines (each whole), every guidance line
    /// and every named recorded path (.rec file). Each item gets a fingerprint,
    /// so what changed since the hub last had the field can be told apart, and
    /// the time its file was last saved. It also turns items into what the hub
    /// takes (lon/lat), and copies items into another folder (a job's new lines
    /// into its master field).
    ///
    /// AgOpenGPS writes the same text back when a field is opened and closed
    /// without changes, so fingerprints of what it reads only change with real edits.
    /// </summary>
    internal class FieldSnapshot
    {
        public const string BoundaryKey = "boundary";
        public const string HeadlandKey = "headland";
        public const string FlagsKey = "flags";
        public const string TramKey = "tram";
        public const string LinePrefix = "line:";
        public const string PathPrefix = "path:";

        /// <summary>The files that hold a field's items (besides the named .rec paths).</summary>
        public static readonly string[] ItemFiles = { "Boundary.txt", "Headland.txt", "TrackLines.txt", "Flags.txt", "Tram.txt" };

        /// <summary>Fingerprint of each item.</summary>
        public readonly Dictionary<string, string> Fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>When each item's file was last saved (UTC).</summary>
        public readonly Dictionary<string, DateTime> SavedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>False if a file couldn't be read: nothing may be sent from this snapshot.</summary>
        public bool Complete = true;

        public string Folder = "";
        public List<CBoundaryList> Boundaries = new List<CBoundaryList>();
        public readonly Dictionary<string, CTrk> Lines = new Dictionary<string, CTrk>(StringComparer.Ordinal);
        public List<CFlag> Flags = new List<CFlag>();
        public TramFiles.TramData Tram;
        public readonly Dictionary<string, string> PathFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The field has something worth keeping on the hub (a boundary, a line, a path or a flag).</summary>
        public bool HasContent
        {
            get { return Fingerprints.ContainsKey(BoundaryKey) || Lines.Count > 0 || PathFiles.Count > 0 || Flags.Count > 0; }
        }

        public static FieldSnapshot Read(string folder)
        {
            var snap = new FieldSnapshot { Folder = folder };

            snap.ReadPart("Boundary.txt", () =>
            {
                snap.Boundaries = BoundaryFiles.Load(folder);
                if (snap.Boundaries.Count > 0 && snap.Boundaries[0].fenceLine.Count >= 3)
                {
                    var sb = new StringBuilder();
                    foreach (CBoundaryList b in snap.Boundaries)
                    {
                        sb.Append(b.isDriveThru ? "D" : "B").Append('\n');
                        AppendPoints(sb, b.fenceLine);
                    }
                    snap.Add(BoundaryKey, sb.ToString(), "Boundary.txt");
                }
            });

            snap.ReadPart("Headland.txt", () =>
            {
                // The headland is kept per boundary, so it's read onto the boundaries.
                HeadlandFiles.AttachLoad(folder, snap.Boundaries);
                var sb = new StringBuilder();
                foreach (CBoundaryList b in snap.Boundaries)
                {
                    sb.Append(b.hdLine.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    AppendPoints(sb, b.hdLine);
                }
                snap.Add(HeadlandKey, sb.ToString(), "Headland.txt");
            });

            snap.ReadPart("TrackLines.txt", () =>
            {
                foreach (CTrk track in TrackFiles.Load(folder))
                {
                    if (KindOf(track.mode) == null) continue; // not a kind the hub keeps
                    string name = UniqueName(snap.Lines, string.IsNullOrWhiteSpace(track.name) ? "Line" : track.name.Trim());
                    track.name = name;
                    snap.Lines[name] = track;
                    snap.Add(LinePrefix + name, TrackText(track), "TrackLines.txt");
                }
            });

            snap.ReadPart("Flags.txt", () =>
            {
                snap.Flags = FlagsFiles.Load(folder);
                var sb = new StringBuilder();
                foreach (CFlag f in snap.Flags)
                {
                    sb.Append(R(f.latitude)).Append(',').Append(R(f.longitude)).Append(',')
                      .Append(R(f.easting)).Append(',').Append(R(f.northing)).Append(',')
                      .Append(R(f.heading)).Append(',').Append(f.color.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(f.ID.ToString(CultureInfo.InvariantCulture)).Append(',').Append(f.notes ?? "").Append('\n');
                }
                snap.Add(FlagsKey, sb.ToString(), "Flags.txt");
            });

            snap.ReadPart("Tram.txt", () =>
            {
                snap.Tram = TramFiles.Load(folder);
                var sb = new StringBuilder();
                AppendPoints(sb, snap.Tram.Outer);
                sb.Append("|\n");
                AppendPoints(sb, snap.Tram.Inner);
                foreach (List<vec2> line in snap.Tram.Lines)
                {
                    sb.Append("|\n");
                    AppendPoints(sb, line);
                }
                snap.Add(TramKey, sb.ToString(), "Tram.txt");
            });

            try
            {
                foreach (string file in Directory.GetFiles(folder, "*.rec"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    snap.PathFiles[name] = file;
                    snap.Fingerprints[PathPrefix + name] = FileFingerprint(file);
                    snap.SavedAt[PathPrefix + name] = File.GetLastWriteTimeUtc(file);
                }
            }
            catch (Exception ex)
            {
                snap.Complete = false;
                Log.EventWriter("[AgOpen] Recorded paths in '" + Path.GetFileName(folder) + "' couldn't be read: " + ex.Message);
            }
            return snap;
        }

        private void ReadPart(string file, Action read)
        {
            if (!File.Exists(Path.Combine(Folder, file))) return;
            try
            {
                read();
            }
            catch (Exception ex)
            {
                Complete = false;
                Log.EventWriter("[AgOpen] " + file + " in '" + Path.GetFileName(Folder) + "' couldn't be read: " + ex.Message);
            }
        }

        private void Add(string key, string text, string file)
        {
            Fingerprints[key] = TextFingerprint(text);
            SavedAt[key] = File.GetLastWriteTimeUtc(Path.Combine(Folder, file));
        }

        /// <summary>The file an item lives in (for the edit time of a removed item).</summary>
        public static string FileOf(string key)
        {
            if (key == BoundaryKey) return "Boundary.txt";
            if (key == HeadlandKey) return "Headland.txt";
            if (key == FlagsKey) return "Flags.txt";
            if (key == TramKey) return "Tram.txt";
            if (key.StartsWith(LinePrefix, StringComparison.Ordinal)) return "TrackLines.txt";
            if (key.StartsWith(PathPrefix, StringComparison.Ordinal)) return key.Substring(PathPrefix.Length) + ".rec";
            return null;
        }

        #region Fingerprints

        private static string R(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void AppendPoints(StringBuilder sb, IEnumerable<vec3> points)
        {
            foreach (vec3 p in points)
            {
                sb.Append(R(p.easting)).Append(',').Append(R(p.northing)).Append(',').Append(R(p.heading)).Append('\n');
            }
        }

        private static void AppendPoints(StringBuilder sb, IEnumerable<vec2> points)
        {
            foreach (vec2 p in points)
            {
                sb.Append(R(p.easting)).Append(',').Append(R(p.northing)).Append('\n');
            }
        }

        // Everything that makes a line what it is (whether it's shown isn't an edit).
        private static string TrackText(CTrk t)
        {
            var sb = new StringBuilder();
            sb.Append(((int)t.mode).ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append(R(t.heading)).Append('\n')
              .Append(R(t.ptA.easting)).Append(',').Append(R(t.ptA.northing)).Append('\n')
              .Append(R(t.ptB.easting)).Append(',').Append(R(t.ptB.northing)).Append('\n')
              .Append(R(t.nudgeDistance)).Append('\n');
            AppendPoints(sb, t.curvePts);
            return sb.ToString();
        }

        public static string TextFingerprint(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").Substring(0, 32);
            }
        }

        private static string FileFingerprint(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 32);
            }
        }

        // AgOpenGPS lets two lines share a name; the hub keeps lines by name.
        private static string UniqueName(Dictionary<string, CTrk> taken, string name)
        {
            if (!taken.ContainsKey(name)) return name;
            for (int n = 2; ; n++)
            {
                string candidate = name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                if (!taken.ContainsKey(candidate)) return candidate;
            }
        }

        #endregion

        #region For the hub (lon/lat)

        /// <summary>The hub's name for an AgOpenGPS track mode, or null if the hub doesn't keep that kind.</summary>
        public static string KindOf(TrackMode mode)
        {
            switch (mode)
            {
                case TrackMode.AB: return "AB";
                case TrackMode.Curve: return "Curve";
                case TrackMode.bndCurve: return "BoundaryCurve";
                case TrackMode.waterPivot: return "Pivot";
                default: return null;
            }
        }

        private static JArray LonLat(LocalPlane plane, double easting, double northing)
        {
            Wgs84 p = plane.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting));
            return new JArray(Math.Round(p.Longitude, 9), Math.Round(p.Latitude, 9));
        }

        private static JArray Points(LocalPlane plane, IEnumerable<vec3> points, bool close)
        {
            var list = new JArray();
            JArray first = null;
            foreach (vec3 p in points)
            {
                JArray point = LonLat(plane, p.easting, p.northing);
                if (first == null) first = point;
                list.Add(point);
            }
            if (close && first != null && list.Count > 1 && !JToken.DeepEquals(first, list[list.Count - 1])) list.Add(first.DeepClone());
            return list;
        }

        private static JArray Points(LocalPlane plane, IEnumerable<vec2> points)
        {
            var list = new JArray();
            foreach (vec2 p in points) list.Add(LonLat(plane, p.easting, p.northing));
            return list;
        }

        private static JObject Geometry(string type, JArray coordinates)
        {
            return new JObject { ["type"] = type, ["coordinates"] = coordinates };
        }

        /// <summary>The boundary as a GeoJSON Polygon: the outer boundary, then the inner ones as holes.</summary>
        public JObject BoundaryGeoJson(LocalPlane plane)
        {
            var rings = new JArray();
            foreach (CBoundaryList b in Boundaries)
            {
                if (b.fenceLine.Count < 3) continue;
                rings.Add(Points(plane, b.fenceLine, true));
            }
            return rings.Count > 0 ? Geometry("Polygon", rings) : null;
        }

        /// <summary>The headland, one line per boundary in the same order (empty: none).</summary>
        public JObject HeadlandGeoJson(LocalPlane plane)
        {
            var lines = new JArray();
            int last = -1;
            for (int i = 0; i < Boundaries.Count; i++)
            {
                if (Boundaries[i].hdLine.Count >= 3) last = i;
            }
            for (int i = 0; i <= last; i++)
            {
                CBoundaryList b = Boundaries[i];
                if (b.hdLine.Count >= 3)
                {
                    lines.Add(Points(plane, b.hdLine, true));
                }
                else
                {
                    // Keeps the next boundary's headland in its place: too short to be used.
                    var placeholder = new JArray();
                    placeholder.Add(b.fenceLine.Count > 0 ? LonLat(plane, b.fenceLine[0].easting, b.fenceLine[0].northing) : LonLat(plane, 0, 0));
                    lines.Add(placeholder);
                }
            }
            return Geometry("MultiLineString", lines);
        }

        public JArray FlagsJson()
        {
            var list = new JArray();
            foreach (CFlag f in Flags)
            {
                string color = f.color == 1 ? "green" : f.color == 2 ? "yellow" : "red";
                list.Add(new JObject
                {
                    ["position"] = new JArray(Math.Round(f.longitude, 9), Math.Round(f.latitude, 9)),
                    ["heading_rad"] = f.heading,
                    ["color"] = color,
                    ["number"] = f.ID,
                    ["notes"] = (f.notes ?? "").Trim()
                });
            }
            return list;
        }

        /// <summary>The tramlines in the field (empty: none).</summary>
        public JObject TramLinesGeoJson(LocalPlane plane)
        {
            var lines = new JArray();
            if (Tram != null)
            {
                foreach (List<vec2> line in Tram.Lines)
                {
                    if (line.Count > 0) lines.Add(Points(plane, line));
                }
            }
            return Geometry("MultiLineString", lines);
        }

        /// <summary>The two tram tracks round the boundary, outer then inner (empty: none).</summary>
        public JObject TramBoundaryGeoJson(LocalPlane plane)
        {
            var lines = new JArray();
            if (Tram != null)
            {
                if (Tram.Outer.Count > 0) lines.Add(Points(plane, Tram.Outer));
                if (Tram.Inner.Count > 0) lines.Add(Points(plane, Tram.Inner));
            }
            return Geometry("MultiLineString", lines);
        }

        /// <summary>A guidance line for the hub, or null if it can't be sent (too few points).</summary>
        public JObject LineJson(string name, LocalPlane plane)
        {
            CTrk t;
            if (!Lines.TryGetValue(name, out t)) return null;
            string kind = KindOf(t.mode);
            JArray coordinates;
            if (t.mode == TrackMode.Curve || t.mode == TrackMode.bndCurve)
            {
                if (t.curvePts.Count < 2) return null;
                coordinates = Points(plane, t.curvePts, false);
            }
            else
            {
                // AB: its A and B points. Pivot: the pivot centre first.
                coordinates = new JArray(LonLat(plane, t.ptA.easting, t.ptA.northing), LonLat(plane, t.ptB.easting, t.ptB.northing));
            }
            return new JObject
            {
                ["name"] = name,
                ["kind"] = kind,
                ["geometry"] = Geometry("LineString", coordinates),
                ["heading"] = t.heading,
                ["nudge_m"] = t.nudgeDistance
            };
        }

        /// <summary>A recorded path for the hub, or null if it can't be sent.</summary>
        public JObject PathJson(string name, LocalPlane plane)
        {
            string file;
            if (!PathFiles.TryGetValue(name, out file)) return null;
            List<CRecPathPt> points = ReadRec(file);
            if (points.Count < 2) return null;
            var coordinates = new JArray();
            var headings = new JArray();
            var speeds = new JArray();
            var autos = new JArray();
            foreach (CRecPathPt p in points)
            {
                coordinates.Add(LonLat(plane, p.easting, p.northing));
                headings.Add(p.heading);
                speeds.Add(p.speed);
                autos.Add(p.autoBtnState);
            }
            return new JObject
            {
                ["name"] = name,
                ["geometry"] = Geometry("LineString", coordinates),
                ["heading_rad"] = headings,
                ["speed_kmh"] = speeds,
                ["auto_steer"] = autos
            };
        }

        // A named recorded path: the same layout as RecPath.txt.
        private static List<CRecPathPt> ReadRec(string file)
        {
            var list = new List<CRecPathPt>();
            string[] lines = File.ReadAllLines(file);
            int start = lines.Length > 0 && lines[0].TrimStart().StartsWith("$", StringComparison.Ordinal) ? 1 : 0;
            int count;
            if (lines.Length <= start || !int.TryParse(lines[start].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) return list;
            for (int i = start + 1; i < lines.Length && list.Count < count; i++)
            {
                string[] w = lines[i].Split(',');
                if (w.Length < 5) continue;
                double e, n, h, s;
                bool auto;
                if (double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e)
                    && double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                    && double.TryParse(w[2], NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                    && double.TryParse(w[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s)
                    && bool.TryParse(w[4].Trim(), out auto))
                {
                    list.Add(new CRecPathPt(e, n, h, s, auto));
                }
            }
            return list;
        }

        #endregion

        #region Copying items into another folder

        /// <summary>
        /// Copy these items from this snapshot's folder into another field folder
        /// with the same origin (a job folder's edits into its master field). A key
        /// this snapshot doesn't have removes the item there.
        /// </summary>
        public void CopyItemsTo(string target, IEnumerable<string> keys)
        {
            var lineKeys = new List<string>();
            foreach (string key in keys)
            {
                if (key.StartsWith(LinePrefix, StringComparison.Ordinal))
                {
                    lineKeys.Add(key.Substring(LinePrefix.Length));
                }
                else if (key.StartsWith(PathPrefix, StringComparison.Ordinal))
                {
                    string file = key.Substring(PathPrefix.Length) + ".rec";
                    CopyOrRemove(file, target, PathFiles.ContainsKey(key.Substring(PathPrefix.Length)));
                }
                else if (key == BoundaryKey)
                {
                    // The headland goes with the boundary it was made on.
                    CopyOrRemove("Boundary.txt", target, true);
                    CopyOrRemove("Headland.txt", target, true);
                }
                else
                {
                    string file = FileOf(key);
                    if (file != null) CopyOrRemove(file, target, true);
                }
            }
            if (lineKeys.Count == 0) return;

            // Lines are kept one by one: change, add or remove just these in the target.
            List<CTrk> targetTracks = TrackFiles.Load(target);
            var byName = new Dictionary<string, CTrk>(StringComparer.Ordinal);
            var order = new List<CTrk>();
            foreach (CTrk t in targetTracks)
            {
                if (KindOf(t.mode) != null)
                {
                    string unique = UniqueName(byName, string.IsNullOrWhiteSpace(t.name) ? "Line" : t.name.Trim());
                    t.name = unique;
                    byName[unique] = t;
                }
                order.Add(t);
            }
            foreach (string name in lineKeys)
            {
                CTrk mine;
                CTrk theirs;
                byName.TryGetValue(name, out theirs);
                if (Lines.TryGetValue(name, out mine))
                {
                    var copy = new CTrk(mine) { name = name };
                    if (theirs != null) order[order.IndexOf(theirs)] = copy;
                    else order.Add(copy);
                    byName[name] = copy;
                }
                else if (theirs != null)
                {
                    order.Remove(theirs);
                    byName.Remove(name);
                }
            }
            TrackFiles.Save(target, order);
        }

        private void CopyOrRemove(string file, string target, bool present)
        {
            string from = Path.Combine(Folder, file);
            string to = Path.Combine(target, file);
            if (present && File.Exists(from)) File.Copy(from, to, true);
            else if (!present && File.Exists(to)) File.Delete(to);
        }

        #endregion
    }
}
