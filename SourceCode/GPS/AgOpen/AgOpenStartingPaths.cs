using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// "Starting paths" for a field that has a boundary but no lines yet:
    /// - a path round the border: the outer boundary as a boundary curve, made the
    ///   way the draw screen's boundary curve button makes one (FormABDraw), and
    /// - a curve along the field's longest side, made the way the draw screen makes
    ///   a curve between two points tapped on the boundary.
    /// So both behave exactly like lines drawn by hand.
    ///
    /// Sides are found from the corners. A corner is where the boundary turns more
    /// than CornerDegrees, measured over CornerReachM of boundary on each side, so a
    /// rounded or wobbly corner counts once and a gentle bend stays part of its side.
    /// The curve leaves out the corners themselves; like every AgOpenGPS curve it
    /// runs on straight past both ends. A round field has no corners, so it gets the
    /// border path only.
    ///
    /// Both lines sit on the boundary itself. AgOpenGPS 6.8 never guides along a
    /// line: every pass is half a pass width (or 1 1/2, 2 1/2...) off it (CABCurve,
    /// CABLine: distAway += 0.5 * widthMinusOverlap). So the first pass along either
    /// line runs the implement's edge along the boundary, and the passes inside a
    /// headland made a whole number of passes wide line up with the headland's edge.
    /// </summary>
    public static class AgOpenStartingPaths
    {
        /// <summary>A turn sharper than this is a corner.</summary>
        public const double CornerDegrees = 35.0;

        /// <summary>How much boundary either side of a point the turn is measured over.</summary>
        public const double CornerReachM = 15.0;

        /// <summary>The shortest stretch of side (corners left out) worth a curve.</summary>
        public const double ShortestSideM = 20.0;

        /// <summary>Boundary points further apart than this get points in between first.</summary>
        private const double LongestGapM = 6.0;

        public const string BorderName = "Border path";
        public const string SideName = "Long side";

        /// <summary>The starting paths for one field, worked out but not added yet.</summary>
        public sealed class Plan
        {
            /// <summary>The path round the border (a boundary curve).</summary>
            public CTrk Border;

            /// <summary>The curve along the longest side; null when there's no clear side (a round field).</summary>
            public CTrk Side;

            /// <summary>The stretch of boundary the side curve follows, for the drawing.</summary>
            public List<vec3> SidePoints = new List<vec3>();

            /// <summary>The longest side, corner to corner, in metres.</summary>
            public double SideLengthM;
        }

        /// <summary>A side of the boundary: Count points from Start (going round), corners left out.</summary>
        public sealed class Side
        {
            public int Start;
            public int Count;

            /// <summary>Corner to corner, in metres (used to pick the longest).</summary>
            public double LengthM;
        }

        /// <summary>
        /// Work out both paths for an outer boundary (AgOpenGPS's fence line, in the
        /// field's metres). addTails runs the curve's tail extensions, as
        /// CABCurve.AddFirstLastPoints does for the draw screen. Null if the boundary
        /// is too small.
        /// </summary>
        public static Plan Make(IList<vec3> outerFence, Action<List<vec3>> addTails)
        {
            CTrk border = BorderPath(outerFence);
            if (border == null) return null;

            var plan = new Plan { Border = border };
            List<vec3> ring = Densify(outerFence);
            Side side = LongestSide(ring);
            if (side != null)
            {
                plan.SidePoints = SidePoints(ring, side);
                plan.SideLengthM = side.LengthM;
                plan.Side = SideCurve(plan.SidePoints, addTails);
            }
            return plan;
        }

        /// <summary>
        /// The path round the border, as the draw screen's boundary curve button
        /// makes it: every boundary point, A at the first and B at the second to last.
        /// </summary>
        public static CTrk BorderPath(IList<vec3> fence)
        {
            if (fence == null || fence.Count < 4) return null;

            var track = new CTrk();
            track.ptA = new vec2(fence[0].easting, fence[0].northing);
            track.ptB = new vec2(fence[fence.Count - 2].easting, fence[fence.Count - 2].northing);
            track.name = BorderName;
            track.heading = 0;
            track.mode = TrackMode.bndCurve;
            foreach (vec3 point in fence) track.curvePts.Add(new vec3(point));
            return track;
        }

        /// <summary>
        /// A curve along these boundary points, as the draw screen makes one between
        /// two tapped points: points no more than 1.6 m apart, headings, the average
        /// heading for the name, and the tail extensions. Null if there are too few points.
        /// </summary>
        public static CTrk SideCurve(List<vec3> sidePoints, Action<List<vec3>> addTails)
        {
            if (sidePoints == null || sidePoints.Count <= 3) return null;

            var points = new List<vec3>(sidePoints.Count);
            foreach (vec3 point in sidePoints) points.Add(new vec3(point));

            var track = new CTrk();
            track.ptA = new vec2(points[0].easting, points[0].northing);
            track.ptB = new vec2(points[points.Count - 1].easting, points[points.Count - 1].northing);

            CABCurve.MakePointMinimumSpacing(ref points, 1.6);
            CABCurve.CalculateHeadings(ref points);

            // The average heading of the line.
            double x = 0, y = 0;
            foreach (vec3 point in points)
            {
                x += Math.Cos(point.heading);
                y += Math.Sin(point.heading);
            }
            x /= points.Count;
            y /= points.Count;
            track.heading = Math.Atan2(y, x);
            if (track.heading < 0) track.heading += glm.twoPI;

            addTails?.Invoke(points);
            CABCurve.CalculateHeadings(ref points);

            double degrees = Math.Round(glm.toDegrees(track.heading), 1);
            if (degrees >= 360) degrees -= 360;
            track.name = SideName + " " + degrees.ToString(CultureInfo.InvariantCulture) + "°";
            track.mode = TrackMode.Curve;
            foreach (vec3 point in points) track.curvePts.Add(point);
            return track;
        }

        /// <summary>
        /// The longest side of a closed boundary, or null when it hasn't two corners
        /// (a round field) or no side is long enough.
        /// </summary>
        public static Side LongestSide(IList<vec3> ring)
        {
            int n = ring == null ? 0 : ring.Count;
            if (n < 8) return null;

            // Distance along the boundary to each point; along[n] is the whole way round.
            var along = new double[n + 1];
            for (int i = 0; i < n; i++) along[i + 1] = along[i] + Distance(ring[i], ring[(i + 1) % n]);
            double perimeter = along[n];
            if (perimeter < 4 * ShortestSideM) return null;

            // How sharply the boundary turns at each point, over a stretch either side.
            double reach = Math.Min(CornerReachM, perimeter / 8.0);
            double cornerTurn = CornerDegrees * Math.PI / 180.0;
            var turn = new double[n];
            var isCorner = new bool[n];
            int corners = 0;
            for (int i = 0; i < n; i++)
            {
                vec2 before = PointAt(ring, along, along[i] - reach);
                vec2 after = PointAt(ring, along, along[i] + reach);
                double headingIn = Math.Atan2(ring[i].easting - before.easting, ring[i].northing - before.northing);
                double headingOut = Math.Atan2(after.easting - ring[i].easting, after.northing - ring[i].northing);
                turn[i] = Math.Abs(AngleBetween(headingIn, headingOut));
                isCorner[i] = turn[i] > cornerTurn;
                if (isCorner[i]) corners++;
            }
            if (corners == 0 || corners == n) return null;

            // Group the corner points into corners: each run of them is one corner.
            int firstStraight = Array.IndexOf(isCorner, false);
            var runs = new List<Run>();
            Run current = null;
            for (int k = 0; k < n; k++)
            {
                int i = (firstStraight + k) % n;
                if (isCorner[i])
                {
                    if (current == null)
                    {
                        current = new Run { First = i, Last = i, Apex = i };
                        runs.Add(current);
                    }
                    current.Last = i;
                    if (turn[i] > turn[current.Apex]) current.Apex = i;
                }
                else
                {
                    current = null;
                }
            }
            if (runs.Count < 2) return null;

            // Each side runs from one corner to the next; the longest (corner to corner) wins.
            Side best = null;
            for (int r = 0; r < runs.Count; r++)
            {
                Run from = runs[r];
                Run to = runs[(r + 1) % runs.Count];
                int start = (from.Last + 1) % n;
                int count = (to.First - from.Last - 1 + n) % n;
                if (count < 4) continue;
                int end = (start + count - 1) % n;
                double stretch = Forward(along, perimeter, start, end);
                if (stretch < ShortestSideM) continue;
                double length = Forward(along, perimeter, from.Apex, to.Apex);
                if (best == null || length > best.LengthM)
                {
                    best = new Side { Start = start, Count = count, LengthM = length };
                }
            }
            return best;
        }

        /// <summary>The boundary points along a side, in order.</summary>
        public static List<vec3> SidePoints(IList<vec3> ring, Side side)
        {
            var points = new List<vec3>(side.Count);
            for (int k = 0; k < side.Count; k++) points.Add(new vec3(ring[(side.Start + k) % ring.Count]));
            return points;
        }

        /// <summary>
        /// A copy of the boundary with points added where two are more than
        /// LongestGapM apart (a boundary drawn with few points). AgOpenGPS's own
        /// boundaries are already closer than that, so they come back unchanged.
        /// </summary>
        public static List<vec3> Densify(IList<vec3> fence)
        {
            var ring = new List<vec3>(fence.Count);
            for (int i = 0; i < fence.Count; i++)
            {
                vec3 a = fence[i];
                vec3 b = fence[(i + 1) % fence.Count];
                ring.Add(new vec3(a));
                double gap = Distance(a, b);
                if (gap <= LongestGapM) continue;
                int steps = (int)Math.Ceiling(gap / 2.0);
                for (int s = 1; s < steps; s++)
                {
                    double t = (double)s / steps;
                    ring.Add(new vec3(a.easting + (b.easting - a.easting) * t, a.northing + (b.northing - a.northing) * t, a.heading));
                }
            }
            return ring;
        }

        private sealed class Run
        {
            public int First;
            public int Last;
            public int Apex;
        }

        private static double Distance(vec3 a, vec3 b)
        {
            double de = a.easting - b.easting, dn = a.northing - b.northing;
            return Math.Sqrt(de * de + dn * dn);
        }

        // Going round the boundary from point a to point b, in metres.
        private static double Forward(double[] along, double perimeter, int a, int b)
        {
            double d = along[b] - along[a];
            if (d < 0) d += perimeter;
            return d;
        }

        // The spot this far along the boundary (going round as often as needed).
        private static vec2 PointAt(IList<vec3> ring, double[] along, double distance)
        {
            int n = ring.Count;
            double perimeter = along[n];
            distance %= perimeter;
            if (distance < 0) distance += perimeter;

            int low = 0, high = n - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (along[mid] <= distance) low = mid;
                else high = mid - 1;
            }
            vec3 a = ring[low];
            vec3 b = ring[(low + 1) % n];
            double span = along[low + 1] - along[low];
            double t = span > 0 ? (distance - along[low]) / span : 0;
            return new vec2(a.easting + (b.easting - a.easting) * t, a.northing + (b.northing - a.northing) * t);
        }

        // The turn from one heading to another, -pi..pi.
        private static double AngleBetween(double from, double to)
        {
            double d = to - from;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d <= -Math.PI) d += 2 * Math.PI;
            return d;
        }
    }
}
