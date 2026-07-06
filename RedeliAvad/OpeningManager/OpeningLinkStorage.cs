using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;

namespace RedeliAvad
{
    /// <summary>
    /// Extensible Storage service for the Opening Manager.
    ///
    /// Two schemas are used:
    ///  1. Link schema ("RKTools.Openings.OpeningLink") — one entity per plugin-created opening,
    ///     stored on the opening FamilyInstance itself. Holds the persistent relationship
    ///     opening ↔ source element ↔ host element (+ linked model info) and geometry snapshots.
    ///  2. Index schema ("RKTools.Openings.Index") — a single project-level DataStorage element with
    ///     one compact JSON entry per managed opening. Because the link entity dies together with the
    ///     opening element, the index is what makes deleted openings ("Ava puudub") detectable and
    ///     recreatable.
    ///
    /// Rules honoured here:
    ///  - Schema GUIDs are stable compile-time constants. Never regenerate them.
    ///  - Elements are referenced primarily by UniqueId; ElementId integers are convenience only.
    ///  - For linked elements both the RevitLinkInstance UniqueId (host doc) and the element
    ///    UniqueId (link doc) are stored. Resolution: host doc → link instance → link doc → element.
    ///    If the link is unloaded, GetLinkDocument() returns null and callers report MissingHost /
    ///    MissingSource with a "link unloaded" message instead of failing.
    ///  - A Version int is stored for forward migrations (see MigrateRecordIfNeeded).
    /// </summary>
    internal static class OpeningLinkStorage
    {
        // ------------------------------------------------------------------
        // Stable identifiers — DO NOT CHANGE once shipped.
        // ------------------------------------------------------------------
        public static readonly Guid LinkSchemaGuid = new Guid("7C2A9E64-51B3-4F8D-9A07-D2E6C4B81F35");
        public static readonly Guid IndexSchemaGuid = new Guid("3B6E0D2A-8F41-4C57-B2D9-64A1E7F0C982");
        public const int CurrentSchemaVersion = 1;
        private const string VendorId = "RKTL";
        private const string IndexStorageName = "RKTools.Openings.Index";

        // Link schema field names (schema names allow only alphanumerics/underscore).
        private const string F_Version = "Version";
        private const string F_OpeningUniqueId = "OpeningUniqueId";
        private const string F_OpeningElementId = "OpeningElementIdInteger";
        private const string F_SourceUniqueId = "SourceElementUniqueId";
        private const string F_SourceElementId = "SourceElementIdInteger";
        private const string F_SourceCategory = "SourceCategory";
        private const string F_SourceName = "SourceName";
        private const string F_SourceDocTitle = "SourceDocumentTitle";
        private const string F_SourceDocPath = "SourceDocumentPath";
        private const string F_SourceLinkUniqueId = "SourceLinkInstanceUniqueId";
        private const string F_HostUniqueId = "HostElementUniqueId";
        private const string F_HostElementId = "HostElementIdInteger";
        private const string F_HostCategory = "HostCategory";
        private const string F_HostName = "HostName";
        private const string F_HostDocTitle = "HostDocumentTitle";
        private const string F_HostDocPath = "HostDocumentPath";
        private const string F_HostLinkUniqueId = "HostLinkInstanceUniqueId";
        private const string F_OpeningType = "OpeningType";
        private const string F_OpeningShape = "OpeningShape";
        private const string F_OpeningWidthMm = "OpeningWidthMm";
        private const string F_OpeningHeightMm = "OpeningHeightMm";
        private const string F_OpeningDiameterMm = "OpeningDiameterMm";
        private const string F_OpeningDepthMm = "OpeningDepthMm";
        private const string F_OffsetMm = "OffsetMm";
        private const string F_ClearanceMm = "ClearanceMm";
        private const string F_ClearanceHeightMm = "ClearanceHeightMm";
        private const string F_ClearanceDepthMm = "ClearanceDepthMm";
        private const string F_SourceCenterXyz = "SourceCenterXyz";
        private const string F_OpeningCenterXyz = "OpeningCenterXyz";
        private const string F_SourceDirectionXyz = "SourceDirectionXyz";
        private const string F_HostNormalXyz = "HostNormalXyz";
        private const string F_LastValidatedUtc = "LastValidatedUtc";
        private const string F_SourceGeomHash = "LastKnownSourceGeometryHash";
        private const string F_OpeningGeomHash = "LastKnownOpeningGeometryHash";
        private const string F_HostGeomHash = "LastKnownHostGeometryHash";
        private const string F_Status = "Status";
        private const string F_IsAllowed = "IsAllowedException";
        private const string F_AllowedBy = "AllowedBy";
        private const string F_AllowedUtc = "AllowedUtc";
        private const string F_AllowedReason = "AllowedReason";
        private const string F_Notes = "Notes";

