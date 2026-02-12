using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure; // <-- fixes CS0103
using Autodesk.Revit.UI;

namespace RedeliAvad
{
    public class PlaceIntersectionsHandler : IExternalEventHandler
    {
        public UIDocument UiDoc { get; set; }
        public RevitLinkInstance SelectedLink { get; set; }
        public FamilySymbol Symbol { get; set; }
        public bool SkipDuplicates { get; set; }
        public double ProximityTol { get; set; } = 5.0 / 304.8; // ~5 mm
        public double ExtWidth { get; set; } = 0.0; // feet
        public double ExtDepth { get; set; } = 0.0; // feet
        public double ExtHeight { get; set; } = 0.0; // feet
        public ICollection<ElementId> SelectedTrayIds { get; set; }
        public bool UseSelectedOnly { get; set; } = false;

        public void Execute(UIApplication app)
        {
            if (UiDoc == null || SelectedLink == null || Symbol == null) return;

            var doc = UiDoc.Document;
            var linkDoc = SelectedLink.GetLinkDocument();
            if (linkDoc == null)
            {
                TaskDialog.Show("RedeliAvad", "Selected link is not loaded.");
                return;
            }

            var linkToHost = SelectedLink.GetTotalTransform();

            // 1) Host cable trays -> solids
            IEnumerable<Element> trayElems;

            if (UseSelectedOnly && SelectedTrayIds != null && SelectedTrayIds.Count > 0)
            {
                trayElems = SelectedTrayIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && e.Category != null &&
                        (e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTray ||
                         e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayFitting));
            }
            else
            {
                // All trays in host: trays + fittings
                var f = new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_CableTray),
                    new ElementCategoryFilter(BuiltInCategory.OST_CableTrayFitting));

