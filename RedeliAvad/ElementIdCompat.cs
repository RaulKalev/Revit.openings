using System;
using System.Reflection;
using Autodesk.Revit.DB;

/// <summary>
/// Version-independent access to ElementId values.
/// ElementId.IntegerValue was removed in Revit 2026 and ElementId.Value only exists from
/// Revit 2024, so neither can be called directly from a single build that targets both.
/// </summary>
static class ElementIdCompat
{
    // Resolved once: "Value" (long, Revit 2024+) or "IntegerValue" (int, Revit 2023 and earlier).
    private static readonly PropertyInfo IdProperty =
        typeof(ElementId).GetProperty("Value") ?? typeof(ElementId).GetProperty("IntegerValue");

    public static long GetIdValue(this ElementId id)
    {
        if (id == null) return -1;
        return Convert.ToInt64(IdProperty.GetValue(id));
    }

    /// <summary>Id value narrowed to int for storage fields; -1 if it does not fit.</summary>
    public static int GetIdValueAsInt(this ElementId id)
    {
        long v = id.GetIdValue();
        return v >= int.MinValue && v <= int.MaxValue ? (int)v : -1;
    }

    public static bool IsCategory(this Element el, BuiltInCategory bic)
    {
        return el != null && el.Category != null && el.Category.Id.GetIdValue() == (long)bic;
    }
}
