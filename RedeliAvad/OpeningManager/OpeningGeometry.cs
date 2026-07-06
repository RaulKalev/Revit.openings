using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RedeliAvad
{
    /// <summary>
    /// Geometry utilities shared by opening creation, validation and fixing.
    /// Kept intentionally simple (bounding boxes, centroids, direction vectors) so a more
    /// advanced implementation can be swapped in later without touching callers.
    /// </summary>
    internal static class OpeningGeometry
    {
        // ---------- solids / boxes ----------

        /// <summary>Largest solid of an element (instance geometry included), or null.</summary>
        public static Solid GetMainSolid(Element e)
        {
            if (e == null) return null;
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geo = null;
            try { geo = e.get_Geometry(opt); } catch { }
            if (geo == null) return null;

            Solid best = null;
            double maxV = 0.0;

            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Volume > maxV) { best = s; maxV = s.Volume; }
                else if (obj is GeometryInstance gi)
                {
                    GeometryElement sym = null;
                    try { sym = gi.GetInstanceGeometry(); } catch { }
                    if (sym == null) continue;
                    foreach (var g in sym)
                    {
                        if (g is Solid ss && ss.Volume > maxV) { best = ss; maxV = ss.Volume; }
                    }
                }
            }
            return best;
        }

        public static BoundingBoxXYZ ComputeBoundingBox(Solid s)
        {
            var bb = new BoundingBoxXYZ
            {
                Min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue),
                Max = new XYZ(double.MinValue, double.MinValue, double.MinValue)
            };

            foreach (Face f in s.Faces)
            {
                Mesh mesh = null;
                try { mesh = f.Triangulate(); } catch { }
                if (mesh == null) continue;
                var n = mesh.Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    var v = mesh.Vertices[i];
                    bb.Min = new XYZ(Math.Min(bb.Min.X, v.X), Math.Min(bb.Min.Y, v.Y), Math.Min(bb.Min.Z, v.Z));
                    bb.Max = new XYZ(Math.Max(bb.Max.X, v.X), Math.Max(bb.Max.Y, v.Y), Math.Max(bb.Max.Z, v.Z));
                }
            }
            return bb;
        }

        public static bool BboxIntersects(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        /// <summary>Axis-aligned box of the 8 transformed corners (for linked-model elements).</summary>
        public static BoundingBoxXYZ TransformBoundingBox(BoundingBoxXYZ bb, Transform t)
        {
            if (bb == null) return null;
            if (t == null || t.IsIdentity) return bb;

            var pts = new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                var q = t.OfPoint(p);
                minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y); minZ = Math.Min(minZ, q.Z);
                maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y); maxZ = Math.Max(maxZ, q.Z);
            }
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        /// <summary>Element bounding box in host-document coordinates (model box + optional link transform).</summary>
        public static BoundingBoxXYZ GetElementBox(Element e, Transform toHost)
        {
            if (e == null) return null;
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch { }
            if (bb == null) return null;
            return TransformBoundingBox(bb, toHost);
        }

        public static XYZ BoxCenter(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;
            return (bb.Min + bb.Max) * 0.5;
        }

        public static BoundingBoxXYZ UnionBox(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z))
            };
        }

        // ---------- element info ----------

        /// <summary>Location point of an element in host coordinates (LocationPoint, curve midpoint or bbox center).</summary>
        public static XYZ GetLocationPoint(Element e, Transform toHost)
        {
            if (e == null) return null;
            XYZ p = null;
            var lp = e.Location as LocationPoint;
            if (lp != null) p = lp.Point;
            else
            {
                var lc = e.Location as LocationCurve;
                if (lc != null && lc.Curve != null) p = lc.Curve.Evaluate(0.5, true);
            }
            if (p == null) p = BoxCenter(GetElementBox(e, null));
            if (p == null) return null;
            return (toHost == null || toHost.IsIdentity) ? p : toHost.OfPoint(p);
        }

        /// <summary>Normalized run direction of a curve-based element in host coordinates, or null.</summary>
        public static XYZ GetDirection(Element e, Transform toHost)
        {
            var lc = e?.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return null;
            XYZ dir;
            try { dir = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0); }
            catch { return null; }
            if (dir.GetLength() < 1e-9) return null;
            if (toHost != null && !toHost.IsIdentity) dir = toHost.OfVector(dir);
            return dir.Normalize();
        }

        /// <summary>True when the penetration is vertical (through floors/roofs) rather than horizontal (through walls).</summary>
        public static bool IsVerticalPenetration(XYZ sourceDirection, string hostCategory)
        {
            if (sourceDirection != null && sourceDirection.GetLength() > 1e-9)
                return Math.Abs(sourceDirection.Normalize().Z) > 0.7;

            if (!string.IsNullOrEmpty(hostCategory))
            {
                var c = hostCategory.ToLowerInvariant();
                if (c.Contains("floor") || c.Contains("roof") || c.Contains("ceiling") ||
                    c.Contains("põrand") || c.Contains("katus") || c.Contains("lagi"))
                    return true;
            }
            return false;
        }

        // ---------- penetration point ----------

        /// <summary>
        /// Expected penetration point = centroid of the boolean intersection of the source and host
        /// solids (both in host coordinates). Falls back to the midpoint of the source curve segment
        /// clipped by the host bounding box. Returns null when the elements no longer intersect.
        /// </summary>
        public static XYZ ComputeExpectedPenetrationPoint(Element source, Transform sourceToHost, Element host, Transform hostToHost)
        {
            var srcSolid = GetTransformedSolid(source, sourceToHost);
            var hostSolid = GetTransformedSolid(host, hostToHost);

            if (srcSolid != null && hostSolid != null)
            {
                var a = ComputeBoundingBox(srcSolid);
                var b = ComputeBoundingBox(hostSolid);
                if (!BboxIntersects(a, b)) return null;

                Solid inter = null;
                try
                {
                    inter = BooleanOperationsUtils.ExecuteBooleanOperation(srcSolid, hostSolid, BooleanOperationsType.Intersect);
                }
                catch { /* robustly ignore, use fallback below */ }

                if (inter != null && inter.Volume > 1e-12)
                {
                    var c = SafeCentroid(inter);
                    if (c != null) return c;
                }
            }

            // Fallback: clip the source location curve by the host box and take the midpoint.
            var hostBox = GetElementBox(host, hostToHost);
            var lc = source?.Location as LocationCurve;
            if (hostBox != null && lc != null && lc.Curve != null)
            {
                var clipped = ClipCurvePointsByBox(lc.Curve, sourceToHost, hostBox);
                if (clipped.Count > 0)
                {
                    double x = clipped.Average(p => p.X);
                    double y = clipped.Average(p => p.Y);
                    double z = clipped.Average(p => p.Z);
                    return new XYZ(x, y, z);
                }
            }
            return null;
        }

        /// <summary>Wall thickness measured from the intersection solid (min plan extent), in feet; NaN when unavailable.</summary>
        public static double MeasureIntersectionThickness(Element source, Transform sourceToHost, Element host, Transform hostToHost)
        {
            var srcSolid = GetTransformedSolid(source, sourceToHost);
            var hostSolid = GetTransformedSolid(host, hostToHost);
            if (srcSolid == null || hostSolid == null) return double.NaN;

            try
            {
                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(srcSolid, hostSolid, BooleanOperationsType.Intersect);
                if (inter == null || inter.Volume <= 1e-12) return double.NaN;
                var bb = ComputeBoundingBox(inter);
                return Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            }
            catch { return double.NaN; }
        }

        public static Solid GetTransformedSolid(Element e, Transform t)
        {
            var s = GetMainSolid(e);
            if (s == null || s.Volume <= 1e-9) return null;
            if (t == null || t.IsIdentity) return s;
            try { return SolidUtils.CreateTransformed(s, t); }
            catch { return null; }
        }

        public static XYZ SafeCentroid(Solid s)
        {
            try { return s.ComputeCentroid(); }
            catch { /* fall back to average of mesh verts */ }

            var pts = new List<XYZ>();
            foreach (Face f in s.Faces)
            {
                Mesh m = null;
                try { m = f.Triangulate(); } catch { }
                if (m == null) continue;
                var n = m.Vertices.Count;
                for (int i = 0; i < n; i++) pts.Add(m.Vertices[i]);
            }
            if (pts.Count == 0) return null;
            return new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
        }

        private static List<XYZ> ClipCurvePointsByBox(Curve curve, Transform toHost, BoundingBoxXYZ box)
        {
            var result = new List<XYZ>();
            try
            {
                const int steps = 64;
                for (int i = 0; i <= steps; i++)
                {
                    var p = curve.Evaluate(i / (double)steps, true);
                    if (toHost != null && !toHost.IsIdentity) p = toHost.OfPoint(p);
                    if (p.X >= box.Min.X && p.X <= box.Max.X &&
                        p.Y >= box.Min.Y && p.Y <= box.Max.Y &&
                        p.Z >= box.Min.Z && p.Z <= box.Max.Z)
                        result.Add(p);
                }
            }
            catch { }
            return result;
        }

        // ---------- geometry signatures ----------

        /// <summary>
        /// Change-detection signature of a source/host element: rounded bounding box, location,
        /// direction and size parameters. Not security related — SHA256 is used purely as a
        /// compact stable digest.
        /// </summary>
        public static string ComputeElementSignature(Element e, Transform toHost)
        {
            if (e == null) return "";
            var sb = new StringBuilder();

            var bb = GetElementBox(e, toHost);
            if (bb != null) { AppendXyz(sb, bb.Min); AppendXyz(sb, bb.Max); }

            var p = GetLocationPoint(e, toHost);
            if (p != null) AppendXyz(sb, p);

            var dir = GetDirection(e, toHost);
            if (dir != null) AppendXyz(sb, dir);

            AppendParam(sb, e, "Width");
            AppendParam(sb, e, "Height");
            AppendParam(sb, e, "Diameter");

            return Sha256Hex(sb.ToString());
        }

        /// <summary>
        /// Signature of the opening instance itself: insertion point + Width/Height/Depth parameters.
        /// Deliberately avoids bounding boxes so it can be computed right after creation without regeneration.
        /// </summary>
        public static string ComputeOpeningSignature(FamilyInstance fi)
        {
            if (fi == null) return "";
            var sb = new StringBuilder();

            var lp = fi.Location as LocationPoint;
            if (lp != null) AppendXyz(sb, lp.Point);

            AppendParam(sb, fi, "Width");
            AppendParam(sb, fi, "Height");
            AppendParam(sb, fi, "Depth");
            AppendParam(sb, fi, "Diameter");

            try { if (lp != null) sb.Append(RoundMm(lp.Rotation * 1000.0)); } catch { }

            return Sha256Hex(sb.ToString());
        }

        private static void AppendParam(StringBuilder sb, Element e, string name)
        {
            try
            {
                var p = e.LookupParameter(name);
                if (p != null && p.StorageType == StorageType.Double)
                    sb.Append('|').Append(RoundMm(FeetToMm(p.AsDouble())).ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static void AppendXyz(StringBuilder sb, XYZ p)
        {
            sb.Append('|').Append(RoundMm(FeetToMm(p.X)).ToString(CultureInfo.InvariantCulture))
              .Append(';').Append(RoundMm(FeetToMm(p.Y)).ToString(CultureInfo.InvariantCulture))
              .Append(';').Append(RoundMm(FeetToMm(p.Z)).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Round to 0.5 mm to suppress floating point noise between validations.</summary>
        private static double RoundMm(double mm) => Math.Round(mm * 2.0, MidpointRounding.AwayFromZero) / 2.0;

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------- units + XYZ serialization ----------

        public static double FeetToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        public static double MmToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        public static string XyzToString(XYZ p)
        {
            if (p == null) return "";
            return p.X.ToString("R", CultureInfo.InvariantCulture) + ";" +
                   p.Y.ToString("R", CultureInfo.InvariantCulture) + ";" +
                   p.Z.ToString("R", CultureInfo.InvariantCulture);
        }

        public static XYZ TryParseXyz(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(';');
            if (parts.Length != 3) return null;
            double x, y, z;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return new XYZ(x, y, z);
            return null;
        }

        public static double GetDoubleParam(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            if (p == null || p.StorageType != StorageType.Double) return double.NaN;
            try { return p.AsDouble(); } catch { return double.NaN; }
        }

        public static void SetDoubleParam(Element e, string name, double value)
        {
            var p = e?.LookupParameter(name);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
            try { p.Set(value); } catch { /* ignore */ }
        }
    }
}
