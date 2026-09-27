using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PotatoMusicPlayer.Models
{
    /// <summary>
    /// 言語設定
    /// </summary>
    public enum Language
    {
        EnglishUS = 0,
        Japanese = 1
    }

    public enum ThemeMode
    {
        Light,
        Dark,
        System,
        Ash
    }

    public enum WaveformZoomUnit
    {
        Percentage,
        Seconds
    }

    public enum CacheSizeUnit
    {
        KB,
        MB,
        GB
    }

    /// <summary>
    /// アプリケーション全体の設定
    /// </summary>
    [Serializable]
    public class AppSettings
    {
        // ウィンドウ設定
        public double WindowWidth { get; set; } = 900;
        public double WindowHeight { get; set; } = 520;
        public double WindowLeft { get; set; } = 100;
        public double WindowTop { get; set; } = 100;
        public bool IsAlwaysOnTop { get; set; } = false;
        public bool IsWindowSizeFixed { get; set; } = false;
        public bool IsFullScreen { get; set; } = false;
        public bool IsMenuBarCollapsed { get; set; } = true;

        // 再生設定
        public float DefaultVolume { get; set; } = 0.8f;  // 0.0 ~ 1.0 (1.0 = 100%)
        public float MaxVolumeMultiplier { get; set; } = 2.0f;  // 音量上限。2.0 = 200%
        public float DefaultPlaybackSpeed { get; set; } = 1.0f;
        public bool ResetVolumeOnStartup { get; set; } = false;
        public bool ResetPlaybackSpeedOnStartup { get; set; } = false;
        public bool RememberLastPlaybackSpeed { get; set; } = true;
        public bool RememberLastVolume { get; set; } = true;
        public bool RememberLastLoopMode { get; set; } = true;
        public LoopMode DefaultLoopMode { get; set; } = LoopMode.Off;
        // ファイル読み込み時に自動で再生を開始する。既定はオフ。
        public bool AutoPlayOnLoad { get; set; } = false;

        // 波形表示設定
        public bool ShowWaveform { get; set; } = true;
        public bool ShowSpectrum { get; set; } = true;
        public bool RememberWaveformZoom { get; set; } = false;
        public double DefaultWaveformZoomValue { get; set; } = 100;
        public WaveformZoomUnit DefaultWaveformZoomUnit { get; set; } = WaveformZoomUnit.Percentage;
        public WaveformZoomSettings WaveformZoom { get; set; } = new WaveformZoomSettings();

        // スキップ・操作値設定
        public int SkipDurationSeconds { get; set; } = 5;  // 5秒スキップ
        public float StepDurationSeconds { get; set; } = 0.1f;  // 0.1秒ステップ
        public int VolumeChangePercent { get; set; } = 5;  // 5%ずつ
        public int ScrollVolumeChangePercent { get; set; } = 1;  // マウスホイール
        public int SpeedChangePercent { get; set; } = 5;  // 5%ずつ
        public int SpeedResetPercent { get; set; } = 100;
        // 曲末から次の再生（ループを含む）を開始するまでの待機時間
        public double TrackTransitionDelaySeconds { get; set; } = 0;

        // ファイル管理
        public List<string> RecentFiles { get; set; } = new List<string>();
        public int MaxRecentFiles { get; set; } = 50;
        public bool RememberLastFile { get; set; } = true;
        public string LastPlayedFilePath { get; set; } = "";

        // 言語・地域
        public Language Language { get; set; } = Language.EnglishUS;
        public ThemeMode Theme { get; set; } = ThemeMode.System;

        // オーディオ設定
        public string AudioOutputDevice { get; set; } = "Default";

        // ホットキー設定
        public List<HotKeyBinding> HotKeyBindings { get; set; } = new List<HotKeyBinding>();

        /// <summary>
        /// デフォルト設定を初期化
        /// </summary>
        public void InitializeDefaultHotKeys()
        {
            if (HotKeyBindings == null)
                HotKeyBindings = new List<HotKeyBinding>();

            HotKeyBindings.Clear();

            // デフォルトホットキー定義
            HotKeyBindings.AddRange(new[]
            {
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.PlayPause, 
                    Key = System.Windows.Input.Key.Space,
                    DisplayName = "再生/一時停止"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.Stop, 
                    Key = System.Windows.Input.Key.End,
                    DisplayName = "停止"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.SkipBackward5s, 
                    Key = System.Windows.Input.Key.Left,
                    DisplayName = "5秒戻る"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.SkipForward5s, 
                    Key = System.Windows.Input.Key.Right,
                    DisplayName = "5秒進む"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.VolumeUp, 
                    Key = System.Windows.Input.Key.Up,
                    DisplayName = "音量上げる"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.VolumeDown, 
                    Key = System.Windows.Input.Key.Down,
                    DisplayName = "音量下げる"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.Mute, 
                    Key = System.Windows.Input.Key.M,
                    DisplayName = "ミュート"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.StepBackward01s, 
                    Key = System.Windows.Input.Key.OemComma,  // コンマ
                    DisplayName = "0.1秒戻る"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.StepForward01s, 
                    Key = System.Windows.Input.Key.OemPeriod,  // ドット
                    DisplayName = "0.1秒進む"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.GoToStart, 
                    Key = System.Windows.Input.Key.Home,
                    DisplayName = "最初に戻る"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.SpeedDecrease, 
                    Key = System.Windows.Input.Key.A,
                    DisplayName = "速度低下(5%)"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.SpeedIncrease, 
                    Key = System.Windows.Input.Key.D,
                    DisplayName = "速度上昇(5%)"
                },
                new HotKeyBinding 
                { 
                    Action = HotKeyAction.SpeedReset, 
                    Key = System.Windows.Input.Key.S,
                    DisplayName = "速度リセット(100%)"
                },
                new HotKeyBinding
                {
                    Action = HotKeyAction.ToggleLoopMode,
                    Key = System.Windows.Input.Key.L,
                    DisplayName = "ループ切り替え"
                },
                new HotKeyBinding
                {
                    Action = HotKeyAction.ToggleWaveform,
                    Key = System.Windows.Input.Key.W,
                    DisplayName = "波形表示切り替え"
                },
                new HotKeyBinding
                {
                    Action = HotKeyAction.WaveformZoomIn,
                    InputType = HotKeyInputType.MouseWheelUp,
                    Modifiers = System.Windows.Input.ModifierKeys.Control,
                    DisplayName = "波形を拡大"
                },
                new HotKeyBinding
                {
                    Action = HotKeyAction.WaveformZoomOut,
                    InputType = HotKeyInputType.MouseWheelDown,
                    Modifiers = System.Windows.Input.ModifierKeys.Control,
                    DisplayName = "波形を縮小"
                }
            });
        }

        public void EnsureDefaultHotKeys()
        {
            if (HotKeyBindings == null || HotKeyBindings.Count == 0)
            {
                InitializeDefaultHotKeys();
                return;
            }

            var defaults = new AppSettings();
            defaults.InitializeDefaultHotKeys();
            foreach (var binding in defaults.HotKeyBindings)
            {
                if (!HotKeyBindings.Exists(existing => existing.Action == binding.Action))
                    HotKeyBindings.Add(binding);
            }
        }
    }

    [Serializable]
    public class WaveformZoomSettings
    {
        public float MinZoomLevel { get; set; } = 1.0f;
        public float ZoomFactor { get; set; } = 2.0f;
        public float ScrollStepSize { get; set; } = 0.2f;
        public CursorDisplayMode CursorMode { get; set; } = CursorDisplayMode.CenterFixed;
        public bool ShowMinimap { get; set; } = true;
        public int MinimapHeight { get; set; } = 16;
        public int HorizontalDetail { get; set; } = 100;
        public int VerticalDetail { get; set; } = 100;
        public bool ProgressiveWaveform { get; set; } = true;
        public bool SaveWaveformCache { get; set; } = true;
        public double WaveformCacheLimitValue { get; set; } = 512;
        public CacheSizeUnit WaveformCacheLimitUnit { get; set; } = CacheSizeUnit.MB;
    }
}
