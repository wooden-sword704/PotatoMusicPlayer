using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using PotatoMusicPlayer.Models;
using PotatoMusicPlayer.Services;
using PotatoMusicPlayer.ViewModels;
using PotatoMusicPlayer.Views;

namespace PotatoMusicPlayer
{
    public partial class MainWindow : Window
    {
        private enum MinimapDragMode
        {
            None,
            MoveRange,
            PointToRange,
            ResizeStart,
            ResizeEnd,
            ZoomAroundCenter
        }

        private readonly MainViewModel _viewModel;
        private readonly LanguageService _languageService;
        private bool _isSidebarOpen;
        private bool _isSidebarAnimating;
        private bool _isDraggingSeekBar = false;
        private bool _wasPlayingBeforeSeekBarDrag;
        private bool _isDraggingWaveform = false;
        private bool _isPotentialWaveformPan = false;
        private bool _isPanningWaveform = false;
        private bool _isCenterWaveformPanMode;
        private bool _keepWaveformRangeDuringDrag;
        private bool _resumePlaybackAfterWaveformDrag;
        private bool _wasPlayingBeforeWaveformDrag;
        private bool _resumePlaybackAfterMinimapDrag;
        private bool _wasPlayingBeforeMinimapDrag;
        private bool _isUpdatingVolumeFromCode = false;
        private bool _isDraggingVolume = false;
        private double _volumeDragStartX;
        private double _volumeDragStartValue;
        private bool _volumeDragMoved;
        private bool _playPauseIsPlaying;
        private bool _playPauseIconInitialized;
        private readonly SpectrumService _spectrumService = new();
        private DispatcherTimer _spectrumTimer;
        private readonly List<Rectangle> _spectrumBars = new();
        private CancellationTokenSource _spectrumLoadCts;
        private float[] _waveformData = Array.Empty<float>();
        private readonly List<Rectangle> _waveformBars = new();
        private Line _playbackCursorLine;
        private Line _centerGuideLine;
        private double _waveformPointerStartX;
        private double _waveformPanInitialRangeStart;
        private double _waveformPanInitialRangeDuration;
        private double _playbackAnchorPosition;
        private long _playbackAnchorTimestamp;
        private float _playbackAnchorSpeed = 1.0f;
        private bool _isPlaybackRenderingAttached;
        private double _drawnWaveformRangeStart;
        private double _drawnWaveformRangeDuration;
        private bool _hasDrawnWaveformRange;
        private bool _showWaveformRangeAsPercentage;
        private MinimapDragMode _minimapDragMode;
        private double _minimapDragStartX;
        private double _minimapDragStartTime;
        private double _minimapInitialRangeStart;
        private double _minimapInitialRangeEnd;
        private double _minimapInitialPosition;
        private double _minimapPreviousExcess;
        private bool _minimapDragStarted;
        private Line _minimapCursorLine;
        private Rectangle _minimapRangeOverlay;
        private Rectangle _minimapLeftHandle;
        private Rectangle _minimapRightHandle;
        private bool _isZoomingFromRightHandle;
        private float[] _minimapWaveformCache;
        private double _minimapCachedWidth;
        private double _minimapCachedHeight;

        public MainWindow()
        {
            InitializeComponent();

            ThemeService.ThemeChanged += ThemeService_ThemeChanged;

            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            _languageService = new LanguageService(_viewModel.Settings.Language);
            ApplyLanguage();

            // ViewModel のプロパティ変更を UI に反映
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;

            // ウィンドウ設定を復元
            RestoreWindowSettings();
            UpdateVolumeIcon(VolumeSlider.Value);
            UpdatePlayPauseIcon(false, animate: false);

            // スペクトラム描画タイマー(約16fps)。再生・停止いずれも減衰表示する。
            _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _spectrumTimer.Tick += (_, _) => DrawSpectrum();
            _spectrumTimer.Start();

            // ホットキー処理
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            // Recent files メニューを初期化
            UpdateRecentFilesMenu();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PlayEntranceAnimation();
            string startupFile = App.StartupFilePath;
            if (string.IsNullOrEmpty(startupFile) || !FileService.FileExists(startupFile))
                return;

            await _viewModel.LoadAndPlayFileAsync(startupFile);
            UpdateRecentFilesMenu();
        }

        private void ThemeService_ThemeChanged(object sender, EventArgs e)
        {
            UpdateVolumeIcon(_viewModel?.PlaybackState?.IsMuted == true ? 0 : VolumeSlider?.Value ?? 0);
            _minimapWaveformCache = null;
            DrawWaveform();
            DrawMinimap();
            DrawSpectrum();
        }

        // ========== ウィンドウ設定の復元・保存 ==========

        private void RestoreWindowSettings()
        {
            var settings = _viewModel.Settings;
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
            Topmost = settings.IsAlwaysOnTop;
            WaveformContainer.Visibility = settings.ShowWaveform ? Visibility.Visible : Visibility.Collapsed;
            ApplyWaveformSettingsToUi();
            VolumeSlider.Maximum = Math.Max(100, settings.MaxVolumeMultiplier * 100.0);

            if (settings.IsWindowSizeFixed)
            {
                ResizeMode = ResizeMode.NoResize;
                SidebarFixWindowSizeCheck.IsChecked = true;
            }

            VolumeSlider.Value = Math.Clamp(settings.DefaultVolume * 100, VolumeSlider.Minimum, VolumeSlider.Maximum);
            UpdateThemeMenuSelection(settings.Theme);
            RestoreSidebarState(settings.IsMenuBarCollapsed);
        }

        // ========== スペクトラム解析(実データ) ==========

