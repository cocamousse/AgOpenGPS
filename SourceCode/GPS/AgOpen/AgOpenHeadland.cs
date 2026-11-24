using System;
using System.Collections.Generic;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// A headland made the way AgOpenGPS's headland screen (FormHeadLine) makes one
    /// at a set distance from the boundary: its Build button (btnBndLoop_Click), then
    /// the points it keeps when the screen is closed (btnExit_Click). So the result
    /// is the same as setting that distance on the headland screen by hand.
    /// Used by "Would you like to create headlands?" after the starting paths, with
    /// the distance set to a number of implement widths.
    /// </summary>
    public static class AgOpenHeadland
    {
        /// <summary>
        /// The headland line this far inside the outer boundary (AgOpenGPS's fence
        /// line, in the field's metres), or null when the field is too small for it.
        /// </summary>
        public static List<vec3> Make(IList<vec3> fence, double distance)
        {
            if (fence == null || fence.Count < 4 || distance <= 0) return null;

            // Build: every boundary point moved in by the distance, leaving out the
            // ones that land closer than that to the boundary (inside corners), and
            // any less than 1 m from the point before.
            int count = fence.Count;
            double distSq = distance * distance * 0.999;
            var line = new List<vec3>();
            for (int i = 0; i < count; i++)
            {
                var point = new vec3(
                    fence[i].easting - (Math.Sin(glm.PIBy2 + fence[i].heading) * distance),
                    fence[i].northing - (Math.Cos(glm.PIBy2 + fence[i].heading) * distance),
                    fence[i].heading);

                bool add = true;
                for (int j = 0; j < count; j++)
                {
                    if (glm.DistanceSquared(point.northing, point.easting, fence[j].northing, fence[j].easting) < distSq)
                    {
                        add = false;
                        break;
                    }
                }
                if (!add) continue;

                if (line.Count > 0)
                {
                    vec3 last = line[line.Count - 1];
                    double gap = ((point.easting - last.easting) * (point.easting - last.easting))
                        + ((point.northing - last.northing) * (point.northing - last.northing));
                    if (gap > 1) line.Add(point);
                }
                else
                {
                    line.Add(point);
                }
            }
            if (line.Count == 0) return null;

            // Closed back to its first point, as the headland screen does it.
            line.Add(new vec3(line[0]));
            if (line.Count <= 3) return null;
            line.Add(new vec3(line[0]));

            CABCurve.MakePointMinimumSpacing(ref line, 1.2);
            CABCurve.CalculateHeadings(ref line);

            // Closing the headland screen keeps the first and last points and those
            // where the line has turned a little since the last one kept.
            vec3[] all = line.ToArray();
            for (int i = 1; i < all.Length; i++)
            {
                all[i - 1].heading = Math.Atan2(all[i - 1].easting - all[i].easting, all[i - 1].northing - all[i].northing);
                if (all[i].heading < 0) all[i].heading += glm.twoPI;
            }

            var kept = new List<vec3>();
            double delta = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (i == 0)
                {
                    kept.Add(new vec3(all[i].easting, all[i].northing, all[i].heading));
                    continue;
                }
                delta += all[i - 1].heading - all[i].heading;
                if (Math.Abs(delta) > 0.005)
                {
                    kept.Add(new vec3(all[i].easting, all[i].northing, all[i].heading));
                    delta = 0;
                }
            }
            vec3 end = all[all.Length - 1];
            kept.Add(new vec3(end.easting, end.northing, end.heading));
            return kept;
        }
    }
}
