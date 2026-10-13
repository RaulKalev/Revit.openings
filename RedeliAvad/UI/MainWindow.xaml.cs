using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MaterialDesignThemes.Wpf;
using TextBox = System.Windows.Controls.TextBox;

namespace RedeliAvad
{
    /// <summary>
    /// Modeless "Avade loomine" window: the creation form on the left, the Opening Manager panel on the right
    /// (the window grows when it opens). Results and validation messages appear in the action bar's result line;
    /// questions use in-window sheets instead of message boxes.
    /// </summary>
    public partial class MainWindow : Window, ISheetPresenter
    {
        #region Fields
        private UIDocument _uiDoc;
        private Document _doc;
        private View _currentView;

        private readonly ThemeManager _themeManager;

        // Core placement + navigation via external events
        private PlaceIntersectionsHandler _handler;
        private ExternalEvent _placeEvent;
        private FocusNavigateHandler _navHandler;
        private ExternalEvent _navEvent;

        // Opening Manager panel. The handler + event are created in the constructor's valid API context —
        // ExternalEvent.Create may not be called later from a WPF button click.
        private OpeningManagerHandler _managerHandler;
        private ExternalEvent _managerEvent;
        private bool _isManagerOpen;
        private double _managerBaseWidth;
        private const double ManagerPanelWidth = 780;
        private const double ManagerMinWidth = 700;

        // Sheets
        private readonly Queue<SheetRequest> _sheetQueue = new Queue<SheetRequest>();
        private SheetRequest _sheet;
        private IInputElement _focusBeforeSheet;
        #endregion

        #region Constructor
        public MainWindow(UIDocument uiDoc, Document doc, View currentView)
        {
            InitializeComponent();
            _themeManager = new ThemeManager(this);
            _themeManager.ApplyTheme();
            _themeManager.ThemeChanged += (s, e) => UpdateThemeButton();
            UpdateThemeButton();

            _uiDoc = uiDoc;
            _doc = doc;
            _currentView = currentView;
            DocumentTitleText.Text = doc?.Title ?? "";

            InitializeWindowPlacement();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
            StateChanged += (s, e) => UpdateMaximizedState();
            PreviewKeyDown += OnPreviewKeyDown;

            _handler = new PlaceIntersectionsHandler { Report = ReportFromRevit };
            _placeEvent = ExternalEvent.Create(_handler);

            _navHandler = new FocusNavigateHandler { UiDoc = _uiDoc, Report = ReportFromRevit };
            _navEvent = ExternalEvent.Create(_navHandler);

            _managerHandler = new OpeningManagerHandler { UiDoc = _uiDoc };
            _managerEvent = ExternalEvent.Create(_managerHandler);
            ManagerPanel.Initialize(_uiDoc, _themeManager, _managerHandler, _managerEvent);
            ManagerPanel.CloseRequested += (s, e) => CollapseManager();
        }
        #endregion

        #region Initialization
        private void InitializeWindowPlacement()
        {
            Width = Math.Max(MinWidth, _themeManager.WindowWidth);
            Height = Math.Max(MinHeight, _themeManager.WindowHeight);

            // Keep window on-screen
            Left = Math.Max(SystemParameters.VirtualScreenLeft,
                Math.Min(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width, _themeManager.WindowLeft));
            Top = Math.Max(SystemParameters.VirtualScreenTop,
                Math.Min(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height, _themeManager.WindowTop));
        }