        private const string F_IndexEntries = "Entries";

        private static readonly string[] StringFields =
        {
            F_OpeningUniqueId, F_SourceUniqueId, F_SourceCategory, F_SourceName, F_SourceDocTitle,
            F_SourceDocPath, F_SourceLinkUniqueId, F_HostUniqueId, F_HostCategory, F_HostName,
            F_HostDocTitle, F_HostDocPath, F_HostLinkUniqueId, F_OpeningType, F_OpeningShape,
            F_SourceCenterXyz, F_OpeningCenterXyz, F_SourceDirectionXyz, F_HostNormalXyz,
            F_LastValidatedUtc, F_SourceGeomHash, F_OpeningGeomHash, F_HostGeomHash, F_Status,
            F_AllowedBy, F_AllowedUtc, F_AllowedReason, F_Notes
        };

        private static readonly string[] LengthFields =
        {
            F_OpeningWidthMm, F_OpeningHeightMm, F_OpeningDiameterMm, F_OpeningDepthMm,
            F_OffsetMm, F_ClearanceMm, F_ClearanceHeightMm, F_ClearanceDepthMm
        };

        // ------------------------------------------------------------------
        // Schemas
        // ------------------------------------------------------------------

        public static Schema GetLinkSchema()
        {
            var schema = Schema.Lookup(LinkSchemaGuid);
            if (schema != null) return schema;

            var sb = new SchemaBuilder(LinkSchemaGuid);
            sb.SetSchemaName("RKToolsOpeningsOpeningLink"); // = RKTools.Openings.OpeningLink (dots not allowed)
            sb.SetVendorId(VendorId);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetDocumentation("RK Tools opening link: persistent relationship between a created opening, " +
                                "its source MEP element and the penetrated host element.");

            sb.AddSimpleField(F_Version, typeof(int));
            sb.AddSimpleField(F_OpeningElementId, typeof(int));
            sb.AddSimpleField(F_SourceElementId, typeof(int));
            sb.AddSimpleField(F_HostElementId, typeof(int));
            sb.AddSimpleField(F_IsAllowed, typeof(bool));

            foreach (var name in StringFields)
                sb.AddSimpleField(name, typeof(string));

            foreach (var name in LengthFields)
                sb.AddSimpleField(name, typeof(double)).SetSpec(SpecTypeId.Length);

            return sb.Finish();
        }

        public static Schema GetIndexSchema()
        {
            var schema = Schema.Lookup(IndexSchemaGuid);
            if (schema != null) return schema;

            var sb = new SchemaBuilder(IndexSchemaGuid);
            sb.SetSchemaName("RKToolsOpeningsIndex");
            sb.SetVendorId(VendorId);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetDocumentation("RK Tools opening index: compact JSON entry per managed opening, " +
                                "used to detect deleted openings and to recreate them.");
            sb.AddArrayField(F_IndexEntries, typeof(string));
            return sb.Finish();
        }

        // ------------------------------------------------------------------
        // Link record: save / read / remove
        // ------------------------------------------------------------------

