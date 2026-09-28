using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer
{
    public partial class MainWindow
    {
        // ========== スペクトラムアナライザ(実データ描画・軽量版) ==========
        //
        // 解析レート(FFTホップ)は変えず、描画側だけを軽量化する:
        // - バーのジオメトリ・配置は SizeChanged 時に確定し、毎フレームは ScaleY の
        //   RenderTransform 更新のみ(RenderTransform はレイアウトを無効化しない)。
        // - ブラシ参照はキャッシュし、FindResource を毎フレーム呼ばない。
        // - CompositionTarget.Rendering 駆動でモニターの VSync に同期し、
        //   補間済み再生位置から分析フレーム間を線形補間して滑らかに動かす。
        // - 一時停止中で位置が変わらないフレームはスキップする。

        private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            CacheSpectrumMetrics();
            _spectrumBars.Clear();
            _lastSpectrumPosition = double.NaN;
            DrawSpectrum();
        }

        private void CacheSpectrumMetrics()
        {
            if (SpectrumCanvas == null)
                return;

            double width = SpectrumCanvas.ActualWidth;
            double height = SpectrumCanvas.ActualHeight;
            _spectrumMaxHeight = Math.Max(1, height);
            if (width > 0)
            {
                _spectrumSlotWidth = width / Services.SpectrumService.BandCount;
                _spectrumBarWidth = Math.Max(1, _spectrumSlotWidth * 0.7);
            }
        }

        private void SetSpectrumRendering(bool enabled)
        {
            if (_isSpectrumRenderingAttached == enabled)
                return;

            if (enabled)
                CompositionTarget.Rendering += SpectrumRendering;
            else
                CompositionTarget.Rendering -= SpectrumRendering;

            _isSpectrumRenderingAttached = enabled;
        }

        private void SpectrumRendering(object sender, EventArgs e)
        {
            // 意図的な非表示のときだけ切り離す。レイアウト前の一時的な
            // サイズ0では切り離さず、次フレームで再試行する。
            if (_viewModel?.Settings?.ShowSpectrum != true ||
                SpectrumContainer?.Visibility != Visibility.Visible)
            {
                SetSpectrumRendering(false);
                return;
            }
            if (SpectrumCanvas == null || SpectrumCanvas.ActualWidth <= 0 || SpectrumCanvas.ActualHeight <= 0)
                return;

            string path = _viewModel?.CurrentMediaFile?.FilePath;
            var playbackState = _viewModel?.PlaybackState;
            bool isPlaying = playbackState?.State == PlayState.Playing;
            double position = _viewModel.PendingWaveformSeekPosition ??
                (isPlaying
                    ? GetInterpolatedPlaybackPosition(playbackState)
                    : playbackState?.CurrentPosition.TotalSeconds ?? 0);

            // 一時停止中で位置もファイルも変わらなければ何も変わらないので描画を省く。
            if (!isPlaying && string.Equals(path, _lastSpectrumPath, StringComparison.OrdinalIgnoreCase) &&
                position == _lastSpectrumPosition)
                return;

            // ブラシ未取得・バー未生成の場合はフルパスで整えてから描く。
            if (_spectrumBrush == null || _spectrumBars.Count != Services.SpectrumService.BandCount)
            {
                DrawSpectrum();
                return;
            }

            DrawSpectrumCore(path, position);
        }

        private bool IsSpectrumVisible()
        {
            return _viewModel?.Settings?.ShowSpectrum == true &&
                SpectrumContainer != null && SpectrumContainer.Visibility == Visibility.Visible &&
                SpectrumCanvas != null && SpectrumCanvas.ActualWidth > 0 && SpectrumCanvas.ActualHeight > 0;
        }

        private void DrawSpectrum()
        {
            if (!IsSpectrumVisible())
                return;

            CacheSpectrumMetrics();
            _spectrumBrush ??= (Brush)FindResource("WaveformBrush");
            EnsureSpectrumBars(Services.SpectrumService.BandCount);

            string path = _viewModel?.CurrentMediaFile?.FilePath;
            var playbackState = _viewModel?.PlaybackState;
            double position = _viewModel.PendingWaveformSeekPosition ??
                (playbackState?.State == PlayState.Playing
                    ? GetInterpolatedPlaybackPosition(playbackState)
                    : playbackState?.CurrentPosition.TotalSeconds ?? 0);
            DrawSpectrumCore(path, position);
            // 可視のはずなのに描画駆動が外れている場合は付け直す。
            SetSpectrumRendering(true);
        }

        private void DrawSpectrumCore(string path, double position)
        {
            float[] levels = null;
            if (!string.IsNullOrEmpty(path))
                levels = _spectrumService?.GetLevels(path, position);

#if DEBUG
            if (levels == null && !string.IsNullOrEmpty(path))
            {
                var error = _spectrumService?.LastError;
                if (!string.IsNullOrEmpty(error))
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Spectrum error: {error}");
            }
#endif

            double idleScale = 2 / _spectrumMaxHeight;
            for (int i = 0; i < _spectrumBars.Count; i++)
            {
                var bar = _spectrumBars[i];
                // 未解析時はフェイクを動かさず、低い平坦バーを出す。
                double scale = levels != null && i < levels.Length
                    ? Math.Max(idleScale, Math.Clamp(levels[i], 0, 1))
                    : idleScale;
                if (bar.RenderTransform is ScaleTransform scaleTransform)
                    scaleTransform.ScaleY = scale;
            }

            _lastSpectrumPath = path;
            _lastSpectrumPosition = position;
        }

        private void EnsureSpectrumBars(int count)
        {
            if (_spectrumBars.Count == count && SpectrumCanvas.Children.Count == count)
            {
                // テーマ切替などでブラシが変わっている場合に備えて Fill を揃える。
                foreach (var existing in _spectrumBars)
                    existing.Fill = _spectrumBrush;
                return;
            }

            SpectrumCanvas.Children.Clear();
            _spectrumBars.Clear();
            for (int i = 0; i < count; i++)
            {
                var bar = new Rectangle
                {
                    IsHitTestVisible = false,
                    Fill = _spectrumBrush,
                    Width = _spectrumBarWidth,
                    Height = _spectrumMaxHeight,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = new ScaleTransform(1, 2 / _spectrumMaxHeight)
                };
                Canvas.SetLeft(bar, i * _spectrumSlotWidth + (_spectrumSlotWidth - _spectrumBarWidth) / 2);
                Canvas.SetTop(bar, 0);
                SpectrumCanvas.Children.Add(bar);
                _spectrumBars.Add(bar);
            }
        }
    }
}
