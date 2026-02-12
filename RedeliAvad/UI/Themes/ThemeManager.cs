using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Newtonsoft.Json;

public class ThemeManager
{
    private const string ConfigFilePath = @"C:\ProgramData\RK Tools\RedeliAvad\config.json";
    private readonly Window _window;
    private bool _isDarkMode = true;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set { _isDarkMode = value; SaveConfig(); }
    }

    public double WindowWidth { get; set; } = 350;
    public double WindowHeight { get; set; } = 480;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;

    // ---- NEW: persisted plugin settings ----
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
        ApplyTheme();
    }

    public void ToggleTheme()
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SaveConfig();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTheme()
    {
        var assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
        var themeUri = _isDarkMode
            ? $"pack://application:,,,/{assemblyName};component/UI/Themes/DarkTheme.xaml"
            : $"pack://application:,,,/{assemblyName};component/UI/Themes/LightTheme.xaml";

        try
        {
            var resourceDict = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) };
            _window.Resources.MergedDictionaries.Clear();
            _window.Resources.MergedDictionaries.Add(resourceDict);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load theme: {ex.Message}", "Theme Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

            // NEW: read plugin settings if present
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
            var config = new Dictionary<string, object>
            {
                ["IsDarkMode"] = _isDarkMode,
                ["WindowWidth"] = _window.Width,
                ["WindowHeight"] = _window.Height,
                ["WindowLeft"] = _window.Left,
                ["WindowTop"] = _window.Top,

                // NEW: plugin settings
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