        /// <summary>Writes the record onto the opening element. Caller must have an open transaction.</summary>
        public static void SaveLink(Element opening, OpeningLinkRecord r)
        {
            if (opening == null || r == null) return;
            var schema = GetLinkSchema();
            var e = new Entity(schema);

            e.Set(F_Version, r.Version);
            e.Set(F_OpeningUniqueId, r.OpeningUniqueId ?? "");
            e.Set(F_OpeningElementId, r.OpeningElementIdValue);
            e.Set(F_SourceUniqueId, r.SourceElementUniqueId ?? "");
            e.Set(F_SourceElementId, r.SourceElementIdValue);
            e.Set(F_SourceCategory, r.SourceCategory ?? "");
            e.Set(F_SourceName, r.SourceName ?? "");
            e.Set(F_SourceDocTitle, r.SourceDocumentTitle ?? "");
            e.Set(F_SourceDocPath, r.SourceDocumentPath ?? "");
            e.Set(F_SourceLinkUniqueId, r.SourceLinkInstanceUniqueId ?? "");
            e.Set(F_HostUniqueId, r.HostElementUniqueId ?? "");
            e.Set(F_HostElementId, r.HostElementIdValue);
            e.Set(F_HostCategory, r.HostCategory ?? "");
            e.Set(F_HostName, r.HostName ?? "");
            e.Set(F_HostDocTitle, r.HostDocumentTitle ?? "");
            e.Set(F_HostDocPath, r.HostDocumentPath ?? "");
            e.Set(F_HostLinkUniqueId, r.HostLinkInstanceUniqueId ?? "");
            e.Set(F_OpeningType, r.OpeningType ?? "");
            e.Set(F_OpeningShape, r.OpeningShape ?? "");
            e.Set(F_OpeningWidthMm, r.OpeningWidthMm, UnitTypeId.Millimeters);
            e.Set(F_OpeningHeightMm, r.OpeningHeightMm, UnitTypeId.Millimeters);
            e.Set(F_OpeningDiameterMm, r.OpeningDiameterMm, UnitTypeId.Millimeters);
            e.Set(F_OpeningDepthMm, r.OpeningDepthMm, UnitTypeId.Millimeters);
            e.Set(F_OffsetMm, r.OffsetMm, UnitTypeId.Millimeters);
            e.Set(F_ClearanceMm, r.ClearanceMm, UnitTypeId.Millimeters);
            e.Set(F_ClearanceHeightMm, r.ClearanceHeightMm, UnitTypeId.Millimeters);
            e.Set(F_ClearanceDepthMm, r.ClearanceDepthMm, UnitTypeId.Millimeters);
            e.Set(F_SourceCenterXyz, r.SourceCenterXyz ?? "");
            e.Set(F_OpeningCenterXyz, r.OpeningCenterXyz ?? "");
            e.Set(F_SourceDirectionXyz, r.SourceDirectionXyz ?? "");
            e.Set(F_HostNormalXyz, r.HostNormalXyz ?? "");
            e.Set(F_LastValidatedUtc, r.LastValidatedUtc ?? "");
            e.Set(F_SourceGeomHash, r.LastKnownSourceGeometryHash ?? "");
            e.Set(F_OpeningGeomHash, r.LastKnownOpeningGeometryHash ?? "");
            e.Set(F_HostGeomHash, r.LastKnownHostGeometryHash ?? "");
            e.Set(F_Status, r.Status ?? "");
            e.Set(F_IsAllowed, r.IsAllowedException);
            e.Set(F_AllowedBy, r.AllowedBy ?? "");
            e.Set(F_AllowedUtc, r.AllowedUtc ?? "");
            e.Set(F_AllowedReason, r.AllowedReason ?? "");
            e.Set(F_Notes, r.Notes ?? "");

            opening.SetEntity(e);
        }

