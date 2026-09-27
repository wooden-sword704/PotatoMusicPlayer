using System;
using System.IO;
using System.Diagnostics;
using Newtonsoft.Json;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// アプリケーション設定の保存・読み込みサービス
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsDirectory;
        private readonly string _settingsFilePath;
        private AppSettings _currentSettings;

        public SettingsService()
        {
            // %LOCALAPPDATA%\PotatoMusicPlayer\ 配下に設定ファイルを保存
            _settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PotatoMusicPlayer"
            );

            _settingsFilePath = Path.Combine(_settingsDirectory, "AppSettings.json");

            // ディレクトリが存在しなければ作成
            if (!Directory.Exists(_settingsDirectory))
                Directory.CreateDirectory(_settingsDirectory);

            // 設定を読み込む
            _currentSettings = LoadSettings();
        }

        /// <summary>
        /// 設定を読み込む
        /// </summary>
        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    settings?.EnsureDefaultHotKeys();
                    if (settings != null && settings.WaveformZoom == null)
                        settings.WaveformZoom = new WaveformZoomSettings();
                    // 旧設定ファイルには存在しない項目は既定値を適用する。
                    if (settings?.WaveformZoom != null && !json.Contains("\"ProgressiveWaveform\"", StringComparison.Ordinal))
                        settings.WaveformZoom.ProgressiveWaveform = true;
                    if (settings != null && !json.Contains("\"ShowSpectrum\"", StringComparison.Ordinal))
                        settings.ShowSpectrum = true;
                    
                    return settings ?? CreateDefaultSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            return CreateDefaultSettings();
        }

        /// <summary>
        /// 設定を保存
        /// </summary>
        public void SaveSettings(AppSettings settings)
        {
            try
            {
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsFilePath, json);
                _currentSettings = settings;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// 現在の設定を取得
        /// </summary>
        public AppSettings GetSettings()
        {
            if (_currentSettings.WaveformZoom == null)
                _currentSettings.WaveformZoom = new WaveformZoomSettings();
            return _currentSettings;
        }

        /// <summary>
        /// デフォルト設定を作成
        /// </summary>
        private AppSettings CreateDefaultSettings()
        {
            var settings = new AppSettings();
            settings.InitializeDefaultHotKeys();
            return settings;
        }

        /// <summary>
        /// 最近使ったファイルを追加
        /// </summary>
        public void AddRecentFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            // 既に存在する場合は削除
            _currentSettings.RecentFiles.Remove(filePath);

            // リストの最初に追加
            _currentSettings.RecentFiles.Insert(0, filePath);

            // 最大数を超えた分は削除
            while (_currentSettings.RecentFiles.Count > _currentSettings.MaxRecentFiles)
                _currentSettings.RecentFiles.RemoveAt(_currentSettings.RecentFiles.Count - 1);

            SaveSettings(_currentSettings);
        }

        /// <summary>
        /// 最近使ったファイルをクリア
        /// </summary>
        public void ClearRecentFiles()
        {
            _currentSettings.RecentFiles.Clear();
            SaveSettings(_currentSettings);
        }

        /// <summary>
        /// 設定ディレクトリを開く
        /// </summary>
        public void OpenSettingsDirectory()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _settingsDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open settings directory: {ex.Message}");
            }
        }

        public string SettingsDirectory => _settingsDirectory;
    }
}
