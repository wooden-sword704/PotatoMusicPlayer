using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer
{
    public partial class MainWindow : Window
    {
        // ========== プレイリスト枠(左: 一覧 / 右: プロパティ) ==========

        private void UpdatePlaylistPanes()
        {
            if (PlaylistPanesContainer == null || PlaylistTrackList == null)
                return;

            var queue = _viewModel?.PlaylistQueue;
            if (queue == null || queue.Count == 0)
            {
                PlaylistPanesContainer.Visibility = Visibility.Collapsed;
                return;
            }

            PlaylistPanesContainer.Visibility = Visibility.Visible;
            if (PlaylistContinuousCheck != null)
            {
                bool continuous = _viewModel.Settings.PlaylistContinuous;
                if (PlaylistContinuousCheck.IsChecked != continuous)
                    PlaylistContinuousCheck.IsChecked = continuous;
            }
            PlaylistTrackList.ItemsSource = null;
            PlaylistTrackList.ItemsSource = queue;

            int index = _viewModel.PlaylistIndex;
            PlaylistTrackList.SelectedIndex = index >= 0 && index < queue.Count ? index : -1;
            ShowTrackProperties(PlaylistTrackList.SelectedItem as PlaylistEntry);
        }

        private void PlaylistTrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaylistTrackList == null)
                return;
            ShowTrackProperties(PlaylistTrackList.SelectedItem as PlaylistEntry);
        }

        private async void PlaylistTrackList_DoubleClick(object sender, RoutedEventArgs e)
        {
            if (PlaylistTrackList == null || PlaylistTrackList.SelectedIndex < 0)
                return;
            await _viewModel.PlayPlaylistTrackAsync(PlaylistTrackList.SelectedIndex);
        }

        private void PlaylistContinuousCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.Settings == null)
                return;
            _viewModel.Settings.PlaylistContinuous = PlaylistContinuousCheck?.IsChecked == true;
            new Services.SettingsService().SaveSettings(_viewModel.Settings);
        }

        private async void ShowTrackProperties(PlaylistEntry entry)
        {
            if (entry == null)
            {
                ClearTrackProperties();
                return;
            }

            string path = entry.FilePath;
            string title = entry.FileName;
            string bitrate = "―";
            string length = "―";
            string size = "―";

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    size = Services.PlaylistService.FormatBytes(new FileInfo(path).Length);
                }
                catch
                {
                    // サイズ取得に失敗した場合は ― のまま
                }

                var current = _viewModel?.CurrentMediaFile;
                if (current != null && string.Equals(current.FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(current.Title))
                        title = current.Title;
                    if (current.Duration > TimeSpan.Zero)
                        length = FormatTime(current.Duration);
                    if (current.Bitrate > 0)
                        bitrate = current.Bitrate + " kbps";
                }
                else
                {
                    try
                    {
                        var info = await Task.Run(() =>
                        {
                            using (var file = TagLib.File.Create(path))
                                return new
                                {
                                    Title = file.Tag.Title,
                                    Bitrate = file.Properties.AudioBitrate,
                                    Duration = file.Properties.Duration
                                };
                        });
                        // 選択が変わっていたら古い結果は捨てる
                        string selectedNow = (PlaylistTrackList.SelectedItem as PlaylistEntry)?.FilePath;
                        if (!string.Equals(selectedNow, path, StringComparison.OrdinalIgnoreCase))
                            return;
                        if (!string.IsNullOrEmpty(info.Title))
                            title = info.Title;
                        if (info.Duration > TimeSpan.Zero)
                            length = FormatTime(info.Duration);
                        if (info.Bitrate > 0)
                            bitrate = info.Bitrate + " kbps";
                    }
                    catch
                    {
                        // タグ読み取りに失敗した場合は分かる範囲だけ表示する
                    }
                }
            }

            PropTitleValue.Text = title;
            PropFileValue.Text = entry.FileName;
            PropSizeValue.Text = size;
            PropLengthValue.Text = length;
            PropAddedValue.Text = entry.AddedAt == DateTime.MinValue ? "―" : entry.AddedAt.ToString("g");
            PropLocationValue.Text = string.IsNullOrEmpty(path) ? "―" : (Path.GetDirectoryName(path) ?? "―");
            PropBitrateValue.Text = bitrate;
        }

        private void ClearTrackProperties()
        {
            if (PropTitleValue == null)
                return;
            PropTitleValue.Text = "";
            PropFileValue.Text = "";
            PropSizeValue.Text = "―";
            PropLengthValue.Text = "―";
            PropAddedValue.Text = "―";
            PropLocationValue.Text = "―";
            PropBitrateValue.Text = "―";
        }
    }
}
