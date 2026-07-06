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
using WpfColor = System.Windows.Media.Color;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfPanel = System.Windows.Controls.Panel;
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

            // The merged dictionaries were only needed for parse-time StaticResource lookups.
            // Clearing them lets DynamicResource resolve against the window's active theme
            // (same trick ThemeManager uses on the main window).
            Resources.MergedDictionaries.Clear();

            ItemsGrid.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterPredicate;
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
            StatusMessageText.Foreground = isError
                ? new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#DC6B6B"))
                : (TryFindResource("IconForegroundBrush") as Brush ?? StatusMessageText.Foreground);
        }

        private void Validate_Click(object sender, RoutedEventArgs e) => RequestRefresh();
        #endregion

        #region Search + filter chips
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            UpdateSummary();
        }

        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            var chip = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (chip == null) return;

            var panel = chip.Parent as WpfPanel;
            if (panel != null)
            {
                foreach (var child in panel.Children)
                {
                    var other = child as System.Windows.Controls.Primitives.ToggleButton;
                    if (other != null && !ReferenceEquals(other, chip))
                        other.IsChecked = false;
                }
            }

            if (chip.IsChecked != true)
            {
                // Un-checking the active chip falls back to "Kõik".
                if (ChipAll != null) ChipAll.IsChecked = true;
                _activeChip = "All";
            }
            else
            {
                _activeChip = chip.Tag as string ?? "All";
            }

            _view?.Refresh();
            UpdateSummary();
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
            if (total == 0)
            {
                SummaryText.Text = "Hallatavaid avasid ei leitud. Loo avad vasakult paneelilt või vajuta „Valideeri“.";
                return;
            }

            var aligned = _items.Count(i => i.Status == OpeningStatus.Aligned);
            var allowed = _items.Count(i => i.Status == OpeningStatus.Allowed);
            var warn = _items.Count(i => i.StatusSeverity == OpeningStatusSeverity.Warning);
            var err = _items.Count(i => i.StatusSeverity == OpeningStatusSeverity.Error);
            var shown = _view != null ? _view.Cast<object>().Count() : total;

            SummaryText.Text = total + " ava • " + aligned + " joondatud • " + warn + " hoiatust • " +
                               err + " viga • " + allowed + " lubatud • kuvatud " + shown;
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

        private MessageBoxResult Confirm(string text, string caption, MessageBoxImage icon)
        {
            var owner = Window.GetWindow(this);
            return owner != null
                ? MessageBox.Show(owner, text, caption, MessageBoxButton.YesNo, icon)
                : MessageBox.Show(text, caption, MessageBoxButton.YesNo, icon);
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

            var msg = BuildFixDescription(item);
            if (Confirm(msg, "Paranda ava", MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _handler.FixPayloads = new List<OpeningFixPayload> { BuildFixPayload(item) };
            _handler.Request = OpeningManagerRequest.FixItems;
            _event.Raise();
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

            var summary = "Parandatakse " + fixable.Count + " ava:\n";
            if (moves > 0) summary += "  • " + moves + " nihutatakse õigesse asukohta\n";
            if (resizes > 0) summary += "  • " + resizes + " suurus uuendatakse\n";
            if (recreates > 0) summary += "  • " + recreates + " taasluuakse (kustutatud avad)\n";
            summary += "\nKas jätkata?";

            if (Confirm(summary, "Paranda valitud", MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _handler.FixPayloads = fixable.Select(BuildFixPayload).ToList();
            _handler.Request = OpeningManagerRequest.FixItems;
            _event.Raise();
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

            var dlg = new AllowReasonDialog(_themeManager == null || _themeManager.IsDarkMode)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            _handler.AllowReason = dlg.Reason;
            RaiseItemRequest(item, OpeningManagerRequest.MarkAllowed);
        }

        private void ClearAllowed_Click(object sender, RoutedEventArgs e) =>
            RaiseItemRequest(ItemFromSender(sender), OpeningManagerRequest.ClearAllowed);

        private void Unlink_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null) return;

            if (Confirm("Eemalda seos? Ava jääb mudelisse alles, kuid haldur seda enam ei jälgi.",
                    "Eemalda seos", MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            RaiseItemRequest(item, OpeningManagerRequest.Unlink);
        }

        private void DeleteOpening_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromSender(sender);
            if (item == null) return;

            if (Confirm("Kustuta ava mudelist?\n\nAllikas: " + item.SourceDisplayName + "\nAlus: " + item.HostDisplayName,
                    "Kustuta ava", MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            RaiseItemRequest(item, OpeningManagerRequest.DeleteOpening);
        }
        #endregion

        #region Details + preview
        private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetails(ItemsGrid.SelectedItem as OpeningManagerItem);
        }

        private void UpdateDetails(OpeningManagerItem item)
        {
            if (DetailLine1 == null) return;

            if (item == null)
            {
                DetailLine1.Text = "–";
                DetailLine2.Text = "";
                DetailLine3.Text = "";
                DetailLine4.Text = "";
                DetailNotes.Text = "–";
                DrawPreview(null);
                return;
            }

            DetailLine1.Text = "Allikas: " + item.SourceText +
                               (string.IsNullOrEmpty(item.SourceModelName) ? "" : " (" + item.SourceModelName + ")");
            DetailLine2.Text = "Alus: " + item.HostDisplayName +
                               (string.IsNullOrEmpty(item.HostModelName) ? "" : " (" + item.HostModelName + ")");
            DetailLine3.Text = "Ava: " + item.OpeningFamilyName + " : " + item.OpeningTypeName +
                               " • " + item.SizeText + " mm • sügavus " + item.DepthMm.ToString("0") + " mm";

            var validated = "";
            DateTime utc;
            if (DateTime.TryParse(item.LastValidated, null, System.Globalization.DateTimeStyles.RoundtripKind, out utc))
                validated = " • valideeritud " + utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            DetailLine4.Text = "Staatus: " + item.StatusText + " • nihe " + item.OffsetText +
                               " • varu " + item.ClearanceMm.ToString("0") + " mm" + validated;

            var notes = item.NotesText;
            if (item.IsAllowed && !string.IsNullOrEmpty(item.AllowedBy))
                notes = (string.IsNullOrEmpty(notes) ? "" : notes + "\n") + "Lubas: " + item.AllowedBy;
            DetailNotes.Text = string.IsNullOrEmpty(notes) ? "–" : notes;

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

            double cw = 208, ch = 102;
            double cx = cw / 2.0, cy = ch / 2.0;

            var borderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
            var fgBrush = TryFindResource("IconForegroundBrush") as Brush ?? Brushes.Gray;
            var statusBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(item.StatusColor));

            // Host band (wall/floor section)
            var host = new WpfRectangle
            {
                Width = cw - 16,
                Height = 34,
                Fill = borderBrush,
                Opacity = 0.35,
                RadiusX = 2,
                RadiusY = 2
            };
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
                    Stroke = fgBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = Brushes.Transparent
                };
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
                    Stroke = statusBrush,
                    StrokeThickness = 1.6,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(current, cx - rw / 2.0 + dx);
                Canvas.SetTop(current, cy - rh / 2.0 + dy);
                PreviewCanvas.Children.Add(current);

                // Center dot
                var dot = new WpfEllipse { Width = 4, Height = 4, Fill = statusBrush };
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
                FontSize = 10,
                Foreground = fgBrush
            };
            Canvas.SetLeft(label, 8);
            Canvas.SetTop(label, ch - 16);
            PreviewCanvas.Children.Add(label);
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
