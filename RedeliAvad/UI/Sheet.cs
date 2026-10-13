using System;

namespace RedeliAvad
{
    /// <summary>
    /// One in-window sheet (Sentinel pattern): a title, a message, an optional text field and one or two buttons.
    /// Sheets replace modal message boxes – the content behind is dimmed and focus stays in the sheet.
    /// </summary>
    public class SheetRequest
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }

        /// <summary>Primary button is destructive (red text, e.g. delete).</summary>
        public bool IsDestructive { get; set; }

        public string PrimaryText { get; set; } = "OK";

        /// <summary>Null = no secondary (cancel) button.</summary>
        public string SecondaryText { get; set; }

        /// <summary>Non-null = show a multi-line text field with this label.</summary>
        public string InputLabel { get; set; }
        public string InputText { get; set; } = "";

        /// <summary>Called with (primary chosen, input text).</summary>
        public Action<bool, string> Completed { get; set; }

        public bool HasSecondary => SecondaryText != null;
        public bool HasInput => InputLabel != null;
    }

    /// <summary>Implemented by the window that hosts sheets (MainWindow).</summary>
    public interface ISheetPresenter
    {
        void ShowSheet(SheetRequest request);
    }
}
