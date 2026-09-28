using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PotatoMusicPlayer.Models;
using PotatoMusicPlayer.Services;
using PotatoMusicPlayer.Utils;

namespace PotatoMusicPlayer.ViewModels
{
    /// <summary>
    /// メイン画面の ViewModel
    /// UI と ビジネスロジックを分離
    /// </summary>
    public class MainViewModel : ObservableObject
    {
        private readonly MediaService _mediaService;
        private readonly SettingsService _settingsService;
        private readonly WaveformZoomService _waveformZoomService;
        private System.Windows.Threading.DispatcherTimer _updateTimer;
        private int _waveformRequestId;
        private bool _isMuted;
        private float _volumeBeforeMute = 0.8f;
        private float? _volumeStateOverride;
        private bool _isManualWaveformNavigationActive;
        private bool _isWaveformSeekPending;
        private double _pendingWaveformSeekPosition;
        private long _pendingWaveformSeekDeadline;

        // プロパティ
        private MediaFile _currentMediaFile;
        private PlaybackState _playbackState;
        private bool _isLoading;
        private bool _isEngineLoading;
        private double _engineBufferingPercent = -1;
        private CancellationTokenSource _engineLoadCts;
        private string _statusMessage;
        private float[] _currentWaveformData = Array.Empty<float>();
        private double _waveformProgress;
        private WaveformZoomState _zoomState;
        private List<PlaylistEntry> _playlistQueue = new List<PlaylistEntry>();
        private int _playlistIndex = -1;
        private string _playlistName = string.Empty;

        public MainViewModel()
        {
            _mediaService = new MediaService();
            _settingsService = new SettingsService();
            _waveformZoomService = new WaveformZoomService();

            // イベント登録
            _mediaService.PlaybackStateChanged += (s, e) => UpdatePlaybackState();
            _mediaService.PositionChanged += (s, e) => UpdatePlaybackState();
            _mediaService.DurationChanged += (s, e) => UpdatePlaybackState();
            _mediaService.MediaEnded += (s, e) => OnMediaEnded();
            _mediaService.ErrorOccurred += (s, msg) => StatusMessage = $"Error: {msg}";
            _mediaService.BufferingChanged += (s, percent) => EngineBufferingPercent = percent;

            // UI 更新タイマー
            InitializeUpdateTimer();

            // コマンド初期化
            InitializeCommands();

            // 初期状態設定
            PlaybackState = new PlaybackState();
            var startupSettings = _settingsService.GetSettings();
            _waveformZoomService.Configure(startupSettings.WaveformZoom);
            float startupVolume = Math.Clamp(startupSettings.DefaultVolume, 0.0f, startupSettings.MaxVolumeMultiplier);
            float startupSpeed = Math.Clamp(startupSettings.DefaultPlaybackSpeed, 0.25f, 4.0f);
            _volumeBeforeMute = startupVolume;
            _mediaService.SetVolume(startupVolume, startupSettings.MaxVolumeMultiplier);
            _mediaService.SetPlaybackSpeed(startupSpeed);
            PlaybackState.Volume = startupVolume;
            PlaybackState.PlaybackSpeed = startupSpeed;
            PlaybackState.LoopMode = startupSettings.RememberLastLoopMode
                ? startupSettings.DefaultLoopMode
                : LoopMode.Off;
            StatusMessage = "Ready to play music";
        }

        // ========== Public Properties ==========

        public MediaFile CurrentMediaFile
        {
            get => _currentMediaFile;
            set
            {
                if (SetProperty(ref _currentMediaFile, value))
                    ResetWaveformZoom(value?.Duration.TotalSeconds ?? 0);
            }
        }

        public WaveformZoomState ZoomState
        {
            get => _zoomState;
            private set => SetProperty(ref _zoomState, value);
        }

