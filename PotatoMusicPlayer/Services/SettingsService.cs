using System;
using System.Collections.Generic;
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
        private static AppSettings _sharedSettings;

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

            // 設定を読み込む(全インスタンスで共有し、終了時の古い上書きを防ぐ)
            if (_sharedSettings == null)
                _sharedSettings = LoadSettings();
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
                    // 旧設定ファイルの enum 値・enum 名は言語コードへ読み替える。
                    if (settings != null)
                        settings.Language = LanguageService.NormalizeCode(settings.Language);
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
                _sharedSettings = settings;
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
            if (_sharedSettings.WaveformZoom == null)
                _sharedSettings.WaveformZoom = new WaveformZoomSettings();
            if (_sharedSettings.Playlists == null)
                _sharedSettings.Playlists = new List<StoredPlaylist>();
            return _sharedSettings;
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
            _sharedSettings.RecentFiles.Remove(filePath);

            // リストの最初に追加
            _sharedSettings.RecentFiles.Insert(0, filePath);

            // 最大数を超えた分は削除
            while (_sharedSettings.RecentFiles.Count > _sharedSettings.MaxRecentFiles)
                _sharedSettings.RecentFiles.RemoveAt(_sharedSettings.RecentFiles.Count - 1);

            SaveSettings(_sharedSettings);
        }

        /// <summary>
        /// 最近使ったファイルをクリア
        /// </summary>
        public void ClearRecentFiles()
        {
            _sharedSettings.RecentFiles.Clear();
            SaveSettings(_sharedSettings);
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
