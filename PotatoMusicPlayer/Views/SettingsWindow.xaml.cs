using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using PotatoMusicPlayer.Services;
using PotatoMusicPlayer.Models;
using Newtonsoft.Json;

namespace PotatoMusicPlayer.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly LanguageService _languageService;
        private AppSettings _editableSettings;
        private HotKeyBinding _capturingBinding;
        private readonly Dictionary<System.Windows.Controls.Control, string> _appliedValues = new();
        private readonly Dictionary<System.Windows.Controls.Control, PendingSettingAdorner> _pendingAdorners = new();

        public SettingsWindow(SettingsService settingsService)
        {
            // XAMLとテーマリソースの読み込み完了前に既定の白背景が表示されないよう、
            // 現在のテーマに合う背景を先に設定する。描画後はスタイルの動的リソースへ戻す。
            Background = Application.Current?.TryFindResource("SurfaceWindowBrush") as Brush;
            InitializeComponent();
            ContentRendered += SettingsWindow_ContentRendered;
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Deep copy settings to editable instance
            var original = _settingsService.GetSettings();
            var json = JsonConvert.SerializeObject(original);
            _editableSettings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            _languageService = new LanguageService(_editableSettings.Language);
            ApplyLanguage();
            HotKeyItemsControl.ItemsSource = _editableSettings.HotKeyBindings;
            SkipDurationText.Text = _editableSettings.SkipDurationSeconds.ToString();
            StepDurationText.Text = _editableSettings.StepDurationSeconds.ToString("0.##");
            SpeedStepText.Text = _editableSettings.SpeedChangePercent.ToString();
            ScrollVolumeText.Text = _editableSettings.ScrollVolumeChangePercent.ToString();
            HotkeyVolumeText.Text = _editableSettings.VolumeChangePercent.ToString();
            SpeedResetText.Text = _editableSettings.SpeedResetPercent.ToString();
            MaxRecentFilesText.Text = _editableSettings.MaxRecentFiles.ToString();
            DefaultVolumeText.Text = ((int)Math.Round(_editableSettings.DefaultVolume * 100)).ToString();
            MaxVolumeText.Text = ((int)Math.Round(_editableSettings.MaxVolumeMultiplier * 100)).ToString();
            UpdateHotKeyDisplayNames();
            PreviewKeyDown += SettingsWindow_PreviewKeyDown;
            PreviewMouseWheel += SettingsWindow_PreviewMouseWheel;

            // ShowWaveform
            ShowWaveformCheck.IsChecked = _editableSettings.ShowWaveform;
            FreeViewCheck.IsChecked = _editableSettings.WaveformZoom.FreeView;
            ShowSpectrumCheck.IsChecked = _editableSettings.ShowSpectrum;
            var waveformZoom = _editableSettings.WaveformZoom ?? new WaveformZoomSettings();
            _editableSettings.WaveformZoom = waveformZoom;
            ProgressiveWaveformCheck.IsChecked = waveformZoom.ProgressiveWaveform;
            SaveWaveformCacheCheck.IsChecked = waveformZoom.SaveWaveformCache;
            DisplayCacheLimitValueText.Text = waveformZoom.WaveformCacheLimitValue.ToString("0.##");
            NumericCacheLimitValueText.Text = DisplayCacheLimitValueText.Text;
            int cacheUnitIndex = Math.Clamp((int)waveformZoom.WaveformCacheLimitUnit, 0, 2);
            DisplayCacheLimitUnitComboBox.SelectedIndex = cacheUnitIndex;
            NumericCacheLimitUnitComboBox.SelectedIndex = cacheUnitIndex;
            DisplayCacheLimitValueText.TextChanged += DisplayCacheLimitValueText_TextChanged;
            NumericCacheLimitValueText.TextChanged += NumericCacheLimitValueText_TextChanged;
            DisplayCacheLimitUnitComboBox.SelectionChanged += DisplayCacheLimitUnitComboBox_SelectionChanged;
            NumericCacheLimitUnitComboBox.SelectionChanged += NumericCacheLimitUnitComboBox_SelectionChanged;
            ShowMinimapCheck.IsChecked = waveformZoom.ShowMinimap;
            CursorModeComboBox.SelectedIndex = waveformZoom.CursorMode == CursorDisplayMode.LeftScroll ? 1 : 0;
            MinZoomLevelText.Text = waveformZoom.MinZoomLevel.ToString("0.##");
            ZoomFactorText.Text = waveformZoom.ZoomFactor.ToString("0.##");
            WaveformScrollStepText.Text = waveformZoom.ScrollStepSize.ToString("0.##");
            MinimapHeightText.Text = waveformZoom.MinimapHeight.ToString();
            HorizontalDetailText.Text = waveformZoom.HorizontalDetail.ToString();
            VerticalDetailText.Text = waveformZoom.VerticalDetail.ToString();

            PopulateLanguageCombo();
            ThemeComboBox.SelectedIndex = (int)_editableSettings.Theme;
            RememberLastVolumeCheck.IsChecked = _editableSettings.RememberLastVolume;
            RememberLastSpeedCheck.IsChecked = _editableSettings.RememberLastPlaybackSpeed;
            RememberLastLoopCheck.IsChecked = _editableSettings.RememberLastLoopMode;
            RememberWaveformZoomCheck.IsChecked = _editableSettings.RememberWaveformZoom;
            AutoPlayOnLoadCheck.IsChecked = _editableSettings.AutoPlayOnLoad;
            GlassBackgroundCheck.IsChecked = _editableSettings.IsGlassBackground;
            DefaultWaveformZoomValueText.Text = _editableSettings.DefaultWaveformZoomValue.ToString("0.##");
            DefaultWaveformZoomUnitComboBox.SelectedIndex = _editableSettings.DefaultWaveformZoomUnit == WaveformZoomUnit.Seconds ? 1 : 0;
            TrackTransitionDelayText.Text = _editableSettings.TrackTransitionDelaySeconds.ToString("0.##");

            // default selection
            CategoryList.SelectedIndex = 0; // select "一般" by default
            Loaded += (_, _) => CaptureAppliedSettingValues();
        }

        /// <summary>
        /// 言語ファイルの一覧から選択肢を作る。言語追加はJSON追加のみでよい。
        /// </summary>
        private void PopulateLanguageCombo()
        {
            LanguageComboBox.Items.Clear();
            int selected = 0;
            int index = 0;
            foreach (var info in LanguageService.GetAvailableLanguages())
            {
                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = info.DisplayName,
                    Tag = info.Code
                };
                LanguageComboBox.Items.Add(item);
                if (string.Equals(info.Code, _editableSettings.Language, StringComparison.OrdinalIgnoreCase))
                    selected = index;
                index++;
            }
            LanguageComboBox.SelectedIndex = LanguageComboBox.Items.Count > 0 ? selected : -1;
        }

        private void SettingsWindow_ContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= SettingsWindow_ContentRendered;
            ClearValue(BackgroundProperty);
            // Material entrance: fade + slight rise. Duration comes from Motion tokens
            // so the whole app can be retimed from one place. One-shot only.
            Duration entranceDuration = TryFindResource("MaterialDurationNormal") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(180));
            var fadeIn = new DoubleAnimation(0, 1, entranceDuration);
            var rise = new DoubleAnimation(8, 0, entranceDuration)
            {
                EasingFunction = TryFindResource("MaterialEaseOut") as IEasingFunction
            };
            SettingsEntranceTranslate?.BeginAnimation(TranslateTransform.YProperty, rise);
            BeginAnimation(OpacityProperty, fadeIn);
        }

        private bool ApplyCurrentSettings()
        {
            // Apply edited values to the original settings instance and save
            _editableSettings.ShowWaveform = ShowWaveformCheck.IsChecked == true;
            _editableSettings.WaveformZoom.FreeView = FreeViewCheck.IsChecked == true;
            _editableSettings.ShowSpectrum = ShowSpectrumCheck.IsChecked == true;
            _editableSettings.WaveformZoom.ShowMinimap = ShowMinimapCheck.IsChecked == true;
            _editableSettings.WaveformZoom.ProgressiveWaveform = ProgressiveWaveformCheck.IsChecked == true;
            _editableSettings.WaveformZoom.SaveWaveformCache = SaveWaveformCacheCheck.IsChecked == true;
            _editableSettings.RememberLastVolume = RememberLastVolumeCheck.IsChecked == true;
            _editableSettings.RememberLastPlaybackSpeed = RememberLastSpeedCheck.IsChecked == true;
            _editableSettings.RememberLastLoopMode = RememberLastLoopCheck.IsChecked == true;
            _editableSettings.RememberWaveformZoom = RememberWaveformZoomCheck.IsChecked == true;
            _editableSettings.AutoPlayOnLoad = AutoPlayOnLoadCheck.IsChecked == true;
            _editableSettings.IsGlassBackground = GlassBackgroundCheck.IsChecked == true;
            if (!TryReadNumericSettings() || HasDuplicateHotKeys())
                return false;
            if (LanguageComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem languageItem &&
                languageItem.Tag is string languageCode)
            {
                _editableSettings.Language = LanguageService.NormalizeCode(languageCode);
            }
            if (ThemeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem themeItem &&
                Enum.TryParse(themeItem.Tag?.ToString(), out ThemeMode theme))
            {
                _editableSettings.Theme = theme;
            }
            if (CursorModeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem cursorItem &&
                Enum.TryParse(cursorItem.Tag?.ToString(), out CursorDisplayMode cursorMode))
            {
                // ビュー自由化と中央固定は排他。自由化がオンなら左流しに寄せる。
                if (_editableSettings.WaveformZoom.FreeView && cursorMode == CursorDisplayMode.CenterFixed)
                {
                    cursorMode = CursorDisplayMode.LeftScroll;
                    CursorModeComboBox.SelectedIndex = 1;
                }
                _editableSettings.WaveformZoom.CursorMode = cursorMode;
            }
            if (DefaultWaveformZoomUnitComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem zoomUnitItem &&
                Enum.TryParse(zoomUnitItem.Tag?.ToString(), out WaveformZoomUnit zoomUnit))
            {
                _editableSettings.DefaultWaveformZoomUnit = zoomUnit;
            }
            _settingsService.SaveSettings(_editableSettings);
            _languageService.Load(_editableSettings.Language);
            ThemeService.Apply(_editableSettings.Theme);
            ApplyLanguage();
            CaptureAppliedSettingValues();
            return true;
        }

        private void CaptureAppliedSettingValues()
        {
            foreach (var control in GetSettingControls(this))
            {
                if (!_appliedValues.ContainsKey(control))
                {
                    if (control is System.Windows.Controls.TextBox textBox)
                        textBox.TextChanged += SettingControlChanged;
                    else if (control is System.Windows.Controls.ComboBox comboBox)
                        comboBox.SelectionChanged += SettingControlChanged;
                    else if (control is System.Windows.Controls.CheckBox checkBox)
                    {
                        checkBox.Checked += SettingControlChanged;
                        checkBox.Unchecked += SettingControlChanged;
                    }
                }

                _appliedValues[control] = GetSettingValue(control);
                RemovePendingAdorner(control);
            }
        }

        private static IEnumerable<System.Windows.Controls.Control> GetSettingControls(DependencyObject parent)
        {
            int children = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < children; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.TextBox || child is System.Windows.Controls.ComboBox || child is System.Windows.Controls.CheckBox)
                    yield return (System.Windows.Controls.Control)child;

                foreach (var descendant in GetSettingControls(child))
                    yield return descendant;
            }
        }

        private static string GetSettingValue(System.Windows.Controls.Control control)
        {
            return control switch
            {
                System.Windows.Controls.TextBox textBox => textBox.Text ?? string.Empty,
                System.Windows.Controls.ComboBox comboBox => comboBox.SelectedIndex.ToString(),
                System.Windows.Controls.CheckBox checkBox => checkBox.IsChecked == true ? "true" : "false",
                _ => string.Empty
            };
        }

        private void SettingControlChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Control control || !_appliedValues.TryGetValue(control, out string applied))
                return;

            if (GetSettingValue(control) == applied)
                RemovePendingAdorner(control);
            else
                AddPendingAdorner(control);
        }

        private void AddPendingAdorner(System.Windows.Controls.Control control)
        {
            if (_pendingAdorners.ContainsKey(control))
                return;

            var layer = AdornerLayer.GetAdornerLayer(control);
            if (layer == null)
                return;

            var adorner = new PendingSettingAdorner(control);
            layer.Add(adorner);
            _pendingAdorners[control] = adorner;
        }

        private void RemovePendingAdorner(System.Windows.Controls.Control control)
        {
            if (_pendingAdorners.Remove(control, out var adorner))
                AdornerLayer.GetAdornerLayer(control)?.Remove(adorner);
        }

        private bool TryReadNumericSettings()
        {
            if (!int.TryParse(SkipDurationText.Text, out int skip) || skip < 1 || skip > 3600 ||
                !float.TryParse(StepDurationText.Text, out float step) || step <= 0 || step > 60 ||
                !int.TryParse(ScrollVolumeText.Text, out int scrollVolume) || scrollVolume < 1 || scrollVolume > 100 ||
                !int.TryParse(HotkeyVolumeText.Text, out int volumeStep) || volumeStep < 1 || volumeStep > 100 ||
                !int.TryParse(SpeedStepText.Text, out int speedStep) || speedStep < 1 || speedStep > 100 ||
                !int.TryParse(SpeedResetText.Text, out int speedReset) || speedReset < 1 || speedReset > 1000 ||
                !int.TryParse(MaxRecentFilesText.Text, out int maxRecentFiles) || maxRecentFiles < 1 || maxRecentFiles > 1000 ||
                !int.TryParse(DefaultVolumeText.Text, out int defaultVolume) || defaultVolume < 0 || defaultVolume > 1000 ||
                !int.TryParse(MaxVolumeText.Text, out int maxVolume) || maxVolume < 100 || maxVolume > 1000 ||
                !float.TryParse(MinZoomLevelText.Text, out float minZoomLevel) || minZoomLevel < 0.1f || minZoomLevel > 60 ||
                !float.TryParse(ZoomFactorText.Text, out float zoomFactor) || zoomFactor < 1.1f || zoomFactor > 10 ||
                !float.TryParse(WaveformScrollStepText.Text, out float waveformScrollStep) || waveformScrollStep <= 0 || waveformScrollStep > 3600 ||
                !int.TryParse(MinimapHeightText.Text, out int minimapHeight) || minimapHeight < 8 || minimapHeight > 64 ||
                !int.TryParse(HorizontalDetailText.Text, out int horizontalDetail) || horizontalDetail < 0 || horizontalDetail > 100 ||
                !int.TryParse(VerticalDetailText.Text, out int verticalDetail) || verticalDetail < 0 || verticalDetail > 100 ||
                !double.TryParse(DefaultWaveformZoomValueText.Text, out double defaultWaveformZoomValue) || defaultWaveformZoomValue <= 0 || defaultWaveformZoomValue > 100000 ||
                !double.TryParse(TrackTransitionDelayText.Text, out double trackTransitionDelay) || trackTransitionDelay < 0 || trackTransitionDelay > 60 ||
                defaultVolume > maxVolume)
            {
                MessageBox.Show("数値設定を確認してください。\n入力された値が範囲外であるか、形式が正しくありません。", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _editableSettings.SkipDurationSeconds = skip;
            _editableSettings.StepDurationSeconds = step;
            _editableSettings.ScrollVolumeChangePercent = scrollVolume;
            _editableSettings.VolumeChangePercent = volumeStep;
            _editableSettings.SpeedChangePercent = speedStep;
            _editableSettings.SpeedResetPercent = speedReset;
            _editableSettings.MaxRecentFiles = maxRecentFiles;
            _editableSettings.DefaultVolume = defaultVolume / 100.0f;
            _editableSettings.MaxVolumeMultiplier = maxVolume / 100.0f;
            _editableSettings.WaveformZoom.MinZoomLevel = minZoomLevel;
            _editableSettings.WaveformZoom.ZoomFactor = zoomFactor;
            _editableSettings.WaveformZoom.ScrollStepSize = waveformScrollStep;
            _editableSettings.WaveformZoom.MinimapHeight = minimapHeight;
            _editableSettings.WaveformZoom.HorizontalDetail = horizontalDetail;
            _editableSettings.WaveformZoom.VerticalDetail = verticalDetail;
            _editableSettings.DefaultWaveformZoomValue = defaultWaveformZoomValue;
            _editableSettings.TrackTransitionDelaySeconds = trackTransitionDelay;
            if (!TryReadWaveformCacheLimit())
                return false;
            UpdateHotKeyDisplayNames();
            return true;
        }

        private bool TryReadWaveformCacheLimit()
        {
            // 表示と数値の2箇所は常時同期している。両方が編集された場合は最後に編集された方を優先する。
            string limitText = _lastCacheLimitEditFromDisplay ? DisplayCacheLimitValueText.Text : NumericCacheLimitValueText.Text;
            var unitComboBox = _lastCacheLimitEditFromDisplay ? DisplayCacheLimitUnitComboBox : NumericCacheLimitUnitComboBox;
            string unitTag = (unitComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "MB";
            if (!double.TryParse(limitText, out double limit) || limit <= 0 || limit > 100000 ||
                !Enum.TryParse(unitTag, out CacheSizeUnit unit))
            {
                MessageBox.Show("数値設定を確認してください。\n入力された値が範囲外であるか、形式が正しくありません。", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _editableSettings.WaveformZoom.WaveformCacheLimitValue = limit;
            _editableSettings.WaveformZoom.WaveformCacheLimitUnit = unit;
            return true;
        }

        private bool _syncingCacheLimit;
        private bool _lastCacheLimitEditFromDisplay = true;

        private void DisplayCacheLimitValueText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_syncingCacheLimit)
                return;
            _lastCacheLimitEditFromDisplay = true;
            if (NumericCacheLimitValueText.Text != DisplayCacheLimitValueText.Text)
            {
                _syncingCacheLimit = true;
                try { NumericCacheLimitValueText.Text = DisplayCacheLimitValueText.Text; }
                finally { _syncingCacheLimit = false; }
            }
        }

        private void NumericCacheLimitValueText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_syncingCacheLimit)
                return;
            _lastCacheLimitEditFromDisplay = false;
            if (DisplayCacheLimitValueText.Text != NumericCacheLimitValueText.Text)
            {
                _syncingCacheLimit = true;
                try { DisplayCacheLimitValueText.Text = NumericCacheLimitValueText.Text; }
                finally { _syncingCacheLimit = false; }
            }
        }

        private void DisplayCacheLimitUnitComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingCacheLimit)
                return;
            _lastCacheLimitEditFromDisplay = true;
            if (NumericCacheLimitUnitComboBox.SelectedIndex != DisplayCacheLimitUnitComboBox.SelectedIndex)
            {
                _syncingCacheLimit = true;
                try { NumericCacheLimitUnitComboBox.SelectedIndex = DisplayCacheLimitUnitComboBox.SelectedIndex; }
                finally { _syncingCacheLimit = false; }
            }
        }

        private void NumericCacheLimitUnitComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingCacheLimit)
                return;
            _lastCacheLimitEditFromDisplay = false;
            if (DisplayCacheLimitUnitComboBox.SelectedIndex != NumericCacheLimitUnitComboBox.SelectedIndex)
            {
                _syncingCacheLimit = true;
                try { DisplayCacheLimitUnitComboBox.SelectedIndex = NumericCacheLimitUnitComboBox.SelectedIndex; }
                finally { _syncingCacheLimit = false; }
            }
        }

        private void ClearWaveformCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var (files, bytes) = new WaveformCacheService(long.MaxValue).Clear();
            MessageBox.Show(
                string.Format(_languageService.Get("Settings.ClearWaveformCacheDone"), files, WaveformCacheService.FormatSize(bytes)),
                _languageService.Get("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool HasDuplicateHotKeys()
        {
            for (int i = 0; i < _editableSettings.HotKeyBindings.Count; i++)
            {
                for (int j = i + 1; j < _editableSettings.HotKeyBindings.Count; j++)
                {
                    var left = _editableSettings.HotKeyBindings[i];
                    var right = _editableSettings.HotKeyBindings[j];
                    bool sameInput = left.InputType == right.InputType &&
                        (left.InputType != HotKeyInputType.Key || left.Key == right.Key);
                    if (sameInput && left.Modifiers == right.Modifiers &&
                        (left.InputType != HotKeyInputType.Key || left.Key != System.Windows.Input.Key.None))
                    {
                        MessageBox.Show($"ホットキーが重複しています: {left}", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return true;
                    }
                }
            }
            return false;
        }

        private void HotKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.DataContext is HotKeyBinding binding)
            {
                _capturingBinding = binding;
                button.Content = "キーを入力...";
                button.Focus();
            }
        }

        private void ResetHotKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.DataContext is HotKeyBinding binding)
            {
                var defaults = new AppSettings();
                defaults.InitializeDefaultHotKeys();
                var defaultBinding = defaults.HotKeyBindings.Find(item => item.Action == binding.Action);
                if (defaultBinding != null)
                {
                    binding.Key = defaultBinding.Key;
                    binding.Modifiers = defaultBinding.Modifiers;
                    binding.InputType = defaultBinding.InputType;
                }
                UpdateHotKeyDisplayNames();
            }
        }

        private void ClearHotKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.DataContext is HotKeyBinding binding)
            {
                binding.Key = System.Windows.Input.Key.None;
                binding.Modifiers = System.Windows.Input.ModifierKeys.None;
                binding.InputType = HotKeyInputType.Key;
                UpdateHotKeyDisplayNames();
            }
        }

        private void UpdateHotKeyDisplayNames()
        {
            foreach (var binding in _editableSettings.HotKeyBindings)
            {
                switch (binding.Action)
                {
                    case HotKeyAction.PlayPause: binding.DisplayName = _languageService.Get("Hotkey.PlayPause"); break;
                    case HotKeyAction.Stop: binding.DisplayName = _languageService.Get("Hotkey.Stop"); break;
                    case HotKeyAction.SkipBackward5s: binding.DisplayName = string.Format(_languageService.Get("Hotkey.SkipBackward"), _editableSettings.SkipDurationSeconds); break;
                    case HotKeyAction.SkipForward5s: binding.DisplayName = string.Format(_languageService.Get("Hotkey.SkipForward"), _editableSettings.SkipDurationSeconds); break;
                    case HotKeyAction.VolumeUp: binding.DisplayName = _languageService.Get("Hotkey.VolumeUp"); break;
                    case HotKeyAction.VolumeDown: binding.DisplayName = _languageService.Get("Hotkey.VolumeDown"); break;
                    case HotKeyAction.Mute: binding.DisplayName = _languageService.Get("Hotkey.Mute"); break;
                    case HotKeyAction.StepBackward01s: binding.DisplayName = string.Format(_languageService.Get("Hotkey.StepBackward"), _editableSettings.StepDurationSeconds.ToString("0.##")); break;
                    case HotKeyAction.StepForward01s: binding.DisplayName = string.Format(_languageService.Get("Hotkey.StepForward"), _editableSettings.StepDurationSeconds.ToString("0.##")); break;
                    case HotKeyAction.GoToStart: binding.DisplayName = _languageService.Get("Hotkey.GoToStart"); break;
                    case HotKeyAction.GoToEnd: binding.DisplayName = _languageService.Get("Hotkey.GoToEnd"); break;
                    case HotKeyAction.SpeedDecrease: binding.DisplayName = string.Format(_languageService.Get("Hotkey.SpeedDecrease"), _editableSettings.SpeedChangePercent); break;
                    case HotKeyAction.SpeedIncrease: binding.DisplayName = string.Format(_languageService.Get("Hotkey.SpeedIncrease"), _editableSettings.SpeedChangePercent); break;
                    case HotKeyAction.SpeedReset: binding.DisplayName = string.Format(_languageService.Get("Hotkey.SpeedReset"), _editableSettings.SpeedResetPercent); break;
                    case HotKeyAction.ToggleLoopMode: binding.DisplayName = _languageService.Get("Hotkey.ToggleLoop"); break;
                    case HotKeyAction.ToggleWaveform: binding.DisplayName = _languageService.Get("Hotkey.ToggleWaveform"); break;
                    case HotKeyAction.WaveformZoomIn: binding.DisplayName = _languageService.Get("Hotkey.WaveformZoomIn"); break;
                    case HotKeyAction.WaveformZoomOut: binding.DisplayName = _languageService.Get("Hotkey.WaveformZoomOut"); break;
                }
            }
            HotKeyItemsControl?.Items.Refresh();
        }

        private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_capturingBinding == null)
                return;

            if (e.Key == System.Windows.Input.Key.Escape)
            {
                _capturingBinding = null;
                HotKeyItemsControl.Items.Refresh();
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.LeftCtrl ||
                e.Key == System.Windows.Input.Key.RightCtrl || e.Key == System.Windows.Input.Key.LeftAlt ||
                e.Key == System.Windows.Input.Key.RightAlt || e.Key == System.Windows.Input.Key.LeftShift ||
                e.Key == System.Windows.Input.Key.RightShift)
                return;

            _capturingBinding.Key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            _capturingBinding.Modifiers = System.Windows.Input.Keyboard.Modifiers;
            _capturingBinding.InputType = HotKeyInputType.Key;
            _capturingBinding.DisplayName = _capturingBinding.DisplayName ?? _capturingBinding.Action.ToString();
            _capturingBinding = null;
            HotKeyItemsControl.Items.Refresh();
            e.Handled = true;
        }

        private void SettingsWindow_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_capturingBinding == null)
                return;

            _capturingBinding.InputType = e.Delta > 0
                ? HotKeyInputType.MouseWheelUp
                : HotKeyInputType.MouseWheelDown;
            _capturingBinding.Key = System.Windows.Input.Key.None;
            _capturingBinding.Modifiers = System.Windows.Input.Keyboard.Modifiers;
            _capturingBinding = null;
            HotKeyItemsControl.Items.Refresh();
            e.Handled = true;
        }

        private void ResetWaveformDetailButton_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new WaveformZoomSettings();
            ShowWaveformCheck.IsChecked = true;
            ProgressiveWaveformCheck.IsChecked = defaults.ProgressiveWaveform;
            SaveWaveformCacheCheck.IsChecked = defaults.SaveWaveformCache;
            DisplayCacheLimitValueText.Text = defaults.WaveformCacheLimitValue.ToString("0.##");
            DisplayCacheLimitUnitComboBox.SelectedIndex = Math.Clamp((int)defaults.WaveformCacheLimitUnit, 0, 2);
            CursorModeComboBox.SelectedIndex = 0;
            FreeViewCheck.IsChecked = defaults.FreeView;
            MinZoomLevelText.Text = defaults.MinZoomLevel.ToString("0.##");
            ZoomFactorText.Text = defaults.ZoomFactor.ToString("0.##");
            WaveformScrollStepText.Text = defaults.ScrollStepSize.ToString("0.##");
            HorizontalDetailText.Text = defaults.HorizontalDetail.ToString();
            VerticalDetailText.Text = defaults.VerticalDetail.ToString();
        }

        private void ResetMinimapDetailButton_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new WaveformZoomSettings();
            ShowMinimapCheck.IsChecked = defaults.ShowMinimap;
            MinimapHeightText.Text = defaults.MinimapHeight.ToString();
        }

        private void ApplyLanguage()
        {
            Title = _languageService.Get("Settings.Title");
            GeneralCategoryItem.Content = _languageService.Get("Settings.General");
            DisplayCategoryItem.Content = _languageService.Get("Settings.Display");
            HotkeysCategoryItem.Content = _languageService.Get("Settings.Hotkeys");
            NumericCategoryItem.Content = _languageService.Get("Settings.Numeric");
            GeneralTitleText.Text = _languageService.Get("Settings.GeneralTitle");
            GeneralHintText.Text = _languageService.Get("Settings.GeneralHint");
            LanguageLabelText.Text = _languageService.Get("Settings.Language");
            ThemeLabelText.Text = _languageService.Get("Settings.Theme");
            RememberLastVolumeCheck.Content = _languageService.Get("Settings.RememberLastVolume");
            RememberLastSpeedCheck.Content = _languageService.Get("Settings.RememberLastSpeed");
            RememberLastLoopCheck.Content = _languageService.Get("Settings.RememberLastLoop");
            RememberWaveformZoomCheck.Content = _languageService.Get("Settings.RememberWaveformZoom");
            AutoPlayOnLoadCheck.Content = _languageService.Get("Settings.AutoPlayOnLoad");
            GlassBackgroundCheck.Content = _languageService.Get("Settings.GlassBackground");
            DefaultWaveformZoomLabelText.Text = _languageService.Get("Settings.DefaultWaveformZoom");
            LightThemeItem.Content = _languageService.Get("Theme.Light");
            DarkThemeItem.Content = _languageService.Get("Theme.Dark");
            AshThemeItem.Content = _languageService.Get("Theme.Ash");
            SystemThemeItem.Content = _languageService.Get("Theme.System");
            MaxVolumeLabelText.Text = _languageService.Get("Settings.MaxVolume");
            DefaultVolumeLabelText.Text = _languageService.Get("Settings.DefaultVolume");
            DisplayTitleText.Text = _languageService.Get("Settings.DisplayTitle");
            DisplayHintText.Text = _languageService.Get("Settings.DisplayHint");
            ShowWaveformLabelText.Text = _languageService.Get("Settings.ShowWaveform");
            FreeViewLabelText.Text = _languageService.Get("Settings.FreeView");
            ProgressiveWaveformLabelText.Text = _languageService.Get("Settings.ProgressiveWaveform");
            SaveWaveformCacheLabelText.Text = _languageService.Get("Settings.SaveWaveformCache");
            ClearWaveformCacheLabelText.Text = _languageService.Get("Settings.ClearWaveformCache");
            CacheLimitLabelText.Text = _languageService.Get("Settings.WaveformCacheLimit");
            NumericCacheLimitLabelText.Text = _languageService.Get("Settings.WaveformCacheLimit");
            ClearWaveformCacheButton.Content = _languageService.Get("Common.Clear");
            ShowMinimapLabelText.Text = _languageService.Get("Settings.ShowMinimap");
            ShowSpectrumLabelText.Text = _languageService.Get("Settings.ShowSpectrum");
            WaveformDetailTitleText.Text = _languageService.Get("Settings.WaveformDetail");
            MinimapDetailTitleText.Text = _languageService.Get("Settings.MinimapDetail");
            CursorModeLabelText.Text = _languageService.Get("Settings.CursorMode");
            CenterFixedCursorItem.Content = _languageService.Get("Settings.CursorMode.CenterFixed");
            LeftScrollCursorItem.Content = _languageService.Get("Settings.CursorMode.LeftScroll");
            MinZoomLevelLabelText.Text = _languageService.Get("Settings.MinZoomLevel");
            ZoomFactorLabelText.Text = _languageService.Get("Settings.ZoomFactor");
            WaveformScrollStepLabelText.Text = _languageService.Get("Settings.WaveformScrollStep");
            MinimapHeightLabelText.Text = _languageService.Get("Settings.MinimapHeight");
            HorizontalDetailLabelText.Text = _languageService.Get("Settings.HorizontalDetail");
            VerticalDetailLabelText.Text = _languageService.Get("Settings.VerticalDetail");
            HotkeysTitleText.Text = _languageService.Get("Settings.HotkeysTitle");
            HotkeysHintText.Text = _languageService.Get("Settings.HotkeysHint");
            SkipDurationLabelText.Text = _languageService.Get("Settings.SkipDuration");
            SmallSkipDurationLabelText.Text = _languageService.Get("Settings.StepDuration");
            ScrollVolumeLabelText.Text = _languageService.Get("Settings.ScrollVolume");
            HotkeyVolumeLabelText.Text = _languageService.Get("Settings.VolumeStep");
            SpeedStepLabelText.Text = _languageService.Get("Settings.SpeedStep");
            SpeedResetLabelText.Text = _languageService.Get("Settings.SpeedReset");
            MaxRecentFilesLabelText.Text = _languageService.Get("Settings.MaxRecentFiles");
            TrackTransitionDelayLabelText.Text = _languageService.Get("Settings.TrackTransitionDelay");
            NumericTitleText.Text = _languageService.Get("Settings.NumericTitle");
            NumericHintText.Text = _languageService.Get("Settings.NumericHint");
            CancelButton.Content = _languageService.Get("Common.Cancel");
            ApplyButton.Content = _languageService.Get("Common.Apply");
            OkButton.Content = _languageService.Get("Common.OK");
            ResetWaveformDetailButton.Content = _languageService.Get("Common.Reset");
            ResetMinimapDetailButton.Content = _languageService.Get("Common.Reset");
            UpdateHotKeyDisplayNames();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // Discard changes
            DialogResult = false;
            Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyCurrentSettings();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyCurrentSettings())
            {
                DialogResult = true;
                Close();
            }
        }

        private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var item = CategoryList.SelectedItem as System.Windows.Controls.ListBoxItem;
            if (item == null) return;
            var tag = item.Tag as string ?? "";
            GeneralPanel.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            HotkeysPanel.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            NumericPanel.Visibility = tag == "Numeric" ? Visibility.Visible : Visibility.Collapsed;
            DisplayPanel.Visibility = tag == "Display" ? Visibility.Visible : Visibility.Collapsed;
        }

        private sealed class PendingSettingAdorner : Adorner
        {
            private static readonly Typeface MarkerTypeface = new("Segoe UI");

            public PendingSettingAdorner(UIElement adornedElement) : base(adornedElement)
            {
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                var marker = new FormattedText("*", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, MarkerTypeface, 14,
                    Application.Current?.TryFindResource("PendingIndicatorBrush") as Brush ?? Brushes.Transparent,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                drawingContext.DrawText(marker, new Point(-7, -10));
            }
        }
    }
}