        private void InitializeData()
        {
            try
            {
                LoadLinks();
                LoadFamilySymbols();

                if (FamilyCombo.ItemsSource != null &&
                    !string.IsNullOrWhiteSpace(_themeManager.SelectedFamilyName) &&
                    !string.IsNullOrWhiteSpace(_themeManager.SelectedTypeName))
                {
                    var match = FamilyCombo.ItemsSource
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(s =>
                            s.Family != null &&
                            string.Equals(s.Family.Name, _themeManager.SelectedFamilyName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.Name, _themeManager.SelectedTypeName, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                        FamilyCombo.SelectedItem = match;
                }

                OnlyOncePerLocation.IsChecked = _themeManager.SkipDuplicatesOpt;
                TolMmBox.Text = _themeManager.TolMm.ToString("0.##");
                WidthExtensionBox.Text = _themeManager.ExtWidthMm.ToString("0.##");
                DepthExtensionBox.Text = _themeManager.ExtDepthMm.ToString("0.##");
                HeightExtensionBox.Text = _themeManager.ExtHeightMm.ToString("0.##");

                // Save when the user changes things
                FamilyCombo.SelectionChanged += (_, __) => SaveUiSettingsToConfig();
                OnlyOncePerLocation.Checked += (_, __) => SaveUiSettingsToConfig();
                OnlyOncePerLocation.Unchecked += (_, __) => SaveUiSettingsToConfig();
                foreach (var box in MmBoxes) box.LostFocus += (_, __) => SaveUiSettingsToConfig();
            }
            catch (Exception ex)
            {
                ShowResult("Andmete laadimine ebaõnnestus: " + ex.Message, true);
            }
        }

        private IEnumerable<TextBox> MmBoxes => new[] { TolMmBox, WidthExtensionBox, DepthExtensionBox, HeightExtensionBox };

        private void SaveUiSettingsToConfig()
        {
            var sym = FamilyCombo.SelectedItem as FamilySymbol;
            _themeManager.SelectedFamilyName = sym?.Family?.Name;
            _themeManager.SelectedTypeName = sym?.Name;
            _themeManager.SkipDuplicatesOpt = OnlyOncePerLocation.IsChecked == true;

            double v;
            if (TryParseMm(TolMmBox.Text, out v)) _themeManager.TolMm = v;
            if (TryParseMm(WidthExtensionBox.Text, out v)) _themeManager.ExtWidthMm = v;
            if (TryParseMm(DepthExtensionBox.Text, out v)) _themeManager.ExtDepthMm = v;
            if (TryParseMm(HeightExtensionBox.Text, out v)) _themeManager.ExtHeightMm = v;

            _themeManager.SaveConfig();
        }

        private void LoadLinks()
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null) return;

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(li => li.GetLinkDocument() != null)
                .OrderBy(li => li.Name)
                .ToList();

            LinkCombo.ItemsSource = links;
            if (links.Count > 0) LinkCombo.SelectedIndex = 0;
        }

