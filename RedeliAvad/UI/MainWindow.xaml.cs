using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure; // StructuralType
using Autodesk.Revit.UI;
using MaterialDesignThemes.Wpf;

namespace RedeliAvad
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        #endregion

        #region Fields
        private UIDocument _uiDoc;
        private Document _doc;
        private View _currentView;

        // Temp navigation via external event
        private FocusNavigateHandler _navHandler;
        private ExternalEvent _navEvent;

        private System.Windows.Point _startPoint; // kept for compatibility
        private readonly WindowResizer _windowResizer;
        private ThemeManager _themeManager;
        private ExternalEvent _applyRevisionExternalEvent; // kept for compatibility

        // New: core placement handler + event
        private PlaceIntersectionsHandler _handler;
        private ExternalEvent _placeEvent;

        // Ensures we can gate logic during close if needed later
        private bool _isClosing = false;
        #endregion

        #region Constructor
        public MainWindow(UIDocument uiDoc, Document doc, View currentView)
        {
            // Theme manager first so we can read saved sizes/positions
            _themeManager = new ThemeManager(this);

            InitializeComponent();

            // Store context
            _uiDoc = uiDoc;
            _doc = doc;
            _currentView = currentView;

            // Window placement & theme
            InitializeWindowPlacement();
            InitializeTheme();

            // Events & resizer
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            PreviewMouseDown += MainWindow_PreviewMouseDown;

            _windowResizer = new WindowResizer(this);
            // TitleBarControl is created by InitializeComponent; set safely here and again on Loaded
            _windowResizer.IgnoreElement = TitleBarControl;

            // Global mouse handlers for resizing
            MouseLeftButtonUp += Window_MouseLeftButtonUp;
            MouseMove += Window_MouseMove;

            // New: handler + external event
            _handler = new PlaceIntersectionsHandler();
            _placeEvent = ExternalEvent.Create(_handler);

            _navHandler = new FocusNavigateHandler { UiDoc = _uiDoc };
            _navEvent = ExternalEvent.Create(_navHandler);

            DataContext = this;
        }
        #endregion

        #region Initialization Helpers
        private void InitializeWindowPlacement()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;

            Width = _themeManager.WindowWidth;
            Height = _themeManager.WindowHeight;

            // Keep window on-screen
            Left = Math.Max(0, Math.Min(SystemParameters.VirtualScreenWidth - Width, _themeManager.WindowLeft));
            Top = Math.Max(0, Math.Min(SystemParameters.VirtualScreenHeight - Height, _themeManager.WindowTop));
        }

        private void InitializeTheme()
        {
            // Reflect saved theme in the toggle and apply on load
            if (ThemeToggleButton != null)
                ThemeToggleButton.IsChecked = _themeManager.IsDarkMode;
        }
        #endregion

        #region Data init (links + symbols)
        private void InitializeData()
        {
            try
            {
                LoadLinks();
                LoadFamilySymbols();

                // Family selection
                var doc = _doc ?? _uiDoc?.Document;
                if (FamilyCombo?.ItemsSource != null &&
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

                // Options
                if (OnlyOncePerLocation != null) OnlyOncePerLocation.IsChecked = _themeManager.SkipDuplicatesOpt;
                if (TolMmBox != null) TolMmBox.Text = _themeManager.TolMm.ToString("0.##");
                if (WidthExtensionBox != null) WidthExtensionBox.Text = _themeManager.ExtWidthMm.ToString("0.##");
                if (DepthExtensionBox != null) DepthExtensionBox.Text = _themeManager.ExtDepthMm.ToString("0.##");
                if (HeightExtensionBox != null) HeightExtensionBox.Text = _themeManager.ExtHeightMm.ToString("0.##");

                // (Optional) save when user changes things
                FamilyCombo.SelectionChanged += (_, __) => SaveUiSettingsToConfig();
                if (OnlyOncePerLocation != null)
                {
                    OnlyOncePerLocation.Checked += (_, __) => SaveUiSettingsToConfig();
                    OnlyOncePerLocation.Unchecked += (_, __) => SaveUiSettingsToConfig();
                }
                if (TolMmBox != null) TolMmBox.LostFocus += (_, __) => SaveUiSettingsToConfig();
                if (WidthExtensionBox != null) WidthExtensionBox.LostFocus += (_, __) => SaveUiSettingsToConfig();
                if (DepthExtensionBox != null) DepthExtensionBox.LostFocus += (_, __) => SaveUiSettingsToConfig();
                if (HeightExtensionBox != null) HeightExtensionBox.LostFocus += (_, __) => SaveUiSettingsToConfig();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RedeliAvad", "Initialization error:\n" + ex.Message);
            }
        }

        private void SaveUiSettingsToConfig()
        {
            var doc = _doc ?? _uiDoc?.Document;

            // Selected family/type
            var sym = FamilyCombo?.SelectedItem as FamilySymbol;
            _themeManager.SelectedFamilyName = sym?.Family?.Name;
            _themeManager.SelectedTypeName = sym?.Name;

            // Options
            _themeManager.SkipDuplicatesOpt = (OnlyOncePerLocation != null && OnlyOncePerLocation.IsChecked == true);

            double tolMm = _themeManager.TolMm;
            double wMm = _themeManager.ExtWidthMm;
            double dMm = _themeManager.ExtDepthMm;
            double hMm = _themeManager.ExtHeightMm;

            if (TolMmBox != null && double.TryParse(TolMmBox.Text, out var tmm)) tolMm = tmm;
            if (WidthExtensionBox != null && double.TryParse(WidthExtensionBox.Text, out var w)) wMm = w;
            if (DepthExtensionBox != null && double.TryParse(DepthExtensionBox.Text, out var d)) dMm = d;
            if (HeightExtensionBox != null && double.TryParse(HeightExtensionBox.Text, out var h)) hMm = h;

            _themeManager.TolMm = tolMm;
            _themeManager.ExtWidthMm = wMm;
            _themeManager.ExtDepthMm = dMm;
            _themeManager.ExtHeightMm = hMm;

            _themeManager.SaveConfig();
        }

        private void LoadLinks()
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null || LinkCombo == null) return;

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(li => li.GetLinkDocument() != null)
                .OrderBy(li => li.Name)
                .ToList();

            LinkCombo.ItemsSource = links;
            LinkCombo.DisplayMemberPath = "Name";
            if (links.Count > 0) LinkCombo.SelectedIndex = 0;
        }

        private void LoadFamilySymbols()
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null || FamilyCombo == null) return;

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && !s.Category.IsTagCategory && s.Family != null)
                .OrderBy(s => s.Family.Name).ThenBy(s => s.Name)
                .ToList();

            FamilyCombo.ItemsSource = symbols;
            FamilyCombo.DisplayMemberPath = "Name";
            if (symbols.Count > 0) FamilyCombo.SelectedIndex = 0;
        }
        #endregion

        #region Window Lifecycle
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Apply saved theme when window is ready
            _themeManager.ApplyTheme();

            // In case the TitleBarControl template wasn’t ready earlier
            if (_windowResizer != null && _windowResizer.IgnoreElement == null)
                _windowResizer.IgnoreElement = TitleBarControl;

            InitializeData();
        }
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _isClosing = true;
            SaveUiSettingsToConfig();
            _themeManager.SaveConfig();

            // NEW: clear temp memory on close
            TempMemory.Clear();

            // Best effort cleanup...
            Loaded -= MainWindow_Loaded;
            Closed -= MainWindow_Closed;
            PreviewMouseDown -= MainWindow_PreviewMouseDown;
            MouseLeftButtonUp -= Window_MouseLeftButtonUp;
            MouseMove -= Window_MouseMove;
        }

        #endregion

        #region Theme
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            _themeManager.ToggleTheme();
            _themeManager.SaveConfig();

            if (ThemeToggleButton != null)
                ThemeToggleButton.IsChecked = _themeManager.IsDarkMode;
        }
        #endregion

        #region Bottom bar action
        // Hooked up to the "Find & Place" Button in XAML
        private void Run_Click(object sender, RoutedEventArgs e)
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("RedeliAvad", "No active document.");
                return;
            }

            var link = LinkCombo?.SelectedItem as RevitLinkInstance;
            var symbol = FamilyCombo?.SelectedItem as FamilySymbol;

            if (link == null || symbol == null)
            {
                TaskDialog.Show("RedeliAvad", "Select a link and a family type.");
                return;
            }

            // Parse mm inputs
            double tolMm = 50;
            if (TolMmBox != null) double.TryParse(TolMmBox.Text, out tolMm);

            double extWmm = 0, extDmm = 100, extHmm = 0;
            if (WidthExtensionBox != null) double.TryParse(WidthExtensionBox.Text, out extWmm);
            if (DepthExtensionBox != null) double.TryParse(DepthExtensionBox.Text, out extDmm);
            if (HeightExtensionBox != null) double.TryParse(HeightExtensionBox.Text, out extHmm);

            // Convert mm -> internal (feet)
            var tol = UnitUtils.ConvertToInternalUnits(tolMm, UnitTypeId.Millimeters);
            var extW = UnitUtils.ConvertToInternalUnits(extWmm, UnitTypeId.Millimeters);
            var extD = UnitUtils.ConvertToInternalUnits(extDmm, UnitTypeId.Millimeters);
            var extH = UnitUtils.ConvertToInternalUnits(extHmm, UnitTypeId.Millimeters);

            _handler.UiDoc = _uiDoc ?? new UIDocument(doc);
            _handler.SelectedLink = link;
            _handler.Symbol = symbol;
            _handler.SkipDuplicates = (OnlyOncePerLocation != null && OnlyOncePerLocation.IsChecked == true);
            _handler.ProximityTol = tol;

            // NEW: pass extensions
            _handler.ExtWidth = extW;
            _handler.ExtDepth = extD;
            _handler.ExtHeight = extH;

            SaveUiSettingsToConfig();

            _placeEvent?.Raise();
        }
        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            _navHandler.UiDoc = _uiDoc ?? new UIDocument(_doc);
            _navHandler.Delta = -1;
            _navHandler.ExplicitId = null;
            _navEvent.Raise();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _navHandler.UiDoc = _uiDoc ?? new UIDocument(_doc);
            _navHandler.Delta = +1;
            _navHandler.ExplicitId = null;
            _navEvent.Raise();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            TempMemory.Clear();
            TaskDialog.Show("RedeliAvad", "Vahemälu tühjendatud.");
        }
        private void Run_Selected_Click(object sender, RoutedEventArgs e)
        {
            var doc = _doc ?? _uiDoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("RedeliAvad", "No active document.");
                return;
            }

            var link = LinkCombo?.SelectedItem as RevitLinkInstance;
            var symbol = FamilyCombo?.SelectedItem as FamilySymbol;

            if (link == null || symbol == null)
            {
                TaskDialog.Show("RedeliAvad", "Vali lingitud mudel ja perekonna tüüp.");
                return;
            }

            // Collect current selection (trays or fittings)
            var selIds = _uiDoc?.Selection?.GetElementIds();
            if (selIds == null || selIds.Count == 0)
            {
                TaskDialog.Show("RedeliAvad", "Vali üks või mitu kaablirennielementi ja proovi uuesti.");
                return;
            }

            // Filter selection to trays + fittings only
            var filtered = selIds
                .Select(id => doc.GetElement(id))
                .Where(el => el != null && el.Category != null &&
                    (el.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTray ||
                     el.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayFitting))
                .Select(el => el.Id)
                .ToList();

            if (filtered.Count == 0)
            {
                TaskDialog.Show("RedeliAvad", "Valikus ei ole kaablirenne ega -ühendusi.");
                return;
            }

            // Parse mm inputs
            double tolMm = 50;
            if (TolMmBox != null) double.TryParse(TolMmBox.Text, out tolMm);

            double extWmm = 0, extDmm = 100, extHmm = 0;
            if (WidthExtensionBox != null) double.TryParse(WidthExtensionBox.Text, out extWmm);
            if (DepthExtensionBox != null) double.TryParse(DepthExtensionBox.Text, out extDmm);
            if (HeightExtensionBox != null) double.TryParse(HeightExtensionBox.Text, out extHmm);

            // Convert mm -> internal (feet)
            var tol = UnitUtils.ConvertToInternalUnits(tolMm, UnitTypeId.Millimeters);
            var extW = UnitUtils.ConvertToInternalUnits(extWmm, UnitTypeId.Millimeters);
            var extD = UnitUtils.ConvertToInternalUnits(extDmm, UnitTypeId.Millimeters);
            var extH = UnitUtils.ConvertToInternalUnits(extHmm, UnitTypeId.Millimeters);

            // Fill handler
            _handler.UiDoc = _uiDoc ?? new UIDocument(doc);
            _handler.SelectedLink = link;
            _handler.Symbol = symbol;
            _handler.SkipDuplicates = (OnlyOncePerLocation != null && OnlyOncePerLocation.IsChecked == true);
            _handler.ProximityTol = tol;
            _handler.ExtWidth = extW;
            _handler.ExtDepth = extD;
            _handler.ExtHeight = extH;

            // NEW: selected-only mode
            _handler.UseSelectedOnly = true;
            _handler.SelectedTrayIds = filtered;

            SaveUiSettingsToConfig();

            _placeEvent?.Raise();
        }

        #endregion

        #region Title Bar Buttons
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        #endregion

        #region Window Resizing + Mouse
        private void Window_MouseMove(object sender, MouseEventArgs e) => _windowResizer?.ResizeWindow(e);
        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _windowResizer?.StopResizing();
        private void LeftEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _windowResizer?.StartResizing(e, ResizeDirection.Left);
        private void RightEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _windowResizer?.StartResizing(e, ResizeDirection.Right);
        private void BottomEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _windowResizer?.StartResizing(e, ResizeDirection.Bottom);
        private void BottomLeftCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _windowResizer?.StartResizing(e, ResizeDirection.BottomLeft);
        private void BottomRightCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _windowResizer?.StartResizing(e, ResizeDirection.BottomRight);
        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _ = e.OriginalSource as DependencyObject;
        #endregion
    }
}
