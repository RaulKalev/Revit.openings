using System;
using System.Windows;

namespace RedeliAvad
{
    /// <summary>Small styled prompt for the optional "mark as allowed" reason.</summary>
    public partial class AllowReasonDialog : Window
    {
        public string Reason { get; private set; } = "";

        public AllowReasonDialog(bool isDarkMode)
        {
            InitializeComponent();
            ApplyTheme(isDarkMode);
            Loaded += (s, e) => ReasonBox.Focus();
        }

        private void ApplyTheme(bool isDarkMode)
        {
            var assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
            var themeUri = isDarkMode
                ? $"pack://application:,,,/{assemblyName};component/UI/Themes/DarkTheme.xaml"
                : $"pack://application:,,,/{assemblyName};component/UI/Themes/LightTheme.xaml";
            try
            {
                var dict = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) };
                Resources.MergedDictionaries.Clear();
                Resources.MergedDictionaries.Add(dict);
            }
            catch { /* keep parse-time theme */ }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Reason = ReasonBox.Text ?? "";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