        /// <summary>Reads the record from an opening element. Returns null when no valid entity exists.</summary>
        public static OpeningLinkRecord TryReadLink(Element opening)
        {
            if (opening == null) return null;
            try
            {
                var schema = GetLinkSchema();
                var e = opening.GetEntity(schema);
                if (e == null || !e.IsValid()) return null;

                var r = new OpeningLinkRecord
                {
                    Version = e.Get<int>(F_Version),
                    OpeningUniqueId = e.Get<string>(F_OpeningUniqueId),
                    OpeningElementIdValue = e.Get<int>(F_OpeningElementId),
                    SourceElementUniqueId = e.Get<string>(F_SourceUniqueId),
                    SourceElementIdValue = e.Get<int>(F_SourceElementId),
                    SourceCategory = e.Get<string>(F_SourceCategory),
                    SourceName = e.Get<string>(F_SourceName),
                    SourceDocumentTitle = e.Get<string>(F_SourceDocTitle),
                    SourceDocumentPath = e.Get<string>(F_SourceDocPath),
                    SourceLinkInstanceUniqueId = e.Get<string>(F_SourceLinkUniqueId),
                    HostElementUniqueId = e.Get<string>(F_HostUniqueId),
                    HostElementIdValue = e.Get<int>(F_HostElementId),
                    HostCategory = e.Get<string>(F_HostCategory),
                    HostName = e.Get<string>(F_HostName),
                    HostDocumentTitle = e.Get<string>(F_HostDocTitle),
                    HostDocumentPath = e.Get<string>(F_HostDocPath),
                    HostLinkInstanceUniqueId = e.Get<string>(F_HostLinkUniqueId),
                    OpeningType = e.Get<string>(F_OpeningType),
                    OpeningShape = e.Get<string>(F_OpeningShape),
                    OpeningWidthMm = e.Get<double>(F_OpeningWidthMm, UnitTypeId.Millimeters),
                    OpeningHeightMm = e.Get<double>(F_OpeningHeightMm, UnitTypeId.Millimeters),
                    OpeningDiameterMm = e.Get<double>(F_OpeningDiameterMm, UnitTypeId.Millimeters),
                    OpeningDepthMm = e.Get<double>(F_OpeningDepthMm, UnitTypeId.Millimeters),
                    OffsetMm = e.Get<double>(F_OffsetMm, UnitTypeId.Millimeters),
                    ClearanceMm = e.Get<double>(F_ClearanceMm, UnitTypeId.Millimeters),
                    ClearanceHeightMm = e.Get<double>(F_ClearanceHeightMm, UnitTypeId.Millimeters),
                    ClearanceDepthMm = e.Get<double>(F_ClearanceDepthMm, UnitTypeId.Millimeters),
                    SourceCenterXyz = e.Get<string>(F_SourceCenterXyz),
                    OpeningCenterXyz = e.Get<string>(F_OpeningCenterXyz),
                    SourceDirectionXyz = e.Get<string>(F_SourceDirectionXyz),
                    HostNormalXyz = e.Get<string>(F_HostNormalXyz),
                    LastValidatedUtc = e.Get<string>(F_LastValidatedUtc),
                    LastKnownSourceGeometryHash = e.Get<string>(F_SourceGeomHash),
                    LastKnownOpeningGeometryHash = e.Get<string>(F_OpeningGeomHash),
                    LastKnownHostGeometryHash = e.Get<string>(F_HostGeomHash),
                    Status = e.Get<string>(F_Status),
                    IsAllowedException = e.Get<bool>(F_IsAllowed),
                    AllowedBy = e.Get<string>(F_AllowedBy),
                    AllowedUtc = e.Get<string>(F_AllowedUtc),
                    AllowedReason = e.Get<string>(F_AllowedReason),
                    Notes = e.Get<string>(F_Notes)
                };

                return MigrateRecordIfNeeded(r);
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("TryReadLink failed for element " + opening.Id, ex);
                return null;
            }
        }

        /// <summary>
        /// Migration hook. Version 1 is current; when the schema evolves, bump
        /// <see cref="CurrentSchemaVersion"/>, add a new schema GUID if fields change shape,
        /// read old entities here and map them onto the new record.
        /// </summary>
        private static OpeningLinkRecord MigrateRecordIfNeeded(OpeningLinkRecord r)
        {
            if (r.Version < CurrentSchemaVersion)
            {
                // No older versions exist yet; future migrations go here.
                r.Version = CurrentSchemaVersion;
            }
            return r;
        }

        /// <summary>Removes the link entity from an opening. Caller must have an open transaction.</summary>
        public static void RemoveLink(Element opening)
        {
            if (opening == null) return;
            try { opening.DeleteEntity(GetLinkSchema()); }
            catch (Exception ex) { OpeningManagerLog.Error("RemoveLink failed", ex); }
        }

