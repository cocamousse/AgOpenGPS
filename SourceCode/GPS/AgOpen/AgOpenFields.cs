using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;

namespace AgOpenGPS.AgOpen
{
    /// <summary>One field folder on this tablet.</summary>
    public class AgOpenFieldEntry
    {
        /// <summary>The folder name, which is what AgOpenGPS calls the field.</summary>
        public string Folder = "";

        /// <summary>The full path of the folder.</summary>
        public string FieldDirectory = "";

        /// <summary>From the tractor to the field's edge in metres (0 inside), when there's a GPS fix.</summary>
        public double? DistanceM;

        public string FieldFile
        {
            get { return Path.Combine(FieldDirectory, "Field.txt"); }
        }
    }

    /// <summary>The outline and lines of a field, in the field's own metres (easting, northing).</summary>
    public class AgOpenFieldShape
    {
        /// <summary>The outer boundary first, then any inner ones (holes).</summary>
        public List<List<vec3>> Fences = new List<List<vec3>>();

        public List<CTrk> Tracks = new List<CTrk>();

        /// <summary>Outer boundary minus the inner ones, in square metres.</summary>
        public double AreaM2;

        /// <summary>Where the field's metres are measured from (Field.txt). Null if it can't be read.</summary>
        public Wgs84? Origin;
    }

