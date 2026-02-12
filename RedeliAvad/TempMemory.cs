using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RedeliAvad
{
    internal static class TempMemory
    {
        public static readonly List<ElementId> CreatedIds = new List<ElementId>();
        public static int Index = -1;

        public static void Clear()
        {
            CreatedIds.Clear();
            Index = -1;
        }
    }
}
