using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PotatoMusicPlayer.Models;
using PotatoMusicPlayer.Services;

namespace PotatoMusicPlayer.Views
{
    public partial class PlaylistWindow : Window
    {
        private readonly Func<string, List<PlaylistEntry>, int, Task> _playCallback;
        private readonly SettingsService _settingsService;
        private readonly LanguageService _languageService;
        private List<StoredPlaylist> _playlists = new List<StoredPlaylist>();
        private readonly ObservableCollection<PlaylistEntry> _entries = new ObservableCollection<PlaylistEntry>();
        private int _editingIndex = -1;

        public PlaylistWindow(Func<string, List<PlaylistEntry>, int, Task> playCallback)
        {
            _playCallback = playCallback;
            _settingsService = new SettingsService();
            _languageService = new LanguageService(_settingsService.GetSettings().Language);
            LoadPlaylists();
            InitializeComponent();
            PlaylistListBox.ItemsSource = _entries;
            ApplyLanguage();
            ShowPanel(HubPanel);
            UpdateStatus("Playlist.Empty");
        }

        private void LoadPlaylists()
        {
            _playlists = (_settingsService.GetSettings().Playlists ?? new List<StoredPlaylist>())
                .Select(p => new StoredPlaylist
                {
                    Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString() : p.Id,
                    Name = p.Name ?? string.Empty,
                    Entries = (p.Entries ?? new List<PlaylistEntry>())
                        .Select(e => new PlaylistEntry { FileName = e.FileName, FilePath = e.FilePath, AddedAt = e.AddedAt })
                        .ToList()
                })
                .ToList();
        }

        private void PersistPlaylists()
        {
            var settings = _settingsService.GetSettings();
            settings.Playlists = _playlists
                .Select(p => new StoredPlaylist
                {
                    Id = p.Id,
                    Name = p.Name,
                    Entries = p.Entries
                        .Select(e => new PlaylistEntry { FileName = e.FileName, FilePath = e.FilePath, AddedAt = e.AddedAt })
                        .ToList()
                })
                .ToList();
            _settingsService.SaveSettings(settings);
        }

        private void ApplyLanguage()
        {
            Title = _languageService.Get("Playlist.Title");
            PlaylistTitleText.Text = _languageService.Get("Playlist.Title");
            HubPlayButton.Content = _languageService.Get("Playlist.HubPlay");
            HubNewButton.Content = _languageService.Get("Playlist.HubNew");
            HubManageButton.Content = _languageService.Get("Playlist.HubManage");
            PlaylistCloseButton.Content = _languageService.Get("Playlist.Close");
            PlaylistPlayButton.Content = _languageService.Get("Playlist.Play");
            PlayBackButton.Content = _languageService.Get("Playlist.Back");
            ManageEditButton.Content = _languageService.Get("Playlist.Edit");
            ManageDeleteButton.Content = _languageService.Get("Playlist.Remove");
            ManageBackButton.Content = _languageService.Get("Playlist.Back");
            PlaylistNameLabel.Text = _languageService.Get("Playlist.Name");
            PlaylistAddButton.Content = _languageService.Get("Playlist.Add");
            PlaylistRemoveButton.Content = _languageService.Get("Playlist.Remove");
            PlaylistMoveUpButton.Content = _languageService.Get("Playlist.MoveUp");
            PlaylistMoveDownButton.Content = _languageService.Get("Playlist.MoveDown");
            PlaylistClearButton.Content = _languageService.Get("Common.Clear");
            PlaylistPackAudioCheck.Content = _languageService.Get("Playlist.PackAudio");
            PlaylistExportButton.Content = _languageService.Get("Playlist.Export");
            PlaylistImportButton.Content = _languageService.Get("Playlist.Import");
            EditSaveButton.Content = _languageService.Get("Playlist.Save");
            EditBackButton.Content = _languageService.Get("Playlist.Back");
        }

        private void UpdateStatus(string key, params object[] args)
        {
            string format = _languageService.Get(key);
            PlaylistStatusText.Text = args.Length == 0 ? format : string.Format(format, args);
        }

