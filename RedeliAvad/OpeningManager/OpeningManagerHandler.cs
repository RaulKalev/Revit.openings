using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RedeliAvad
{
    public enum OpeningManagerRequest
    {
        None,
        Refresh,
        GoTo,
        SelectOpening,
        SelectSource,
        HighlightPair,
        FixItems,
        MarkAllowed,
        ClearAllowed,
        Unlink,
        DeleteOpening
    }

    /// <summary>Everything a Fix/Recreate operation needs, captured on the UI thread.</summary>
    public class OpeningFixPayload
    {
        public string OpeningUniqueId { get; set; }
        public bool Recreate { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string SourceUniqueId { get; set; }
        public string SourceLinkUniqueId { get; set; }
        public string HostUniqueId { get; set; }
        public string HostLinkUniqueId { get; set; }
        public XYZ TargetCenter { get; set; }
        public double TargetWidthMm { get; set; }   // <= 0 → leave unchanged
        public double TargetHeightMm { get; set; }  // <= 0 → leave unchanged
        public double TargetDepthMm { get; set; }   // <= 0 → leave unchanged
        public double ClearanceWidthMm { get; set; }
        public double ClearanceHeightMm { get; set; }
        public double ClearanceDepthMm { get; set; }
    }

    /// <summary>
    /// Single external event handler for all Opening Manager operations that touch the Revit
    /// model (modeless-safe: the WPF window only sets the request fields and raises the event).
    /// Results are pushed back through the callbacks, which the window marshals to its dispatcher.
    /// </summary>
    public class OpeningManagerHandler : IExternalEventHandler
    {
        public UIDocument UiDoc { get; set; }
        public OpeningManagerRequest Request { get; set; } = OpeningManagerRequest.None;

        /// <summary>Row the request refers to (navigation / allow / unlink / delete).</summary>
        public OpeningManagerItem TargetItem { get; set; }
        /// <summary>Fix payloads (one or many rows).</summary>
        public List<OpeningFixPayload> FixPayloads { get; set; }
        public string AllowReason { get; set; }

        /// <summary>Called with the fresh row list after any model-changing request.</summary>
        public Action<List<OpeningManagerItem>> ItemsReady { get; set; }
        /// <summary>Called with a user-facing message (text, isError).</summary>
        public Action<string, bool> Message { get; set; }

        private const string FocusViewName = "RedeliAvad Focus";
        private static readonly double NavPaddingFt = OpeningGeometry.MmToFeet(400);

        public string GetName() => "RedeliAvad.OpeningManagerHandler";

        public void Execute(UIApplication app)
        {
            var request = Request;
            Request = OpeningManagerRequest.None;

            var uiDoc = UiDoc ?? app.ActiveUIDocument;
            var doc = uiDoc != null ? uiDoc.Document : null;
            if (doc == null) return;

            try
            {
                switch (request)
                {
                    case OpeningManagerRequest.Refresh:
                        DoRefresh(doc);
                        break;
                    case OpeningManagerRequest.GoTo:
                        DoNavigate(uiDoc, doc, TargetItem, true, true, false);
                        break;
                    case OpeningManagerRequest.SelectOpening:
                        DoSelectOpening(uiDoc, doc, TargetItem);
                        break;
                    case OpeningManagerRequest.SelectSource:
                        DoSelectSource(uiDoc, doc, TargetItem);
                        break;
                    case OpeningManagerRequest.HighlightPair:
                        DoNavigate(uiDoc, doc, TargetItem, true, true, true);
                        break;
                    case OpeningManagerRequest.FixItems:
                        DoFix(doc, FixPayloads);
                        break;
                    case OpeningManagerRequest.MarkAllowed:
                        DoSetAllowed(doc, TargetItem, true, AllowReason);
                        break;
                    case OpeningManagerRequest.ClearAllowed:
                        DoSetAllowed(doc, TargetItem, false, null);
                        break;
                    case OpeningManagerRequest.Unlink:
                        DoUnlink(doc, TargetItem);
                        break;
                    case OpeningManagerRequest.DeleteOpening:
                        DoDelete(doc, TargetItem);
                        break;
                }
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("OpeningManagerHandler." + request + " failed", ex);
                Notify("Toiming ebaõnnestus: " + ex.Message, true);
            }
        }

        // ------------------------------------------------------------------
        // Refresh / validation
        // ------------------------------------------------------------------

        private void DoRefresh(Document doc)
        {
            List<OpeningManagerItem> items;
            using (var t = new Transaction(doc, "Avade haldur – valideerimine"))
            {
                t.Start();
                items = OpeningValidationService.ValidateAll(doc);
                t.Commit();
            }
            OpeningManagerLog.Info("Validated " + items.Count + " opening(s).");
            ItemsReady?.Invoke(items);
        }

        // ------------------------------------------------------------------
        // Navigation / selection
        // ------------------------------------------------------------------

        private void DoNavigate(UIDocument uiDoc, Document doc, OpeningManagerItem item,
            bool selectOpening, bool zoom, bool includeSourceAndHost)
        {
            if (item == null) return;

            var opening = string.IsNullOrEmpty(item.OpeningUniqueId) ? null : doc.GetElement(item.OpeningUniqueId);

            // Combined box: opening + (optionally) source and host.
            BoundingBoxXYZ box = null;
            if (opening != null) box = OpeningGeometry.GetElementBox(opening, null);

            if (includeSourceAndHost || box == null)
            {
                Transform st, ht; string e1, e2;
                var source = OpeningValidationService.ResolveElement(doc, item.SourceUniqueId, item.SourceLinkUniqueId, out st, out e1);
                var host = OpeningValidationService.ResolveElement(doc, item.HostUniqueId, item.HostLinkUniqueId, out ht, out e2);
                if (includeSourceAndHost && source != null) box = OpeningGeometry.UnionBox(box, OpeningGeometry.GetElementBox(source, st));
                if (includeSourceAndHost && host != null) box = OpeningGeometry.UnionBox(box, OpeningGeometry.GetElementBox(host, ht));
                if (box == null && source != null) box = OpeningGeometry.GetElementBox(source, st);
            }

            if (box == null)
            {
                // Missing opening: fall back to the expected/last-known center.
                var c = item.ExpectedCenter ?? item.CurrentCenter;
                if (c == null)
                {
                    Notify("Asukohta ei õnnestunud määrata – element ja salvestatud keskpunkt puuduvad.", true);
                    return;
                }
                var half = OpeningGeometry.MmToFeet(500);
                box = new BoundingBoxXYZ
                {
                    Min = new XYZ(c.X - half, c.Y - half, c.Z - half),
                    Max = new XYZ(c.X + half, c.Y + half, c.Z + half)
                };
            }

            var view = EnsureFocus3DView(doc);
            if (view == null) return;

            using (var t = new Transaction(doc, "Avade haldur – fookus"))
            {
                t.Start();
                var padded = new BoundingBoxXYZ
                {
                    Min = new XYZ(box.Min.X - NavPaddingFt, box.Min.Y - NavPaddingFt, box.Min.Z - NavPaddingFt),
                    Max = new XYZ(box.Max.X + NavPaddingFt, box.Max.Y + NavPaddingFt, box.Max.Z + NavPaddingFt)
                };
                view.IsSectionBoxActive = true;
                view.SetSectionBox(padded);
                t.Commit();
            }

            uiDoc.ActiveView = view;

            // Select what lives in the host document (linked elements cannot be put into the selection).
            var ids = new List<ElementId>();
            if (selectOpening && opening != null) ids.Add(opening.Id);
            if (includeSourceAndHost && string.IsNullOrEmpty(item.SourceLinkUniqueId) &&
                item.SourceElementId != null && item.SourceElementId != ElementId.InvalidElementId &&
                doc.GetElement(item.SourceElementId) != null)
                ids.Add(item.SourceElementId);

            try { uiDoc.Selection.SetElementIds(ids); } catch { }

            if (zoom)
            {
                if (ids.Count > 0)
                {
                    try { uiDoc.ShowElements(ids); } catch { }
                }
                else
                {
                    // Nothing selectable (e.g. missing opening) — zoom the UI view to the box.
                    try
                    {
                        var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                        if (uiView != null) uiView.ZoomAndCenterRectangle(box.Min, box.Max);
                    }
                    catch { }
                }
            }

            if (opening == null && item.Status == OpeningStatus.MissingOpening)
                Notify("Ava on kustutatud – näitan viimast teadaolevat asukohta.", false);
        }

        private void DoSelectOpening(UIDocument uiDoc, Document doc, OpeningManagerItem item)
        {
            if (item == null) return;
            var opening = string.IsNullOrEmpty(item.OpeningUniqueId) ? null : doc.GetElement(item.OpeningUniqueId);
            if (opening == null)
            {
                Notify("Ava elementi ei leitud (kustutatud?).", true);
                return;
            }
            try
            {
                uiDoc.Selection.SetElementIds(new List<ElementId> { opening.Id });
                uiDoc.ShowElements(opening.Id);
            }
            catch (Exception ex) { OpeningManagerLog.Error("SelectOpening failed", ex); }
        }

        private void DoSelectSource(UIDocument uiDoc, Document doc, OpeningManagerItem item)
        {
            if (item == null) return;

            Transform st; string err;
            var source = OpeningValidationService.ResolveElement(doc, item.SourceUniqueId, item.SourceLinkUniqueId, out st, out err);
            if (source == null)
            {
                Notify("Allika elementi ei leitud: " + err, true);
                return;
            }

            if (string.IsNullOrEmpty(item.SourceLinkUniqueId))
            {
                try
                {
                    uiDoc.Selection.SetElementIds(new List<ElementId> { source.Id });
                    uiDoc.ShowElements(source.Id);
                }
                catch (Exception ex) { OpeningManagerLog.Error("SelectSource failed", ex); }
            }
            else
            {
                // Linked element: cannot be added to the selection — zoom to its transformed box instead.
                var box = OpeningGeometry.GetElementBox(source, st);
                if (box == null)
                {
                    Notify("Lingitud allika asukohta ei õnnestunud arvutada.", true);
                    return;
                }
                var view = EnsureFocus3DView(doc);
                if (view == null) return;
                using (var t = new Transaction(doc, "Avade haldur – fookus"))
                {
                    t.Start();
                    view.IsSectionBoxActive = true;
                    view.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(box.Min.X - NavPaddingFt, box.Min.Y - NavPaddingFt, box.Min.Z - NavPaddingFt),
                        Max = new XYZ(box.Max.X + NavPaddingFt, box.Max.Y + NavPaddingFt, box.Max.Z + NavPaddingFt)
                    });
                    t.Commit();
                }
                uiDoc.ActiveView = view;
                try
                {
                    var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                    if (uiView != null) uiView.ZoomAndCenterRectangle(box.Min, box.Max);
                }
                catch { }
                Notify("Allikas asub lingitud mudelis – valik pole võimalik, näitan asukohta.", false);
            }
        }

        // ------------------------------------------------------------------
        // Fix / recreate
        // ------------------------------------------------------------------

        private void DoFix(Document doc, List<OpeningFixPayload> payloads)
        {
            if (payloads == null || payloads.Count == 0) return;

            int fixedCount = 0, recreated = 0, failed = 0;

            using (var t = new Transaction(doc, "Avade haldur – paranda avad"))
            {
                t.Start();

                foreach (var p in payloads)
                {
                    try
                    {
                        var opening = string.IsNullOrEmpty(p.OpeningUniqueId) ? null : doc.GetElement(p.OpeningUniqueId) as FamilyInstance;
                        if (opening != null)
                        {
                            if (FixExistingOpening(doc, opening, p)) fixedCount++;
                            else failed++;
                        }
                        else if (p.Recreate)
                        {
                            if (RecreateOpening(doc, p)) recreated++;
                            else failed++;
                        }
                        else failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        OpeningManagerLog.Error("Fix failed for " + p.OpeningUniqueId, ex);
                    }
                }

                t.Commit();
            }

            var msg = "Parandatud: " + fixedCount +
                      (recreated > 0 ? ", taasloodud: " + recreated : "") +
                      (failed > 0 ? ", ebaõnnestus: " + failed : "") + ".";
            OpeningManagerLog.Info(msg);
            Notify(msg, failed > 0);

            DoRefresh(doc);
        }

        private bool FixExistingOpening(Document doc, FamilyInstance opening, OpeningFixPayload p)
        {
            var rec = OpeningLinkStorage.TryReadLink(opening);
            if (rec == null) return false;

            // 1) Move to the expected penetration point.
            var lp = opening.Location as LocationPoint;
            if (p.TargetCenter != null && lp != null)
            {
                var delta = p.TargetCenter - lp.Point;
                if (delta.GetLength() > 1e-9)
                    ElementTransformUtils.MoveElement(doc, opening.Id, delta);
            }

            // 2) Resize to source size + clearance.
            if (p.TargetWidthMm > 0.1)
                OpeningGeometry.SetDoubleParam(opening, "Width", OpeningGeometry.MmToFeet(p.TargetWidthMm));
            if (p.TargetHeightMm > 0.1)
                OpeningGeometry.SetDoubleParam(opening, "Height", OpeningGeometry.MmToFeet(p.TargetHeightMm));
            if (p.TargetDepthMm > 0.1)
                OpeningGeometry.SetDoubleParam(opening, "Depth", OpeningGeometry.MmToFeet(p.TargetDepthMm));

            // 3) Refresh the stored snapshot: post-fix state becomes the new baseline.
            Transform st, ht; string e1, e2;
            var source = OpeningValidationService.ResolveElement(doc, rec.SourceElementUniqueId, rec.SourceLinkInstanceUniqueId, out st, out e1);
            var host = OpeningValidationService.ResolveElement(doc, rec.HostElementUniqueId, rec.HostLinkInstanceUniqueId, out ht, out e2);

            var lpNow = opening.Location as LocationPoint;
            rec.OpeningCenterXyz = OpeningGeometry.XyzToString(lpNow != null ? lpNow.Point : p.TargetCenter);
            if (p.TargetWidthMm > 0.1) rec.OpeningWidthMm = p.TargetWidthMm;
            if (p.TargetHeightMm > 0.1) rec.OpeningHeightMm = p.TargetHeightMm;
            if (p.TargetDepthMm > 0.1) rec.OpeningDepthMm = p.TargetDepthMm;
            if (source != null)
            {
                rec.SourceCenterXyz = OpeningGeometry.XyzToString(OpeningGeometry.GetLocationPoint(source, st));
                rec.SourceDirectionXyz = OpeningGeometry.XyzToString(OpeningGeometry.GetDirection(source, st));
                rec.LastKnownSourceGeometryHash = OpeningGeometry.ComputeElementSignature(source, st);
            }
            if (host != null)
                rec.LastKnownHostGeometryHash = OpeningGeometry.ComputeElementSignature(host, ht);
            rec.LastKnownOpeningGeometryHash = OpeningGeometry.ComputeOpeningSignature(opening);
            rec.Status = OpeningStatus.Aligned.ToString();
            rec.OffsetMm = 0.0;
            rec.LastValidatedUtc = DateTime.UtcNow.ToString("o");
            OpeningLinkStorage.SaveLink(opening, rec);
            return true;
        }

        private bool RecreateOpening(Document doc, OpeningFixPayload p)
        {
            if (p.TargetCenter == null) return false;

            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family != null &&
                    string.Equals(s.Family.Name, p.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Name, p.TypeName, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
            {
                Notify("Perekonda \"" + p.FamilyName + " : " + p.TypeName + "\" ei leitud – ava ei saa taasluua.", true);
                return false;
            }

            Transform st, ht; string e1, e2;
            var source = OpeningValidationService.ResolveElement(doc, p.SourceUniqueId, p.SourceLinkUniqueId, out st, out e1);
            var host = OpeningValidationService.ResolveElement(doc, p.HostUniqueId, p.HostLinkUniqueId, out ht, out e2);
            if (source == null || host == null)
            {
                Notify("Ava taasloomiseks on vaja nii allikat kui alust (" + (source == null ? e1 : e2) + ").", true);
                return false;
            }

            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

            var level = PlacementHelpers.FindNearestLevel(doc, p.TargetCenter.Z);
            var fi = level != null
                ? doc.Create.NewFamilyInstance(p.TargetCenter, symbol, level, StructuralType.NonStructural)
                : doc.Create.NewFamilyInstance(p.TargetCenter, symbol, StructuralType.NonStructural);

            PlacementHelpers.SetLevelAndOffset(fi, level, p.TargetCenter.Z);

            // Orient along the source run direction (plan angle), like the original placement.
            var dir = OpeningGeometry.GetDirection(source, st);
            if (dir != null && Math.Abs(dir.Z) < 0.99)
            {
                var angle = Math.Atan2(dir.Y, dir.X);
                var axis = Line.CreateBound(p.TargetCenter, p.TargetCenter + XYZ.BasisZ);
                try { ElementTransformUtils.RotateElement(doc, fi.Id, axis, angle); } catch { }
            }

            var wFt = OpeningGeometry.MmToFeet(p.TargetWidthMm > 0.1 ? p.TargetWidthMm : 100);
            var hFt = OpeningGeometry.MmToFeet(p.TargetHeightMm > 0.1 ? p.TargetHeightMm : 100);
            var dFt = OpeningGeometry.MmToFeet(p.TargetDepthMm > 0.1 ? p.TargetDepthMm : 100);
            OpeningGeometry.SetDoubleParam(fi, "Width", wFt);
            OpeningGeometry.SetDoubleParam(fi, "Height", hFt);
            OpeningGeometry.SetDoubleParam(fi, "Depth", dFt);

            // Old index entry out, fresh record + entry in.
            OpeningLinkStorage.RemoveIndexEntry(doc, p.OpeningUniqueId);
            var hostLink = string.IsNullOrEmpty(p.HostLinkUniqueId) ? null : doc.GetElement(p.HostLinkUniqueId) as RevitLinkInstance;
            OpeningLinkStorage.SaveNewLink(doc, fi, source, host, hostLink, p.TargetCenter,
                wFt, hFt, dFt,
                OpeningGeometry.MmToFeet(p.ClearanceWidthMm),
                OpeningGeometry.MmToFeet(p.ClearanceHeightMm),
                OpeningGeometry.MmToFeet(p.ClearanceDepthMm));

            if (!TempMemory.CreatedIds.Contains(fi.Id))
                TempMemory.CreatedIds.Add(fi.Id);
            return true;
        }

        // ------------------------------------------------------------------
        // Allowed exception / unlink / delete
        // ------------------------------------------------------------------

        private void DoSetAllowed(Document doc, OpeningManagerItem item, bool allowed, string reason)
        {
            if (item == null) return;
            var opening = string.IsNullOrEmpty(item.OpeningUniqueId) ? null : doc.GetElement(item.OpeningUniqueId);
            if (opening == null)
            {
                Notify("Puuduvat ava ei saa lubatuks märkida – eemalda seos või taasloo ava.", true);
                return;
            }

            var rec = OpeningLinkStorage.TryReadLink(opening);
            if (rec == null)
            {
                Notify("Seose andmeid ei õnnestunud lugeda.", true);
                return;
            }

            using (var t = new Transaction(doc, allowed ? "Avade haldur – märgi lubatuks" : "Avade haldur – eemalda lubatud märge"))
            {
                t.Start();
                rec.IsAllowedException = allowed;
                rec.AllowedBy = allowed ? Environment.UserName : "";
                rec.AllowedUtc = allowed ? DateTime.UtcNow.ToString("o") : "";
                rec.AllowedReason = allowed ? (reason ?? "") : "";
                OpeningLinkStorage.SaveLink(opening, rec);
                t.Commit();
            }

            OpeningManagerLog.Info((allowed ? "Allowed" : "Cleared allowed") + " for opening " + item.OpeningUniqueId);
            DoRefresh(doc);
        }

        private void DoUnlink(Document doc, OpeningManagerItem item)
        {
            if (item == null) return;
            using (var t = new Transaction(doc, "Avade haldur – eemalda seos"))
            {
                t.Start();
                var opening = string.IsNullOrEmpty(item.OpeningUniqueId) ? null : doc.GetElement(item.OpeningUniqueId);
                if (opening != null) OpeningLinkStorage.RemoveLink(opening);
                OpeningLinkStorage.RemoveIndexEntry(doc, item.OpeningUniqueId);
                t.Commit();
            }
            OpeningManagerLog.Info("Unlinked opening " + item.OpeningUniqueId);
            DoRefresh(doc);
        }

        private void DoDelete(Document doc, OpeningManagerItem item)
        {
            if (item == null) return;
            using (var t = new Transaction(doc, "Avade haldur – kustuta ava"))
            {
                t.Start();
                var opening = string.IsNullOrEmpty(item.OpeningUniqueId) ? null : doc.GetElement(item.OpeningUniqueId);
                if (opening != null)
                {
                    TempMemory.CreatedIds.Remove(opening.Id);
                    doc.Delete(opening.Id);
                }
                OpeningLinkStorage.RemoveIndexEntry(doc, item.OpeningUniqueId);
                t.Commit();
            }
            OpeningManagerLog.Info("Deleted opening " + item.OpeningUniqueId);
            DoRefresh(doc);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private View3D EnsureFocus3DView(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(FocusViewName, StringComparison.OrdinalIgnoreCase));

            if (existing != null) return existing;

            try
            {
                using (var t = new Transaction(doc, "RedeliAvad – Create Focus View"))
                {
                    t.Start();
                    var vft = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                    if (vft == null)
                    {
                        t.RollBack();
                        Notify("3D-vaate tüüpi ei leitud.", true);
                        return null;
                    }

                    var v3d = View3D.CreateIsometric(doc, vft.Id);
                    v3d.Name = FocusViewName;
                    v3d.DisplayStyle = DisplayStyle.Shading;
                    v3d.DetailLevel = ViewDetailLevel.Medium;
                    v3d.IsSectionBoxActive = true;
                    t.Commit();
                    return v3d;
                }
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("EnsureFocus3DView failed", ex);
                return null;
            }
        }

        private void Notify(string text, bool isError)
        {
            var cb = Message;
            if (cb != null) cb(text, isError);
        }
    }
}
