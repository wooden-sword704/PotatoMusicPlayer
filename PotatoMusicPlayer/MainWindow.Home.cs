using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PotatoMusicPlayer.Models;
using PotatoMusicPlayer.Services;

namespace PotatoMusicPlayer
{
    public partial class MainWindow : Window
    {
        // ========== ホーム画面(起動時・ファイル未選択時) ==========

        private bool _isHomeVisible = true;
        private bool _isHomeAnimating;

        private Duration HomeTransitionDuration() =>
            TryFindResource("MaterialDurationNormal") is Duration token
                ? token
                : new Duration(TimeSpan.FromMilliseconds(180));

        private void ShowHome(bool animate)
        {
            if (_isHomeVisible || HomeOverlay == null)
                return;

            _isHomeVisible = true;
            SetPlayerContentVisible(false);
            UpdateHomeRecentList();
            HomeOverlay.Visibility = Visibility.Visible;

            if (!animate || _isHomeAnimating)
            {
                HomeOverlay.Opacity = 1;
                if (HomeOverlaySlide != null)
                    HomeOverlaySlide.Y = 0;
                return;
            }

            _isHomeAnimating = true;
            HomeOverlay.Opacity = 0;
            var storyboard = new Storyboard();
            var fadeIn = new DoubleAnimation(0, 1, HomeTransitionDuration());
            Storyboard.SetTarget(fadeIn, HomeOverlay);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);
            if (HomeOverlaySlide != null)
            {
                var rise = new DoubleAnimation(12, 0, HomeTransitionDuration())
                {
                    EasingFunction = TryFindResource("MaterialEaseOut") as IEasingFunction
                };
                Storyboard.SetTarget(rise, HomeOverlaySlide);
                Storyboard.SetTargetProperty(rise, new PropertyPath(TranslateTransform.YProperty));
                storyboard.Children.Add(rise);
            }
            storyboard.Completed += (_, _) => _isHomeAnimating = false;
            storyboard.Begin();
        }

        private void ShowPlayer(bool animate)
        {
            if (!_isHomeVisible || HomeOverlay == null)
                return;

            _isHomeVisible = false;
            SetPlayerContentVisible(true);
            UpdatePlaylistPanes();

            if (!animate || _isHomeAnimating)
            {
                HomeOverlay.Visibility = Visibility.Collapsed;
                HomeOverlay.Opacity = 1;
                return;
            }

            _isHomeAnimating = true;
            var fadeOut = new DoubleAnimation(1, 0, HomeTransitionDuration());
            Storyboard.SetTarget(fadeOut, HomeOverlay);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            var storyboard = new Storyboard();
            storyboard.Children.Add(fadeOut);
            storyboard.Completed += (_, _) =>
            {
                HomeOverlay.Visibility = Visibility.Collapsed;
                HomeOverlay.Opacity = 1;
                _isHomeAnimating = false;
            };
            storyboard.Begin();
        }

        private async void HomeOpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (await PickAndPlayFileAsync())
                ShowPlayer(true);
        }

        private void HomeSettingsButton_Click(object sender, RoutedEventArgs e) =>
            _viewModel.OpenSettings();

        private async void HomeRecentFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                _viewModel.ClearPlaylist();
                await _viewModel.LoadAndPlayFileAsync(path);
                UpdateRecentFilesMenu();
                UpdateHomeRecentList();
                ShowPlayer(true);
            }
        }

        private void GoHome_Click(object sender, RoutedEventArgs e)
        {
            CloseSidebarAfterAction();
            // 再生中なら停止し、再生状態を破棄する
            _viewModel.Stop();
            _viewModel.UnloadCurrentFile();
            _lastRepeatCheckPosition = double.NaN;
            _viewModel.Settings.RepeatRangeEnabled = false;
            UpdateRepeatRangeUi();
            ShowHome(true);
        }

        private void HomePlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new Views.PlaylistWindow(PlayPlaylistAsync);
            window.Owner = this;
            window.ShowDialog();
        }

        private async Task PlayPlaylistAsync(string name, List<PlaylistEntry> entries, int startIndex)
        {
            await _viewModel.PlayPlaylistAsync(entries, startIndex, name);
            UpdateRecentFilesMenu();
            UpdateHomeRecentList();
            ShowPlayer(true);
        }

        /// <summary>
        /// プレイヤー本体(タイトルバー以外)の表示切替。ホーム表示中は下地だけ残す。
        /// プレイリスト枠は UpdatePlaylistPanes の管理なので触らない。
        /// </summary>
        private void SetPlayerContentVisible(bool visible)
        {
            if (MainContentGrid == null)
                return;

            foreach (UIElement child in MainContentGrid.Children)
            {
                if (child == HomeOverlay || child == PlaylistPanesContainer || Grid.GetRow(child) == 0)
                    continue;
                child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// ファイル選択ダイアログで選んだファイルを再生する。選択されれば true。
        /// </summary>
        private async Task<bool> PickAndPlayFileAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = FileService.GetFileDialogFilter(),
                Title = "音楽ファイルを開く"
            };

            if (dialog.ShowDialog() != true)
                return false;

            _viewModel.ClearPlaylist();
            await _viewModel.LoadAndPlayFileAsync(dialog.FileName);
            UpdateRecentFilesMenu();
            UpdateHomeRecentList();
            return true;
        }

        private void UpdateHomeRecentList()
        {
            try
            {
                if (HomeRecentFilesPanel == null || HomeRecentHeader == null)
                    return;

                HomeRecentFilesPanel.Children.Clear();

                var recent = _viewModel?.Settings?.RecentFiles;
                if (recent == null || recent.Count == 0)
                {
                    HomeRecentHeader.Visibility = Visibility.Collapsed;
                    return;
                }

                HomeRecentHeader.Visibility = Visibility.Visible;
                var buttonStyle = TryFindResource("SidebarMenuButtonStyle") as Style;
                int maxToShow = Math.Min(5, recent.Count);
                for (int i = 0; i < maxToShow; i++)
                {
                    string path = recent[i];
                    var btn = new Button
                    {
                        Content = System.IO.Path.GetFileName(path),
                        ToolTip = path,
                        Tag = path,
                        Style = buttonStyle,
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    btn.Click += HomeRecentFile_Click;
                    HomeRecentFilesPanel.Children.Add(btn);
                }
            }
            catch (Exception) { /* ignore UI update failures */ }
        }
    }
}