        private void BeginSpectrumAnalysis()
        {
            _spectrumLoadCts?.Cancel();
            _spectrumLoadCts?.Dispose();
            _spectrumLoadCts = null;

            string path = _viewModel?.CurrentMediaFile?.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                DrawSpectrum();
                return;
            }

            if (!System.IO.File.Exists(path))
            {
                Debug.WriteLine($"[MainWindow] BeginSpectrumAnalysis: File not found: {path}");
                DrawSpectrum();
                return;
            }

            var cts = new CancellationTokenSource();
            _spectrumLoadCts = cts;
            _ = _spectrumService.LoadFileAsync(path, cts.Token);
        }

        // ========== サイドバー(Web風ドロワー) ==========

        private void RestoreSidebarState(bool collapsed)
        {
            // Web風ドロワーは起動時は常に閉じる。設定値は次回トグル時の初期値として保持のみ。
            _isSidebarOpen = false;
            _isSidebarAnimating = false;
            if (SidebarPanel == null || SidebarDimOverlay == null || MainContentGrid == null)
                return;

            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarDimOverlay.Visibility = Visibility.Collapsed;
            SidebarDimOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            SidebarDimOverlay.Opacity = 0;
            if (SidebarSlide != null)
            {
                SidebarSlide.BeginAnimation(TranslateTransform.XProperty, null);
                SidebarSlide.X = -GetSidebarWidth();
            }
            MainContentGrid.Effect = null;
            SyncSidebarChecks();
            UpdateMenuToggleTooltip();
        }

