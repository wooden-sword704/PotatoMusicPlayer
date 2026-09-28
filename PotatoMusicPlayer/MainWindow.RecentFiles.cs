using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PotatoMusicPlayer
{
    public partial class MainWindow : Window
    {
        // Recent files handling (sidebar list)
        private void UpdateRecentFilesMenu()
        {
            try
            {
                if (SidebarRecentFilesPanel == null)
                    return;

                SidebarRecentFilesPanel.Children.Clear();

                var recent = _viewModel?.Settings?.RecentFiles;
                if (recent == null || recent.Count == 0)
                {
                    var none = new TextBlock
                    {
                        Text = "(なし)",
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                        Margin = new Thickness(12, 2, 12, 2),
                        FontSize = 11
                    };
                    SidebarRecentFilesPanel.Children.Add(none);
                    return;
                }

                var buttonStyle = TryFindResource("SidebarMenuButtonStyle") as Style;
                int maxToShow = Math.Min(10, recent.Count);
                for (int i = 0; i < maxToShow; i++)
                {
                    var path = recent[i];
                    var btn = new Button
                    {
                        Content = System.IO.Path.GetFileName(path),
                        ToolTip = path,
                        Tag = path,
                        Style = buttonStyle,
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    btn.Click += SidebarRecentFile_Click;
                    SidebarRecentFilesPanel.Children.Add(btn);
                }
            }
            catch (Exception) { /* ignore UI update failures */ }
        }

        private async void SidebarRecentFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                CloseSidebarAfterAction();
                _viewModel.ClearPlaylist();
                await _viewModel.LoadAndPlayFileAsync(path);
                UpdateRecentFilesMenu();
                UpdateHomeRecentList();
                ShowPlayer(true);
            }
        }

        private void ClearRecentFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsService = new PotatoMusicPlayer.Services.SettingsService();
                settingsService.ClearRecentFiles();

                // Also update in-memory settings accessed by viewmodel
                var s = _viewModel.Settings;
                s.RecentFiles.Clear();
                settingsService.SaveSettings(s);

                UpdateRecentFilesMenu();
                UpdateHomeRecentList();
            }
            catch (Exception) { }
        }
    }
}
