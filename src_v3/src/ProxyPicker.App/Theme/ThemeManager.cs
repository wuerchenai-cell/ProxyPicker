using System;
using System.Windows;
using Microsoft.Win32;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.App.Theme
{
    /// <summary>深色 / 浅色 / 跟随系统动态主题热切换管理器。</summary>
    public static class ThemeManager
    {
        static ResourceDictionary _activeThemeDict;
        static bool _isDark;

        public static bool IsDark => _isDark;

        public static bool DetectWindowsDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int i) return i == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void Apply(ResourceDictionary targetRoot, ThemePreference preference)
        {
            if (targetRoot == null) return;
            bool dark = preference == ThemePreference.Dark ||
                        (preference == ThemePreference.System && DetectWindowsDarkMode());
            _isDark = dark;

            string uriStr = dark
                ? "pack://application:,,,/ProxyPicker;component/Theme/ThemeDark.xaml"
                : "pack://application:,,,/ProxyPicker;component/Theme/ThemeLight.xaml";

            var next = new ResourceDictionary { Source = new Uri(uriStr, UriKind.Absolute) };
            if (_activeThemeDict != null)
                targetRoot.MergedDictionaries.Remove(_activeThemeDict);
            targetRoot.MergedDictionaries.Insert(0, next);
            _activeThemeDict = next;
        }
    }
}