        private void ShowPanel(Panel panel)
        {
            HubPanel.Visibility = panel == HubPanel ? Visibility.Visible : Visibility.Collapsed;
            PlayPanel.Visibility = panel == PlayPanel ? Visibility.Visible : Visibility.Collapsed;
            ManagePanel.Visibility = panel == ManagePanel ? Visibility.Visible : Visibility.Collapsed;
            EditPanel.Visibility = panel == EditPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        // ========== ハブ ==========

        private void HubPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlists.Count == 0)
            {
                UpdateStatus("Playlist.Empty");
                return;
            }
            RefreshPlaylistSelectors();
            ShowPanel(PlayPanel);
        }

        private void HubNewButton_Click(object sender, RoutedEventArgs e)
        {
            _editingIndex = -1;
            PlaylistNameBox.Text = _languageService.Get("Playlist.NewDefault");
            _entries.Clear();
            ShowPanel(EditPanel);
        }

        private void HubManageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlists.Count == 0)
            {
                UpdateStatus("Playlist.Empty");
                return;
            }
            RefreshPlaylistSelectors();
            ShowPanel(ManagePanel);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => ShowPanel(HubPanel);

        private void RefreshPlaylistSelectors()
        {
            ManagePlaylistBox.ItemsSource = null;
            ManagePlaylistBox.ItemsSource = _playlists;
            PlayPlaylistCombo.ItemsSource = null;
            PlayPlaylistCombo.ItemsSource = _playlists;
            if (_playlists.Count > 0)
            {
                ManagePlaylistBox.SelectedIndex = 0;
                PlayPlaylistCombo.SelectedIndex = 0;
            }
            else
            {
                PlayTrackListBox.ItemsSource = null;
            }
        }

        // ========== 再生 ==========

        private void PlayPlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayPlaylistCombo.SelectedItem is StoredPlaylist playlist)
                PlayTrackListBox.ItemsSource = playlist.Entries;
            else
                PlayTrackListBox.ItemsSource = null;
        }

        private async void PlaylistPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(PlayPlaylistCombo.SelectedItem is StoredPlaylist playlist))
            {
                UpdateStatus("Playlist.NoSelection");
                return;
            }
            string selectedPath = (PlayTrackListBox.SelectedItem as PlaylistEntry)?.FilePath;
            await PlayEntriesAsync(playlist, selectedPath);
        }

        private async void PlayTrackListBox_DoubleClick(object sender, RoutedEventArgs e)
        {
            if (PlayPlaylistCombo.SelectedItem is StoredPlaylist playlist &&
                PlayTrackListBox.SelectedItem is PlaylistEntry selected)
                await PlayEntriesAsync(playlist, selected.FilePath);
        }

        private async Task PlayEntriesAsync(StoredPlaylist playlist, string selectedPath)
        {
            var entries = playlist.Entries
                .Where(x => x != null && !string.IsNullOrEmpty(x.FilePath) && File.Exists(x.FilePath))
                .Select(x => new PlaylistEntry { FileName = x.FileName, FilePath = x.FilePath, AddedAt = x.AddedAt })
                .ToList();
            if (entries.Count == 0)
            {
                UpdateStatus("Playlist.FileNotFound");
                return;
            }
            int startIndex = entries.FindIndex(x =>
                string.Equals(x.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
                startIndex = 0;
            if (_playCallback != null)
                await _playCallback(playlist.Name, entries, startIndex);
            Close();
        }

        // ========== 管理 ==========

        private void ManageEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(ManagePlaylistBox.SelectedItem is StoredPlaylist playlist))
            {
                UpdateStatus("Playlist.NoSelection");
                return;
            }
            _editingIndex = _playlists.IndexOf(playlist);
            PlaylistNameBox.Text = playlist.Name;
            _entries.Clear();
            foreach (var entry in playlist.Entries)
                _entries.Add(new PlaylistEntry { FileName = entry.FileName, FilePath = entry.FilePath, AddedAt = entry.AddedAt });
            ShowPanel(EditPanel);
        }

        private void ManageDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(ManagePlaylistBox.SelectedItem is StoredPlaylist playlist))
            {
                UpdateStatus("Playlist.NoSelection");
                return;
            }
            _playlists.Remove(playlist);
            PersistPlaylists();
            RefreshPlaylistSelectors();
        }

        // ========== 編集 ==========

        private void EditSaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = (PlaylistNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                name = _languageService.Get("Playlist.NewDefault");

            var entries = _entries
                .Select(x => new PlaylistEntry { FileName = x.FileName, FilePath = x.FilePath, AddedAt = x.AddedAt })
                .ToList();
            if (_editingIndex >= 0 && _editingIndex < _playlists.Count)
            {
                _playlists[_editingIndex].Name = name;
                _playlists[_editingIndex].Entries = entries;
            }
            else
            {
                _playlists.Add(new StoredPlaylist { Name = name, Entries = entries });
                _editingIndex = _playlists.Count - 1;
            }
            PersistPlaylists();
            RefreshPlaylistSelectors();
            UpdateStatus("Playlist.Saved");
        }

        private void PlaylistAddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = FileService.GetFileDialogFilter(),
                Title = _languageService.Get("Playlist.Add"),
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            int added = 0;
            foreach (string fileName in dialog.FileNames)
            {
                if (!FileService.IsSupportedFormat(fileName))
                    continue;
                _entries.Add(new PlaylistEntry
                {
                    FileName = Path.GetFileName(fileName),
                    FilePath = fileName,
                    AddedAt = DateTime.Now
                });
                added++;
            }
            UpdateStatus("Playlist.ImportDone", added);
        }

        private void PlaylistRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistListBox.SelectedItem is PlaylistEntry entry)
                _entries.Remove(entry);
        }

        private void MoveSelected(int offset)
        {
            if (!(PlaylistListBox.SelectedItem is PlaylistEntry entry))
                return;
            int index = _entries.IndexOf(entry);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= _entries.Count)
                return;
            _entries.Move(index, target);
            PlaylistListBox.SelectedItem = entry;
        }

        private void PlaylistMoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void PlaylistMoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void PlaylistClearButton_Click(object sender, RoutedEventArgs e) => _entries.Clear();

        private void PlaylistListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaylistListBox.SelectedItem is PlaylistEntry entry && !string.IsNullOrEmpty(entry.FilePath))
                PlaylistStatusText.Text = entry.FilePath;
        }

        private void PlaylistExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_entries.Count == 0)
            {
                UpdateStatus("Playlist.Empty");
                return;
            }

            bool packAudio = PlaylistPackAudioCheck.IsChecked == true;
            if (packAudio)
            {
                long totalBytes = PlaylistService.EstimatePackSize(_entries);
                if (totalBytes > PlaylistService.PackSizeWarningThresholdBytes)
                {
                    var answer = MessageBox.Show(
                        string.Format(_languageService.Get("Playlist.LargeMessage"),
                            PlaylistService.FormatBytes(totalBytes)),
                        _languageService.Get("Playlist.LargeTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes)
                        return;
                }
            }

            var dialog = new SaveFileDialog
            {
                Filter = "ZIP アーカイブ (*.zip)|*.zip",
                FileName = "playlist.zip",
                Title = _languageService.Get("Playlist.Export")
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                string name = (PlaylistNameBox.Text ?? string.Empty).Trim();
                var result = PlaylistService.Export(dialog.FileName, name, _entries, packAudio);
                if (result.SkippedCount > 0)
                    UpdateStatus("Playlist.Skipped", result.SkippedCount);
                else
                    UpdateStatus("Playlist.ExportDone", dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, _languageService.Get("Playlist.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaylistImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "ZIP アーカイブ (*.zip)|*.zip",
                Title = _languageService.Get("Playlist.Import")
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var result = PlaylistService.Import(dialog.FileName);
                foreach (var entry in result.Entries)
                    _entries.Add(entry);
                UpdateStatus("Playlist.ImportDone", result.Entries.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show(_languageService.Get("Playlist.InvalidArchive") + "\n" + ex.Message,
                    _languageService.Get("Playlist.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaylistCloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