        private void MenuToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSidebarOpen)
                CloseSidebar();
            else
                OpenSidebar();
        }

        private void OpenSidebar()
        {
            if (_isSidebarAnimating || _isSidebarOpen || SidebarPanel == null || SidebarSlide == null)
                return;

            _isSidebarAnimating = true;
            _isSidebarOpen = true;
            SyncSidebarChecks();
            Duration duration = TryFindResource("MaterialDurationNormal") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(180));
            IEasingFunction ease = TryFindResource("MaterialEaseInOut") as IEasingFunction;
            double panelWidth = GetSidebarWidth();

            SidebarPanel.Visibility = Visibility.Visible;
            SidebarDimOverlay.Visibility = Visibility.Visible;
            // 開始値を明示してからアニメーションさせる(画面外に残る不具合の防止)。
            SidebarSlide.X = -panelWidth;
            SidebarDimOverlay.Opacity = 0;

            // 背景に軽いぼかしをかける。
            MainContentGrid.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };

            var slideIn = new DoubleAnimation(-panelWidth, 0, duration) { EasingFunction = ease };
            slideIn.Completed += (_, _) =>
            {
                SidebarSlide.BeginAnimation(TranslateTransform.XProperty, null);
                SidebarSlide.X = 0;
                _isSidebarAnimating = false;
            };

            var fadeIn = new DoubleAnimation(0, 1, duration);
            fadeIn.Completed += (_, _) =>
            {
                SidebarDimOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                SidebarDimOverlay.Opacity = 1;
            };

            PersistSidebarState();
            UpdateMenuToggleTooltip();
            SidebarSlide.BeginAnimation(TranslateTransform.XProperty, slideIn);
            SidebarDimOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void CloseSidebar()
        {
            if (_isSidebarAnimating || !_isSidebarOpen || SidebarPanel == null || SidebarSlide == null)
                return;

            _isSidebarAnimating = true;
            Duration duration = TryFindResource("MaterialDurationNormal") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(180));
            IEasingFunction ease = TryFindResource("MaterialEaseInOut") as IEasingFunction;
            double panelWidth = GetSidebarWidth();

            var slideOut = new DoubleAnimation(SidebarSlide.X, -panelWidth, duration) { EasingFunction = ease };
            slideOut.Completed += (_, _) =>
            {
                SidebarSlide.BeginAnimation(TranslateTransform.XProperty, null);
                SidebarDimOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarDimOverlay.Visibility = Visibility.Collapsed;
                SidebarDimOverlay.Opacity = 0;
                SidebarSlide.X = -panelWidth;
                MainContentGrid.Effect = null;
                _isSidebarAnimating = false;
            };

            var fadeOut = new DoubleAnimation(SidebarDimOverlay.Opacity, 0, duration);

            _isSidebarOpen = false;
            PersistSidebarState();
            UpdateMenuToggleTooltip();
            SidebarSlide.BeginAnimation(TranslateTransform.XProperty, slideOut);
            SidebarDimOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private double GetSidebarWidth()
        {
            double panelWidth = SidebarPanel?.Width ?? 0;
            if (double.IsNaN(panelWidth) || panelWidth <= 0)
                panelWidth = SidebarPanel?.ActualWidth ?? 0;
            if (panelWidth <= 0)
                panelWidth = 280;
            return panelWidth;
        }

        private void PersistSidebarState()
        {
            // IsMenuBarCollapsed を「サイドバーが閉じているか」として再利用し、既存設定との互換を保つ。
            _viewModel.Settings.IsMenuBarCollapsed = !_isSidebarOpen;
            new SettingsService().SaveSettings(_viewModel.Settings);
        }

        private void SyncSidebarChecks()
        {
            if (_viewModel?.Settings == null || SidebarAlwaysOnTopCheck == null)
                return;

            var settings = _viewModel.Settings;
            SidebarAlwaysOnTopCheck.IsChecked = Topmost;
            SidebarFixWindowSizeCheck.IsChecked = ResizeMode == ResizeMode.NoResize;
            SidebarShowWaveformCheck.IsChecked = settings.ShowWaveform;
            SidebarShowSpectrumCheck.IsChecked = settings.ShowSpectrum;
            SidebarCenterFixedCheck.IsChecked = settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed;
            UpdateThemeMenuSelection(settings.Theme);
        }

        private void SidebarDimOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CloseSidebar();
            e.Handled = true;
        }

        private void SidebarCloseButton_Click(object sender, RoutedEventArgs e) => CloseSidebar();

        private void CloseSidebarAfterAction()
        {
            if (_isSidebarOpen)
                CloseSidebar();
        }

        private void UpdateMenuToggleTooltip()
        {
            if (MenuToggleButton == null || _languageService == null)
                return;

            MenuToggleButton.ToolTip = _languageService.Get(
                _isSidebarOpen ? "Main.MenuCollapse" : "Main.MenuExpand");
        }

        private void PlayEntranceAnimation()
        {
            // 起動時のワンショットフェード。Motion トークンで全体と歩調を合わせる。
            Duration duration = TryFindResource("MaterialDurationNormal") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(180));
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SetPlaybackRendering(false);
            ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
            _spectrumTimer?.Stop();
            _spectrumLoadCts?.Cancel();
            _spectrumLoadCts?.Dispose();
            _spectrumService?.Dispose();
            // ウィンドウの状態を保存
            var settings = _viewModel.Settings;
            if (settings.RememberLastVolume)
                settings.DefaultVolume = (float)Math.Clamp(VolumeSlider.Value / 100.0, 0.0, settings.MaxVolumeMultiplier);
            if (settings.RememberLastPlaybackSpeed && _viewModel.PlaybackState != null)
                settings.DefaultPlaybackSpeed = Math.Clamp(_viewModel.PlaybackState.PlaybackSpeed, 0.25f, 4.0f);
            if (settings.RememberLastLoopMode && _viewModel.PlaybackState != null)
                settings.DefaultLoopMode = _viewModel.PlaybackState.LoopMode;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.IsAlwaysOnTop = Topmost;

            var settingsService = new SettingsService();
            settingsService.SaveSettings(settings);

            _viewModel.Dispose();
        }

        // ========== ViewModel との連携 ==========

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(MainViewModel.CurrentMediaFile))
                {
                    UpdateMediaInfoDisplay();
                    BeginSpectrumAnalysis();
                }
                else if (e.PropertyName == nameof(MainViewModel.PlaybackState))
                {
                    UpdatePlaybackDisplay();
                }
                else if (e.PropertyName == nameof(MainViewModel.CurrentWaveformData))
                {
                    _waveformData = _viewModel.CurrentWaveformData;
                    DrawWaveform();
                    DrawMinimap();
                }
                else if (e.PropertyName == nameof(MainViewModel.ZoomState))
                {
                    bool isAutomaticCenterFollow = _viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed &&
                        _viewModel.PlaybackState?.State == PlayState.Playing &&
                        !_isDraggingWaveform && _minimapDragMode == MinimapDragMode.None;
                    if (!isAutomaticCenterFollow)
                    {
                        if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                        {
                            RefreshWaveformRange();
                            DrawPlaybackCursorAtCenter();
                        }
                        else
                            DrawWaveform();
                    }
                    DrawMinimap();
                    UpdateWaveformRangeDisplay();
                }
                else if (e.PropertyName == nameof(MainViewModel.WaveformProgress))
                {
                    UpdateWaveformProgress();
                }
                else if (e.PropertyName == nameof(MainViewModel.Settings))
                {
                    _languageService.Load(_viewModel.Settings.Language);
                    ApplyLanguage();
                    ApplyAudioSettingsToUi();
                    UpdateThemeMenuSelection(_viewModel.Settings.Theme);
                    ApplyWaveformSettingsToUi();
                }
            });
        }

        private void UpdateMediaInfoDisplay()
        {
            var media = _viewModel.CurrentMediaFile;
            if (media == null)
            {
                TitleText.Text = "再生するファイルがありません";
                ArtistText.Text = "";
                Title = _languageService.Get("Main.Title");
                return;
            }

            TitleText.Text = string.IsNullOrEmpty(media.Title) ? media.FileName : media.Title;
            ArtistText.Text = !string.IsNullOrEmpty(media.Artist)
                ? $"{media.Artist}  |  {media.Bitrate}kbps  |  {media.SampleRate}Hz"
                : $"{media.Bitrate}kbps | {media.SampleRate}Hz";

            Title = $"{TitleText.Text} - {_languageService.Get("Main.Title")}";
            TotalTimeText.Text = FormatTime(media.Duration);
            SeekBar.Maximum = media.Duration.TotalSeconds;
        }

        private void UpdatePlaybackDisplay()
        {
            var state = _viewModel.PlaybackState;
            if (state == null) return;

            bool resetPlaybackAnchor = state.State != PlayState.Playing || !_isPlaybackRenderingAttached;
            if (!resetPlaybackAnchor)
            {
                double predictedPosition = _playbackAnchorPosition +
                    Stopwatch.GetElapsedTime(_playbackAnchorTimestamp).TotalSeconds * _playbackAnchorSpeed;
                double correction = state.CurrentPosition.TotalSeconds - predictedPosition;
                // LibVLC の定期通知は描画時刻より遅れて届くことがある。通常の遅れで
                // 基準を巻き戻すと、200msごとにカーソルと波形が逆方向へ跳ねてしまう。
                // 明確なシーク・実測の進みだけを再同期対象にする。
                resetPlaybackAnchor = correction > 0.5 || correction < -0.75 ||
                    Math.Abs(state.PlaybackSpeed - _playbackAnchorSpeed) > 0.001f;
            }
            if (resetPlaybackAnchor)
            {
                _playbackAnchorPosition = state.CurrentPosition.TotalSeconds;
                _playbackAnchorTimestamp = Stopwatch.GetTimestamp();
                _playbackAnchorSpeed = state.PlaybackSpeed;
            }
            SetPlaybackRendering(state.State == PlayState.Playing);
            double displayedPosition = _viewModel.PendingWaveformSeekPosition ?? (state.State == PlayState.Playing
                ? GetInterpolatedPlaybackPosition(state)
                : state.CurrentPosition.TotalSeconds);

            UpdatePlayPauseIcon(state.State == PlayState.Playing);
            TaskbarPlayPauseButton.Description = state.State == PlayState.Playing ? "Pause" : "Play";
            TaskbarPlayPauseButton.ImageSource = state.State == PlayState.Playing
                ? (System.Windows.Media.ImageSource)FindResource("TaskbarPauseIcon")
                : (System.Windows.Media.ImageSource)FindResource("TaskbarPlayIcon");
            CurrentTimeText.Text = FormatTime(state.CurrentPosition);
            SpeedText.Text = $"{state.PlaybackSpeed:0.00}x";
            LoopButton.Content = $"{_languageService.Get("Loop.Label")}: {GetLoopModeDisplayString(state.LoopMode)}";

            // Duration はメディア読み込み直後は 0 のことがあるため、
            // 再生中は毎tickで Maximum を実際の長さに追従させる（シークバー右端張り付き対策）
            if (state.Duration.TotalSeconds > 0)
            {
                if (SeekBar.Maximum != state.Duration.TotalSeconds)
                    SeekBar.Maximum = state.Duration.TotalSeconds;

                TotalTimeText.Text = FormatTime(state.Duration);
            }

            if (!_isDraggingSeekBar && !_isDraggingWaveform &&
                !(state.State == PlayState.Playing && _isPlaybackRenderingAttached))
            {
                SeekBar.Value = state.CurrentPosition.TotalSeconds;
            }

            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
            {
                DrawPlaybackCursorAtCenter(displayedPosition);
            }
            else if (!_isDraggingWaveform)
            {
                DrawPlaybackCursor(TimeSpan.FromSeconds(_minimapCarriedPosition ?? displayedPosition));
            }
            UpdateMinimapCursor(displayedPosition);

            // 音量バーを実際の音量に追従させる（ホットキー操作時も反映）。
            // 音量ドラッグ中はマウス操作を優先し、プログラム側で上書きしない。
            _isUpdatingVolumeFromCode = true;
            if (!state.IsMuted && !_isDraggingVolume)
                VolumeSlider.Value = state.VolumePercent;
            VolumeText.Text = $"{(int)VolumeSlider.Value}%";
            _isUpdatingVolumeFromCode = false;
            UpdateVolumeIcon(state.IsMuted ? 0 : VolumeSlider.Value);
        }

        private void SetPlaybackRendering(bool enabled)
        {
            if (_isPlaybackRenderingAttached == enabled)
                return;

            if (enabled)
                CompositionTarget.Rendering += PlaybackRendering;
            else
                CompositionTarget.Rendering -= PlaybackRendering;

            _isPlaybackRenderingAttached = enabled;
        }

        private void PlaybackRendering(object sender, EventArgs e)
        {
            var playbackState = _viewModel?.PlaybackState;
            var zoomState = _viewModel?.ZoomState;
            if (playbackState?.State != PlayState.Playing || zoomState == null || zoomState.VisibleRangeDuration <= 0)
            {
                SetPlaybackRendering(false);
                return;
            }

            double position = _viewModel.PendingWaveformSeekPosition ??
                GetInterpolatedPlaybackPosition(playbackState);

            if (!_isDraggingSeekBar && !_isDraggingWaveform)
                SeekBar.Value = position;

            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
            {
                // 波形・ミニマップのドラッグ中は描画で競合しないよう、追従の上書きを休止する。
                if (!_isDraggingWaveform && _minimapDragMode == MinimapDragMode.None)
                {
                    _viewModel.FollowWaveformPosition(position);
                    RefreshWaveformRange();
                    DrawPlaybackCursorAtCenter(position);
                }
                UpdateMinimapCursor(position);
                return;
            }

            _viewModel.FollowWaveformPosition(position);
            DrawPlaybackCursor(TimeSpan.FromSeconds(_minimapCarriedPosition ?? position));
            UpdateMinimapCursor(position);
        }

        private double GetInterpolatedPlaybackPosition(PlaybackState state)
        {
            double elapsed = Stopwatch.GetElapsedTime(_playbackAnchorTimestamp).TotalSeconds;
            double position = _playbackAnchorPosition + elapsed * _playbackAnchorSpeed;
            return Math.Clamp(position, 0, Math.Max(0, state.Duration.TotalSeconds));
        }

        // ========== 再生・一時停止アイコンの図形トランジション ==========

        private void UpdatePlayPauseIcon(bool isPlaying, bool animate = true)
        {
            if (PlayIconPath == null || PauseIconPath == null)
                return;

            // 状態通知は約100ms間隔で届くため、変化時のみアニメーションさせる。
            if (_playPauseIconInitialized && _playPauseIsPlaying == isPlaying)
                return;

            _playPauseIsPlaying = isPlaying;
            _playPauseIconInitialized = true;
            Duration duration = TryFindResource("MaterialDurationFast") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(100));
            IEasingFunction ease = TryFindResource("MaterialEaseOut") as IEasingFunction;

            var showPath = isPlaying ? PauseIconPath : PlayIconPath;
            var hidePath = isPlaying ? PlayIconPath : PauseIconPath;
            var showScale = isPlaying ? PauseIconScale : PlayIconScale;
            var hideScale = isPlaying ? PlayIconScale : PauseIconScale;

            if (!animate)
            {
                hidePath.BeginAnimation(UIElement.OpacityProperty, null);
                showPath.BeginAnimation(UIElement.OpacityProperty, null);
                hideScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                hideScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                showScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                showScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                hidePath.Opacity = 0;
                showPath.Opacity = 1;
                if (hideScale != null)
                    hideScale.ScaleX = hideScale.ScaleY = 0.7;
                if (showScale != null)
                    showScale.ScaleX = showScale.ScaleY = 1;
                return;
            }

            hidePath.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(hidePath.Opacity, 0, duration));
            if (hideScale != null)
            {
                hideScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(hideScale.ScaleX, 0.7, duration) { EasingFunction = ease });
                hideScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(hideScale.ScaleY, 0.7, duration) { EasingFunction = ease });
            }

            showPath.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(showPath.Opacity, 1, duration));
            if (showScale != null)
            {
                showScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });
                showScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });
            }
        }

        private string FormatTime(TimeSpan ts)
        {
            return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }

        private void ApplyLanguage()
        {
            Title = _languageService.Get("Main.Title");
            SidebarMenuHeaderText.Text = _languageService.Get("Main.MenuTitle");
            SidebarFileExpander.Header = _languageService.Get("Main.File");
            SidebarPlaybackExpander.Header = _languageService.Get("Main.Playback");
            SidebarViewExpander.Header = _languageService.Get("Main.View");
            SidebarOtherExpander.Header = _languageService.Get("Main.Other");
            VolumeIcon.ToolTip = _languageService.Get("Main.VolumeTooltip");
            UpdateMenuToggleTooltip();
            if (_viewModel.CurrentMediaFile == null)
                TitleText.Text = _languageService.Get("Main.NoFile");

            // ファイルセクション
            SidebarOpenButton.Content = _languageService.Get("Menu.File.Open");
            SidebarOpenLocationButton.Content = _languageService.Get("Menu.File.OpenLocation");
            SidebarOpenTerminalButton.Content = _languageService.Get("Menu.File.OpenTerminal");
            SidebarRecentHeader.Text = _languageService.Get("Menu.File.RecentFiles");
            SidebarClearRecentButton.Content = _languageService.Get("Menu.File.ClearRecent");
            SidebarExitButton.Content = _languageService.Get("Menu.File.Exit");
            SidebarSettingsButton.Content = _languageService.Get("Menu.Edit.Settings");

            // 再生セクション
            SidebarPlayPauseButton.Content = _languageService.Get("Menu.Playback.PlayPause");
            SidebarStopButton.Content = _languageService.Get("Menu.Playback.Stop");
            SidebarGoToStartButton.Content = _languageService.Get("Menu.Playback.GoToStart");
            SidebarSeekToTimeButton.Content = _languageService.Get("Menu.Playback.SeekToTime");
            SidebarSpeedResetButton.Content = _languageService.Get("Menu.Playback.SpeedReset");
            SidebarSpeedDecreaseButton.Content = _languageService.Get("Menu.Playback.SpeedDecrease");
            SidebarSpeedIncreaseButton.Content = _languageService.Get("Menu.Playback.SpeedIncrease");
            SidebarSkipForwardButton.Content = _languageService.Get("Menu.Playback.SkipForward");
            SidebarSkipBackwardButton.Content = _languageService.Get("Menu.Playback.SkipBackward");

            // 表示セクション
            SidebarAlwaysOnTopCheck.Content = _languageService.Get("Menu.View.AlwaysOnTop");
            SidebarFixWindowSizeCheck.Content = _languageService.Get("Menu.View.FixWindowSize");
            SidebarShowWaveformCheck.Content = _languageService.Get("Menu.View.ShowWaveform");
            SidebarShowSpectrumCheck.Content = _languageService.Get("Menu.View.ShowSpectrum");
            SidebarCenterFixedCheck.Content = _languageService.Get("Menu.View.CenterFixedWaveform");
            SidebarWaveformRangeButton.Content = _languageService.Get("Menu.View.WaveformRange");
            SidebarFullScreenButton.Content = _languageService.Get("Menu.View.FullScreen");
            SidebarThemeLabel.Text = _languageService.Get("Menu.View.Theme");
            SidebarThemeLightRadio.Content = _languageService.Get("Theme.Light");
            SidebarThemeDarkRadio.Content = _languageService.Get("Theme.Dark");
            SidebarThemeAshRadio.Content = _languageService.Get("Theme.Ash");
            SidebarThemeSystemRadio.Content = _languageService.Get("Theme.System");

            // その他セクション
            SidebarAboutButton.Content = _languageService.Get("Menu.Other.About");
            UpdateRecentFilesMenu();
            if (_viewModel?.PlaybackState != null)
                LoopButton.Content = $"{_languageService.Get("Loop.Label")}: {GetLoopModeDisplayString(_viewModel.PlaybackState.LoopMode)}";
            UpdateWaveformRangeDisplay();
        }

        private string GetLoopModeDisplayString(LoopMode mode)
        {
            string key = mode switch
            {
                LoopMode.One => "Loop.One",
                LoopMode.All => "Loop.All",
                _ => "Loop.Off"
            };
            return _languageService.Get(key);
        }

        // ========== タイトルバー(カスタム) ==========

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void TaskbarPrevious_Click(object sender, EventArgs e) => _viewModel.SetPosition(0);
        private void TaskbarPlayPause_Click(object sender, EventArgs e) => _viewModel.TogglePlayPause();
        private void TaskbarNext_Click(object sender, EventArgs e) => _viewModel.Stop();

        // ========== サイドバー: ファイル ==========

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            var dialog = new OpenFileDialog
            {
                Filter = FileService.GetFileDialogFilter(),
                Title = "音楽ファイルを開く"
            };

            if (dialog.ShowDialog() == true)
            {
                await _viewModel.LoadAndPlayFileAsync(dialog.FileName);
                UpdateRecentFilesMenu();
            }
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            if (_viewModel.CurrentMediaFile != null)
            {
                // FileService はファイルパスも受け取り選択表示するのでそのまま渡す
                FileService.OpenFolderInExplorer(_viewModel.CurrentMediaFile.FilePath);
            }
        }

        private void OpenInTerminal_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            if (_viewModel.CurrentMediaFile != null)
            {
                var folder = FileService.GetParentDirectory(_viewModel.CurrentMediaFile.FilePath);
                FileService.OpenFolderInTerminal(folder);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        // ========== サイドバー: 再生 ==========

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.TogglePlayPause();
        }
        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.Stop();
        }
        private void GoToStart_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.SetPosition(0);
        }
        private void SeekToTime_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            if (!InputPromptWindow.TryShow(this, _languageService.Get("Dialog.Seek.Title"), _languageService.Get("Dialog.Seek.Prompt"),
                FormatTime(_viewModel.PlaybackState.CurrentPosition), out string input,
                value => TryParsePosition(value, _viewModel.PlaybackState.Duration.TotalSeconds, out _), _languageService))
                return;

            if (TryParsePosition(input, _viewModel.PlaybackState.Duration.TotalSeconds, out double seconds))
                _viewModel.SetPosition(seconds);
            else
                MessageBox.Show(_languageService.Get("Dialog.Seek.Invalid"), _languageService.Get("Dialog.Seek.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        private void SpeedReset_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.ResetSpeed();
        }
        private void SpeedDecrease_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.DecreaseSpeed();
        }
        private void SpeedIncrease_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.IncreaseSpeed();
        }
        private void SkipForward_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.SkipForward();
        }
        private void SkipBackward_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.SkipBackward();
        }
        private void ToggleLoop_Click(object sender, RoutedEventArgs e) => _viewModel.CycleLoopMode();

        // ========== サイドバー: 編集 ==========

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            _viewModel.OpenSettings();
        }

        // ========== サイドバー: 表示 ==========

        private void SidebarAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            Topmost = SidebarAlwaysOnTopCheck.IsChecked == true;
        }

        private void SidebarFixWindowSize_Click(object sender, RoutedEventArgs e)
        {
            ResizeMode = SidebarFixWindowSizeCheck.IsChecked == true ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        }

        private void SidebarShowWaveform_Click(object sender, RoutedEventArgs e)
        {
            bool show = SidebarShowWaveformCheck.IsChecked == true;
            _viewModel.Settings.ShowWaveform = show;
            WaveformContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
                DrawWaveform();
        }

        private void SidebarShowSpectrum_Click(object sender, RoutedEventArgs e)
        {
            bool show = SidebarShowSpectrumCheck.IsChecked == true;
            _viewModel.Settings.ShowSpectrum = show;
            ApplySpectrumVisibility();
        }

        private void ApplySpectrumVisibility()
        {
            if (SpectrumContainer == null || SpectrumRow == null)
                return;

            bool show = _viewModel?.Settings?.ShowSpectrum == true;
            SpectrumContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SpectrumRow.Height = show ? new GridLength(60) : new GridLength(0);
            if (show)
                DrawSpectrum();
        }

        private void SidebarCenterFixed_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Settings.WaveformZoom.CursorMode = SidebarCenterFixedCheck.IsChecked == true
                ? CursorDisplayMode.CenterFixed
                : CursorDisplayMode.LeftScroll;
            DrawWaveform();
            DrawMinimap();
        }

        private void WaveformRange_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            var zoomState = _viewModel.ZoomState;
            if (zoomState == null || !InputPromptWindow.TryShow(this, _languageService.Get("Dialog.WaveformRange.Title"), _languageService.Get("Dialog.WaveformRange.Prompt"),
                zoomState.VisibleRangeDuration.ToString("0.##"), out string input,
                value => TryParsePosition(value, zoomState.TotalDuration, out double seconds) && seconds > 0, _languageService))
                return;

            if (!TryParsePosition(input, zoomState.TotalDuration, out double seconds) || seconds <= 0)
            {
                MessageBox.Show(_languageService.Get("Dialog.WaveformRange.Invalid"), _languageService.Get("Dialog.WaveformRange.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                _viewModel.ZoomWaveformPreservingRatio(zoomState.VisibleRangeStart,
                    zoomState.VisibleRangeEnd, _viewModel.PlaybackState.CurrentPosition.TotalSeconds, seconds);
            else
            {
                double center = zoomState.VisibleRangeStart + zoomState.VisibleRangeDuration / 2;
                _viewModel.SetWaveformRangeCentered(center, seconds);
            }
        }

        private void ApplyWaveformSettingsToUi()
        {
            if (_viewModel == null || MinimapContainer == null)
                return;

            var appSettings = _viewModel.Settings;
            var settings = appSettings.WaveformZoom;
            if (SidebarShowWaveformCheck != null)
                SidebarShowWaveformCheck.IsChecked = appSettings.ShowWaveform;
            if (SidebarShowSpectrumCheck != null)
                SidebarShowSpectrumCheck.IsChecked = appSettings.ShowSpectrum;
            if (SidebarCenterFixedCheck != null)
                SidebarCenterFixedCheck.IsChecked = settings.CursorMode == CursorDisplayMode.CenterFixed;
            WaveformContainer.Visibility = appSettings.ShowWaveform ? Visibility.Visible : Visibility.Collapsed;
            ApplySpectrumVisibility();
            MinimapContainer.Visibility = settings.ShowMinimap ? Visibility.Visible : Visibility.Collapsed;
            int height = Math.Clamp(settings.MinimapHeight, 8, 64);
            MinimapRow.Height = settings.ShowMinimap ? new GridLength(height + 2) : new GridLength(0);
            MinimapContainer.Height = height;
            DrawMinimap();
            UpdateWaveformRangeDisplay();
        }

        private void UpdateWaveformRangeDisplay()
        {
            var zoomState = _viewModel?.ZoomState;
            if (WaveformRangeText == null || zoomState == null)
                return;

            WaveformRangeText.Text = _showWaveformRangeAsPercentage
                ? $"{_languageService.Get("Waveform.RangeLabel")}: {zoomState.VisibleRangeDuration / zoomState.TotalDuration * 100:0.##}%"
                : $"{_languageService.Get("Waveform.RangeLabel")}: {zoomState.VisibleRangeDuration:0.##} {_languageService.Get("Waveform.SecondsUnit")}";
        }

        private void WaveformRangeText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _showWaveformRangeAsPercentage = !_showWaveformRangeAsPercentage;
            UpdateWaveformRangeDisplay();
            e.Handled = true;
        }

        private static bool TryParsePlaybackTime(string input, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string[] parts = input.Trim().Split(':');
            if (parts.Length > 3)
                return false;

            double multiplier = 1;
            for (int index = parts.Length - 1; index >= 0; index--)
            {
                if (!double.TryParse(parts[index], out double value) || value < 0 ||
                    (index > 0 && value >= 60))
                    return false;

                seconds += value * multiplier;
                multiplier *= 60;
            }

            return true;
        }

        private static bool TryParsePosition(string input, double totalDuration, out double seconds)
        {
            input = input?.Trim() ?? string.Empty;
            if (input.EndsWith("%") && double.TryParse(input[..^1].Trim(), out double percentage) &&
                percentage >= 0 && percentage <= 100)
            {
                seconds = totalDuration * percentage / 100;
                return true;
            }

            return TryParsePlaybackTime(input, out seconds) && seconds <= totalDuration;
        }

        private void SidebarTheme_Click(object sender, RoutedEventArgs e)
        {
            string tag = (sender as FrameworkElement)?.Tag?.ToString();
            if (!Enum.TryParse(tag, out ThemeMode mode))
                return;

            var settings = _viewModel.Settings;
            settings.Theme = mode;
            new SettingsService().SaveSettings(settings);
            ThemeService.Apply(mode);
            UpdateThemeMenuSelection(mode);
        }

        private void UpdateThemeMenuSelection(ThemeMode mode)
        {
            if (SidebarThemeLightRadio == null)
                return;

            SidebarThemeLightRadio.IsChecked = mode == ThemeMode.Light;
            SidebarThemeDarkRadio.IsChecked = mode == ThemeMode.Dark;
            SidebarThemeAshRadio.IsChecked = mode == ThemeMode.Ash;
            SidebarThemeSystemRadio.IsChecked = mode == ThemeMode.System;
        }

        private void ApplyAudioSettingsToUi()
        {
            if (VolumeSlider == null || _viewModel == null)
                return;

            double maximum = Math.Max(100, _viewModel.Settings.MaxVolumeMultiplier * 100.0);
            double previousValue = VolumeSlider.Value;
            _isUpdatingVolumeFromCode = true;
            VolumeSlider.Maximum = maximum;
            VolumeSlider.Value = Math.Min(previousValue, maximum);
            _isUpdatingVolumeFromCode = false;

            if (previousValue > maximum)
                _viewModel.SetVolume(maximum);
            UpdateVolumeIcon(VolumeSlider.Value);
        }

        private void FullScreen_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            if (WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        // ========== サイドバー: その他 ==========

        private void About_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            MessageBox.Show(
                $"{Utils.Constants.AppName}\nVersion {Utils.Constants.AppVersion}",
                "バージョン情報",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ========== 再生バー(シークバー) ==========

        private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _wasPlayingBeforeSeekBarDrag = _viewModel.PlaybackState?.State == PlayState.Playing;
            _isDraggingSeekBar = true;
            SeekBar.CaptureMouse();
            UpdateSeekBarValueFromMouse(e);
        }

        private void SeekBar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingSeekBar || e.LeftButton != MouseButtonState.Pressed)
                return;

            UpdateSeekBarValueFromMouse(e);
        }

        private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSeekBar = false;
            SeekBar.ReleaseMouseCapture();
            if (_wasPlayingBeforeSeekBarDrag)
                _viewModel.SeekAndPlay(SeekBar.Value);
            else
                _viewModel.SetPosition(SeekBar.Value);
            // 中央固定ではシーク位置へ範囲を中央合わせする。
            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                _viewModel.CenterWaveformRangeOnPosition(SeekBar.Value);
            _wasPlayingBeforeSeekBarDrag = false;
        }

        private void UpdateSeekBarValueFromMouse(MouseEventArgs e)
        {
            // マウス位置からシークバー値を計算して即時反映（UI スレッド）
            var pos = e.GetPosition(SeekBar);
            double relative = SeekBar.ActualWidth > 0 ? pos.X / SeekBar.ActualWidth : 0;
            relative = Math.Max(0.0, Math.Min(1.0, relative));
            double newVal = SeekBar.Minimum + relative * (SeekBar.Maximum - SeekBar.Minimum);
            SeekBar.Value = newVal;
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(newVal));
        }

        // ========== 音量スライダー ==========
        //
        // SHIFT+ドラッグはカーソルより遅く(0.25倍)、CTRL+ドラッグは5%刻みで動く。
        // 修飾キーなしは通常速度のドラッグ、クリックのみはその位置へジャンプする。

        private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVolume = true;
            _volumeDragMoved = false;
            _volumeDragStartX = e.GetPosition(VolumeSlider).X;
            _volumeDragStartValue = VolumeSlider.Value;
            VolumeSlider.CaptureMouse();
            e.Handled = true;
        }

        private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingVolume || e.LeftButton != MouseButtonState.Pressed || VolumeSlider.ActualWidth <= 0)
                return;

            double currentX = e.GetPosition(VolumeSlider).X;
            if (!_volumeDragMoved &&
                Math.Abs(currentX - _volumeDragStartX) < SystemParameters.MinimumHorizontalDragDistance)
                return;

            _volumeDragMoved = true;
            VolumeSlider.Value = CalculateVolumeDragValue(currentX);
            e.Handled = true;
        }

        private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingVolume)
                return;

            if (!_volumeDragMoved)
            {
                // クリックのみの場合はその位置へジャンプする(CTRL時は5%刻み)。
                VolumeSlider.Value = CalculateVolumeDragValue(e.GetPosition(VolumeSlider).X);
            }

            _isDraggingVolume = false;
            VolumeSlider.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _isDraggingVolume = false;
        }

        private double CalculateVolumeDragValue(double currentX)
        {
            double width = VolumeSlider.ActualWidth;
            if (width <= 0)
                return VolumeSlider.Value;

            double range = VolumeSlider.Maximum - VolumeSlider.Minimum;
            double factor = (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.None ? 0.25 : 1.0;
            double value = _volumeDragStartValue + (currentX - _volumeDragStartX) / width * range * factor;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.None)
                value = Math.Round(value / 5.0) * 5.0;
            return Math.Clamp(value, VolumeSlider.Minimum, VolumeSlider.Maximum);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText != null)
            {
                VolumeText.Text = $"{(int)e.NewValue}%";
            }
            UpdateVolumeIcon(e.NewValue);

            // 初期化中やプログラム的な更新時は再反映しない（無限ループ防止）
            if (_isUpdatingVolumeFromCode || _viewModel == null)
                return;

            _viewModel.SetVolume(e.NewValue);
        }

        private void VolumeIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _viewModel.ToggleMute();
            e.Handled = true;
        }

        // 音量領域(アイコン・スライダー・テキスト)上でのマウススクロールで音量を1%ずつ調整
        private void VolumeArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0
                ? _viewModel.Settings.ScrollVolumeChangePercent
                : -_viewModel.Settings.ScrollVolumeChangePercent;
            double newValue = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);

            if (Math.Abs(newValue - VolumeSlider.Value) > 0.001)
            {
                VolumeSlider.Value = newValue;
            }

            e.Handled = true;
        }


        private void UpdateVolumeIcon(double volumePercent)
        {
            if (VolumeIcon == null)
                return;

            int level = volumePercent <= 0
                ? 0
                : Math.Min(3, (int)Math.Ceiling(volumePercent / 50.0));
            VolumeIcon.Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri($"pack://application:,,,/Resources/Icons/speaker_{(ThemeService.IsLightMode ? "light" : "dark")}_{level}.png"));
        }

        // ========== ホットキー処理(アプリ内フォーカス時) ==========

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isSidebarOpen)
            {
                CloseSidebar();
                e.Handled = true;
                return;
            }

            // NOTE: これはアプリがフォーカスされている場合のホットキー。
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            var binding = _viewModel.Settings.HotKeyBindings.Find(item =>
                item.Key == key && item.Modifiers == modifiers);
            if (binding == null)
                return;

            if (ExecuteHotKeyAction(binding.Action))
                e.Handled = true;
        }

        private bool ExecuteHotKeyAction(HotKeyAction action)
        {
            switch (action)
            {
                case HotKeyAction.PlayPause: _viewModel.TogglePlayPause(); break;
                case HotKeyAction.Stop:
                case HotKeyAction.GoToEnd: _viewModel.Stop(); break;
                case HotKeyAction.SkipBackward5s: _viewModel.SkipBackward(); break;
                case HotKeyAction.SkipForward5s: _viewModel.SkipForward(); break;
                case HotKeyAction.VolumeUp: _viewModel.IncreaseVolume(); break;
                case HotKeyAction.VolumeDown: _viewModel.DecreaseVolume(); break;
                case HotKeyAction.Mute: _viewModel.ToggleMute(); break;
                case HotKeyAction.StepBackward01s:
                    _viewModel.SetPosition(Math.Max(0, _viewModel.PlaybackState.CurrentPosition.TotalSeconds - _viewModel.Settings.StepDurationSeconds));
                    break;
                case HotKeyAction.StepForward01s:
                    _viewModel.SetPosition(_viewModel.PlaybackState.CurrentPosition.TotalSeconds + _viewModel.Settings.StepDurationSeconds);
                    break;
                case HotKeyAction.GoToStart: _viewModel.SetPosition(0); break;
                case HotKeyAction.SpeedDecrease: _viewModel.DecreaseSpeed(); break;
                case HotKeyAction.SpeedIncrease: _viewModel.IncreaseSpeed(); break;
                case HotKeyAction.SpeedReset: _viewModel.ResetSpeed(); break;
                case HotKeyAction.ToggleLoopMode: _viewModel.CycleLoopMode(); break;
                case HotKeyAction.ToggleWaveform:
                    SidebarShowWaveformCheck.IsChecked = !(SidebarShowWaveformCheck.IsChecked == true);
                    SidebarShowWaveform_Click(this, new RoutedEventArgs());
                    break;
                case HotKeyAction.WaveformZoomIn: _viewModel.ZoomIn(); break;
                case HotKeyAction.WaveformZoomOut: _viewModel.ZoomOut(); break;
                default: return false;
            }

            return true;
        }
    }
}