        /// <summary>All elements in the document that carry a link record (fast quick-filter collector).</summary>
        public static IList<Element> GetManagedOpenings(Document doc)
        {
            if (doc == null) return new List<Element>();
            return new FilteredElementCollector(doc)
                .WherePasses(new ExtensibleStorageFilter(LinkSchemaGuid))
                .WhereElementIsNotElementType()
                .ToElements();
        }

        // ------------------------------------------------------------------
        // Project index (DataStorage)
        // ------------------------------------------------------------------

        private static DataStorage FindIndexStorage(Document doc)
        {
            var schema = GetIndexSchema();
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(ds =>
                {
                    try { var ent = ds.GetEntity(schema); return ent != null && ent.IsValid(); }
                    catch { return false; }
                });
        }

        public static List<OpeningIndexEntry> ReadIndexEntries(Document doc)
        {
            var result = new List<OpeningIndexEntry>();
            try
            {
                var ds = FindIndexStorage(doc);
                if (ds == null) return result;

                var ent = ds.GetEntity(GetIndexSchema());
                var raw = ent.Get<IList<string>>(F_IndexEntries);
                if (raw == null) return result;

                foreach (var json in raw)
                {
                    if (string.IsNullOrEmpty(json)) continue;
                    try
                    {
                        var entry = JsonConvert.DeserializeObject<OpeningIndexEntry>(json);
                        if (entry != null && !string.IsNullOrEmpty(entry.OpeningUniqueId))
                            result.Add(entry);
                    }
                    catch { /* skip corrupt entry */ }
                }
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("ReadIndexEntries failed", ex);
            }
            return result;
        }

        /// <summary>Writes the full entry list. Caller must have an open transaction.</summary>
        public static void WriteIndexEntries(Document doc, List<OpeningIndexEntry> entries)
        {
            var schema = GetIndexSchema();
            var ds = FindIndexStorage(doc);
            if (ds == null)
            {
                ds = DataStorage.Create(doc);
                try { ds.Name = IndexStorageName; } catch { /* name is cosmetic */ }
            }

            var ent = new Entity(schema);
            IList<string> raw = entries.Select(x => JsonConvert.SerializeObject(x, Formatting.None)).ToList();
            ent.Set(F_IndexEntries, raw);
            ds.SetEntity(ent);
        }

