using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer.Services
{
    /// <summary>アプリケーションの配色を、設定値またはWindowsの配色設定から適用する。</summary>
    public static class ThemeService
    {
        public static bool IsLightMode { get; private set; }
        public static event EventHandler ThemeChanged;
        private const string DarkThemeUri = "Resources/Themes/DarkTheme.xaml";
        private const string LightThemeUri = "Resources/Themes/LightTheme.xaml";
        private const string AshThemeUri = "Resources/Themes/AshTheme.xaml";
        private static ThemeMode _currentMode = ThemeMode.System;
        private static bool _isWatchingSystemTheme;
        private static string _appliedThemeUri;

        public static void Apply(ThemeMode mode)
        {
            _currentMode = mode;
            bool watchSystemTheme = mode == ThemeMode.System;
            if (watchSystemTheme != _isWatchingSystemTheme)
            {
                if (watchSystemTheme)
                    SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                else
                    SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
                _isWatchingSystemTheme = watchSystemTheme;
            }

            string targetUri = ResolveThemeUri(mode);
            if (_appliedThemeUri == targetUri)
                return;

            _appliedThemeUri = targetUri;
            IsLightMode = string.Equals(targetUri, LightThemeUri, StringComparison.OrdinalIgnoreCase);
            var dictionaries = Application.Current?.Resources?.MergedDictionaries;
            if (dictionaries != null)
            {
                for (int index = dictionaries.Count - 1; index >= 0; index--)
                {
                    var source = dictionaries[index].Source?.OriginalString;
                    if (source != null &&
                        (source.EndsWith(DarkThemeUri, StringComparison.OrdinalIgnoreCase) ||
                         source.EndsWith(LightThemeUri, StringComparison.OrdinalIgnoreCase) ||
                         source.EndsWith(AshThemeUri, StringComparison.OrdinalIgnoreCase)))
                        dictionaries.RemoveAt(index);
                }

                dictionaries.Add(new ResourceDictionary { Source = new Uri(targetUri, UriKind.Relative) });
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static string ResolveThemeUri(ThemeMode mode)
        {
            return mode switch
            {
                ThemeMode.Light => LightThemeUri,
                ThemeMode.Dark => DarkThemeUri,
                ThemeMode.Ash => AshThemeUri,
                // システムがダークの場合は低コントラストのアッシュを既定にする。
                _ => IsSystemLight() ? LightThemeUri : AshThemeUri,
            };
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentMode == ThemeMode.System)
                    Apply(_currentMode);
            }), DispatcherPriority.ApplicationIdle);
        }

        private static bool IsSystemLight()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 0);
                return value is int enabled && enabled != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
