using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical; // if needed
using System;
using System.Linq;

static class PlacementHelpers
{
    public static void SetLevelAndOffset(Element e, Level targetLevel, double targetWorldZ_Ft)
    {
        if (e == null || targetLevel == null) return;

        // Compute correct offset from the *new* level
        double offsetFromLevel_Ft = targetWorldZ_Ft - targetLevel.Elevation;

        // --- Family instances (level-based, non-hosted) ---
        if (e is FamilyInstance fi)
        {
            // Set Level
            fi.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.Set(targetLevel.Id);

            // Try common elevation/offset params
            var pElev = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)
                    ?? fi.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)
                    ?? fi.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM); // fallback (rare)

            if (pElev != null && !pElev.IsReadOnly)
                pElev.Set(offsetFromLevel_Ft);

            return;
        }

        // --- MEP curves (CableTray, Duct, Pipe, Conduit) ---
        if (e is MEPCurve mc)
        {
            // Align both ends to the same reference level (flat run)
            mc.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)?.Set(targetLevel.Id);
            mc.get_Parameter(BuiltInParameter.RBS_END_LEVEL_PARAM)?.Set(targetLevel.Id);

            // Single offset for the run (if you’re not doing slopes)
            var pOff = mc.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
            if (pOff != null && !pOff.IsReadOnly)
                pOff.Set(offsetFromLevel_Ft);

            return;
        }

        // --- MEP fittings (incl. Cable Tray fittings) ---
        // Many fittings expose RBS_OFFSET_PARAM relative to their level
        var fitOff = e.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
        var fitLvl = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);


        if (fitLvl != null && !fitLvl.IsReadOnly)
            fitLvl.Set(targetLevel.Id);

        if (fitOff != null && !fitOff.IsReadOnly)
            fitOff.Set(offsetFromLevel_Ft);
    }

    public static Level FindNearestLevel(Document doc, double worldZ_Ft)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => Math.Abs(worldZ_Ft - l.Elevation))
            .FirstOrDefault();
    }
}
