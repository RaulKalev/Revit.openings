using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RedeliAvad
{
    public class FocusNavigateHandler : IExternalEventHandler
    {
        public UIDocument UiDoc { get; set; }
        public int Delta { get; set; } = 0;                // -1 prev, +1 next
        public ElementId ExplicitId { get; set; } = null;  // if set, go to this id
        public double Padding { get; set; } = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters); // section padding

        public void Execute(UIApplication app)
        {
            var uiDoc = UiDoc ?? app.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null) return;

            // Prune memory (remove deleted)
            TempMemory.CreatedIds.RemoveAll(id => doc.GetElement(id) == null);

            // Pick target
            ElementId targetId = ExplicitId;
            if (targetId == null || targetId == ElementId.InvalidElementId)
            {
                var count = TempMemory.CreatedIds.Count;
                if (count == 0)
                {
                    TaskDialog.Show("RedeliAvad", "Ava elemente pole vahemälus, palun loo kõigepealt avad.");
                    return;
                }

                if (TempMemory.Index < 0) TempMemory.Index = 0;
                else TempMemory.Index = ((TempMemory.Index + Delta) % count + count) % count;

                targetId = TempMemory.CreatedIds[TempMemory.Index];
            }

            var el = doc.GetElement(targetId);
            if (el == null)
            {
                TaskDialog.Show("RedeliAvad", "Element not found.");
                return;
            }

            // Ensure a 3D focus view
            var view = EnsureFocus3DView(doc);

            // Set section box around the element
            using (var t = new Transaction(doc, "RedeliAvad – Focus"))
            {
                t.Start();

                var bb = el.get_BoundingBox(null);
                if (bb == null)
                {
                    t.RollBack();
                    TaskDialog.Show("RedeliAvad", "Element has no bounding box.");
                    return;
                }

                var min = bb.Min; var max = bb.Max;
                var padded = new BoundingBoxXYZ
                {
                    Min = new XYZ(min.X - Padding, min.Y - Padding, min.Z - Padding),
                    Max = new XYZ(max.X + Padding, max.Y + Padding, max.Z + Padding)
                };

                view.IsSectionBoxActive = true;
                view.SetSectionBox(padded);

                t.Commit();
            }

            // Switch and zoom
            uiDoc.ActiveView = view;
            uiDoc.ShowElements(targetId);
        }

        public string GetName() => "RedeliAvad.FocusNavigateHandler";

        private View3D EnsureFocus3DView(Document doc)
        {
            const string viewName = "RedeliAvad Focus";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing;

            using (var t = new Transaction(doc, "RedeliAvad – Create Focus View"))
            {
                t.Start();

                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                if (vft == null)
                    throw new InvalidOperationException("No 3D ViewFamilyType available.");

                var v3d = View3D.CreateIsometric(doc, vft.Id);
                v3d.Name = viewName;
                v3d.DisplayStyle = DisplayStyle.Shading;
                v3d.DetailLevel = ViewDetailLevel.Medium;
                v3d.IsSectionBoxActive = true;

                t.Commit();
                return v3d;
            }
        }
    }
}
