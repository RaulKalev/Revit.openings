using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RedeliAvad
{
    /// <summary>
    /// Validation engine: compares the stored opening↔source↔host relationship against the
    /// current model state and produces <see cref="OpeningManagerItem"/> rows.
    ///
    /// Performance notes: only plugin-managed openings are collected (quick ExtensibleStorage
    /// filter); geometry hashes short-circuit unchanged rows before any boolean operation;
    /// bounding-box prechecks guard solid intersections.
    ///
    /// Callers must wrap <see cref="ValidateAll"/> in a transaction — it writes Status /
    /// OffsetMm / LastValidatedUtc back to Extensible Storage.
    /// </summary>
    internal static class OpeningValidationService
    {
        /// <summary>Opening center may deviate from the expected penetration point by this much.</summary>
        public const double AlignmentToleranceMm = 5.0;
        /// <summary>Slack for size comparisons.</summary>
        public const double SizeToleranceMm = 1.0;

        /// <summary>
        /// Validates every managed opening plus stale index entries (deleted openings).
        /// Writes updated status fields back to storage (transaction required).
        /// </summary>
        public static List<OpeningManagerItem> ValidateAll(Document doc)
        {
            var items = new List<OpeningManagerItem>();
            if (doc == null) return items;

            var openings = OpeningLinkStorage.GetManagedOpenings(doc);
            var indexEntries = OpeningLinkStorage.ReadIndexEntries(doc);
            var liveUniqueIds = new HashSet<string>(StringComparer.Ordinal);
            var indexDirty = false;

            foreach (var el in openings)
            {
                var fi = el as FamilyInstance;
                if (fi == null) continue;

                var rec = OpeningLinkStorage.TryReadLink(fi);
                if (rec == null)
                {
                    items.Add(BuildErrorItem(fi, "Seose andmed on vigased või loetamatud."));
                    continue;
                }

                liveUniqueIds.Add(fi.UniqueId);

                OpeningManagerItem item;
                try
                {
                    item = ValidateOpening(doc, fi, rec);

                    // Persist validation outcome on the element.
                    rec.Status = item.Status.ToString();
                    rec.OffsetMm = item.OffsetMm;
                    rec.LastValidatedUtc = DateTime.UtcNow.ToString("o");
                    OpeningLinkStorage.SaveLink(fi, rec);

                    // Keep the index entry's last known center fresh (used for recreation).
                    var entry = indexEntries.FirstOrDefault(x => x.OpeningUniqueId == fi.UniqueId);
                    if (entry == null)
                    {
                        // Self-heal: opening has a record but no index entry (e.g. created before
                        // the index existed) — register it now.
                        entry = BuildIndexEntryFromRecord(fi, rec);
                        indexEntries.Add(entry);
                        indexDirty = true;
                    }
                    var centerNow = OpeningGeometry.XyzToString(item.CurrentCenter);
                    if (!string.IsNullOrEmpty(centerNow) && entry.CenterXyz != centerNow)
                    {
                        entry.CenterXyz = centerNow;
                        entry.WidthMm = item.WidthMm;
                        entry.HeightMm = item.HeightMm;
                        entry.DepthMm = item.DepthMm;
                        indexDirty = true;
                    }
                }
                catch (Exception ex)
                {
                    OpeningManagerLog.Error("Validation failed for opening " + fi.Id, ex);
                    item = BuildErrorItem(fi, "Valideerimine ebaõnnestus: " + ex.Message);
                }

                items.Add(item);
            }

            // Index entries whose opening no longer exists → MissingOpening rows.
            foreach (var entry in indexEntries)
            {
                if (liveUniqueIds.Contains(entry.OpeningUniqueId)) continue;
                if (doc.GetElement(entry.OpeningUniqueId) != null) continue; // exists but lost its record — rare; skip
                items.Add(BuildMissingOpeningItem(doc, entry));
            }

            if (indexDirty)
            {
                try { OpeningLinkStorage.WriteIndexEntries(doc, indexEntries); }
                catch (Exception ex) { OpeningManagerLog.Error("Index update failed", ex); }
            }

            return items
                .OrderByDescending(i => (int)i.StatusSeverity)
                .ThenBy(i => i.SourceDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // ------------------------------------------------------------------
        // Single-opening validation
        // ------------------------------------------------------------------

        public static OpeningManagerItem ValidateOpening(Document doc, FamilyInstance opening, OpeningLinkRecord rec)
        {
            var item = new OpeningManagerItem
            {
                OpeningElementId = opening.Id,
                OpeningUniqueId = opening.UniqueId,
                SourceUniqueId = rec.SourceElementUniqueId,
                SourceLinkUniqueId = rec.SourceLinkInstanceUniqueId,
                HostUniqueId = rec.HostElementUniqueId,
                HostLinkUniqueId = rec.HostLinkInstanceUniqueId,
                SourceDisplayName = rec.SourceName,
                SourceCategory = rec.SourceCategory,
                SourceModelName = rec.SourceDocumentTitle,
                HostDisplayName = rec.HostName,
                HostCategory = rec.HostCategory,
                HostModelName = rec.HostDocumentTitle,
                OpeningFamilyName = opening.Symbol != null && opening.Symbol.Family != null ? opening.Symbol.Family.Name : "",
                OpeningTypeName = opening.Symbol != null ? opening.Symbol.Name : "",
                OpeningShape = string.IsNullOrEmpty(rec.OpeningShape) ? "Rectangular" : rec.OpeningShape,
                ClearanceMm = rec.ClearanceMm,
                ClearanceHeightMm = rec.ClearanceHeightMm,
                ClearanceDepthMm = rec.ClearanceDepthMm,
                IsAllowed = rec.IsAllowedException,
                AllowedReason = rec.AllowedReason,
                AllowedBy = rec.AllowedBy,
                LastValidated = rec.LastValidatedUtc,
                CanNavigate = true
            };

            // Current opening state
            var lp = opening.Location as LocationPoint;
            item.CurrentCenter = lp != null ? lp.Point : OpeningGeometry.GetLocationPoint(opening, null);
            item.WidthMm = ParamMm(opening, "Width");
            item.HeightMm = ParamMm(opening, "Height");
            item.DepthMm = ParamMm(opening, "Depth");
            item.DiameterMm = ParamMm(opening, "Diameter");
            item.LevelName = GetLevelName(doc, opening);

            var openingHashNow = OpeningGeometry.ComputeOpeningSignature(opening);
            var openingChanged = !string.IsNullOrEmpty(rec.LastKnownOpeningGeometryHash) &&
                                 openingHashNow != rec.LastKnownOpeningGeometryHash;

            // Resolve source + host (linked model aware)
            Transform sourceT, hostT;
            string sourceError, hostError;
            var source = ResolveElement(doc, rec.SourceElementUniqueId, rec.SourceLinkInstanceUniqueId, out sourceT, out sourceError);
            var host = ResolveElement(doc, rec.HostElementUniqueId, rec.HostLinkInstanceUniqueId, out hostT, out hostError);

            if (source != null)
            {
                item.SourceElementId = source.Id;
                item.SourceDisplayName = source.Name ?? rec.SourceName;
                try
                {
                    // Resolve the type in the element's own document (source may live in a link).
                    var st = source.Document.GetElement(source.GetTypeId()) as ElementType;
                    item.SourceTypeName = st != null ? st.Name : "";
                }
                catch { item.SourceTypeName = ""; }
            }
            if (host != null)
            {
                item.HostElementId = host.Id;
                item.HostDisplayName = host.Name ?? rec.HostName;
            }

            // Orientation (prefer live direction, fall back to stored)
            var dirNow = source != null ? OpeningGeometry.GetDirection(source, sourceT) : null;
            if (dirNow == null) dirNow = OpeningGeometry.TryParseXyz(rec.SourceDirectionXyz);
            item.IsVertical = OpeningGeometry.IsVerticalPenetration(dirNow, rec.HostCategory);

            // ----- missing elements -----
            if (source == null)
            {
                item.Status = OpeningStatus.MissingSource;
                item.ErrorMessage = sourceError;
                item.CanFix = false;
                return item;
            }
            if (host == null)
            {
                item.Status = OpeningStatus.MissingHost;
                item.ErrorMessage = hostError;
                item.CanFix = false;
                return item;
            }

            // ----- change detection via geometry hashes (cheap) -----
            var sourceHashNow = OpeningGeometry.ComputeElementSignature(source, sourceT);
            var hostHashNow = OpeningGeometry.ComputeElementSignature(host, hostT);
            var sourceChanged = !string.IsNullOrEmpty(rec.LastKnownSourceGeometryHash) &&
                                sourceHashNow != rec.LastKnownSourceGeometryHash;
            var hostChanged = !string.IsNullOrEmpty(rec.LastKnownHostGeometryHash) &&
                              hostHashNow != rec.LastKnownHostGeometryHash;

            // ----- expected penetration point -----
            XYZ expected = null;
            var anythingChanged = sourceChanged || hostChanged || openingChanged;
            if (anythingChanged)
            {
                expected = OpeningGeometry.ComputeExpectedPenetrationPoint(source, sourceT, host, hostT);
            }
            else
            {
                // Nothing changed since the last snapshot — the stored center is still valid.
                expected = OpeningGeometry.TryParseXyz(rec.OpeningCenterXyz);
                if (expected == null)
                    expected = OpeningGeometry.ComputeExpectedPenetrationPoint(source, sourceT, host, hostT);
            }
            item.ExpectedCenter = expected;

            // ----- size expectation -----
            double srcWmm, srcHmm;
            GetSourceSizesMm(source, out srcWmm, out srcHmm);
            var haveSourceSize = !double.IsNaN(srcWmm) && srcWmm > 0.1;
            item.ExpectedWidthMm = haveSourceSize ? srcWmm + 2.0 * rec.ClearanceMm : item.WidthMm;
            item.ExpectedHeightMm = !double.IsNaN(srcHmm) && srcHmm > 0.1 ? srcHmm + 2.0 * rec.ClearanceHeightMm : item.HeightMm;

            var thicknessFt = anythingChanged
                ? OpeningGeometry.MeasureIntersectionThickness(source, sourceT, host, hostT)
                : double.NaN;
            item.ExpectedDepthMm = !double.IsNaN(thicknessFt)
                ? OpeningGeometry.FeetToMm(thicknessFt) + 2.0 * rec.ClearanceDepthMm
                : item.DepthMm;

            var tooNarrow = haveSourceSize && item.WidthMm < item.ExpectedWidthMm - SizeToleranceMm;
            var tooLow = !double.IsNaN(srcHmm) && srcHmm > 0.1 && item.HeightMm < item.ExpectedHeightMm - SizeToleranceMm;
            item.NeedsResize = tooNarrow || tooLow;

            // ----- offset -----
            double offsetMm = 0.0;
            if (expected != null && item.CurrentCenter != null)
                offsetMm = OpeningGeometry.FeetToMm(expected.DistanceTo(item.CurrentCenter));
            item.OffsetMm = offsetMm;
            item.NeedsMove = expected != null && offsetMm > AlignmentToleranceMm;

            // ----- classify -----
            OpeningStatus status;
            if (expected == null)
            {
                // Elements exist but no longer intersect (or geometry failed) — needs attention.
                status = OpeningStatus.NotAligned;
                item.ErrorMessage = "Allikas ja alus ei ristu enam (või geomeetriat ei õnnestunud arvutada).";
                item.CanFix = false;
            }
            else if (item.NeedsMove)
            {
                if (sourceChanged && !openingChanged) status = OpeningStatus.SourceMoved;
                else if (openingChanged && !sourceChanged && !hostChanged) status = OpeningStatus.OpeningMoved;
                else if (hostChanged && !sourceChanged && !openingChanged) status = OpeningStatus.HostChanged;
                else status = OpeningStatus.NotAligned;
                item.CanFix = true;
            }
            else if (item.NeedsResize)
            {
                status = OpeningStatus.SizeMismatch;
                item.ErrorMessage = "Ava on allika jaoks liiga väike (vajalik " +
                                    item.ExpectedWidthMm.ToString("0") + "×" + item.ExpectedHeightMm.ToString("0") + " mm).";
                item.CanFix = true;
            }
            else
            {
                status = OpeningStatus.Aligned;
                item.CanFix = false;
            }

            // ----- allowed exception override (missing statuses are never masked) -----
            if (rec.IsAllowedException && IsAllowableStatus(status))
                status = OpeningStatus.Allowed;

            item.Status = status;
            return item;
        }

        private static bool IsAllowableStatus(OpeningStatus s)
        {
            return s == OpeningStatus.NotAligned || s == OpeningStatus.SourceMoved ||
                   s == OpeningStatus.OpeningMoved || s == OpeningStatus.HostChanged ||
                   s == OpeningStatus.SizeMismatch || s == OpeningStatus.ShapeMismatch ||
                   s == OpeningStatus.NeedsReview;
        }

        // ------------------------------------------------------------------
        // Missing opening (from index) + error rows
        // ------------------------------------------------------------------

        private static OpeningManagerItem BuildMissingOpeningItem(Document doc, OpeningIndexEntry entry)
        {
            var item = new OpeningManagerItem
            {
                OpeningElementId = ElementId.InvalidElementId,
                OpeningUniqueId = entry.OpeningUniqueId,
                SourceUniqueId = entry.SourceUniqueId,
                SourceLinkUniqueId = entry.SourceLinkUniqueId,
                HostUniqueId = entry.HostUniqueId,
                HostLinkUniqueId = entry.HostLinkUniqueId,
                SourceDisplayName = entry.SourceName,
                SourceCategory = entry.SourceCategory,
                HostDisplayName = entry.HostName,
                HostCategory = entry.HostCategory,
                OpeningFamilyName = entry.FamilyName,
                OpeningTypeName = entry.TypeName,
                WidthMm = entry.WidthMm,
                HeightMm = entry.HeightMm,
                DepthMm = entry.DepthMm,
                ClearanceMm = entry.ClearanceMm,
                ClearanceHeightMm = entry.ClearanceHeightMm,
                ClearanceDepthMm = entry.ClearanceDepthMm,
                Status = OpeningStatus.MissingOpening,
                ErrorMessage = "Ava element on mudelist kustutatud.",
                IsRecreate = true
            };

            Transform sourceT, hostT;
            string e1, e2;
            var source = ResolveElement(doc, entry.SourceUniqueId, entry.SourceLinkUniqueId, out sourceT, out e1);
            var host = ResolveElement(doc, entry.HostUniqueId, entry.HostLinkUniqueId, out hostT, out e2);
            if (source != null) item.SourceElementId = source.Id;
            if (host != null) item.HostElementId = host.Id;

            var dir = source != null ? OpeningGeometry.GetDirection(source, sourceT) : null;
            item.IsVertical = OpeningGeometry.IsVerticalPenetration(dir, entry.HostCategory);

            // Target for recreation: fresh penetration point, else the last known center.
            XYZ target = null;
            if (source != null && host != null)
                target = OpeningGeometry.ComputeExpectedPenetrationPoint(source, sourceT, host, hostT);
            if (target == null)
                target = OpeningGeometry.TryParseXyz(entry.CenterXyz);

            item.ExpectedCenter = target;
            item.ExpectedWidthMm = entry.WidthMm;
            item.ExpectedHeightMm = entry.HeightMm;
            item.ExpectedDepthMm = entry.DepthMm;

            if (source != null)
            {
                double w, h;
                GetSourceSizesMm(source, out w, out h);
                if (!double.IsNaN(w) && w > 0.1) item.ExpectedWidthMm = w + 2.0 * entry.ClearanceMm;
                if (!double.IsNaN(h) && h > 0.1) item.ExpectedHeightMm = h + 2.0 * entry.ClearanceHeightMm;
            }

            item.CanFix = target != null && !string.IsNullOrEmpty(entry.FamilyName);
            item.CanNavigate = target != null;
            return item;
        }

        private static OpeningManagerItem BuildErrorItem(FamilyInstance fi, string message)
        {
            return new OpeningManagerItem
            {
                OpeningElementId = fi.Id,
                OpeningUniqueId = fi.UniqueId,
                SourceDisplayName = "?",
                OpeningFamilyName = fi.Symbol != null && fi.Symbol.Family != null ? fi.Symbol.Family.Name : "",
                OpeningTypeName = fi.Symbol != null ? fi.Symbol.Name : "",
                Status = OpeningStatus.Error,
                ErrorMessage = message,
                CanNavigate = true,
                CurrentCenter = OpeningGeometry.GetLocationPoint(fi, null)
            };
        }

        private static OpeningIndexEntry BuildIndexEntryFromRecord(FamilyInstance fi, OpeningLinkRecord rec)
        {
            return new OpeningIndexEntry
            {
                OpeningUniqueId = fi.UniqueId,
                SourceUniqueId = rec.SourceElementUniqueId,
                SourceLinkUniqueId = rec.SourceLinkInstanceUniqueId,
                HostUniqueId = rec.HostElementUniqueId,
                HostLinkUniqueId = rec.HostLinkInstanceUniqueId,
                SourceName = rec.SourceName,
                SourceCategory = rec.SourceCategory,
                HostName = rec.HostName,
                HostCategory = rec.HostCategory,
                FamilyName = fi.Symbol != null && fi.Symbol.Family != null ? fi.Symbol.Family.Name : "",
                TypeName = fi.Symbol != null ? fi.Symbol.Name : "",
                CenterXyz = rec.OpeningCenterXyz,
                WidthMm = rec.OpeningWidthMm,
                HeightMm = rec.OpeningHeightMm,
                DepthMm = rec.OpeningDepthMm,
                ClearanceMm = rec.ClearanceMm,
                ClearanceHeightMm = rec.ClearanceHeightMm,
                ClearanceDepthMm = rec.ClearanceDepthMm
            };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves an element by UniqueId, optionally through a RevitLinkInstance.
        /// Returns null with an Estonian error message when the element (or link) is unavailable.
        /// The returned transform maps the element's coordinates into the host document.
        /// </summary>
        public static Element ResolveElement(Document doc, string uniqueId, string linkUniqueId,
            out Transform toHost, out string error)
        {
            toHost = Transform.Identity;
            error = "";

            if (string.IsNullOrEmpty(uniqueId))
            {
                error = "Elemendi viide puudub.";
                return null;
            }

            try
            {
                if (string.IsNullOrEmpty(linkUniqueId))
                {
                    var el = doc.GetElement(uniqueId);
                    if (el == null) error = "Element on mudelist kustutatud.";
                    return el;
                }

                var li = doc.GetElement(linkUniqueId) as RevitLinkInstance;
                if (li == null)
                {
                    error = "Lingitud mudeli eksemplari ei leitud (link eemaldatud?).";
                    return null;
                }

                var linkDoc = li.GetLinkDocument();
                if (linkDoc == null)
                {
                    error = "Lingitud mudel pole laaditud.";
                    return null;
                }

                toHost = li.GetTotalTransform();
                var linked = linkDoc.GetElement(uniqueId);
                if (linked == null) error = "Element on lingitud mudelist kustutatud.";
                return linked;
            }
            catch (Exception ex)
            {
                error = "Elemendi lahendamine ebaõnnestus: " + ex.Message;
                return null;
            }
        }

        public static void GetSourceSizesMm(Element source, out double widthMm, out double heightMm)
        {
            widthMm = double.NaN;
            heightMm = double.NaN;

            var w = OpeningGeometry.GetDoubleParam(source, "Width");
            var h = OpeningGeometry.GetDoubleParam(source, "Height");
            if (!double.IsNaN(w)) widthMm = OpeningGeometry.FeetToMm(w);
            if (!double.IsNaN(h)) heightMm = OpeningGeometry.FeetToMm(h);

            if (double.IsNaN(widthMm) || double.IsNaN(heightMm))
            {
                // Round sources (pipes/conduits/ducts): use outer diameter for both dimensions.
                var d = OpeningGeometry.GetDoubleParam(source, "Outside Diameter");
                if (double.IsNaN(d)) d = OpeningGeometry.GetDoubleParam(source, "Diameter");
                if (!double.IsNaN(d))
                {
                    var dMm = OpeningGeometry.FeetToMm(d);
                    if (double.IsNaN(widthMm)) widthMm = dMm;
                    if (double.IsNaN(heightMm)) heightMm = dMm;
                }
            }
        }

        private static double ParamMm(Element e, string name)
        {
            var v = OpeningGeometry.GetDoubleParam(e, name);
            return double.IsNaN(v) ? 0.0 : OpeningGeometry.FeetToMm(v);
        }

        private static string GetLevelName(Document doc, FamilyInstance fi)
        {
            try
            {
                var p = fi.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
                        fi.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                if (p != null && p.AsElementId() != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(p.AsElementId()) as Level;
                    if (lvl != null) return lvl.Name;
                }
            }
            catch { }
            return "";
        }
    }
}
