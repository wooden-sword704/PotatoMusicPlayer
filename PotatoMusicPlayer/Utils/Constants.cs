namespace PotatoMusicPlayer.Utils
{
    /// <summary>
    /// アプリケーション全体で使用する定数
    /// </summary>
    public static class Constants
    {
        // アプリケーション情報
        public const string AppName = "Potato Music Player";
        public const string AppVersion = "WSMod-1.4.0 RC1";
        public const string AppAuthor = "Velters,wooden_sword704";

        // ウィンドウs
        public const double DefaultWindowWidth = 400;
        public const double DefaultWindowHeight = 150;

        // 再生設定
        public const float MinVolume = 0.0f;
        public const float MaxVolume = 1.0f;
        public const float DefaultVolume = 0.8f;
        public const float VolumeStep = 0.05f;  // 5%

        public const float MinPlaybackSpeed = 0.25f;
        public const float MaxPlaybackSpeed = 2.0f;
        public const float DefaultPlaybackSpeed = 1.0f;
        public const float PlaybackSpeedStep = 0.05f;  // 5%

        // スキップ・ステップ
        public const int DefaultSkipSeconds = 5;
        public const float DefaultStepSeconds = 0.1f;

        // ファイル
        public const int MaxRecentFiles = 50;

        // UI更新頻度
        public const int UIUpdateIntervalMs = 100;  // 100ms ごとに UI 更新
    }
}