        /// <summary>Adds or replaces one entry. Caller must have an open transaction.</summary>
        public static void AddOrUpdateIndexEntry(Document doc, OpeningIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.OpeningUniqueId)) return;
            var entries = ReadIndexEntries(doc);
            entries.RemoveAll(x => x.OpeningUniqueId == entry.OpeningUniqueId);
            entries.Add(entry);
            WriteIndexEntries(doc, entries);
        }

        /// <summary>Removes one entry. Caller must have an open transaction.</summary>
        public static void RemoveIndexEntry(Document doc, string openingUniqueId)
        {
            if (string.IsNullOrEmpty(openingUniqueId)) return;
            var entries = ReadIndexEntries(doc);
            if (entries.RemoveAll(x => x.OpeningUniqueId == openingUniqueId) > 0)
                WriteIndexEntries(doc, entries);
        }

        // ------------------------------------------------------------------
        // Record construction at placement time
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds and stores the full link record + index entry for a freshly placed opening.
        /// Must be called inside the placement transaction, after instance parameters are set.
        /// </summary>
        public static void SaveNewLink(
            Document doc,
            FamilyInstance opening,
            Element source,
            Element host,
            RevitLinkInstance hostLink,
            XYZ placementPoint,
            double widthFt,
            double heightFt,
            double depthFt,
            double clearanceWidthFt,
            double clearanceHeightFt,
            double clearanceDepthFt)
        {
            var linkDoc = hostLink != null ? hostLink.GetLinkDocument() : null;
            var linkTransform = hostLink != null ? hostLink.GetTotalTransform() : Transform.Identity;

            var sourceDir = OpeningGeometry.GetDirection(source, null);
            XYZ hostNormal = null;
            var wall = host as Wall;
            if (wall != null)
            {
                try { hostNormal = linkTransform.OfVector(wall.Orientation); } catch { }
            }

            var record = new OpeningLinkRecord
            {
                Version = CurrentSchemaVersion,

                OpeningUniqueId = opening.UniqueId,
                OpeningElementIdValue = opening.Id.IntegerValue,

                SourceElementUniqueId = source.UniqueId,
                SourceElementIdValue = source.Id.IntegerValue,
                SourceCategory = source.Category != null ? source.Category.Name : "",
                SourceName = source.Name ?? "",
                SourceDocumentTitle = doc.Title ?? "",
                SourceDocumentPath = SafePath(doc),
                SourceLinkInstanceUniqueId = "", // source (cable tray) lives in the current model

                HostElementUniqueId = host.UniqueId,
                HostElementIdValue = host.Id.IntegerValue,
                HostCategory = host.Category != null ? host.Category.Name : "",
                HostName = host.Name ?? "",
                HostDocumentTitle = linkDoc != null ? (linkDoc.Title ?? "") : (doc.Title ?? ""),
                HostDocumentPath = linkDoc != null ? SafePath(linkDoc) : SafePath(doc),
                HostLinkInstanceUniqueId = hostLink != null ? hostLink.UniqueId : "",

                OpeningType = "Rectangular",
                OpeningShape = "Rectangular",
                OpeningWidthMm = OpeningGeometry.FeetToMm(widthFt),
                OpeningHeightMm = OpeningGeometry.FeetToMm(heightFt),
                OpeningDiameterMm = 0.0,
                OpeningDepthMm = OpeningGeometry.FeetToMm(depthFt),
                OffsetMm = 0.0,
                ClearanceMm = OpeningGeometry.FeetToMm(clearanceWidthFt),
                ClearanceHeightMm = OpeningGeometry.FeetToMm(clearanceHeightFt),
                ClearanceDepthMm = OpeningGeometry.FeetToMm(clearanceDepthFt),

                SourceCenterXyz = OpeningGeometry.XyzToString(OpeningGeometry.GetLocationPoint(source, null)),
                OpeningCenterXyz = OpeningGeometry.XyzToString(placementPoint),
                SourceDirectionXyz = OpeningGeometry.XyzToString(sourceDir),
                HostNormalXyz = OpeningGeometry.XyzToString(hostNormal),

                LastValidatedUtc = DateTime.UtcNow.ToString("o"),
                LastKnownSourceGeometryHash = OpeningGeometry.ComputeElementSignature(source, null),
                LastKnownOpeningGeometryHash = OpeningGeometry.ComputeOpeningSignature(opening),
                LastKnownHostGeometryHash = OpeningGeometry.ComputeElementSignature(host, linkTransform),

                Status = OpeningStatus.Aligned.ToString(),
                IsAllowedException = false
            };

            SaveLink(opening, record);

            AddOrUpdateIndexEntry(doc, new OpeningIndexEntry
            {
                OpeningUniqueId = record.OpeningUniqueId,
                SourceUniqueId = record.SourceElementUniqueId,
                SourceLinkUniqueId = record.SourceLinkInstanceUniqueId,
                HostUniqueId = record.HostElementUniqueId,
                HostLinkUniqueId = record.HostLinkInstanceUniqueId,
                SourceName = record.SourceName,
                SourceCategory = record.SourceCategory,
                HostName = record.HostName,
                HostCategory = record.HostCategory,
                FamilyName = opening.Symbol != null && opening.Symbol.Family != null ? opening.Symbol.Family.Name : "",
                TypeName = opening.Symbol != null ? opening.Symbol.Name : "",
                CenterXyz = record.OpeningCenterXyz,
                WidthMm = record.OpeningWidthMm,
                HeightMm = record.OpeningHeightMm,
                DepthMm = record.OpeningDepthMm,
                ClearanceMm = record.ClearanceMm,
                ClearanceHeightMm = record.ClearanceHeightMm,
                ClearanceDepthMm = record.ClearanceDepthMm
            });
        }

        private static string SafePath(Document d)
        {
            try { return d.PathName ?? ""; }
            catch { return ""; }
        }
    }
}
