using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using MaterialDesignThemes.Wpf;
using Shape = System.Windows.Shapes.Shape;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace RedeliAvad
{
    /// <summary>
    /// Opening Manager panel, hosted inside MainWindow (expands the window to the right).
    /// Lists all plugin-managed openings, their link status and row actions. Modeless —
    /// every Revit write goes through <see cref="OpeningManagerHandler"/> + ExternalEvent,
    /// both created by MainWindow inside a valid API context and passed in via Initialize.
    /// </summary>
    public partial class OpeningManagerPanel : UserControl
    {
        #region Fields
        private UIDocument _uiDoc;
        private ThemeManager _themeManager;
        private OpeningManagerHandler _handler;
        private ExternalEvent _event;

        private readonly ObservableCollection<OpeningManagerItem> _items = new ObservableCollection<OpeningManagerItem>();
        private ICollectionView _view;
        private string _activeChip = "All";
        private bool _docChangedSubscribed;
        private bool _isActive;
        #endregion

        /// <summary>Raised when the user clicks the panel close button (MainWindow collapses the column).</summary>
        public event EventHandler CloseRequested;

        public OpeningManagerPanel()
        {
            InitializeComponent();

            ItemsGrid.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterPredicate;
            PreviewKeyDown += OnPreviewKeyDown;
            UpdateSummary();
            UpdateDetails(null);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5 && ValidateButton.IsEnabled)
            {
                RequestRefresh();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        #region Lifecycle (driven by MainWindow)
        /// <summary>Called once from MainWindow's constructor (valid API context owns the event).</summary>
        public void Initialize(UIDocument uiDoc, ThemeManager themeManager,
            OpeningManagerHandler handler, ExternalEvent externalEvent)
        {
            _uiDoc = uiDoc;
            _themeManager = themeManager;
            _handler = handler;
            _event = externalEvent;
        }

        /// <summary>Called when the panel is expanded: wires callbacks and runs the initial scan.</summary>
        public void Activate()
        {
            if (_handler == null || _event == null) return;
            _isActive = true;
            _handler.UiDoc = _uiDoc;
            _handler.ItemsReady = OnItemsReady;
            _handler.Message = OnHandlerMessage;
            SubscribeDocumentChanged();
            RequestRefresh();
        }

        /// <summary>Called when the panel is collapsed or the window closes.</summary>
        public void Deactivate()
        {
            _isActive = false;
            UnsubscribeDocumentChanged();
            if (_handler != null)
            {
                _handler.ItemsReady = null;
                _handler.Message = null;
            }
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region Refresh / handler callbacks
        private void RequestRefresh()
        {
            if (ValidateButton != null) ValidateButton.IsEnabled = false;
            SetStatusMessage("Valideerin...", false);
            _handler.Request = OpeningManagerRequest.Refresh;
            _event.Raise();
        }

        private void OnItemsReady(List<OpeningManagerItem> items)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var selectedUid = (ItemsGrid.SelectedItem as OpeningManagerItem)?.OpeningUniqueId;

                _items.Clear();
                foreach (var it in items) _items.Add(it);

                if (!string.IsNullOrEmpty(selectedUid))
                {
                    var again = _items.FirstOrDefault(x => x.OpeningUniqueId == selectedUid);
                    if (again != null) ItemsGrid.SelectedItem = again;
                }

                if (ValidateButton != null) ValidateButton.IsEnabled = true;
                SetStatusMessage("", false);
                _view.Refresh();
                UpdateSummary();
                UpdateDetails(ItemsGrid.SelectedItem as OpeningManagerItem);
                UpdateFixSelected();
            }));
        }

        private void OnHandlerMessage(string text, bool isError)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatusMessage(text, isError);
                if (ValidateButton != null) ValidateButton.IsEnabled = true;
            }));
        }

        private void SetStatusMessage(string text, bool isError)
        {
            if (StatusMessageText == null) return;
            StatusMessageText.Text = text ?? "";
            StatusMessageText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Status.Error" : "Text.Secondary");
        }

        private void Validate_Click(object sender, RoutedEventArgs e) => RequestRefresh();
        #endregion

        #region Search + filter chips
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            UpdateSummary();
        }

        private void Chip_Checked(object sender, RoutedEventArgs e)
        {
            var chip = sender as FrameworkElement;
            _activeChip = chip?.Tag as string ?? "All";
            if (_view == null) return; // initial IsChecked during InitializeComponent
            _view.Refresh();
            UpdateSummary();
        }

        private void EmptyAction_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0)
            {
                RequestRefresh();
                return;
            }
            // No matches: clear the search and the filter.
            SearchBox.Text = "";
            ChipAll.IsChecked = true;
        }

        private bool FilterPredicate(object obj)
        {
            var item = obj as OpeningManagerItem;
            if (item == null) return false;

            var search = SearchBox != null ? (SearchBox.Text ?? "").Trim().ToLowerInvariant() : "";
            if (search.Length > 0 && !item.SearchBlob.Contains(search))
                return false;

            switch (_activeChip)
            {
                case "Vertical": return item.IsVertical;
                case "Horizontal": return !item.IsVertical;
                case "Aligned": return item.Status == OpeningStatus.Aligned;
                case "NotAligned":
                    return item.Status == OpeningStatus.NotAligned || item.Status == OpeningStatus.SourceMoved ||
                           item.Status == OpeningStatus.OpeningMoved || item.Status == OpeningStatus.HostChanged ||
                           item.Status == OpeningStatus.SizeMismatch || item.Status == OpeningStatus.ShapeMismatch;
                case "NeedsReview":
                    return item.Status == OpeningStatus.NeedsReview || item.Status == OpeningStatus.Error || item.IsDirty;
                case "Allowed": return item.Status == OpeningStatus.Allowed || item.IsAllowed;
                case "Missing":
                    return item.Status == OpeningStatus.MissingOpening || item.Status == OpeningStatus.MissingSource ||
                           item.Status == OpeningStatus.MissingHost;
                default: return true;
            }
        }

        private void UpdateSummary()
        {
            if (SummaryText == null) return;
            var total = _items.Count;
            var shown = _view != null ? _view.Cast<object>().Count() : total;

            if (total == 0)
            {
                SummaryText.Text = "Hallatavaid avasid pole laaditud.";
                ShowEmptyState(PackIconKind.TableSearch, "Avasid pole laaditud",
                    "Vajuta „Valideeri“, et mudel läbi vaadata. Avad luuakse vasakult paneelilt.", "Valideeri");
                return;
            }

            var aligned = _items.Count(i => i.Status == OpeningStatus.Aligned);
            var allowed = _items.Count(i => i.Status == OpeningStatus.Allowed);
            var warn = _items.Count(i => i.StatusSeverity == OpeningStatusSeverity.Warning);
            var err = _items.Count(i => i.StatusSeverity == OpeningStatusSeverity.Error);

            var parts = new List<string> { total + " ava", aligned + " joondatud" };
            if (warn > 0) parts.Add(warn + " hoiatust");
            if (err > 0) parts.Add(err + " viga");
            if (allowed > 0) parts.Add(allowed + " lubatud");
            if (shown != total) parts.Add("kuvatud " + shown);
            SummaryText.Text = string.Join(" · ", parts);

            if (shown == 0)
                ShowEmptyState(PackIconKind.FilterOffOutline, "Sobivaid avasid pole",
                    "Ükski ava ei vasta otsingule või filtrile.", "Tühjenda filter");
            else
                EmptyState.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void ShowEmptyState(PackIconKind icon, string title, string message, string action)
        {
            EmptyIcon.Kind = icon;
            EmptyTitle.Text = title;
            EmptyMessage.Text = message;
            EmptyAction.Content = action;
            EmptyState.Visibility = System.Windows.Visibility.Visible;
        }
        #endregion

        #region Row actions
        private OpeningManagerItem ItemFromSender(object sender)
        {
            var fe = sender as FrameworkElement;
            return fe != null ? fe.DataContext as OpeningManagerItem : null;
        }

        private void RaiseItemRequest(OpeningManagerItem item, OpeningManagerRequest request)
        {
            if (item == null || _handler == null) return;
            _handler.TargetItem = item;
            _handler.Request = request;
            _event.Raise();
        }

        /// <summary>Asks in a sheet of the hosting window (falls back to a message box outside it).</summary>
        private void Confirm(string title, string message, string yesText, bool destructive, Action onYes)
        {
            var host = Window.GetWindow(this) as ISheetPresenter;
            if (host == null)
            {
                if (MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) onYes();
                return;
            }
            host.ShowSheet(new SheetRequest
            {
                Title = title,
                Message = message,
                PrimaryText = yesText,
                SecondaryText = "Loobu",
                IsDestructive = destructive,
                Completed = (ok, _) => { if (ok) onYes(); }
            });
        }

        /// <summary>"…" button: opens its menu below it (the menu shares the row's DataContext).</summary>
        private void More_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void GoTo_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.GoTo);

        private void SelectOpening_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.SelectOpening);

        private void SelectSource_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.SelectSource);

        private void HighlightPair_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.HighlightPair);

        private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsGrid.SelectedItem as OpeningManagerItem;
            if (item != null) RaiseItemRequest(item, OpeningManagerRequest.GoTo);
        }

        private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsGrid.SelectedItem as OpeningManagerItem;
            if (item != null) RaiseItemRequest(item, OpeningManagerRequest.GoTo);
        }

        // ----- fix -----

        private void Fix_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null || !item.CanFix) return;

            Confirm(item.IsRecreate ? "Taasloo ava?" : "Paranda ava?", BuildFixDescription(item),
                item.IsRecreate ? "Taasloo" : "Paranda", false, () =>
                {
                    _handler.FixPayloads = new List<OpeningFixPayload> { BuildFixPayload(item) };
                    _handler.Request = OpeningManagerRequest.FixItems;
                    _event.Raise();
                });
        }

        private void FixSelected_Click(object sender, RoutedEventArgs e)
        {
            var fixable = ItemsGrid.SelectedItems.Cast<OpeningManagerItem>().Where(i => i.CanFix).ToList();
            if (fixable.Count == 0)
            {
                SetStatusMessage("Valikus pole ühtegi parandatavat rida.", true);
                return;
            }

            var moves = fixable.Count(i => i.NeedsMove && !i.IsRecreate);
            var resizes = fixable.Count(i => i.NeedsResize && !i.IsRecreate);
            var recreates = fixable.Count(i => i.IsRecreate);

            var lines = new List<string>();
            if (moves > 0) lines.Add("• " + moves + " nihutatakse õigesse asukohta");
            if (resizes > 0) lines.Add("• " + resizes + " suurus uuendatakse");
            if (recreates > 0) lines.Add("• " + recreates + " taasluuakse (kustutatud avad)");

            Confirm("Paranda " + fixable.Count + " ava?", string.Join("\n", lines), "Paranda", false, () =>
            {
                _handler.FixPayloads = fixable.Select(BuildFixPayload).ToList();
                _handler.Request = OpeningManagerRequest.FixItems;
                _event.Raise();
            });
        }

        private static string BuildFixDescription(OpeningManagerItem item)
        {
            if (item.IsRecreate)
            {
                return "Ava on mudelist kustutatud.\n\nTaasloo ava perekonnaga \"" +
                       item.OpeningFamilyName + " : " + item.OpeningTypeName + "\" (" +
                       item.ExpectedWidthMm.ToString("0") + "×" + item.ExpectedHeightMm.ToString("0") +
                       " mm) allika ja aluse ristumiskohas?";
            }

            var parts = new List<string>();
            if (item.NeedsMove)
                parts.Add("nihuta ava " + item.OffsetMm.ToString("0.#") + " mm, et see ühtiks allikaga");
            if (item.NeedsResize)
                parts.Add("uuenda suurus " + item.WidthMm.ToString("0") + "×" + item.HeightMm.ToString("0") +
                          " → " + item.ExpectedWidthMm.ToString("0") + "×" + item.ExpectedHeightMm.ToString("0") + " mm");
            if (parts.Count == 0) parts.Add("uuenda ava vastavalt allikale");

            var text = string.Join(" ja ", parts);
            return char.ToUpper(text[0]) + text.Substring(1) + "?";
        }

        private static OpeningFixPayload BuildFixPayload(OpeningManagerItem item)
        {
            return new OpeningFixPayload
            {
                OpeningUniqueId = item.OpeningUniqueId,
                Recreate = item.IsRecreate,
                FamilyName = item.OpeningFamilyName,
                TypeName = item.OpeningTypeName,
                SourceUniqueId = item.SourceUniqueId,
                SourceLinkUniqueId = item.SourceLinkUniqueId,
                HostUniqueId = item.HostUniqueId,
                HostLinkUniqueId = item.HostLinkUniqueId,
                TargetCenter = item.ExpectedCenter,
                TargetWidthMm = item.IsRecreate || item.NeedsResize ? item.ExpectedWidthMm : 0.0,
                TargetHeightMm = item.IsRecreate || item.NeedsResize ? item.ExpectedHeightMm : 0.0,
                TargetDepthMm = item.IsRecreate || item.NeedsResize ? item.ExpectedDepthMm : 0.0,
                ClearanceWidthMm = item.ClearanceMm,
                ClearanceHeightMm = item.ClearanceHeightMm,
                ClearanceDepthMm = item.ClearanceDepthMm
            };
        }

        // ----- allowed / unlink / delete -----

        private void MarkAllowed_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null) return;

            if (item.Status == OpeningStatus.MissingOpening)
            {
                SetStatusMessage("Puuduvat ava ei saa lubatuks märkida – eemalda seos või taasloo ava.", true);
                return;
            }

            var host = Window.GetWindow(this) as ISheetPresenter;
            if (host == null)
            {
                _handler.AllowReason = "";
                RaiseItemRequest(item, OpeningManagerRequest.MarkAllowed);
                return;
            }
            host.ShowSheet(new SheetRequest
            {
                Title = "Märgi ava lubatuks?",
                Message = "Lubatud ava jäetakse valideerimisel vahele, kuni märge eemaldatakse.",
                InputLabel = "Põhjus (valikuline)",
                PrimaryText = "Märgi lubatuks",
                SecondaryText = "Loobu",
                Completed = (ok, reason) =>
                {
                    if (!ok) return;
                    _handler.AllowReason = reason ?? "";
                    RaiseItemRequest(item, OpeningManagerRequest.MarkAllowed);
                }
            });
        }

        private void ClearAllowed_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.ClearAllowed);

        private void Unlink_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null) return;

            Confirm("Eemalda seos?", "Ava jääb mudelisse alles, kuid haldur seda enam ei jälgi.", "Eemalda seos", false,
                () => RaiseItemRequest(item, OpeningManagerRequest.Unlink));
        }

        private void DeleteOpening_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null) return;

            Confirm("Kustuta ava mudelist?", "Allikas: " + item.SourceDisplayName + "\nAlus: " + item.HostDisplayName,
                "Kustuta", true, () => RaiseItemRequest(item, OpeningManagerRequest.DeleteOpening));
        }
        #endregion

        #region Details + preview
        private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetails(ItemsGrid.SelectedItem as OpeningManagerItem);
            UpdateFixSelected();
        }

        /// <summary>Bulk fix is available (and says how many) only when the selection has fixable rows.</summary>
        private void UpdateFixSelected()
        {
            var fixable = ItemsGrid.SelectedItems.Cast<OpeningManagerItem>().Count(i => i.CanFix);
            FixSelectedButton.IsEnabled = fixable > 0;
            FixSelectedText.Text = fixable > 1 ? "Paranda " + fixable + " ava" : "Paranda valitud";
        }

        private void UpdateDetails(OpeningManagerItem item)
        {
            if (DetailSource == null) return;

            var has = item != null;
            DetailsGrid.Visibility = has ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            DetailsPlaceholder.Visibility = has ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            PreviewBox.Visibility = DetailsGrid.Visibility;
            if (!has)
            {
                DrawPreview(null);
                return;
            }

            DetailSource.Text = item.SourceText +
                                (string.IsNullOrEmpty(item.SourceModelName) ? "" : " · " + item.SourceModelName);
            DetailHost.Text = item.HostDisplayName +
                              (string.IsNullOrEmpty(item.HostModelName) ? "" : " · " + item.HostModelName);
            DetailOpening.Text = item.OpeningFamilyName + " : " + item.OpeningTypeName +
                                 " · " + item.SizeText + " mm · sügavus " + item.DepthMm.ToString("0") + " mm";

            var validated = "";
            DateTime utc;
            if (DateTime.TryParse(item.LastValidated, null, System.Globalization.DateTimeStyles.RoundtripKind, out utc))
                validated = " · valideeritud " + utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            DetailStatus.Text = item.StatusText + " · nihe " + item.OffsetText +
                                " · varu " + item.ClearanceMm.ToString("0") + " mm" + validated;

            var notes = item.NotesText;
            if (item.IsAllowed && !string.IsNullOrEmpty(item.AllowedBy))
                notes = (string.IsNullOrEmpty(notes) ? "" : notes + "\n") + "Lubas: " + item.AllowedBy;
            var hasNotes = !string.IsNullOrEmpty(notes);
            DetailNotes.Text = notes ?? "";
            DetailNotes.Visibility = hasNotes ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            DetailNotesLabel.Visibility = DetailNotes.Visibility;

            DrawPreview(item);
        }

        /// <summary>
        /// MVP schematic preview: host band, expected opening position (dashed) and current
        /// opening position (solid, status colour). Clicking the preview navigates to the opening.
        /// </summary>
        private void DrawPreview(OpeningManagerItem item)
        {
            if (PreviewCanvas == null) return;
            PreviewCanvas.Children.Clear();
            if (item == null) return;

            double cw = 208, ch = 108;
            double cx = cw / 2.0, cy = ch / 2.0;

            var statusKey = StatusBrushKey(item);

            // Host band (wall/floor section)
            var host = new WpfRectangle
            {
                Width = cw - 16,
                Height = 34,
                Opacity = 0.6,
                RadiusX = 2,
                RadiusY = 2
            };
            host.SetResourceReference(Shape.FillProperty, "Surface.Stroke");
            Canvas.SetLeft(host, 8);
            Canvas.SetTop(host, cy - 17);
            PreviewCanvas.Children.Add(host);

            // Opening footprint (aspect from real dimensions, clamped)
            double w = item.WidthMm > 1 ? item.WidthMm : 100;
            double h = item.HeightMm > 1 ? item.HeightMm : 100;
            double aspect = h / w;
            double rw = 56;
            double rh = Math.Max(14, Math.Min(48, rw * aspect));

            // Displacement between current and expected centers (plan projection, scaled)
            double dxMm = 0, dyMm = 0;
            if (item.ExpectedCenter != null && item.CurrentCenter != null)
            {
                dxMm = OpeningGeometry.FeetToMm(item.CurrentCenter.X - item.ExpectedCenter.X);
                dyMm = OpeningGeometry.FeetToMm(item.CurrentCenter.Y - item.ExpectedCenter.Y);
                if (Math.Abs(dxMm) < 0.01 && Math.Abs(dyMm) < 0.01)
                {
                    var dzMm = OpeningGeometry.FeetToMm(item.CurrentCenter.Z - item.ExpectedCenter.Z);
                    dyMm = -dzMm; // show vertical drift on the Y axis of the schematic
                }
            }
            double maxD = Math.Max(Math.Abs(dxMm), Math.Abs(dyMm));
            double scale = maxD > 0.5 ? Math.Min(30.0 / maxD, 1.0) : 0.0;
            double dx = dxMm * scale, dy = -dyMm * scale;

            // Expected position (dashed)
            if (item.ExpectedCenter != null)
            {
                var expected = new WpfRectangle
                {
                    Width = rw,
                    Height = rh,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = Brushes.Transparent
                };
                expected.SetResourceReference(Shape.StrokeProperty, "Text.Tertiary");
                Canvas.SetLeft(expected, cx - rw / 2.0);
                Canvas.SetTop(expected, cy - rh / 2.0);
                PreviewCanvas.Children.Add(expected);
            }

            // Current position (solid, status colour) — only when the opening exists
            if (item.Status != OpeningStatus.MissingOpening)
            {
                var current = new WpfRectangle
                {
                    Width = rw,
                    Height = rh,
                    StrokeThickness = 1.6,
                    Fill = Brushes.Transparent
                };
                current.SetResourceReference(Shape.StrokeProperty, statusKey);
                Canvas.SetLeft(current, cx - rw / 2.0 + dx);
                Canvas.SetTop(current, cy - rh / 2.0 + dy);
                PreviewCanvas.Children.Add(current);

                // Center dot
                var dot = new WpfEllipse { Width = 4, Height = 4 };
                dot.SetResourceReference(Shape.FillProperty, statusKey);
                Canvas.SetLeft(dot, cx - 2 + dx);
                Canvas.SetTop(dot, cy - 2 + dy);
                PreviewCanvas.Children.Add(dot);
            }

            // Legend
            var label = new TextBlock
            {
                Text = item.Status == OpeningStatus.MissingOpening
                    ? "Ava puudub"
                    : "Nihe: " + item.OffsetText,
                FontSize = 11
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            Canvas.SetLeft(label, 8);
            Canvas.SetTop(label, ch - 18);
            PreviewCanvas.Children.Add(label);
        }

        private static string StatusBrushKey(OpeningManagerItem item)
        {
            if (item.IsDirty) return "Status.Warning";
            switch (item.StatusSeverity)
            {
                case OpeningStatusSeverity.Ok: return "Status.Ok";
                case OpeningStatusSeverity.Info: return "Status.Info";
                case OpeningStatusSeverity.Warning: return "Status.Warning";
                default: return "Status.Error";
            }
        }
        #endregion

        #region DocumentChanged (lightweight dirty marking)
        private void SubscribeDocumentChanged()
        {
            if (_docChangedSubscribed) return;
            try
            {
                var app = _uiDoc?.Application?.Application;
                if (app != null)
                {
                    app.DocumentChanged += OnDocumentChanged;
                    _docChangedSubscribed = true;
                }
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("DocumentChanged subscribe failed", ex);
            }
        }

        private void UnsubscribeDocumentChanged()
        {
            if (!_docChangedSubscribed) return;
            try
            {
                var app = _uiDoc?.Application?.Application;
                if (app != null) app.DocumentChanged -= OnDocumentChanged;
            }
            catch { }
            _docChangedSubscribed = false;
        }

        /// <summary>
        /// Marks affected rows as "changed" — no heavy geometry work here. The user re-validates
        /// explicitly with the Valideeri button.
        /// </summary>
        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (!_isActive) return;

                // Ignore our own transactions (validation writes, focus views, fixes trigger a refresh anyway).
                var names = e.GetTransactionNames();
                if (names != null && names.Any(n => n != null &&
                        (n.StartsWith("Avade haldur") ||
                         n == "RedeliAvad – Focus" ||
                         n == "RedeliAvad – Create Focus View")))
                    return;

                var deleted = new HashSet<ElementId>(e.GetDeletedElementIds());
                var modified = new HashSet<ElementId>(e.GetModifiedElementIds());

                // New plugin-created openings (placement from the left panel) → hint to refresh.
                var addedManaged = false;
                try
                {
                    var added = e.GetAddedElementIds();
                    if (added != null && added.Count > 0 && added.Count <= 500)
                    {
                        var doc = e.GetDocument();
                        addedManaged = added.Any(id =>
                        {
                            var el = doc.GetElement(id);
                            return el is FamilyInstance && OpeningLinkStorage.TryReadLink(el) != null;
                        });
                    }
                }
                catch { }

                if (deleted.Count == 0 && modified.Count == 0 && !addedManaged) return;

                var dirtyRows = new List<OpeningManagerItem>();
                var missingRows = new List<OpeningManagerItem>();

                foreach (var item in _items)
                {
                    var oid = item.OpeningElementId;
                    var sid = item.SourceElementId;

                    if (oid != null && oid != ElementId.InvalidElementId && deleted.Contains(oid))
                        missingRows.Add(item);
                    else if ((oid != null && modified.Contains(oid)) ||
                             (sid != null && string.IsNullOrEmpty(item.SourceLinkUniqueId) && (modified.Contains(sid) || deleted.Contains(sid))))
                        dirtyRows.Add(item);
                }

                if (dirtyRows.Count == 0 && missingRows.Count == 0 && !addedManaged) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var r in missingRows)
                    {
                        r.Status = OpeningStatus.MissingOpening;
                        r.IsDirty = true;
                    }
                    foreach (var r in dirtyRows)
                        r.IsDirty = true;
                    SetStatusMessage("Mudel on muutunud – vajuta „Valideeri“ värskendamiseks.", false);
                }));
            }
            catch (Exception ex)
            {
                OpeningManagerLog.Error("OnDocumentChanged failed", ex);
            }
        }
        #endregion
    }
}
