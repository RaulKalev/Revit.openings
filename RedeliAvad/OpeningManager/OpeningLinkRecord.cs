using System;

namespace RedeliAvad
{
    /// <summary>
    /// In-memory mirror of the Extensible Storage record stored on every plugin-created opening.
    /// All lengths are millimetres; all XYZ values are serialized internal (feet) coordinates
    /// in the HOST document coordinate system ("x;y;z", invariant culture).
    /// </summary>
    public class OpeningLinkRecord
    {
        public int Version { get; set; } = OpeningLinkStorage.CurrentSchemaVersion;

        // Opening
        public string OpeningUniqueId { get; set; } = "";
        public int OpeningElementIdValue { get; set; }

        // Source (cable tray / conduit / pipe / duct ...)
        public string SourceElementUniqueId { get; set; } = "";
        public int SourceElementIdValue { get; set; }
        public string SourceCategory { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string SourceDocumentTitle { get; set; } = "";
        public string SourceDocumentPath { get; set; } = "";
        /// <summary>Empty when the source element lives in the current model.</summary>
        public string SourceLinkInstanceUniqueId { get; set; } = "";

        // Host (wall / floor / framing ...)
        public string HostElementUniqueId { get; set; } = "";
        public int HostElementIdValue { get; set; }
        public string HostCategory { get; set; } = "";
        public string HostName { get; set; } = "";
        public string HostDocumentTitle { get; set; } = "";
        public string HostDocumentPath { get; set; } = "";
        /// <summary>Empty when the host element lives in the current model.</summary>
        public string HostLinkInstanceUniqueId { get; set; } = "";

        // Opening description
        public string OpeningType { get; set; } = "";
        public string OpeningShape { get; set; } = "";
        public double OpeningWidthMm { get; set; }
        public double OpeningHeightMm { get; set; }
        public double OpeningDiameterMm { get; set; }
        public double OpeningDepthMm { get; set; }
        public double OffsetMm { get; set; }
        /// <summary>Width clearance per side (mm) applied at creation.</summary>
        public double ClearanceMm { get; set; }
        /// <summary>Height clearance per side (mm) applied at creation.</summary>
        public double ClearanceHeightMm { get; set; }
        /// <summary>Depth clearance per side (mm) applied at creation.</summary>
        public double ClearanceDepthMm { get; set; }

        // Geometry snapshot (host document coordinates, internal units)
        public string SourceCenterXyz { get; set; } = "";
        public string OpeningCenterXyz { get; set; } = "";
        public string SourceDirectionXyz { get; set; } = "";
        public string HostNormalXyz { get; set; } = "";

        // Validation bookkeeping
        public string LastValidatedUtc { get; set; } = "";
        public string LastKnownSourceGeometryHash { get; set; } = "";
        public string LastKnownOpeningGeometryHash { get; set; } = "";
        public string LastKnownHostGeometryHash { get; set; } = "";
        public string Status { get; set; } = "";

        // Allowed exception workflow
        public bool IsAllowedException { get; set; }
        public string AllowedBy { get; set; } = "";
        public string AllowedUtc { get; set; } = "";
        public string AllowedReason { get; set; } = "";

        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// Compact per-opening entry kept in the project-level index (DataStorage element).
    /// The index makes deleted openings detectable (their on-element record disappears
    /// together with the element) and carries enough data to recreate an opening.
    /// </summary>
    public class OpeningIndexEntry
    {
        public string OpeningUniqueId { get; set; } = "";
        public string SourceUniqueId { get; set; } = "";
        public string SourceLinkUniqueId { get; set; } = "";
        public string HostUniqueId { get; set; } = "";
        public string HostLinkUniqueId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string SourceCategory { get; set; } = "";
        public string HostName { get; set; } = "";
        public string HostCategory { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public string TypeName { get; set; } = "";
        /// <summary>Last known opening center ("x;y;z", internal units, host coords).</summary>
        public string CenterXyz { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double DepthMm { get; set; }
        public double ClearanceMm { get; set; }
        public double ClearanceHeightMm { get; set; }
        public double ClearanceDepthMm { get; set; }
    }
}
