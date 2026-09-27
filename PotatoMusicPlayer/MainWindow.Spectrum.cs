using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer
{
    public partial class MainWindow
    {
        // ========== スペクトラムアナライザ(実データ描画) ==========

        private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _spectrumBars.Clear();
            DrawSpectrum();
        }

        private void DrawSpectrum()
        {
            if (SpectrumCanvas == null || SpectrumContainer == null)
                return;

            if (_viewModel?.Settings?.ShowSpectrum != true ||
                SpectrumContainer.Visibility != Visibility.Visible ||
                SpectrumCanvas.ActualWidth <= 0 || SpectrumCanvas.ActualHeight <= 0)
                return;

            string path = _viewModel?.CurrentMediaFile?.FilePath;
            float[] levels = null;
            if (!string.IsNullOrEmpty(path))
            {
                var playbackState = _viewModel.PlaybackState;
                double position = _viewModel.PendingWaveformSeekPosition ??
                    (playbackState?.State == PlayState.Playing
                        ? GetInterpolatedPlaybackPosition(playbackState)
                        : playbackState?.CurrentPosition.TotalSeconds ?? 0);
                levels = _spectrumService?.GetLevels(path, position);
            }

#if DEBUG
            if (levels == null && !string.IsNullOrEmpty(path))
            {
                var error = _spectrumService?.LastError;
                if (!string.IsNullOrEmpty(error))
                    Debug.WriteLine($"[MainWindow] Spectrum error: {error}");
                else
                    Debug.WriteLine($"[MainWindow] Spectrum: levels=null, path={path}, playing={_viewModel?.PlaybackState?.State}");
            }
#endif

            EnsureSpectrumBars(Services.SpectrumService.BandCount);
            var brush = (Brush)FindResource("WaveformBrush");
            double slotWidth = SpectrumCanvas.ActualWidth / Services.SpectrumService.BandCount;
            double barWidth = Math.Max(1, slotWidth * 0.7);
            double maxHeight = Math.Max(1, SpectrumCanvas.ActualHeight - 4);

            for (int i = 0; i < _spectrumBars.Count; i++)
            {
                var bar = _spectrumBars[i];
                // 未解析時はフェイクを動かさず、低い平坦バーを出す。
                double height = levels != null && i < levels.Length
                    ? Math.Max(2, Math.Clamp(levels[i], 0, 1) * maxHeight)
                    : 2;
                bar.Fill = brush;
                bar.Width = barWidth;
                bar.Height = height;
                Canvas.SetLeft(bar, i * slotWidth + (slotWidth - barWidth) / 2);
                Canvas.SetTop(bar, SpectrumCanvas.ActualHeight - height);
            }
        }

        private void EnsureSpectrumBars(int count)
        {
            if (_spectrumBars.Count == count && SpectrumCanvas.Children.Count == count)
                return;

            SpectrumCanvas.Children.Clear();
            _spectrumBars.Clear();
            for (int i = 0; i < count; i++)
            {
                var bar = new Rectangle { IsHitTestVisible = false };
                SpectrumCanvas.Children.Add(bar);
                _spectrumBars.Add(bar);
            }
        }
    }
}