                trayElems = new FilteredElementCollector(doc)
                    .WherePasses(f)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }

            var traySolids = new List<(Element Tray, Solid Solid)>();
            foreach (var e in trayElems)
            {
                var s = GetMainSolid(e);
                if (s != null && s.Volume > 1e-9) traySolids.Add((e, s));
            }

            if (traySolids.Count == 0)
            {
                TaskDialog.Show("RedeliAvad",
                    UseSelectedOnly ? "Valikust ei leitud kaablirenne/ühendusi." : "Põhifailis kaablirenne ei leitud.");
                // Reset the flag so a next run (all) still works
                UseSelectedOnly = false;
                SelectedTrayIds = null;
                return;
            }


            // 2) Link walls + structural framing -> solids (transformed to host)
            var hostFilter = new LogicalOrFilter(
                new ElementCategoryFilter(BuiltInCategory.OST_Walls),
                new ElementCategoryFilter(BuiltInCategory.OST_StructuralFraming));

            var hostElements = new FilteredElementCollector(linkDoc)
                .WherePasses(hostFilter)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            if (hostElements.Count == 0)
            {
                // Fallback for quirky IFC categorization
                hostElements = new FilteredElementCollector(linkDoc)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Where(el => el.Category != null &&
                        (el.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Walls ||
                         el.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralFraming))
                    .ToList();
            }

            var hostSolids = new List<Solid>();
            foreach (var w in hostElements)
            {
                var s = GetMainSolid(w);
                if (s == null || s.Volume <= 1e-9) continue;
                var st = SolidUtils.CreateTransformed(s, linkToHost);
                if (st != null && st.Volume > 1e-9) hostSolids.Add(st);
            }

            if (hostSolids.Count == 0)
            {
                TaskDialog.Show("RedeliAvad", "No usable wall/framing solids found in link.");
                return;
            }

            // 3) Intersect and place
            var newlyPlaced = new List<ElementId>();
            var placed = 0;
            var duplicates = 0;
            var pointsPlaced = new List<XYZ>();

            // Existing instances of the selected symbol in the model (for cross-run duplicate avoidance)
            var existingPts = GetExistingInstancePoints(doc, Symbol);

            using (var t = new Transaction(doc, "RedeliAvad – Place at intersections"))
            {
                t.Start();

                if (!Symbol.IsActive) { Symbol.Activate(); doc.Regenerate(); }

                foreach (var (tray, traySolid) in traySolids)
                {
                    var trayB = ComputeBoundingBox(traySolid);

                    // Try to get tray's width/height (used for instance params)
                    var trayWidth = GetDoubleParam(tray, "Width");
                    var trayHeight = GetDoubleParam(tray, "Height");

                    foreach (var hostS in hostSolids)
                    {
                        var wallB = ComputeBoundingBox(hostS);
                        if (!BboxIntersects(trayB, wallB)) continue;

                        Solid intersect = null;
                        try
                        {
                            intersect = BooleanOperationsUtils.ExecuteBooleanOperation(
                                traySolid, hostS, BooleanOperationsType.Intersect);
                        }
                        catch { /* robustly ignore */ }

                        if (intersect == null || intersect.Volume <= 1e-12) continue;

                        var pt = SafeCentroid(intersect);
                        if (pt == null) continue;

                        // Skip duplicates within this run
                        if (SkipDuplicates && pointsPlaced.Any(p => p.DistanceTo(pt) <= ProximityTol))
                        {
                            duplicates++;
                            continue;
                        }

                        // Skip if an instance already exists in the model near this point
                        if (existingPts.Any(p => p.DistanceTo(pt) <= ProximityTol))
                        {
                            duplicates++;
                            continue;
                        }

                        Level level = TryGetElementLevel(doc, tray) ?? TryNearestLevel(doc, pt);

                        var fi = (level != null)
                            ? doc.Create.NewFamilyInstance(pt, Symbol, level, StructuralType.NonStructural)
                            : doc.Create.NewFamilyInstance(pt, Symbol, StructuralType.NonStructural);

                        // ✅ Recompute offset relative to the *chosen* level
                        PlacementHelpers.SetLevelAndOffset(fi, level ?? TryNearestLevel(doc, pt), pt.Z);

                        // Rotate to tray direction if possible
                        TryRotateToTrayDirection(doc, tray, fi, pt);

                        // --- measure wall thickness from the intersection solid (plan thickness) ---
                        var ibb = ComputeBoundingBox(intersect);
                        double dx = ibb.Max.X - ibb.Min.X;
                        double dy = ibb.Max.Y - ibb.Min.Y;
                        double wallThickness = Math.Min(dx, dy); // feet

                        // --------------------------
                        // Set instance parameters
                        // Width  = tray width  + 2 * ExtWidth
                        // Depth  = wall thickness + 2 * ExtDepth
                        // Height = tray height + 2 * ExtHeight
                        // --------------------------
                        if (!double.IsNaN(trayWidth))
                            SetDoubleParam(fi, "Width", trayWidth + 2.0 * ExtWidth);
                        else
                            SetDoubleParam(fi, "Width", 2.0 * ExtWidth); // fallback

                        SetDoubleParam(fi, "Depth", wallThickness + 2.0 * ExtDepth);

                        if (!double.IsNaN(trayHeight))
                            SetDoubleParam(fi, "Height", trayHeight + 2.0 * ExtHeight);
                        else
                            SetDoubleParam(fi, "Height", 2.0 * ExtHeight);
                        newlyPlaced.Add(fi.Id);

                        // Record placement to avoid duplicates later in this run and across runs
                        existingPts.Add(pt);
                        pointsPlaced.Add(pt);
                        placed++;
                    }
                }

                t.Commit();

                if (newlyPlaced.Count > 0)
                {
                    // Keep them unique in memory (in case of re-runs with duplicates off)
                    foreach (var id in newlyPlaced)
                        if (!TempMemory.CreatedIds.Contains(id))
                            TempMemory.CreatedIds.Add(id);
                }
                UseSelectedOnly = false;
                SelectedTrayIds = null;


            }

            // Final report
            if (placed == 0)
            {
                TaskDialog.Show("RedeliAvad",
                    duplicates > 0
                        ? $"No new instances placed.\nSkipped {duplicates} duplicate location(s)."
                        : "No intersections found.");
            }
            else
            {
                TaskDialog.Show("RedeliAvad",
                    duplicates > 0
                        ? $"Placed {placed} instance(s).\nSkipped {duplicates} duplicate location(s)."
                        : $"Placed {placed} instance(s).");
            }
        }

        public string GetName() => "RedeliAvad.PlaceIntersectionsHandler";

        // ---------- helpers ----------
        private static List<XYZ> GetExistingInstancePoints(Document doc, FamilySymbol symbol)
        {
            var pts = new List<XYZ>();
            var col = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null && fi.Symbol.Id == symbol.Id);

            foreach (var fi in col)
            {
                var lp = fi.Location as LocationPoint;
                if (lp != null) pts.Add(lp.Point);
            }
            return pts;
        }

        private static Solid GetMainSolid(Element e)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var geo = e.get_Geometry(opt);
            if (geo == null) return null;

            Solid best = null;
            double maxV = 0.0;

            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Volume > maxV) { best = s; maxV = s.Volume; }
                else if (obj is GeometryInstance gi)
                {
                    var sym = gi.GetInstanceGeometry();
                    if (sym == null) continue;
                    foreach (var g in sym)
                    {
                        if (g is Solid ss && ss.Volume > maxV) { best = ss; maxV = ss.Volume; }
                    }
                }
            }
            return best;
        }
        private static double GetDoubleParam(Element e, string name)
        {
            var p = e.LookupParameter(name);
            if (p == null || p.StorageType != StorageType.Double) return double.NaN;
            try { return p.AsDouble(); } catch { return double.NaN; }
        }

        private static void SetDoubleParam(Element e, string name, double value)
        {
            var p = e.LookupParameter(name);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
            try { p.Set(value); } catch { /* ignore */ }
        }

        // Replacement for the invalid extension method
        private static BoundingBoxXYZ ComputeBoundingBox(Solid s)
        {
            var bb = new BoundingBoxXYZ
            {
                Min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue),
                Max = new XYZ(double.MinValue, double.MinValue, double.MinValue)
            };

            foreach (Face f in s.Faces)
            {
                var mesh = f.Triangulate();
                var n = mesh.Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    var v = mesh.Vertices[i];
                    bb.Min = new XYZ(
                        Math.Min(bb.Min.X, v.X),
                        Math.Min(bb.Min.Y, v.Y),
                        Math.Min(bb.Min.Z, v.Z));
                    bb.Max = new XYZ(
                        Math.Max(bb.Max.X, v.X),
                        Math.Max(bb.Max.Y, v.Y),
                        Math.Max(bb.Max.Z, v.Z));
                }
            }
            return bb;
        }

        private static bool BboxIntersects(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        private static XYZ SafeCentroid(Solid s)
        {
            try { return s.ComputeCentroid(); }
            catch { /* fall back to average of mesh verts */ }

            var pts = new List<XYZ>();
            foreach (Face f in s.Faces)
            {
                var m = f.Triangulate();
                var n = m.Vertices.Count;
                for (int i = 0; i < n; i++) pts.Add(m.Vertices[i]);
            }
            if (pts.Count == 0) return null;
            var x = pts.Average(p => p.X);
            var y = pts.Average(p => p.Y);
            var z = pts.Average(p => p.Z);
            return new XYZ(x, y, z);
        }

        private static Level TryGetElementLevel(Document doc, Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM) ??
                    e.get_Parameter(BuiltInParameter.LEVEL_PARAM) ??
                    e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);

            if (p != null && p.AsElementId() != ElementId.InvalidElementId)
                return doc.GetElement(p.AsElementId()) as Level;

            return null;
        }

        private static Level TryNearestLevel(Document doc, XYZ pt)
        {
            Level best = null; double dMin = double.MaxValue;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            foreach (var lv in levels)
            {
                var d = Math.Abs(lv.Elevation - pt.Z);
                if (d < dMin) { dMin = d; best = lv; }
            }
            return best;
        }

        private static void TryRotateToTrayDirection(Document doc, Element tray, FamilyInstance fi, XYZ pivot)
        {
            var lc = tray.Location as LocationCurve;
            if (lc == null) return;

            var dir = (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0));
            if (dir.GetLength() < 1e-9) return;

            var angle = Math.Atan2(dir.Y, dir.X); // align to tray plan direction
            var axis = Line.CreateBound(pivot, pivot + XYZ.BasisZ);

            try { ElementTransformUtils.RotateElement(doc, fi.Id, axis, angle); } catch { }
        }
    }
}
