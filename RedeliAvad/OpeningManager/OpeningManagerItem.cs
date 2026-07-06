using System;
using System.ComponentModel;
using Autodesk.Revit.DB;

namespace RedeliAvad
{
    /// <summary>
    /// Row model for the Opening Manager list. Produced by <see cref="OpeningValidationService"/>;
    /// carries display values plus the payload needed by row actions (navigate/fix/allow/...).
    /// </summary>
    public class OpeningManagerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ---------- identity ----------
        public ElementId OpeningElementId { get; set; }
        public string OpeningUniqueId { get; set; } = "";
        public ElementId SourceElementId { get; set; }
        public string SourceUniqueId { get; set; } = "";
        public string SourceLinkUniqueId { get; set; } = "";
        public ElementId HostElementId { get; set; }
        public string HostUniqueId { get; set; } = "";
        public string HostLinkUniqueId { get; set; } = "";

        // ---------- display ----------
        public string SourceDisplayName { get; set; } = "";
        public string SourceCategory { get; set; } = "";
        public string SourceTypeName { get; set; } = "";
        public string SourceModelName { get; set; } = "";
        public string HostDisplayName { get; set; } = "";
        public string HostCategory { get; set; } = "";
        public string HostModelName { get; set; } = "";
        public string LevelName { get; set; } = "";
        public string OpeningFamilyName { get; set; } = "";
        public string OpeningTypeName { get; set; } = "";
        public string OpeningShape { get; set; } = "";
        public double DiameterMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double DepthMm { get; set; }
        public double OffsetMm { get; set; }
        public double ClearanceMm { get; set; }
        public double ClearanceHeightMm { get; set; }
        public double ClearanceDepthMm { get; set; }
        public bool IsVertical { get; set; }
        public string LastValidated { get; set; } = "";
        public string ErrorMessage { get; set; } = "";

        // ---------- status ----------
        private OpeningStatus _status = OpeningStatus.NeedsReview;
        public OpeningStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                Raise("Status"); Raise("StatusText"); Raise("StatusColor"); Raise("StatusSeverity");
            }
        }

        public string StatusText => _isDirty
            ? OpeningStatusInfo.GetText(_status) + " • muudetud"
            : OpeningStatusInfo.GetText(_status);

        public OpeningStatusSeverity StatusSeverity => OpeningStatusInfo.GetSeverity(_status);
        public string StatusColor => OpeningStatusInfo.GetColorHex(_status);

        private bool _isDirty;
        /// <summary>Set by the DocumentChanged listener; cleared by the next validation run.</summary>
        public bool IsDirty
        {
            get => _isDirty;
            set { _isDirty = value; Raise("IsDirty"); Raise("StatusText"); }
        }

        public bool IsAllowed { get; set; }
        /// <summary>Convenience for XAML visibility binding of the "mark allowed" button.</summary>
        public bool IsAllowedInverse => !IsAllowed;
        public string AllowedReason { get; set; } = "";
        public string AllowedBy { get; set; } = "";

        // ---------- capabilities ----------
        public bool CanFix { get; set; }
        public bool CanNavigate { get; set; }
        /// <summary>True when the opening element is gone and Fix means "recreate".</summary>
        public bool IsRecreate { get; set; }

        // ---------- fix payload (computed during validation) ----------
        /// <summary>Expected penetration point in host coordinates (internal units); null when unknown.</summary>
        public XYZ ExpectedCenter { get; set; }
        /// <summary>Current opening insertion point; null when the opening is missing.</summary>
        public XYZ CurrentCenter { get; set; }
        public double ExpectedWidthMm { get; set; }
        public double ExpectedHeightMm { get; set; }
        public double ExpectedDepthMm { get; set; }
        public bool NeedsResize { get; set; }
        public bool NeedsMove { get; set; }

        // ---------- derived texts ----------
        public string SizeText
        {
            get
            {
                if (DiameterMm > 0.1) return "Ø" + DiameterMm.ToString("0");
                if (WidthMm > 0.1 || HeightMm > 0.1)
                    return WidthMm.ToString("0") + "×" + HeightMm.ToString("0");
                return "–";
            }
        }

        public string OffsetText => OffsetMm > 0.05 ? OffsetMm.ToString("0.#") + " mm" : "0 mm";

        public string OrientationText => IsVertical ? "Vertikaalne" : "Horisontaalne";

        public string NotesText
        {
            get
            {
                if (IsAllowed && !string.IsNullOrEmpty(AllowedReason)) return AllowedReason;
                if (!string.IsNullOrEmpty(ErrorMessage)) return ErrorMessage;
                return "";
            }
        }

        public string SourceText =>
            string.IsNullOrEmpty(SourceCategory) ? SourceDisplayName : SourceCategory + " – " + SourceDisplayName;

        /// <summary>Single lowercase haystack for the search box.</summary>
        public string SearchBlob
        {
            get
            {
                var idText = OpeningElementId != null ? OpeningElementId.IntegerValue.ToString() : "";
                return (idText + " " + SourceDisplayName + " " + SourceCategory + " " + SourceTypeName + " " +
                        SourceModelName + " " + HostDisplayName + " " + HostCategory + " " + HostModelName + " " +
                        OpeningFamilyName + " " + OpeningTypeName + " " + LevelName + " " + StatusText + " " +
                        AllowedReason + " " + ErrorMessage).ToLowerInvariant();
            }
        }
    }
}
