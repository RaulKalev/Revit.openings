using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;

/// <summary>
/// Appearance + persisted settings for the RedeliAvad window.
/// Appearance follows Revit.Sentinel: a Dark/Light palette plus the Windows accessibility preferences
///  - High contrast       → palette built from the system high-contrast colours,
///  - Transparency off    → solid materials (no gradients/shadows),
///  - Animations off      → <see cref="ReducedMotion"/> (Motion helper uses opacity only).
/// Only the palette dictionaries are swapped; the shared styles stay merged.
/// Settings (theme, window placement, last used family/options) persist to
/// C:\ProgramData\RK Tools\RedeliAvad\config.json.
/// </summary>
public class ThemeManager
{
    private const string ConfigFilePath = @"C:\ProgramData\RK Tools\RedeliAvad\config.json";
    private readonly Window _window;
    private bool _isDarkMode = true;
    private ResourceDictionary[] _applied;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set { _isDarkMode = value; SaveConfig(); }
    }

    public double WindowWidth { get; set; } = 380;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;

    // ---- persisted plugin settings ----
    public string SelectedFamilyName { get; set; } = null;      // e.g., "MyFamily"
    public string SelectedTypeName { get; set; } = null;      // e.g., "100x100"
    public bool SkipDuplicatesOpt { get; set; } = true;
    public double TolMm { get; set; } = 50;        // Proximity tolerance (mm)
    public double ExtWidthMm { get; set; } = 0;         // Width extension (mm)
    public double ExtDepthMm { get; set; } = 100;       // Depth extension (mm)
    public double ExtHeightMm { get; set; } = 0;         // Height extension (mm)

    public event EventHandler ThemeChanged;

    public ThemeManager(Window window)
    {
        _window = window;
        LoadConfig();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        window.Closed += (s, e) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    // ------------------------------------------------------------------ accessibility preferences

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>Windows "Transparency effects" (Settings → Personalization → Colors) switched off.</summary>
    public static bool SolidMaterials
    {
        get
        {
            if (SystemParameters.HighContrast) return true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key?.GetValue("EnableTransparency");
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Windows "Animation effects" switched off.</summary>
    public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    private void OnSystemParametersChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTheme();
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    // ------------------------------------------------------------------ apply

    public void ToggleTheme()
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SaveConfig();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Swaps the palette (+ materials) in the window's resources; must run after InitializeComponent.</summary>
    public void ApplyTheme()
    {
        try
        {
            var target = _window.Resources;
            var dicts = BuildDictionaries();

            // Remove what we applied before, plus the palette merged from XAML at parse time.
            if (_applied != null)
                foreach (var d in _applied) target.MergedDictionaries.Remove(d);
            foreach (var d in target.MergedDictionaries.Where(IsPaletteSource).ToList()) target.MergedDictionaries.Remove(d);

            for (int i = 0; i < dicts.Length; i++) target.MergedDictionaries.Insert(i, dicts[i]);
            _applied = dicts;
        }
        catch (Exception ex)
        {
            RedeliAvad.OpeningManagerLog.Error("Applying theme failed", ex);
        }
    }

    private static bool IsPaletteSource(ResourceDictionary d)
    {
        var s = d.Source?.OriginalString;
        return s != null && s.Contains("/UI/Themes/") && (s.EndsWith("DarkTheme.xaml") || s.EndsWith("LightTheme.xaml"));
    }

    private ResourceDictionary[] BuildDictionaries()
    {
        if (HighContrast) return new[] { BuildHighContrast() };

        var palette = new ResourceDictionary
        {
            Source = new Uri(_isDarkMode
                ? "pack://application:,,,/RedeliAvad;component/UI/Themes/DarkTheme.xaml"
                : "pack://application:,,,/RedeliAvad;component/UI/Themes/LightTheme.xaml", UriKind.Absolute)
        };
        var materials = SolidMaterials ? BuildSolidMaterials(palette) : new ResourceDictionary { ["Shadow.Opacity"] = _isDarkMode ? 0.45 : 0.16 };
        return new[] { palette, materials };
    }

    /// <summary>Reduced transparency: materials become solid surfaces with a defined edge, no shadows.</summary>
    private static ResourceDictionary BuildSolidMaterials(ResourceDictionary palette)
    {
        var raised = palette["Surface.Raised"] as Brush;
        var stroke = palette["Surface.Stroke"] as Brush;
        var content = palette["Surface.Content"] as Brush;
        return new ResourceDictionary
        {
            ["Sidebar.Material"] = palette["Surface.Inset"],
            ["Toolbar.Material"] = palette["Window.Background"],
            ["Floating.Material"] = raised ?? content,
            ["Floating.Edge"] = stroke,
            ["Sidebar.Edge"] = stroke,
            ["Shadow.Opacity"] = 0.0
        };
    }

    /// <summary>High contrast: every brush maps to a Windows system colour so the user's HC scheme is honoured.</summary>
    private static ResourceDictionary BuildHighContrast()
    {
        Func<Color, SolidColorBrush> b = c => { var br = new SolidColorBrush(c); br.Freeze(); return br; };
        var window = b(SystemColors.WindowColor);
        var text = b(SystemColors.WindowTextColor);
        var gray = b(SystemColors.GrayTextColor);
        var highlight = b(SystemColors.HighlightColor);
        var highlightText = b(SystemColors.HighlightTextColor);
        var hot = b(SystemColors.HotTrackColor);
        var btn = b(SystemColors.ControlColor);
        var d = new ResourceDictionary();
        foreach (var k in new[] { "Window.Background", "Sidebar.Material", "Toolbar.Material", "Floating.Material", "Surface.Content",
                                  "Surface.Raised", "Surface.Inset", "Input.Fill", "Control.Fill",
                                  "Row.Alternate", "Status.Ok.Subtle", "Status.Info.Subtle", "Status.Warning.Subtle",
                                  "Status.Error.Subtle", "Status.Neutral.Subtle", "Accent.Subtle" })
            d[k] = window;
        foreach (var k in new[] { "Window.Stroke", "Sidebar.Edge", "Floating.Edge", "Surface.Stroke", "Separator", "Control.Stroke",
                                  "Input.Stroke", "Input.StrokeHover", "Text.Primary", "Text.Secondary",
                                  "Status.Ok", "Status.Info", "Status.Warning", "Status.Error", "Status.Neutral" })
            d[k] = text;
        d["Text.Tertiary"] = text;
        d["Text.Disabled"] = gray;
        foreach (var k in new[] { "Accent", "Accent.Hover", "Accent.Pressed", "Row.Selected", "Row.SelectedInactive", "Control.FillPressed" })
            d[k] = highlight;
        d["Text.OnAccent"] = highlightText;
        d["Accent.Text"] = hot;
        d["Focus.Ring"] = hot;
        d["Control.FillHover"] = btn;
        d["Control.Plain.Hover"] = btn;
        d["Control.Plain.Pressed"] = highlight;
        d["Row.Hover"] = btn;
        d["Scrim"] = b(Color.FromArgb(0x80, 0, 0, 0));
        d["Shadow.Color"] = Colors.Black;
        d["Shadow.Opacity"] = 0.0;
        return d;
    }

    // ------------------------------------------------------------------ settings

    public void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (config == null) return;

            if (config.ContainsKey("IsDarkMode")) _isDarkMode = Convert.ToBoolean(config["IsDarkMode"]);
            if (config.ContainsKey("WindowWidth")) WindowWidth = Convert.ToDouble(config["WindowWidth"]);
            if (config.ContainsKey("WindowHeight")) WindowHeight = Convert.ToDouble(config["WindowHeight"]);
            if (config.ContainsKey("WindowLeft")) WindowLeft = Convert.ToDouble(config["WindowLeft"]);
            if (config.ContainsKey("WindowTop")) WindowTop = Convert.ToDouble(config["WindowTop"]);

            if (config.ContainsKey("SelectedFamilyName")) SelectedFamilyName = Convert.ToString(config["SelectedFamilyName"]);
            if (config.ContainsKey("SelectedTypeName")) SelectedTypeName = Convert.ToString(config["SelectedTypeName"]);
            if (config.ContainsKey("SkipDuplicatesOpt")) SkipDuplicatesOpt = Convert.ToBoolean(config["SkipDuplicatesOpt"]);
            if (config.ContainsKey("TolMm")) TolMm = Convert.ToDouble(config["TolMm"]);
            if (config.ContainsKey("ExtWidthMm")) ExtWidthMm = Convert.ToDouble(config["ExtWidthMm"]);
            if (config.ContainsKey("ExtDepthMm")) ExtDepthMm = Convert.ToDouble(config["ExtDepthMm"]);
            if (config.ContainsKey("ExtHeightMm")) ExtHeightMm = Convert.ToDouble(config["ExtHeightMm"]);
        }
        catch { /* ignore */ }
    }

    public void SaveConfig()
    {
        try
        {
            // Only a normal-state window has meaningful bounds.
            if (_window.WindowState == WindowState.Normal)
            {
                WindowWidth = _window.Width;
                WindowHeight = _window.Height;
                WindowLeft = _window.Left;
                WindowTop = _window.Top;
            }

            var config = new Dictionary<string, object>
            {
                ["IsDarkMode"] = _isDarkMode,
                ["WindowWidth"] = WindowWidth,
                ["WindowHeight"] = WindowHeight,
                ["WindowLeft"] = WindowLeft,
                ["WindowTop"] = WindowTop,

                ["SelectedFamilyName"] = SelectedFamilyName,
                ["SelectedTypeName"] = SelectedTypeName,
                ["SkipDuplicatesOpt"] = SkipDuplicatesOpt,
                ["TolMm"] = TolMm,
                ["ExtWidthMm"] = ExtWidthMm,
                ["ExtDepthMm"] = ExtDepthMm,
                ["ExtHeightMm"] = ExtHeightMm
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch { /* ignore */ }
    }
}
