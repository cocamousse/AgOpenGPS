using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// What the tablet remembers about a field folder it wrote from the hub. Kept
    /// as agopen.json inside the field folder.
    /// </summary>
    public class HubFieldState
    {
        public const string FileName = "agopen.json";

        [JsonProperty("field_id")]
        public string FieldId { get; set; } = "";

        /// <summary>Fingerprint of the hub's version last written here.</summary>
        [JsonProperty("hub_hash")]
        public string HubHash { get; set; } = "";

        /// <summary>The origin used for this folder's local coordinates: [latitude, longitude].</summary>
        [JsonProperty("origin")]
        public double[] Origin { get; set; }

        [JsonProperty("synced_at")]
        public string SyncedAt { get; set; } = "";

        /// <summary>Files written from the hub, with their fingerprints as written.</summary>
        [JsonProperty("files")]
        public Dictionary<string, string> Files { get; set; } = new Dictionary<string, string>();

        public static HubFieldState Load(string folder)
        {
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<HubFieldState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Unreadable " + path + ": " + ex.Message);
                return null;
            }
        }

        public void Save(string folder)
        {
            HubFiles.WriteAllTextSafely(Path.Combine(folder, FileName), JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// True if a file written from the hub has been changed or removed on this
        /// tablet since (an edit that hasn't gone to the hub yet).
        /// </summary>
        public bool HasLocalChanges(string folder)
        {
            foreach (KeyValuePair<string, string> file in Files)
            {
                string path = Path.Combine(folder, file.Key);
                if (!File.Exists(path) || HubFieldWriter.FileHash(path) != file.Value) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Writes a field from the hub (lon/lat) as an AgOpenGPS field folder (local
    /// meters around the field's origin), using AgOpenGPS's own file writers so
    /// the files are exactly what AgOpenGPS expects.
    /// </summary>
    public static class HubFieldWriter
    {
        /// <summary>
        /// Write one hub field into folder. Returns the new state, or null if the
        /// field has no shape at all to place it by.
        /// </summary>
        public static HubFieldState Write(JObject field, string folder, HubFieldState previous, string hubHash, string tempRoot)
        {
            Wgs84? origin = ChooseOrigin(field, previous);
            if (origin == null) return null;
            var plane = new LocalPlane(origin.Value, new SharedFieldProperties());

            List<CBoundaryList> boundaries = Boundaries(field["boundary"], plane);
            bool hasHeadland = AttachHeadland(field["headland"], boundaries, plane);
            List<CTrk> tracks = Tracks(field["lines"] as JArray, plane);
            List<CFlag> flags = Flags(field["flags"] as JArray, plane);

            // Write everything into a scratch folder first, then copy it over, so the
            // field folder is never left half written.
            string temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                FieldPlaneFiles.Save(temp, DateTime.Now, origin.Value);
                BoundaryFiles.Save(temp, boundaries);
                TrackFiles.Save(temp, tracks);
                FlagsFiles.Save(temp, flags);
                if (hasHeadland) HeadlandFiles.Save(temp, boundaries);
                WritePaths(field["paths"] as JArray, plane, temp);

                Directory.CreateDirectory(folder);
                var state = new HubFieldState
                {
                    FieldId = (string)field["id"] ?? "",
                    HubHash = hubHash,
                    Origin = new[] { origin.Value.Latitude, origin.Value.Longitude },
                    SyncedAt = DateTime.UtcNow.ToString("o")
                };
                foreach (string file in Directory.GetFiles(temp))
                {
                    string name = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(folder, name), true);
                    state.Files[name] = FileHash(file);
                }

                // Remove files this sync wrote before that the hub no longer has
                // (e.g. a recorded path deleted on another tablet).
                if (previous != null)
                {
                    foreach (string oldFile in previous.Files.Keys)
                    {
                        if (state.Files.ContainsKey(oldFile)) continue;
                        string path = Path.Combine(folder, oldFile);
                        if (File.Exists(path)) File.Delete(path);
                    }
                }

                // Files AgOpenGPS keeps for itself: create them if missing, never overwrite.
                if (!File.Exists(Path.Combine(folder, "Sections.txt"))) SectionsFiles.CreateEmpty(folder);
                if (!File.Exists(Path.Combine(folder, "Contour.txt"))) ContourFiles.CreateFile(folder);
                if (!File.Exists(Path.Combine(folder, "RecPath.txt"))) RecPathFiles.CreateEmpty(folder);
                if (!File.Exists(Path.Combine(folder, "Headland.txt"))) HeadlandFiles.Save(folder, new List<CBoundaryList>());

                state.Save(folder);
                return state;
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (Exception ex)
                {
                    Log.EventWriter("[AgOpen] Couldn't remove scratch folder: " + ex.Message);
                }
            }
        }

        public static string FileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }

        public static string TextHash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "");
            }
        }

        /// <summary>A name that is safe for a Windows folder or file.</summary>
        public static string SafeName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            string safe = sb.ToString().TrimEnd('.', ' ');
            return safe.Length == 0 ? "Field" : safe;
        }

        // Keep the origin a folder was first written with, so its coordinates never
        // shift. Otherwise use the hub's, or the first point of the field's shape.
        private static Wgs84? ChooseOrigin(JObject field, HubFieldState previous)
        {
            if (previous != null && previous.Origin != null && previous.Origin.Length == 2)
            {
                var kept = new Wgs84(previous.Origin[0], previous.Origin[1]);
                if (kept.IsValid) return kept;
            }

            List<Wgs84> origin = Positions(field["origin"] is JArray ? new JArray(field["origin"]) : null);
            if (origin.Count > 0) return origin[0];

            JToken boundary = field["boundary"];
            if (boundary != null && boundary.Type == JTokenType.Object)
            {
                JToken coords = boundary["coordinates"];
                string type = (string)boundary["type"];
                JToken firstRing = type == "MultiPolygon" ? First(First(coords)) : First(coords);
                List<Wgs84> ring = Positions(firstRing);
                if (ring.Count > 0) return ring[0];
            }

            JArray lines = field["lines"] as JArray;
            if (lines != null)
            {
                foreach (JToken line in lines)
                {
                    List<Wgs84> points = Positions(Coordinates(line["geometry"]));
                    if (points.Count > 0) return points[0];
                }
            }
            return null;
        }

        private static JToken First(JToken token)
        {
            JArray array = token as JArray;
            return array != null && array.Count > 0 ? array[0] : null;
        }

        private static JToken Coordinates(JToken geometry)
        {
            return geometry != null && geometry.Type == JTokenType.Object ? geometry["coordinates"] : null;
        }

        // [[lon, lat], ...] as positions (bad points are skipped).
        private static List<Wgs84> Positions(JToken coordinates)
        {
            var list = new List<Wgs84>();
            JArray array = coordinates as JArray;
            if (array == null) return list;
            foreach (JToken item in array)
            {
                JArray point = item as JArray;
                if (point == null || point.Count < 2) continue;
                try
                {
                    var position = new Wgs84((double)point[1], (double)point[0]);
                    if (position.IsValid) list.Add(position);
                }
                catch (Exception)
                {
                    // not a number: skip the point
                }
            }
            return list;
        }

        private static List<vec3> ToLocal(List<Wgs84> positions, LocalPlane plane)
        {
            var list = new List<vec3>(positions.Count);
            foreach (Wgs84 position in positions)
            {
                GeoCoord local = plane.ConvertWgs84ToGeoCoord(position);
                list.Add(new vec3(local.Easting, local.Northing, 0));
            }
            return list;
        }

        private static double Heading(vec3 from, vec3 to)
        {
            return new GeoDir(from.ToGeoCoord(), to.ToGeoCoord()).AngleInRadians;
        }

        // Each point gets the heading towards the next one (the last keeps the one
        // before, or for a closed loop points back to the first).
        private static List<vec3> WithHeadings(List<vec3> points, bool closed)
        {
            var list = new List<vec3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                double heading;
                if (points.Count < 2) heading = 0;
                else if (i < points.Count - 1) heading = Heading(points[i], points[i + 1]);
                else if (closed) heading = Heading(points[i], points[0]);
                else heading = Heading(points[i - 1], points[i]);
                list.Add(new vec3(points[i].easting, points[i].northing, heading));
            }
            return list;
        }

        private static double SignedArea(List<vec3> points)
        {
            double area = 0;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                area += (points[j].easting + points[i].easting) * (points[j].northing - points[i].northing);
            }
            return area / 2;
        }

        private static List<vec3> OpenRing(List<vec3> ring)
        {
            if (ring.Count > 1)
            {
                vec3 first = ring[0];
                vec3 last = ring[ring.Count - 1];
                if (Math.Abs(first.easting - last.easting) < 0.001 && Math.Abs(first.northing - last.northing) < 0.001)
                {
                    ring.RemoveAt(ring.Count - 1);
                }
            }
            return ring;
        }

        // A GeoJSON Polygon or MultiPolygon as AgOpenGPS boundaries: the outer
        // boundary first, then its holes. AgOpenGPS has one outer boundary per
        // field, so of a MultiPolygon only the biggest part is used.
        private static List<CBoundaryList> Boundaries(JToken geometry, LocalPlane plane)
        {
            var result = new List<CBoundaryList>();
            if (geometry == null || geometry.Type != JTokenType.Object) return result;

            string type = (string)geometry["type"];
            JArray polygon = null;
            if (type == "Polygon")
            {
                polygon = geometry["coordinates"] as JArray;
            }
            else if (type == "MultiPolygon")
            {
                double biggest = -1;
                JArray parts = geometry["coordinates"] as JArray;
                if (parts != null)
                {
                    foreach (JToken part in parts)
                    {
                        JArray rings = part as JArray;
                        if (rings == null || rings.Count == 0) continue;
                        double area = Math.Abs(SignedArea(ToLocal(Positions(rings[0]), plane)));
                        if (area > biggest)
                        {
                            biggest = area;
                            polygon = rings;
                        }
                    }
                    if (parts.Count > 1) Log.EventWriter("[AgOpen] Field has several separate parts; only the biggest is used.");
                }
            }
            if (polygon == null) return result;

            foreach (JToken ring in polygon)
            {
                List<vec3> points = OpenRing(ToLocal(Positions(ring), plane));
                if (points.Count < 3)
                {
                    if (result.Count == 0) return result; // no usable outer boundary
                    continue;
                }
                var boundary = new CBoundaryList();
                boundary.fenceLine = points;
                boundary.isDriveThru = false;
                int index = result.Count;
                boundary.CalculateFenceArea(index, cleanIntersections: false);
                boundary.FixFenceLine(index);
                result.Add(boundary);
            }
            return result;
        }

        // The headland: one line per boundary, in the same order.
        private static bool AttachHeadland(JToken geometry, List<CBoundaryList> boundaries, LocalPlane plane)
        {
            if (geometry == null || geometry.Type != JTokenType.Object || boundaries.Count == 0) return false;

            var lines = new List<JToken>();
            string type = (string)geometry["type"];
            JArray coordinates = geometry["coordinates"] as JArray;
            if (coordinates == null) return false;
            if (type == "LineString") lines.Add(coordinates);
            else if (type == "MultiLineString") lines.AddRange(coordinates);
            else if (type == "Polygon") lines.AddRange(coordinates);
            else if (type == "MultiPolygon")
            {
                foreach (JToken part in coordinates)
                {
                    JArray rings = part as JArray;
                    if (rings != null) lines.AddRange(rings);
                }
            }

            bool any = false;
            for (int i = 0; i < boundaries.Count && i < lines.Count; i++)
            {
                List<vec3> points = OpenRing(ToLocal(Positions(lines[i]), plane));
                if (points.Count < 3) continue;
                boundaries[i].hdLine = WithHeadings(points, true);
                any = true;
            }
            return any && boundaries[0].hdLine.Count > 0;
        }

        private static List<CTrk> Tracks(JArray lines, LocalPlane plane)
        {
            var tracks = new List<CTrk>();
            if (lines == null) return tracks;
            foreach (JToken line in lines)
            {
                List<vec3> points = ToLocal(Positions(Coordinates(line["geometry"])), plane);
                if (points.Count < 2) continue;

                var track = new CTrk();
                track.name = (string)line["name"] ?? "Unnamed";
                track.isVisible = true;
                track.nudgeDistance = line["nudge_m"] != null && line["nudge_m"].Type != JTokenType.Null ? (double)line["nudge_m"] : 0;
                vec3 a = points[0];
                vec3 b = points[points.Count - 1];
                track.ptA = new vec2(a.easting, a.northing);
                track.ptB = new vec2(b.easting, b.northing);
                bool hasHeading = line["heading"] != null && line["heading"].Type != JTokenType.Null;
                track.heading = hasHeading ? (double)line["heading"] : Heading(a, b);

                if ((string)line["kind"] == "Curve")
                {
                    track.mode = TrackMode.Curve;
                    track.curvePts = WithHeadings(points, false);
                }
                else
                {
                    track.mode = TrackMode.AB;
                    track.curvePts = new List<vec3>();
                }
                tracks.Add(track);
            }
            return tracks;
        }

        private static int FlagColor(string color)
        {
            if (color == "green") return 1;
            if (color == "yellow") return 2;
            return 0;
        }

        private static List<CFlag> Flags(JArray flags, LocalPlane plane)
        {
            var list = new List<CFlag>();
            if (flags == null) return list;
            int number = 0;
            foreach (JToken flag in flags)
            {
                number++;
                List<Wgs84> positions = Positions(flag["position"] is JArray ? new JArray(flag["position"]) : null);
                if (positions.Count == 0) continue;
                Wgs84 position = positions[0];
                GeoCoord local = plane.ConvertWgs84ToGeoCoord(position);
                double heading = flag["heading_rad"] != null && flag["heading_rad"].Type != JTokenType.Null ? (double)flag["heading_rad"] : 0;
                int id = flag["number"] != null && flag["number"].Type == JTokenType.Integer ? (int)flag["number"] : number;
                // Flags.txt is comma separated: keep notes on one line, without commas.
                string notes = ((string)flag["notes"] ?? "").Replace(",", ";").Replace("\r", " ").Replace("\n", " ");
                list.Add(new CFlag(position.Latitude, position.Longitude, local.Easting, local.Northing, heading,
                    FlagColor((string)flag["color"]), id, notes));
            }
            return list;
        }

        // Each recorded path as a named .rec file, as AgOpenGPS saves them.
        private static void WritePaths(JArray paths, LocalPlane plane, string folder)
        {
            if (paths == null) return;
            foreach (JToken path in paths)
            {
                List<vec3> points = ToLocal(Positions(Coordinates(path["geometry"])), plane);
                if (points.Count < 2) continue;
                List<vec3> withHeadings = WithHeadings(points, false);
                JArray headings = path["heading_rad"] as JArray;
                JArray speeds = path["speed_kmh"] as JArray;
                JArray autos = path["auto_steer"] as JArray;
                bool sameLength = true;
                foreach (JArray list in new[] { headings, speeds, autos })
                {
                    if (list != null && list.Count != points.Count) sameLength = false;
                }

                var recorded = new List<CRecPathPt>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    double heading = sameLength && headings != null ? (double)headings[i] : withHeadings[i].heading;
                    double speed = sameLength && speeds != null ? (double)speeds[i] : 8.0;
                    bool auto = sameLength && autos != null && (bool)autos[i];
                    recorded.Add(new CRecPathPt(points[i].easting, points[i].northing, heading, speed, auto));
                }
                RecPathFiles.Save(folder, recorded, SafeName((string)path["name"]) + ".rec");
            }
        }
    }
}