    /// <summary>
    /// Reads the field folders for the AgOpen pop-ups. Shapes are kept in memory
    /// and read again only when their files change.
    /// </summary>
    public static class AgOpenFields
    {
        private class CachedShape
        {
            public long Stamp;
            public AgOpenFieldShape Shape;
        }

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CachedShape> Cache =
            new Dictionary<string, CachedShape>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every master field: each folder in the fields folder that has a Field.txt
        /// and isn't a job folder (a job is a copy of its master). With a position,
        /// each one also gets its distance from there.
        /// </summary>
        public static List<AgOpenFieldEntry> List(string fieldsDirectory, Wgs84? here)
        {
            var list = new List<AgOpenFieldEntry>();
            if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory)) return list;

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(fieldsDirectory);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Can't list the fields folder: " + ex.Message);
                return list;
            }

            foreach (string folder in folders)
            {
                if (!File.Exists(Path.Combine(folder, "Field.txt")) || AgOpenJobs.IsJobFolder(folder)) continue;
                var entry = new AgOpenFieldEntry
                {
                    Folder = Path.GetFileName(folder),
                    FieldDirectory = folder
                };
                if (here.HasValue)
                {
                    entry.DistanceM = DistanceFrom(Shape(entry), here.Value);
                }
                list.Add(entry);
            }
            return list;
        }

        /// <summary>How many master fields there are (quick: doesn't read them).</summary>
        public static int Count(string fieldsDirectory)
        {
            if (string.IsNullOrEmpty(fieldsDirectory) || !Directory.Exists(fieldsDirectory)) return 0;
            try
            {
                int count = 0;
                foreach (string folder in Directory.GetDirectories(fieldsDirectory))
                {
                    if (File.Exists(Path.Combine(folder, "Field.txt")) && !AgOpenJobs.IsJobFolder(folder)) count++;
                }
                return count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The field's outline and lines. Never null; empty if they can't be read.</summary>
        public static AgOpenFieldShape Shape(AgOpenFieldEntry entry)
        {
            string folder = entry.FieldDirectory;
            long stamp = Stamp(folder);
            lock (CacheLock)
            {
                CachedShape cached;
                if (Cache.TryGetValue(folder, out cached) && cached.Stamp == stamp) return cached.Shape;
            }

            AgOpenFieldShape shape = ReadShape(folder);
            lock (CacheLock)
            {
                Cache[folder] = new CachedShape { Stamp = stamp, Shape = shape };
            }
            return shape;
        }

        private static AgOpenFieldShape ReadShape(string folder)
        {
            var shape = new AgOpenFieldShape();
            try
            {
                shape.Origin = FieldPlaneFiles.LoadOrigin(folder);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Field.txt in '" + Path.GetFileName(folder) + "' can't be read: " + ex.Message);
            }

            try
            {
                List<CBoundaryList> boundaries = BoundaryFiles.Load(folder);
                // The first one is the outer boundary; without it the inner ones mean nothing.
                if (boundaries.Count > 0 && boundaries[0].fenceLine.Count >= 3)
                {
                    for (int i = 0; i < boundaries.Count; i++)
                    {
                        if (boundaries[i].fenceLine.Count < 3) continue;
                        shape.Fences.Add(boundaries[i].fenceLine);
                        shape.AreaM2 += i == 0 ? boundaries[i].area : -boundaries[i].area;
                    }
                    if (shape.AreaM2 < 0) shape.AreaM2 = 0;
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Boundary.txt in '" + Path.GetFileName(folder) + "' can't be read: " + ex.Message);
            }

            try
            {
                shape.Tracks = TrackFiles.Load(folder);
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] TrackLines.txt in '" + Path.GetFileName(folder) + "' can't be read: " + ex.Message);
            }
            return shape;
        }

        // Changes whenever one of the files the shape comes from changes.
        private static long Stamp(string folder)
        {
            long stamp = 0;
            foreach (string name in new[] { "Field.txt", "Boundary.txt", "TrackLines.txt" })
            {
                try
                {
                    var info = new FileInfo(Path.Combine(folder, name));
                    if (info.Exists) stamp = unchecked(stamp * 31 + info.LastWriteTimeUtc.Ticks + info.Length);
                }
                catch (Exception)
                {
                    // Treat an unreadable file as missing.
                }
            }
            return stamp;
        }

        /// <summary>A position in the field's own metres, or null if the field has no origin.</summary>
        public static GeoCoord? ToField(AgOpenFieldShape shape, Wgs84 position)
        {
            if (shape == null || !shape.Origin.HasValue) return null;
            var plane = new LocalPlane(shape.Origin.Value, new SharedFieldProperties());
            return plane.ConvertWgs84ToGeoCoord(position);
        }

        /// <summary>
        /// Metres from a position to the field's edge: 0 inside the field. Without a
        /// boundary, the distance to where the field starts (Field.txt). Null if neither is known.
        /// </summary>
        public static double? DistanceFrom(AgOpenFieldShape shape, Wgs84 position)
        {
            if (shape == null || !shape.Origin.HasValue) return null;
            if (shape.Fences.Count == 0) return shape.Origin.Value.DistanceInMeters(position);
            GeoCoord? local = ToField(shape, position);
            if (!local.HasValue) return null;
            return DistanceToEdge(shape, local.Value.Easting, local.Value.Northing);
        }

        /// <summary>Metres from a point (field metres) to the field's edge: 0 inside the field.</summary>
        public static double DistanceToEdge(AgOpenFieldShape shape, double east, double north)
        {
            List<vec3> outer = shape.Fences[0];
            if (Inside(outer, east, north))
            {
                for (int i = 1; i < shape.Fences.Count; i++)
                {
                    if (Inside(shape.Fences[i], east, north)) return DistanceToRing(shape.Fences[i], east, north);
                }
                return 0;
            }
            return DistanceToRing(outer, east, north);
        }

        /// <summary>Is the point inside the ring? (Even-odd rule.)</summary>
        public static bool Inside(List<vec3> ring, double east, double north)
        {
            bool inside = false;
            int count = ring.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double ni = ring[i].northing, nj = ring[j].northing;
                if ((ni > north) != (nj > north))
                {
                    double crossing = ring[j].easting + (north - nj) / (ni - nj) * (ring[i].easting - ring[j].easting);
                    if (east < crossing) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Metres from the point to the nearest side of the ring.</summary>
        public static double DistanceToRing(List<vec3> ring, double east, double north)
        {
            double best = double.MaxValue;
            int count = ring.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double ax = ring[j].easting, ay = ring[j].northing;
                double dx = ring[i].easting - ax, dy = ring[i].northing - ay;
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared > 0 ? ((east - ax) * dx + (north - ay) * dy) / lengthSquared : 0;
                t = Math.Max(0, Math.Min(1, t));
                double px = ax + t * dx - east, py = ay + t * dy - north;
                double distanceSquared = px * px + py * py;
                if (distanceSquared < best) best = distanceSquared;
            }
            return best == double.MaxValue ? double.MaxValue : Math.Sqrt(best);
        }

        /// <summary>"80.3 ac" or "32.5 ha".</summary>
        public static string Area(double squareMetres, bool metric)
        {
            return metric
                ? (squareMetres * glm.m2ha).ToString("N1", CultureInfo.CurrentCulture) + " ha"
                : (squareMetres * glm.m2ac).ToString("N1", CultureInfo.CurrentCulture) + " ac";
        }

        /// <summary>"350 ft", "1.2 mi", "90 m" or "2.4 km".</summary>
        public static string Distance(double metres, bool metric)
        {
            if (metric)
            {
                if (metres < 1000) return Math.Round(metres).ToString("N0", CultureInfo.CurrentCulture) + " m";
                return (metres / 1000.0).ToString("N1", CultureInfo.CurrentCulture) + " km";
            }
            if (metres < 160.9)
            {
                double feet = Math.Round(metres * 3.28084 / 10.0) * 10.0;
                return feet.ToString("N0", CultureInfo.CurrentCulture) + " ft";
            }
            return (metres / 1609.344).ToString("N1", CultureInfo.CurrentCulture) + " mi";
        }
    }
}
