namespace RedeliAvad
{
    /// <summary>Validation status of a managed opening.</summary>
    public enum OpeningStatus
    {
        Aligned,
        NotAligned,
        SourceMoved,
        OpeningMoved,
        HostChanged,
        MissingOpening,
        MissingSource,
        MissingHost,
        SizeMismatch,
        ShapeMismatch,
        Allowed,
        NeedsReview,
        Error
    }

    /// <summary>Coarse severity: drives the status glyph and colour in the manager UI.</summary>
    public enum OpeningStatusSeverity
    {
        Ok,
        Info,
        Warning,
        Error
    }

    /// <summary>Display helpers (Estonian UI texts + severity; colours come from the palette Status.* keys).</summary>
    public static class OpeningStatusInfo
    {
        public static string GetText(OpeningStatus status)
        {
            switch (status)
            {
                case OpeningStatus.Aligned: return "Joondatud";
                case OpeningStatus.NotAligned: return "Joondamata";
                case OpeningStatus.SourceMoved: return "Allikas liikunud";
                case OpeningStatus.OpeningMoved: return "Ava liikunud";
                case OpeningStatus.HostChanged: return "Alus muutunud";
                case OpeningStatus.MissingOpening: return "Ava puudub";
                case OpeningStatus.MissingSource: return "Allikas puudub";
                case OpeningStatus.MissingHost: return "Alus puudub";
                case OpeningStatus.SizeMismatch: return "Vale suurus";
                case OpeningStatus.ShapeMismatch: return "Vale kuju";
                case OpeningStatus.Allowed: return "Lubatud";
                case OpeningStatus.NeedsReview: return "Vajab ülevaatust";
                default: return "Viga";
            }
        }

        public static OpeningStatusSeverity GetSeverity(OpeningStatus status)
        {
            switch (status)
            {
                case OpeningStatus.Aligned: return OpeningStatusSeverity.Ok;
                case OpeningStatus.Allowed: return OpeningStatusSeverity.Info;
                case OpeningStatus.NeedsReview:
                case OpeningStatus.SourceMoved:
                case OpeningStatus.OpeningMoved:
                case OpeningStatus.HostChanged:
                case OpeningStatus.SizeMismatch:
                case OpeningStatus.ShapeMismatch:
                case OpeningStatus.NotAligned: return OpeningStatusSeverity.Warning;
                default: return OpeningStatusSeverity.Error; // Missing*, Error
            }
        }
    }
}