        public PlaybackState PlaybackState
        {
            get => _playbackState;
            set => SetProperty(ref _playbackState, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        /// <summary>エンジンが音声を読み込み中かどうか。読み込み完了まで再生操作を安定させる。</summary>
        public bool IsEngineLoading
        {
            get => _isEngineLoading;
            private set => SetProperty(ref _isEngineLoading, value);
        }

        /// <summary>エンジンのバッファリング進捗(0 ~ 100)。不明時は -1。</summary>
        public double EngineBufferingPercent
        {
            get => _engineBufferingPercent;
            private set => SetProperty(ref _engineBufferingPercent, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public float[] CurrentWaveformData
        {
            get => _currentWaveformData;
            private set => SetProperty(ref _currentWaveformData, value);
        }

        public double WaveformProgress
        {
            get => _waveformProgress;
            private set => SetProperty(ref _waveformProgress, value);
        }

        public AppSettings Settings => _settingsService.GetSettings();
        public bool IsMuted => _isMuted;
        public double? PendingWaveformSeekPosition => _isWaveformSeekPending
            ? _pendingWaveformSeekPosition : null;

        // ========== Commands ==========

        public ICommand PlayPauseCommand { get; private set; }
        public ICommand StopCommand { get; private set; }
        public ICommand SkipForwardCommand { get; private set; }
        public ICommand SkipBackwardCommand { get; private set; }
        public ICommand VolumeUpCommand { get; private set; }
        public ICommand VolumeDownCommand { get; private set; }
        public ICommand SpeedIncreaseCommand { get; private set; }
        public ICommand SpeedDecreaseCommand { get; private set; }
        public ICommand SpeedResetCommand { get; private set; }
        public ICommand ToggleLoopCommand { get; private set; }
        public ICommand SetPositionCommand { get; private set; }
        public ICommand OpenSettingsCommand { get; private set; }

        // ========== Command Implementations ==========

        private void InitializeCommands()
        {
            PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
            StopCommand = new RelayCommand(_ => Stop());
            SkipForwardCommand = new RelayCommand(_ => SkipForward());
            SkipBackwardCommand = new RelayCommand(_ => SkipBackward());
            VolumeUpCommand = new RelayCommand(_ => IncreaseVolume());
            VolumeDownCommand = new RelayCommand(_ => DecreaseVolume());
            SpeedIncreaseCommand = new RelayCommand(_ => IncreaseSpeed());
            SpeedDecreaseCommand = new RelayCommand(_ => DecreaseSpeed());
            SpeedResetCommand = new RelayCommand(_ => ResetSpeed());
            ToggleLoopCommand = new RelayCommand(_ => CycleLoopMode());
            SetPositionCommand = new RelayCommand<double>(pos => SetPosition(pos));
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        }

        public async Task LoadAndPlayFileAsync(string filePath)
        {
            if (!FileService.IsSupportedFormat(filePath))
            {
                StatusMessage = "Unsupported file format";
                return;
            }

            IsLoading = true;
            try
            {
                bool loaded = await _mediaService.LoadFileAsync(filePath);
                if (loaded)
                {
                    CurrentMediaFile = await _mediaService.GetMediaInfoAsync(filePath);
                    _settingsService.AddRecentFile(filePath);
                    if (_settingsService.GetSettings().AutoPlayOnLoad)
                    {
                        // エンジンの読み込みが安定するまで待ってから再生する。
                        // 安定前のシークが音の途切れの原因になるため、範囲内復帰も安定後に行う。
                        _engineLoadCts?.Cancel();
                        _engineLoadCts?.Dispose();
                        _engineLoadCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var loadCts = _engineLoadCts;
                        IsEngineLoading = true;
                        EngineBufferingPercent = -1;
                        try
                        {
                            Play();
                            await _mediaService.WaitForStablePlaybackAsync(loadCts.Token);
                        }
                        finally
                        {
                            IsEngineLoading = false;
                            if (_engineLoadCts == loadCts)
                                _engineLoadCts = null;
                            loadCts.Dispose();
                        }
                        if (!string.Equals(CurrentMediaFile?.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                            return;
                        UpdatePlaybackState();
                        EnsureRepeatRangeStart();
                    }
                    else
                        UpdatePlaybackState();
                    StatusMessage = $"Loaded: {CurrentMediaFile.FileName}";
                    _ = LoadWaveformAsync(filePath);
                }
                else
                {
                    StatusMessage = "Failed to load file";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                Debug.WriteLine(ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void TogglePlayPause()
        {
            if (PlaybackState?.State == PlayState.Playing)
            {
                _mediaService.Pause();
                UpdatePlaybackState();
                return;
            }

            Play();
        }

        public void Play()
        {
            if (_isWaveformSeekPending)
                _pendingWaveformSeekDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            // 中央固定では再生開始時に範囲を再生位置へ中央合わせする。
            // ただし位置が範囲内の中央より左にある間は動かさず、追いつくのを待つ。
            if (Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
            {
                double position = _isWaveformSeekPending
                    ? _pendingWaveformSeekPosition
                    : PlaybackState?.CurrentPosition.TotalSeconds ?? double.NaN;
                var state = ZoomState;
                bool hold = !double.IsNaN(position) &&
                    _waveformZoomService.ShouldHoldCenterFixed(state, position);
                if (!hold)
                    CenterWaveformRangeOnPosition(position);
            }
            // MediaService resets LibVLC's Ended state when necessary. Do not
            // use the cached UI position here, since it can be stale after a seek.
            _mediaService.Play();
            UpdatePlaybackState();
            // 再生開始時に範囲外にいたら範囲内へ戻す。読み込み中は安定後に行う。
            if (!IsEngineLoading)
                EnsureRepeatRangeStart();
        }

        public void PlayKeepingWaveformRange()
        {
            if (_isWaveformSeekPending)
                _pendingWaveformSeekDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            _mediaService.Play();
            UpdatePlaybackState();
        }

        /// <summary>
        /// 再生開始時にリピート範囲外にいたら範囲先頭へ戻す(範囲内は何もしない)。
        /// </summary>
        public void EnsureRepeatRangeStart()
        {
            var settings = Settings;
            if (settings == null || !settings.RepeatRangeEnabled)
                return;

            double total = ZoomState?.TotalDuration ?? PlaybackState?.Duration.TotalSeconds ?? 0;
            if (total <= 0)
                return;

            double start = Math.Clamp(settings.RepeatRangeStart, 0, total);
            double end = Math.Clamp(settings.RepeatRangeEnd, 0, total);
            if (end - start < 0.1)
                return;

            double position = PlaybackState?.CurrentPosition.TotalSeconds ?? 0;
            if (position < start || position >= end)
                SeekAndPlay(start);
        }

        public void Pause()
        {
            _engineLoadCts?.Cancel();
            _mediaService.Pause();
            UpdatePlaybackState();
        }

        public void Stop()
        {
            _engineLoadCts?.Cancel();
            CancelPendingWaveformSeek();
            _mediaService.Stop();
            UpdatePlaybackState();
        }

        /// <summary>
        /// 再生を停止し、読み込み中のファイルと再生状態を破棄する(ホームへ戻る用)。
        /// </summary>
        public void UnloadCurrentFile()
        {
            Stop();
            _playlistQueue.Clear();
            _playlistIndex = -1;
            _playlistName = string.Empty;
            OnPropertyChanged(nameof(PlaylistQueue));
            OnPropertyChanged(nameof(PlaylistIndex));
            CurrentWaveformData = Array.Empty<float>();
            WaveformProgress = 0;
            CurrentMediaFile = null;
        }

        public bool IsPlaylistActive => _playlistQueue.Count > 0;

        /// <summary>
        /// 単体再生用にプレイリスト状態を捨てる(枠を隠す)。
        /// </summary>
        public void ClearPlaylist()
        {
            _playlistQueue.Clear();
            _playlistIndex = -1;
            _playlistName = string.Empty;
            OnPropertyChanged(nameof(PlaylistQueue));
            OnPropertyChanged(nameof(PlaylistIndex));
        }
        public IReadOnlyList<PlaylistEntry> PlaylistQueue => _playlistQueue;

        public int PlaylistIndex => _playlistIndex;

        public string PlaylistName => _playlistName;

        /// <summary>
        /// プレイリストをまとめて再生する。指定位置から開始し、終端で次へ進む。
        /// </summary>
        public async Task PlayPlaylistAsync(List<PlaylistEntry> entries, int startIndex, string name)
        {
            _playlistQueue = new List<PlaylistEntry>(entries ?? new List<PlaylistEntry>());
            _playlistName = name ?? string.Empty;
            _playlistIndex = Math.Clamp(startIndex, 0, Math.Max(0, _playlistQueue.Count - 1));
            OnPropertyChanged(nameof(PlaylistQueue));
            await PlayPlaylistIndexAsync();
            OnPropertyChanged(nameof(PlaylistIndex));
        }

        private async Task PlayPlaylistIndexAsync()
        {
            while (_playlistIndex >= 0 && _playlistIndex < _playlistQueue.Count)
            {
                string path = _playlistQueue[_playlistIndex].FilePath;
                await LoadAndPlayFileAsync(path);
                if (string.Equals(CurrentMediaFile?.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    return;
                _playlistIndex++;
            }
            Stop();
        }

        /// <summary>
        /// キュー内の指定位置から再生する(枠のダブルクリック用)。
        /// </summary>
        public async Task PlayPlaylistTrackAsync(int index)
        {
            if (!IsPlaylistActive || index < 0 || index >= _playlistQueue.Count)
                return;
            _playlistIndex = index;
            await PlayPlaylistIndexAsync();
            OnPropertyChanged(nameof(PlaylistIndex));
        }

        /// <summary>
        /// 停止ボタン用。プレイリスト再生中は先頭曲の先頭へ巻き戻して停止する。
        /// </summary>
        public async Task RewindPlaylistToStartAsync()
        {
            if (!IsPlaylistActive)
            {
                Stop();
                return;
            }

            _playlistIndex = 0;
            string path = _playlistQueue[0].FilePath;
            Stop();
            if (await _mediaService.LoadFileAsync(path))
            {
                CurrentMediaFile = await _mediaService.GetMediaInfoAsync(path);
                SetPosition(0);
                UpdatePlaybackState();
                _ = LoadWaveformAsync(path);
            }
            OnPropertyChanged(nameof(PlaylistIndex));
        }

        public void SkipForward()
        {
            float skipSeconds = _settingsService.GetSettings().SkipDurationSeconds;
            _mediaService.SkipRelative(skipSeconds);
        }

        public void SkipBackward()
        {
            float skipSeconds = _settingsService.GetSettings().SkipDurationSeconds;
            _mediaService.SkipRelative(-skipSeconds);
        }

        public void IncreaseVolume()
        {
            var settings = _settingsService.GetSettings();
            if (_isMuted)
                SetMuted(false);
            float change = settings.VolumeChangePercent / 100.0f;
            float maxVolume = settings.MaxVolumeMultiplier;
            float newVolume = Math.Min(PlaybackState.Volume + change, maxVolume);
            _mediaService.SetVolume(newVolume, maxVolume);
            UpdatePlaybackState();
        }

        public void DecreaseVolume()
        {
            var settings = _settingsService.GetSettings();
            if (_isMuted)
                SetMuted(false);
            float change = settings.VolumeChangePercent / 100.0f;
            float newVolume = Math.Max(PlaybackState.Volume - change, 0.0f);
            _mediaService.SetVolume(newVolume, settings.MaxVolumeMultiplier);
            UpdatePlaybackState();
        }

        /// <summary>
        /// 音量をパーセント指定で直接設定する（音量スライダーのドラッグ操作用）
        /// </summary>
        public void SetVolume(double percent)
        {
            var settings = _settingsService.GetSettings();
            float volume = (float)(percent / 100.0);
            volume = Math.Clamp(volume, 0.0f, settings.MaxVolumeMultiplier);
            _isMuted = false;
            _volumeBeforeMute = volume;
            _volumeStateOverride = volume;
            _mediaService.SetVolume(volume, settings.MaxVolumeMultiplier);
            UpdatePlaybackState();
        }

        public void ToggleMute()
        {
            SetMuted(!_isMuted);
        }

        private void SetMuted(bool muted)
        {
            var settings = _settingsService.GetSettings();
            if (muted)
            {
                if (!_isMuted)
                    _volumeBeforeMute = PlaybackState?.Volume ?? _volumeBeforeMute;

                _isMuted = true;
                _mediaService.SetVolume(0, settings.MaxVolumeMultiplier);
            }
            else
            {
                _isMuted = false;
                float restoredVolume = Math.Clamp(_volumeBeforeMute, 0.0f, settings.MaxVolumeMultiplier);
                // LibVLC の状態取得が一瞬だけ旧値(0)を返しても、復元値を先にUIへ渡す。
                _volumeStateOverride = restoredVolume;
                _mediaService.SetVolume(restoredVolume, settings.MaxVolumeMultiplier);
            }

            OnPropertyChanged(nameof(IsMuted));
            UpdatePlaybackState();
        }

        public void IncreaseSpeed()
        {
            var settings = _settingsService.GetSettings();
            float change = settings.SpeedChangePercent / 100.0f;
            float newSpeed = Math.Min(PlaybackState.PlaybackSpeed + change, 2.0f);
            _mediaService.SetPlaybackSpeed(newSpeed);
            UpdatePlaybackState();
        }

        public void DecreaseSpeed()
        {
            var settings = _settingsService.GetSettings();
            float change = settings.SpeedChangePercent / 100.0f;
            float newSpeed = Math.Max(PlaybackState.PlaybackSpeed - change, 0.25f);
            _mediaService.SetPlaybackSpeed(newSpeed);
            UpdatePlaybackState();
        }

        public void ResetSpeed()
        {
            _mediaService.SetPlaybackSpeed(_settingsService.GetSettings().SpeedResetPercent / 100.0f);
            UpdatePlaybackState();
        }

        public void CycleLoopMode()
        {
            PlaybackState.LoopMode = (LoopMode)(((int)PlaybackState.LoopMode + 1) % 3);
            OnPropertyChanged(nameof(PlaybackState));
        }

        public void SetPosition(double seconds)
        {
            _pendingWaveformSeekPosition = Math.Clamp(seconds, 0,
                Math.Max(0, ZoomState?.TotalDuration ?? PlaybackState?.Duration.TotalSeconds ?? 0));
            _pendingWaveformSeekDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            _isWaveformSeekPending = true;
            _mediaService.SetPosition((long)(_pendingWaveformSeekPosition * 1000));
        }

        public void SetPositionKeepingWaveformRange(double seconds)
        {
            SetPosition(seconds);
        }

        public void BeginManualWaveformNavigation()
        {
            _isManualWaveformNavigationActive = true;
        }

        public void EndManualWaveformNavigation()
        {
            _isManualWaveformNavigationActive = false;
        }

        public void ZoomIn()
        {
            ZoomWithEdgePivot(true);
        }

        public void ZoomOut()
        {
            ZoomWithEdgePivot(false);
        }

        /// <summary>
        /// 指定位置へ表示範囲を中央合わせする（端では丸められる）。
        /// </summary>
        public void CenterWaveformRangeOnPosition(double position)
        {
            var state = ZoomState;
            if (state == null || state.TotalDuration <= 0 || double.IsNaN(position))
                return;
            if (Math.Abs((state.VisibleRangeStart + state.VisibleRangeEnd) / 2 - position) < 0.001)
                return;
            _waveformZoomService.CenterOnPositionAllowingEdges(state, position);
            OnPropertyChanged(nameof(ZoomState));
        }

        private void ZoomWithEdgePivot(bool zoomIn)
        {
            var state = ZoomState;
            if (state == null)
                return;

            // 中央固定では再生位置の相対割合を保って拡大縮小し、位置を動かさない。
            // 左流しでは従来通り範囲中央を軸にする。
            double position = PlaybackState?.CurrentPosition.TotalSeconds ?? double.NaN;
            if (Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed && !double.IsNaN(position))
            {
                if (zoomIn)
                    _waveformZoomService.ZoomInPreservingRatio(state, position);
                else
                    _waveformZoomService.ZoomOutPreservingRatio(state, position);
            }
            else
            {
                double center = (state.VisibleRangeStart + state.VisibleRangeEnd) / 2;
                if (zoomIn)
                    _waveformZoomService.ZoomInAroundPoint(state, center);
                else
                    _waveformZoomService.ZoomOutAroundPoint(state, center);
            }
            OnPropertyChanged(nameof(ZoomState));
        }

        public void ScrollWaveform(double seconds)
        {
            _waveformZoomService.Scroll(ZoomState, seconds);
            OnPropertyChanged(nameof(ZoomState));
        }

        public void SetWaveformVisibleRange(double startTime, double endTime)
        {
            _waveformZoomService.SetVisibleRange(ZoomState, startTime, endTime);
            OnPropertyChanged(nameof(ZoomState));
        }

        public void SetWaveformRangeStart(double startTime)
        {
            _waveformZoomService.SetVisibleRangeStart(ZoomState, startTime);
            OnPropertyChanged(nameof(ZoomState));
        }

        public void SetWaveformRangeEnd(double endTime)
        {
            _waveformZoomService.SetVisibleRangeEnd(ZoomState, endTime);
            OnPropertyChanged(nameof(ZoomState));
        }

        public void MoveWaveformRange(double startTime)
        {
            if (ZoomState == null)
                return;
            double previousStart = ZoomState.VisibleRangeStart;
            double previousEnd = ZoomState.VisibleRangeEnd;
            _waveformZoomService.SetRangeStart(ZoomState, startTime);
            if (Math.Abs(ZoomState.VisibleRangeStart - previousStart) > 0.000001 ||
                Math.Abs(ZoomState.VisibleRangeEnd - previousEnd) > 0.000001)
                OnPropertyChanged(nameof(ZoomState));
        }

        public void PanWaveformRange(double startTime)
        {
            if (ZoomState == null)
                return;
            double previousStart = ZoomState.VisibleRangeStart;
            double previousEnd = ZoomState.VisibleRangeEnd;
            _waveformZoomService.PanBeyondEdges(ZoomState, startTime);
            if (Math.Abs(ZoomState.VisibleRangeStart - previousStart) > 0.000001 ||
                Math.Abs(ZoomState.VisibleRangeEnd - previousEnd) > 0.000001)
                OnPropertyChanged(nameof(ZoomState));
        }

        public void SetWaveformRangeCentered(double center, double width)
        {
            _waveformZoomService.SetVisibleRangeCentered(ZoomState, center, width);
            OnPropertyChanged(nameof(ZoomState));
        }

        public void ZoomWaveformPreservingRatio(double rangeStart, double rangeEnd, double position, double width)
        {
            _waveformZoomService.ZoomPreservingRatio(ZoomState, rangeStart, rangeEnd, position, width);
            OnPropertyChanged(nameof(ZoomState));
        }

        public (double Start, double End) GetMinimapVisibleRange()
        {
            return _waveformZoomService.GetVisibleRangeWithinTrack(ZoomState);
        }

        public void ApplyWaveformSettings()
        {
            var previousState = ZoomState;
            double totalDuration = CurrentMediaFile?.Duration.TotalSeconds ?? 0;
            double previousStart = previousState?.VisibleRangeStart ?? 0;
            double previousEnd = previousState?.VisibleRangeEnd ?? 0;
            bool canRestoreRange = previousState != null &&
                previousState.TotalDuration > 0 && totalDuration > 0 &&
                Math.Abs(previousState.TotalDuration - totalDuration) < 0.001 &&
                previousState.VisibleRangeDuration > 0;

            _isManualWaveformNavigationActive = false;
            _waveformZoomService.Configure(Settings.WaveformZoom);

            if (!canRestoreRange)
            {
                ResetWaveformZoom(totalDuration);
                return;
            }

            var restoredState = _waveformZoomService.CreateInitialState(totalDuration);
            _waveformZoomService.SetVisibleRange(restoredState, previousStart, previousEnd);
            ZoomState = restoredState;
        }

        public void FollowWaveformPosition(double position)
        {
            UpdateWaveformFollow(position);
        }

        private void ResetWaveformZoom(double totalDuration)
        {
            CancelPendingWaveformSeek();
            _isManualWaveformNavigationActive = false;
            double previousWidth = ZoomState?.CurrentZoomLevel ?? 0;
            double previousTotalDuration = ZoomState?.TotalDuration ?? 0;
            var newState = _waveformZoomService.CreateInitialState(totalDuration);
            if (totalDuration <= 0)
            {
                ZoomState = newState;
                return;
            }

            double width;
            if (Settings.RememberWaveformZoom && previousWidth > 0)
            {
                width = Settings.DefaultWaveformZoomUnit == WaveformZoomUnit.Percentage && previousTotalDuration > 0
                    ? totalDuration * previousWidth / previousTotalDuration
                    : previousWidth;
            }
            else
            {
                width = Settings.DefaultWaveformZoomUnit == WaveformZoomUnit.Percentage
                    ? totalDuration * Settings.DefaultWaveformZoomValue / 100
                    : Settings.DefaultWaveformZoomValue;
            }

            _waveformZoomService.SetVisibleRangeCentered(newState, Math.Min(width, totalDuration) / 2, width);
            ZoomState = newState;
        }

        public void SeekAndPlay(double seconds)
        {
            // シーク完了まで追従を抑止する。再生エンジンの位置反映は非同期のため、
            // 直後の追従が古い位置で表示範囲を引き戻してしまう（ドラッグ解放時のちらつき対策）。
            _pendingWaveformSeekPosition = Math.Clamp(seconds, 0,
                Math.Max(0, ZoomState?.TotalDuration ?? PlaybackState?.Duration.TotalSeconds ?? 0));
            _pendingWaveformSeekDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            _isWaveformSeekPending = true;
            _mediaService.PlayFromPosition((long)(_pendingWaveformSeekPosition * 1000));
            UpdatePlaybackState();
        }

        public void SeekAndPlayKeepingWaveformRange(double seconds)
        {
            SeekAndPlay(seconds);
        }

        /// <summary>
        /// 再生を妨げずに、表示用のピーク振幅データをバックグラウンドで生成する。
        /// 新しいファイルを読み込んだ場合は、古い要求の結果を破棄する。
        /// </summary>
        public async Task LoadWaveformAsync(string filePath)
        {
            // 画面幅ではなく時間軸の詳細度を確保し、ズームしても波形を確認できるようにする。
            const int barCount = 1_000_000;
            int requestId = Interlocked.Increment(ref _waveformRequestId);
            CurrentWaveformData = Array.Empty<float>();
            WaveformProgress = 0;

            var waveformService = new WaveformService();
            EventHandler<double> progressHandler = (sender, progress) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (requestId == _waveformRequestId)
                        WaveformProgress = progress;
                }));
            };
            waveformService.ProgressChanged += progressHandler;

            // 逐次表示が有効な場合、生成途中の部分波形も公開して描画する。
            EventHandler<float[]> partialHandler = null;
            if (Settings.WaveformZoom.ProgressiveWaveform)
            {
                partialHandler = (sender, snapshot) =>
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (requestId == _waveformRequestId)
                            CurrentWaveformData = snapshot;
                    }));
                };
                waveformService.PartialWaveformReady += partialHandler;
            }

            try
            {
                WaveformCacheService cacheService = null;
                if (Settings.WaveformZoom.SaveWaveformCache)
                {
                    long maxBytes = WaveformCacheService.ToBytes(
                        Settings.WaveformZoom.WaveformCacheLimitValue,
                        Settings.WaveformZoom.WaveformCacheLimitUnit);
                    cacheService = new WaveformCacheService(maxBytes);
                    if (cacheService.TryLoad(filePath, barCount, out float[] cachedData) &&
                        requestId == _waveformRequestId)
                    {
                        CurrentWaveformData = cachedData;
                        WaveformProgress = 1;
                        return;
                    }
                }

                float[] data = await waveformService.GenerateWaveformAsync(filePath, barCount);
                if (requestId == _waveformRequestId)
                {
                    CurrentWaveformData = data;
                    WaveformProgress = 1;
                }
                if (cacheService != null && data.Length > 0)
                    cacheService.Save(filePath, barCount, data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Waveform generation failed: {ex}");
                if (requestId == _waveformRequestId)
                {
                    CurrentWaveformData = Array.Empty<float>();
                    WaveformProgress = 1;
                }
            }
            finally
            {
                if (partialHandler != null)
                    waveformService.PartialWaveformReady -= partialHandler;
                waveformService.ProgressChanged -= progressHandler;
            }
        }

        public void OpenSettings()
        {
            // 設定ウィンドウを開く（UIスレッドで実行される前提）
            try
            {
                var settingsBeforeDialog = _settingsService.GetSettings();
                var window = new PotatoMusicPlayer.Views.SettingsWindow(_settingsService);
                var owner = System.Windows.Application.Current?.MainWindow;
                if (owner != null)
                    window.Owner = owner;

                bool? result = window.ShowDialog();

                // 設定が適用された可能性があるため、プロパティを更新して UI に反映させる
                if (!ReferenceEquals(settingsBeforeDialog, _settingsService.GetSettings()))
                    ApplyWaveformSettings();
                OnPropertyChanged(nameof(Settings));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to open settings: {ex.Message}";
            }
        }

        // ========== Private Methods ==========

        private void InitializeUpdateTimer()
        {
            _updateTimer = new System.Windows.Threading.DispatcherTimer();
            _updateTimer.Interval = TimeSpan.FromMilliseconds(Constants.UIUpdateIntervalMs);
            _updateTimer.Tick += (s, e) => UpdatePlaybackState();
            _updateTimer.Start();
        }

        private void UpdatePlaybackState()
        {
            var state = _mediaService.GetPlaybackState();
            // Preserve UI-controlled properties (LoopMode) so they are not overwritten by media service snapshot
            if (PlaybackState != null)
            {
                state.LoopMode = PlaybackState.LoopMode;
            }
            state.IsMuted = _isMuted;
            if (_volumeStateOverride.HasValue)
            {
                state.Volume = _volumeStateOverride.Value;
                _volumeStateOverride = null;
            }
            PlaybackState = state;
            UpdatePendingWaveformSeek(state.CurrentPosition.TotalSeconds);
            if (ZoomState != null)
            {
                ZoomState.CurrentPlaybackPosition = state.CurrentPosition.TotalSeconds;
                ReconcileWaveformDuration(state.Duration.TotalSeconds);
                if (state.State == PlayState.Playing && Settings.WaveformZoom.CursorMode == CursorDisplayMode.LeftScroll)
                    UpdateWaveformFollow(state.CurrentPosition.TotalSeconds);
            }
        }

        /// <summary>
        /// 再生中の実長さとズーム状態の全体長さが食い違っている場合、表示範囲の割合を保ったまま補正する。
        /// 読込時の長さは推定値のため、VBR等で実長さとずれることがある。補正後は通知して再描画する。
        /// 再生直後は実長さが落ち着かないため、安定するまで補正しない。
        /// </summary>
        private double _lastPlayerTotalSeconds;
        private int _playerTotalStableTicks;

        private void ReconcileWaveformDuration(double playerTotalSeconds)
        {
            var zoomState = ZoomState;
            if (zoomState == null || playerTotalSeconds <= 0 || _isManualWaveformNavigationActive)
                return;

            if (Math.Abs(playerTotalSeconds - _lastPlayerTotalSeconds) > 0.5)
            {
                _lastPlayerTotalSeconds = playerTotalSeconds;
                _playerTotalStableTicks = 0;
                return;
            }
            _lastPlayerTotalSeconds = playerTotalSeconds;
            if (_playerTotalStableTicks < 3)
            {
                _playerTotalStableTicks++;
                return;
            }

            double oldTotal = zoomState.TotalDuration;
            if (oldTotal <= 0)
            {
                ResetWaveformZoom(playerTotalSeconds);
                return;
            }

            if (Math.Abs(playerTotalSeconds - oldTotal) <= Math.Max(0.5, oldTotal * 0.005))
                return;

            double factor = playerTotalSeconds / oldTotal;
            zoomState.TotalDuration = playerTotalSeconds;
            zoomState.MaxZoomLevel = playerTotalSeconds;
            zoomState.MinZoomLevel = Math.Min(Math.Clamp(Settings.WaveformZoom.MinZoomLevel, 0.05, 60), playerTotalSeconds);
            double newWidth = Math.Clamp(zoomState.CurrentZoomLevel * factor, zoomState.MinZoomLevel, playerTotalSeconds);
            double minStart = Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed
                ? -newWidth / 2 : 0;
            double maxStart = Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed
                ? playerTotalSeconds - newWidth / 2 : Math.Max(0, playerTotalSeconds - newWidth);
            zoomState.VisibleRangeStart = Math.Clamp(zoomState.VisibleRangeStart * factor, minStart, maxStart);
            zoomState.CurrentZoomLevel = newWidth;
            zoomState.VisibleRangeEnd = zoomState.VisibleRangeStart + newWidth;
            OnPropertyChanged(nameof(ZoomState));
        }

        private void UpdatePendingWaveformSeek(double position)
        {
            if (!_isWaveformSeekPending)
                return;

            var zoomState = ZoomState;
            // エンジンが目標位置へ追いついたら抑止を解く。範囲内外は問わない（3秒で打ち切り）。
            bool targetReached = zoomState != null &&
                Math.Abs(position - _pendingWaveformSeekPosition) <= 0.1 &&
                PlaybackState?.State != PlayState.Stopped;
            bool timedOut = PlaybackState?.State == PlayState.Playing &&
                Stopwatch.GetTimestamp() >= _pendingWaveformSeekDeadline;
            if (targetReached || timedOut)
            {
                CancelPendingWaveformSeek();
            }
        }

        private void CancelPendingWaveformSeek()
        {
            _isWaveformSeekPending = false;
            _pendingWaveformSeekDeadline = 0;
        }

        private void UpdateWaveformFollow(double position)
        {
            if (_isWaveformSeekPending || _isManualWaveformNavigationActive)
                return;

            var settings = Settings.WaveformZoom;
            var zoomState = ZoomState;
            if (zoomState == null || zoomState.TotalDuration <= 0 || zoomState.VisibleRangeDuration <= 0)
                return;

            // ビュー自由化中は再生バーが範囲外に出ても何もしない。
            if (settings.FreeView)
                return;

            if (settings.CursorMode == CursorDisplayMode.CenterFixed)
            {
                double previousStart = zoomState.VisibleRangeStart;
                _waveformZoomService.FollowCenterFixed(zoomState, position);
                if (Math.Abs(zoomState.VisibleRangeStart - previousStart) > 0.001)
                    OnPropertyChanged(nameof(ZoomState));
                return;
            }

            if (position < zoomState.VisibleRangeStart)
            {
                double pageStart = Math.Floor(position / zoomState.CurrentZoomLevel) * zoomState.CurrentZoomLevel;
                MoveWaveformRange(pageStart);
            }
            else if (position >= zoomState.VisibleRangeEnd)
            {
                double pages = Math.Floor((position - zoomState.VisibleRangeEnd) / zoomState.CurrentZoomLevel) + 1;
                MoveWaveformRange(zoomState.VisibleRangeStart + pages * zoomState.CurrentZoomLevel);
            }
        }

        private async void OnMediaEnded()
        {
            var settings = _settingsService.GetSettings();
            if (settings.RememberLastFile && CurrentMediaFile != null)
            {
                settings.LastPlayedFilePath = CurrentMediaFile.FilePath;
                _settingsService.SaveSettings(settings);
            }

            // ループモード・プレイリスト・連続設定に応じた処理
            bool playlistFlow = IsPlaylistActive && settings.PlaylistContinuous;
            if (CurrentMediaFile != null)
            {
                if (PlaybackState.LoopMode == LoopMode.One ||
                    (!playlistFlow && PlaybackState.LoopMode == LoopMode.All))
                {
                    // 単曲ループ、または連続なし・未使用時の全体ループは同じ曲の先頭へ戻す。
                    // 左流モードでは表示範囲も先頭ページへ戻す。
                    Stop();
                    SetPosition(0);
                    UpdateWaveformFollow(0);
                    double delaySeconds = Math.Clamp(_settingsService.GetSettings().TrackTransitionDelaySeconds, 0, 60);
                    if (delaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    Play();
                }
                else if (playlistFlow)
                {
                    double delaySeconds = Math.Clamp(_settingsService.GetSettings().TrackTransitionDelaySeconds, 0, 60);
                    if (delaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                    int next = _playlistIndex + 1;
                    if (next >= _playlistQueue.Count)
                    {
                        if (PlaybackState.LoopMode == LoopMode.All)
                            next = 0;
                        else
                        {
                            Stop();
                            StatusMessage = "Playlist finished";
                            OnPropertyChanged(nameof(PlaylistIndex));
                            return;
                        }
                    }
                    _playlistIndex = next;
                    await PlayPlaylistIndexAsync();
                    OnPropertyChanged(nameof(PlaylistIndex));
                }
            }

            if (PlaybackState.LoopMode == LoopMode.Off && !playlistFlow)
            {
                UpdatePlaybackState();
            }

            StatusMessage = "Playback finished";
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _waveformRequestId);
            _updateTimer?.Stop();
            _mediaService?.Dispose();
        }
    }
}