        private void LoadFamilySymbols()
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null) return;

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && !s.Category.IsTagCategory && s.Family != null)
                .OrderBy(s => s.Family.Name).ThenBy(s => s.Name)
                .ToList();

            FamilyCombo.ItemsSource = symbols;
            if (symbols.Count > 0) FamilyCombo.SelectedIndex = 0;
        }
        #endregion

        #region Window lifecycle + chrome
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeData();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Collapse the manager first so the saved window width is the base width.
            try { CollapseManager(); } catch { }
            SaveUiSettingsToConfig();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            TempMemory.Clear();
        }

        private void UpdateMaximizedState()
        {
            // A maximized WindowChrome window extends past the work area by the resize frame; pad the content back in.
            var frame = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = WindowState == WindowState.Maximized
                ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
                : new Thickness(0);
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Kind = _themeManager.IsDarkMode ? PackIconKind.WeatherNight : PackIconKind.WhiteBalanceSunny;
            ThemeButton.ToolTip = _themeManager.IsDarkMode ? "Tume välimus – vaheta heledaks" : "Hele välimus – vaheta tumedaks";
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e) => _themeManager.ToggleTheme();
        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
        #endregion

        #region Result line
        /// <summary>Called by the Revit handlers (API thread) with the outcome of a run.</summary>
        private void ReportFromRevit(string text, bool isError) =>
            Dispatcher.BeginInvoke(new Action(() => ShowResult(text, isError)));

        private void ShowResult(string text, bool isError)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                ResultLine.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            ResultText.Text = text;
            ResultIcon.Kind = isError ? PackIconKind.AlertCircleOutline : PackIconKind.CheckCircleOutline;
            ResultIcon.SetResourceReference(ForegroundProperty, isError ? "Status.Error" : "Status.Ok");
            ResultText.SetResourceReference(ForegroundProperty, isError ? "Status.Error" : "Text.Secondary");
            ResultLine.Visibility = System.Windows.Visibility.Visible;
            Motion.Play(ResultLine, 4);
        }
        #endregion

        #region Placement
        private void Run_Click(object sender, RoutedEventArgs e) => StartPlacement(null);

        private void Run_Selected_Click(object sender, RoutedEventArgs e)
        {
            var doc = _doc ?? _uiDoc?.Document;
            var selIds = _uiDoc?.Selection?.GetElementIds();
            if (doc == null || selIds == null || selIds.Count == 0)
            {
                ShowResult("Vali Revitis üks või mitu kaablirenni ja proovi uuesti.", true);
                return;
            }

            // Trays + fittings only
            var filtered = selIds
                .Select(id => doc.GetElement(id))
                .Where(el => el != null &&
                             (el.IsCategory(BuiltInCategory.OST_CableTray) || el.IsCategory(BuiltInCategory.OST_CableTrayFitting)))
                .Select(el => el.Id)
                .ToList();

            if (filtered.Count == 0)
            {
                ShowResult("Valikus ei ole kaablirenne ega -ühendusi.", true);
                return;
            }

            StartPlacement(filtered);
        }

        /// <summary>Validates the form and raises the placement event (selectedTrays = null → whole model).</summary>
        private void StartPlacement(List<ElementId> selectedTrays)
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null)
            {
                ShowResult("Aktiivset dokumenti pole.", true);
                return;
            }

            var link = LinkCombo.SelectedItem as RevitLinkInstance;
            var symbol = FamilyCombo.SelectedItem as FamilySymbol;
            if (link == null || symbol == null)
            {
                ShowResult("Vali seinte mudel ja ava perekond.", true);
                (link == null ? (UIElement)LinkCombo : FamilyCombo).Focus();
                return;
            }

            double tolMm, extWmm, extDmm, extHmm;
            if (!ReadMm(TolMmBox, "Tolerants", out tolMm) ||
                !ReadMm(WidthExtensionBox, "Laius", out extWmm) ||
                !ReadMm(DepthExtensionBox, "Sügavus", out extDmm) ||
                !ReadMm(HeightExtensionBox, "Kõrgus", out extHmm))
                return;

            _handler.UiDoc = _uiDoc ?? new UIDocument(doc);
            _handler.SelectedLink = link;
            _handler.Symbol = symbol;
            _handler.SkipDuplicates = OnlyOncePerLocation.IsChecked == true;
            _handler.ProximityTol = UnitUtils.ConvertToInternalUnits(tolMm, UnitTypeId.Millimeters);
            _handler.ExtWidth = UnitUtils.ConvertToInternalUnits(extWmm, UnitTypeId.Millimeters);
            _handler.ExtDepth = UnitUtils.ConvertToInternalUnits(extDmm, UnitTypeId.Millimeters);
            _handler.ExtHeight = UnitUtils.ConvertToInternalUnits(extHmm, UnitTypeId.Millimeters);
            _handler.UseSelectedOnly = selectedTrays != null;
            _handler.SelectedTrayIds = selectedTrays;

            SaveUiSettingsToConfig();
            ShowResult(null, false);
            _placeEvent?.Raise();
        }

        /// <summary>Reads a non-negative millimetre value; on failure reports it and focuses the field.</summary>
        private bool ReadMm(TextBox box, string name, out double value)
        {
            if (TryParseMm(box.Text, out value) && value >= 0) return true;
            ShowResult(name + ": sisesta arv millimeetrites (nt 50).", true);
            box.Focus();
            box.SelectAll();
            return false;
        }

        /// <summary>Accepts both "12.5" and "12,5".</summary>
        private static bool TryParseMm(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        #endregion

        #region Created openings navigation
        private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
        private void Next_Click(object sender, RoutedEventArgs e) => Navigate(+1);

        private void Navigate(int delta)
        {
            _navHandler.UiDoc = _uiDoc ?? new UIDocument(_doc);
            _navHandler.Delta = delta;
            _navHandler.ExplicitId = null;
            _navEvent.Raise();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            TempMemory.Clear();
            ShowResult("Loodud avade loend tühjendatud.", false);
        }
        #endregion

        #region Opening Manager panel
        private void OpenManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isManagerOpen) CollapseManager();
                else ExpandManager();
            }
            catch (Exception ex)
            {
                ShowResult("Avade halduri avamine ebaõnnestus: " + ex.Message, true);
            }
        }

        private void ExpandManager()
        {
            if (_isManagerOpen) return;

            _managerBaseWidth = ActualWidth;

            // Left column keeps its current width; the manager takes the rest.
            LeftColumn.Width = new GridLength(_managerBaseWidth);
            ManagerColumn.Width = new GridLength(1, GridUnitType.Star);
            ManagerPanel.Visibility = System.Windows.Visibility.Visible;

            MinWidth = _managerBaseWidth + ManagerMinWidth;
            Width = _managerBaseWidth + ManagerPanelWidth;

            // Keep the window on-screen when it grows to the right.
            var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            if (Left + Width > virtualRight)
                Left = Math.Max(SystemParameters.VirtualScreenLeft, virtualRight - Width);

            ManagerChevron.Kind = PackIconKind.ChevronLeft;
            ManagerToggleText.Text = "Sulge avade haldur";

            ManagerPanel.Activate();
            _isManagerOpen = true;
        }

        private void CollapseManager()
        {
            if (!_isManagerOpen) return;

            ManagerPanel.Deactivate();
            ManagerPanel.Visibility = System.Windows.Visibility.Collapsed;
            ManagerColumn.Width = new GridLength(0);
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);

            MinWidth = 360;
            Width = _managerBaseWidth > 0 ? _managerBaseWidth : 380;

            ManagerChevron.Kind = PackIconKind.ChevronRight;
            ManagerToggleText.Text = "Avade haldur";
            _isManagerOpen = false;
        }
        #endregion

        #region Sheets
        public void ShowSheet(SheetRequest request)
        {
            if (request == null) return;
            _sheetQueue.Enqueue(request);
            if (_sheet == null) NextSheet();
        }

        private void NextSheet()
        {
            var wasOpen = _sheet != null;
            _sheet = _sheetQueue.Count > 0 ? _sheetQueue.Dequeue() : null;

            if (_sheet == null)
            {
                SheetHost.Visibility = System.Windows.Visibility.Collapsed;
                var restore = _focusBeforeSheet as UIElement;
                _focusBeforeSheet = null;
                if (restore != null && restore.IsVisible && restore.IsEnabled) restore.Focus();
                return;
            }

            if (!wasOpen) _focusBeforeSheet = Keyboard.FocusedElement;

            SheetTitle.Text = _sheet.Title ?? "";
            SheetMessage.Text = _sheet.Message ?? "";
            SheetMessage.Visibility = string.IsNullOrEmpty(_sheet.Message) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            SheetPrimary.Content = _sheet.PrimaryText;
            SheetPrimary.Style = (Style)FindResource(_sheet.IsDestructive ? "Button.Destructive" : "Button.Primary");
            SheetSecondary.Content = _sheet.SecondaryText;
            SheetSecondary.Visibility = _sheet.HasSecondary ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SheetInputPanel.Visibility = _sheet.HasInput ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SheetInputLabel.Text = _sheet.InputLabel ?? "";
            SheetInput.Text = _sheet.InputText ?? "";

            SheetIcon.Kind = _sheet.IsError ? PackIconKind.AlertCircleOutline
                : _sheet.IsDestructive ? PackIconKind.AlertOutline
                : _sheet.HasSecondary ? PackIconKind.HelpCircleOutline
                : PackIconKind.InformationOutline;
            SheetIcon.SetResourceReference(ForegroundProperty,
                _sheet.IsError || _sheet.IsDestructive ? "Status.Error" : "Accent.Text");

            SheetHost.Visibility = System.Windows.Visibility.Visible;
            // The sheet drops from the title area and settles.
            Motion.Play(SheetCard, -10);
            var sheet = _sheet;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_sheet != sheet) return;
                if (sheet.HasInput) SheetInput.Focus();
                else if (sheet.IsDestructive && sheet.HasSecondary) SheetSecondary.Focus(); // destructive is never the default
                else SheetPrimary.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CompleteSheet(bool primary)
        {
            var r = _sheet;
            if (r == null) return;
            var text = SheetInput.Text ?? "";
            NextSheet();
            r.Completed?.Invoke(primary, text);
        }

        private void SheetPrimary_Click(object sender, RoutedEventArgs e) => CompleteSheet(true);
        private void SheetSecondary_Click(object sender, RoutedEventArgs e) => CompleteSheet(false);

        // Clicks on the dimmed content do nothing (the sheet needs an answer); they must not reach the page.
        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_sheet == null) return;

            if (e.Key == Key.Escape)
            {
                CompleteSheet(!_sheet.HasSecondary);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button))
            {
                // In the multi-line field Enter adds a line; Ctrl+Enter confirms.
                if (Keyboard.FocusedElement == SheetInput && Keyboard.Modifiers != ModifierKeys.Control) return;
                CompleteSheet(true);
                e.Handled = true;
            }
        }
        #endregion
    }
}
