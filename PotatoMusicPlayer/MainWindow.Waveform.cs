using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer
{
    public partial class MainWindow
    {
        // ========== 波形表示・シーク ==========

        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawWaveform();
        }

        private void MinimapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawMinimap();
        }

        private void DrawWaveform()
        {
            WaveformCanvas.Children.Clear();
            _waveformBars.Clear();
            _playbackCursorLine = null;
            _centerGuideLine = null;
            _hasDrawnWaveformRange = false;

            if (_waveformData == null || _waveformData.Length == 0 ||
                WaveformCanvas.ActualWidth <= 0 || WaveformCanvas.ActualHeight <= 0)
                return;

            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.TotalDuration <= 0 || zoomState.VisibleRangeDuration <= 0)
                return;

            _drawnWaveformRangeStart = zoomState.VisibleRangeStart;
            _drawnWaveformRangeDuration = zoomState.VisibleRangeDuration;
            _hasDrawnWaveformRange = true;

            // 両側に表示幅の25%を先読みし、フレーム間の平行移動でも端の波形を欠かさない。
            const double cacheScale = 1.5;
            double cacheStart = zoomState.VisibleRangeStart - zoomState.VisibleRangeDuration * 0.25;
            double cacheEnd = zoomState.VisibleRangeEnd + zoomState.VisibleRangeDuration * 0.25;
            double initialLeft = -WaveformCanvas.ActualWidth * 0.25;
            // 画面幅に合わせてデータをピーク値でまとめる。バー本体は 1～4px に保つ。
            int horizontalDetail = Math.Clamp(_viewModel.Settings.WaveformZoom.HorizontalDetail, 0, 100);
            double detailScale = 0.1 + horizontalDetail / 100.0 * 0.9;
            int visibleBars = Math.Min(_waveformData.Length,
                Math.Max(1, (int)(WaveformCanvas.ActualWidth / 2 * detailScale * cacheScale)));
            int rangeStart = (int)Math.Floor(cacheStart / zoomState.TotalDuration * _waveformData.Length);
            int rangeEnd = (int)Math.Ceiling(cacheEnd / zoomState.TotalDuration * _waveformData.Length);
            // 範囲外（ドラッグによるはみ出し）は空白として描画するため、ここでは丸めない。
            int rangeLength = Math.Max(1, rangeEnd - rangeStart);
            double slotWidth = WaveformCanvas.ActualWidth * cacheScale / visibleBars;
            double barWidth = Math.Clamp(slotWidth * 0.75, 1.0, 4.0);
            double availableHeight = Math.Max(1, WaveformCanvas.ActualHeight - 4);
            var waveformBrush = (Brush)FindResource("WaveformBrush");

            for (int bar = 0; bar < visibleBars; bar++)
            {
                int start = rangeStart + bar * rangeLength / visibleBars;
                int end = Math.Max(start + 1, rangeStart + (bar + 1) * rangeLength / visibleBars);
                if (end <= 0 || start >= _waveformData.Length)
                    continue;
                float peak = 0;

                int clampedStart = Math.Max(start, 0);
                for (int sample = clampedStart; sample < end && sample < _waveformData.Length; sample++)
                    peak = Math.Max(peak, _waveformData[sample]);

                int verticalDetail = Math.Clamp(_viewModel.Settings.WaveformZoom.VerticalDetail, 0, 100);
                if (verticalDetail == 0)
                    peak = peak > 0.01f ? 1f : 0f;
                else
                {
                    int levels = verticalDetail + 1;
                    peak = (float)(Math.Ceiling(peak * levels) / levels);
                }

                // 振幅の中心を波形ボックス中央に置き、上下へ均等に伸ばす。
                double height = Math.Max(1, peak * availableHeight);
                var rectangle = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = waveformBrush,
                    IsHitTestVisible = false,
                    RenderTransform = new TranslateTransform()
                };

                Canvas.SetLeft(rectangle, initialLeft + bar * slotWidth + (slotWidth - barWidth) / 2);
                Canvas.SetTop(rectangle, (WaveformCanvas.ActualHeight - height) / 2);
                WaveformCanvas.Children.Add(rectangle);
                _waveformBars.Add(rectangle);
            }

            var state = _viewModel?.PlaybackState;
            if (_viewModel?.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                DrawPlaybackCursorAtCenter();
            else if (state != null)
                DrawPlaybackCursor(TimeSpan.FromSeconds(_minimapCarriedPosition ??
                    _viewModel.PendingWaveformSeekPosition ?? state.CurrentPosition.TotalSeconds));
        }

        private void RefreshWaveformRange()
        {
            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.VisibleRangeDuration <= 0)
                return;

            if (!_hasDrawnWaveformRange ||
                Math.Abs(zoomState.VisibleRangeDuration - _drawnWaveformRangeDuration) > 0.0001 ||
                Math.Abs(zoomState.VisibleRangeStart - _drawnWaveformRangeStart) >
                    zoomState.VisibleRangeDuration * 0.2)
                DrawWaveform();

            if (_hasDrawnWaveformRange)
            {
                double offset = (_drawnWaveformRangeStart - zoomState.VisibleRangeStart) /
                    zoomState.VisibleRangeDuration * WaveformCanvas.ActualWidth;
                ApplyWaveformHorizontalOffset(offset);
            }
        }

        private void DrawPlaybackCursor(TimeSpan position)
        {
            if (_waveformData == null || _waveformData.Length == 0 ||
                WaveformCanvas.ActualWidth <= 0)
                return;

            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.VisibleRangeDuration <= 0)
                return;

            double positionSeconds = position.TotalSeconds;
            if (positionSeconds < zoomState.VisibleRangeStart || positionSeconds > zoomState.VisibleRangeEnd)
            {
                if (_playbackCursorLine != null)
                    _playbackCursorLine.Visibility = Visibility.Collapsed;
                return;
            }

            double ratio = Math.Clamp((positionSeconds - zoomState.VisibleRangeStart) / zoomState.VisibleRangeDuration, 0, 1);
            double cursorX = ratio * WaveformCanvas.ActualWidth;
            if (_playbackCursorLine == null)
            {
                _playbackCursorLine = new Line
                {
                    Y1 = 0,
                    Stroke = (Brush)FindResource("PlaybackCursorBrush"),
                    StrokeThickness = 1,
                    Tag = "PlaybackCursor",
                    IsHitTestVisible = false
                };
                WaveformCanvas.Children.Add(_playbackCursorLine);
            }

            _playbackCursorLine.X1 = cursorX;
            _playbackCursorLine.X2 = cursorX;
            _playbackCursorLine.Y2 = WaveformCanvas.ActualHeight;
            _playbackCursorLine.Visibility = Visibility.Visible;
        }

        private void ApplyWaveformHorizontalOffset(double offset)
        {
            foreach (var bar in _waveformBars)
            {
                if (bar.RenderTransform is TranslateTransform translate)
                    translate.X = offset;
            }
        }

        private void DrawMinimap()
        {
            if (MinimapCanvas == null)
                return;

            if (_waveformData == null || _waveformData.Length == 0 ||
                MinimapCanvas.ActualWidth <= 0 || MinimapCanvas.ActualHeight <= 0)
            {
                MinimapCanvas.Children.Clear();
                _minimapWaveformCache = null;
                _minimapCursorLine = null;
                _minimapRangeOverlay = null;
                _minimapLeftOutOfBoundsIndicator = null;
                _minimapRightOutOfBoundsIndicator = null;
                _minimapLeftHandle = null;
                _minimapRightHandle = null;
                return;
            }

            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.TotalDuration <= 0)
                return;

            bool rebuildWaveform = !ReferenceEquals(_minimapWaveformCache, _waveformData) ||
                _minimapCachedWidth != MinimapCanvas.ActualWidth || _minimapCachedHeight != MinimapCanvas.ActualHeight;
            if (rebuildWaveform)
            {
                MinimapCanvas.Children.Clear();
                _minimapCursorLine = null;
                _minimapRangeOverlay = null;
                _minimapLeftOutOfBoundsIndicator = null;
                _minimapRightOutOfBoundsIndicator = null;
                _minimapLeftHandle = null;
                _minimapRightHandle = null;
                int visibleBars = Math.Min(_waveformData.Length,
                    Math.Max(1, (int)(MinimapCanvas.ActualWidth / 2)));
                double slotWidth = MinimapCanvas.ActualWidth / visibleBars;
                var waveformBrush = (Brush)FindResource("MinimapWaveformBrush");

                for (int bar = 0; bar < visibleBars; bar++)
                {
                    int start = bar * _waveformData.Length / visibleBars;
                    int end = Math.Max(start + 1, (bar + 1) * _waveformData.Length / visibleBars);
                    float peak = 0;
                    for (int sample = start; sample < end && sample < _waveformData.Length; sample++)
                        peak = Math.Max(peak, _waveformData[sample]);

                    double height = Math.Max(1, peak * Math.Max(1, MinimapCanvas.ActualHeight - 2));
                    var rectangle = new Rectangle
                    {
                        Width = Math.Max(1, slotWidth),
                        Height = height,
                        Fill = waveformBrush,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(rectangle, bar * slotWidth);
                    Canvas.SetTop(rectangle, (MinimapCanvas.ActualHeight - height) / 2);
                    MinimapCanvas.Children.Add(rectangle);
                }

                _minimapWaveformCache = _waveformData;
                _minimapCachedWidth = MinimapCanvas.ActualWidth;
                _minimapCachedHeight = MinimapCanvas.ActualHeight;
            }
            // 共有の範囲UIはモードで束縛先が変わる。
            // ビューモード: 表示範囲(緑) / リピートモード: リピート範囲(青)。
            // リピート範囲はビュー範囲とは別に設定へ保持する。
            double widgetStart;
            double widgetEnd;
            bool showWidget;
            if (_isRepeatEditMode)
            {
                var repeatSettings = _viewModel.Settings;
                widgetStart = Math.Clamp(repeatSettings.RepeatRangeStart, 0, zoomState.TotalDuration);
                widgetEnd = Math.Clamp(repeatSettings.RepeatRangeEnd, 0, zoomState.TotalDuration);
                showWidget = repeatSettings.RepeatRangeEnabled && widgetEnd - widgetStart >= 0.1;
            }
            else
            {
                var (clippedStart, clippedEnd) = _viewModel.GetMinimapVisibleRange();
                widgetStart = clippedStart;
                widgetEnd = clippedEnd;
                showWidget = true;
            }
            double left = widgetStart / zoomState.TotalDuration * MinimapCanvas.ActualWidth;
            double width = (widgetEnd - widgetStart) / zoomState.TotalDuration * MinimapCanvas.ActualWidth;
            if (_minimapRangeOverlay == null)
            {
                _minimapRangeOverlay = new Rectangle
                {
                    StrokeThickness = 1,
                    Tag = "RangeOverlay",
                    Cursor = Cursors.SizeAll
                };
                Canvas.SetTop(_minimapRangeOverlay, 0);
                MinimapCanvas.Children.Add(_minimapRangeOverlay);
            }
            ApplyMinimapOverlayColors();
            if (!showWidget)
            {
                _minimapRangeOverlay.Visibility = Visibility.Collapsed;
                if (_minimapLeftHandle != null)
                    _minimapLeftHandle.Visibility = Visibility.Collapsed;
                if (_minimapRightHandle != null)
                    _minimapRightHandle.Visibility = Visibility.Collapsed;
                UpdateMinimapOutOfBoundsIndicator(ref _minimapLeftOutOfBoundsIndicator, false, true, left);
                UpdateMinimapOutOfBoundsIndicator(ref _minimapRightOutOfBoundsIndicator, false, false, left + width);
                UpdateMinimapCursor();
                return;
            }
            _minimapRangeOverlay.Width = Math.Max(1, width);
            _minimapRangeOverlay.Height = MinimapCanvas.ActualHeight;
            Canvas.SetLeft(_minimapRangeOverlay, left);
            _minimapRangeOverlay.Visibility = Visibility.Visible;

            UpdateMinimapHandle(ref _minimapLeftHandle, "LeftHandle", left);
            UpdateMinimapHandle(ref _minimapRightHandle, "RightHandle", Math.Clamp(left + _minimapRangeOverlay.Width - 8, 0,
                Math.Max(0, MinimapCanvas.ActualWidth - 8)));
            _minimapLeftHandle.Visibility = Visibility.Visible;
            _minimapRightHandle.Visibility = Visibility.Visible;
            // はみ出し表示はビュー範囲の概念なので、リピートモードでは出さない。
            bool extendsPastLeft = !_isRepeatEditMode && zoomState.VisibleRangeStart < 0;
            bool extendsPastRight = !_isRepeatEditMode && zoomState.VisibleRangeEnd > zoomState.TotalDuration;
            _minimapLeftHandle.Opacity = extendsPastLeft ? 0.45 : 1;
            _minimapRightHandle.Opacity = extendsPastRight ? 0.45 : 1;
            UpdateMinimapOutOfBoundsIndicator(ref _minimapLeftOutOfBoundsIndicator,
                extendsPastLeft, true, left);
            UpdateMinimapOutOfBoundsIndicator(ref _minimapRightOutOfBoundsIndicator,
                extendsPastRight, false, left + width);
            UpdateMinimapCursor();
        }

        private double? _minimapCarriedPosition;
        private double _minimapCarryBasePosition;
        private Polygon _minimapLeftOutOfBoundsIndicator;
        private Polygon _minimapRightOutOfBoundsIndicator;

        private void UpdateMinimapOutOfBoundsIndicator(ref Polygon indicator, bool isVisible,
            bool onLeft, double edgeX)
        {
            if (!isVisible)
            {
                if (indicator != null)
                    indicator.Visibility = Visibility.Collapsed;
                return;
            }

            if (indicator == null)
            {
                indicator = new Polygon
                {
                    Fill = (Brush)FindResource("RangeOverlayBorderBrush"),
                    Stroke = (Brush)FindResource("PlaybackCursorBrush"),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                    Tag = onLeft ? "LeftRangeOutOfBounds" : "RightRangeOutOfBounds"
                };
                MinimapCanvas.Children.Add(indicator);
            }

            const double markerWidth = 12;
            double markerHeight = Math.Min(12, Math.Max(6, MinimapCanvas.ActualHeight - 4));
            indicator.Points = onLeft
                ? new PointCollection
                {
                    new Point(0, markerHeight / 2),
                    new Point(markerWidth, 0),
                    new Point(markerWidth, markerHeight)
                }
                : new PointCollection
                {
                    new Point(markerWidth, markerHeight / 2),
                    new Point(0, 0),
                    new Point(0, markerHeight)
                };
            indicator.Visibility = Visibility.Visible;
            Canvas.SetLeft(indicator, Math.Clamp(onLeft ? edgeX : edgeX - markerWidth,
                0, Math.Max(0, MinimapCanvas.ActualWidth - markerWidth)));
            Canvas.SetTop(indicator, (MinimapCanvas.ActualHeight - markerHeight) / 2);
        }

        /// <summary>
        /// ミニマップ上に再生位置マーカーを描く。再生位置とは常時同期する。
        /// </summary>
        private void UpdateMinimapCursor(double? displayedPosition = null)
        {
            if (MinimapCanvas == null)
                return;

            var zoomState = _viewModel?.ZoomState;
            // ドラッグ中は確定前の位置を先行表示する。
            double position = _minimapCarriedPosition ??
                displayedPosition ?? _viewModel?.PendingWaveformSeekPosition ??
                _viewModel?.PlaybackState?.CurrentPosition.TotalSeconds ?? double.NaN;
            if (zoomState == null || zoomState.TotalDuration <= 0 || double.IsNaN(position) ||
                position < 0 || position > zoomState.TotalDuration ||
                MinimapCanvas.ActualWidth <= 0 || MinimapCanvas.ActualHeight <= 0)
            {
                if (_minimapCursorLine != null)
                    _minimapCursorLine.Visibility = Visibility.Collapsed;
                return;
            }

            double cursorX = position / zoomState.TotalDuration * MinimapCanvas.ActualWidth;
            if (_minimapCursorLine == null)
            {
                _minimapCursorLine = new Line
                {
                    Y1 = 0,
                    Stroke = (Brush)FindResource("PlaybackCursorBrush"),
                    StrokeThickness = 1,
                    Tag = "MinimapCursor",
                    IsHitTestVisible = false
                };
                MinimapCanvas.Children.Add(_minimapCursorLine);
            }
            _minimapCursorLine.X1 = cursorX;
            _minimapCursorLine.X2 = cursorX;
            _minimapCursorLine.Y2 = MinimapCanvas.ActualHeight;
            _minimapCursorLine.Visibility = Visibility.Visible;
        }

        private void UpdateMinimapHandle(ref Rectangle handle, string name, double left)
        {
            if (handle == null)
            {
                handle = new Rectangle
                {
                    Width = 8,
                    Fill = (Brush)FindResource("RangeOverlayBorderBrush"),
                    Tag = name,
                    Cursor = Cursors.SizeWE
                };
                MinimapCanvas.Children.Add(handle);
            }
            handle.Height = MinimapCanvas.ActualHeight;
            Canvas.SetLeft(handle, left);
        }

        private void MinimapCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var zoomState = _viewModel.ZoomState;
            if (zoomState == null || zoomState.TotalDuration <= 0 || MinimapCanvas.ActualWidth <= 0)
                return;

            double x = Math.Clamp(e.GetPosition(MinimapCanvas).X, 0, MinimapCanvas.ActualWidth);
            double clickedTime = x / MinimapCanvas.ActualWidth * zoomState.TotalDuration;
            string hitName = (e.OriginalSource as FrameworkElement)?.Tag as string;

            // リピートモードでは共有の範囲UIでリピート範囲を編集する(ビュー範囲は不変)。
            if (_isRepeatEditMode)
            {
                _minimapFreeViewDrag = false;
                if (hitName == "LeftHandle")
                {
                    _isZoomingFromRightHandle = false;
                    _minimapDragMode = MinimapDragMode.ResizeStart;
                }
                else if (hitName == "RightHandle")
                {
                    _isZoomingFromRightHandle = true;
                    _minimapDragMode = MinimapDragMode.ResizeEnd;
                }
                else if (hitName == "RangeOverlay")
                {
                    _minimapDragMode = MinimapDragMode.MoveRange;
                }
                else
                {
                    _minimapDragMode = MinimapDragMode.SelectRepeat;
                    _repeatSelectAnchor = clickedTime;
                }
                var repeatSettings = _viewModel.Settings;
                _minimapDragStartX = x;
                _minimapDragStartTime = clickedTime;
                _minimapInitialRangeStart = Math.Clamp(repeatSettings.RepeatRangeStart, 0, zoomState.TotalDuration);
                _minimapInitialRangeEnd = Math.Clamp(repeatSettings.RepeatRangeEnd, 0, zoomState.TotalDuration);
                _minimapDragStarted = false;
                // リピート編集は再生位置・再生状態に触れない。
                MinimapCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // ビュー自由化時: 緑の範囲部分だけがビュー移動、それ以外は再生バーの移動。
            // 再生バーと重なっている箇所は再生バーを優先する。
            _minimapFreeViewDrag = false;
            // 中央固定では片側ハンドルも両側ズームとして扱う。
            bool centerFixedZoom = _viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed;
            bool symmetricZoom = (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.None || centerFixedZoom;
            if (_viewModel.Settings.WaveformZoom.FreeView)
            {
                // ハンドルはビュー範囲の変更(シークなし)を維持する。
                if (hitName == "LeftHandle")
                {
                    _isZoomingFromRightHandle = false;
                    _minimapDragMode = symmetricZoom ? MinimapDragMode.ZoomAroundCenter : MinimapDragMode.ResizeStart;
                }
                else if (hitName == "RightHandle")
                {
                    _isZoomingFromRightHandle = true;
                    _minimapDragMode = symmetricZoom ? MinimapDragMode.ZoomAroundCenter : MinimapDragMode.ResizeEnd;
                }
                else
                {
                    double displayedPosition = _viewModel.PendingWaveformSeekPosition ??
                        _viewModel.PlaybackState?.CurrentPosition.TotalSeconds ?? 0;
                    double cursorX = displayedPosition / zoomState.TotalDuration * MinimapCanvas.ActualWidth;
                    if (Math.Abs(x - cursorX) <= 6 || hitName != "RangeOverlay")
                        _minimapDragMode = MinimapDragMode.PointToRange;
                    else
                        _minimapDragMode = MinimapDragMode.MoveRange;
                }
                _minimapFreeViewDrag = true;
            }
            else if (hitName == "LeftHandle")
            {
                _isZoomingFromRightHandle = false;
                _minimapDragMode = symmetricZoom ? MinimapDragMode.ZoomAroundCenter : MinimapDragMode.ResizeStart;
            }
            else if (hitName == "RightHandle")
            {
                _isZoomingFromRightHandle = true;
                _minimapDragMode = symmetricZoom ? MinimapDragMode.ZoomAroundCenter : MinimapDragMode.ResizeEnd;
            }
            else if (hitName == "RangeOverlay")
                _minimapDragMode = MinimapDragMode.MoveRange;
            else
                _minimapDragMode = MinimapDragMode.PointToRange;

            _minimapDragStartX = x;
            _minimapDragStartTime = clickedTime;
            _minimapInitialRangeStart = zoomState.VisibleRangeStart;
            _minimapInitialRangeEnd = zoomState.VisibleRangeEnd;
            _minimapCarriedPosition = null;
            _minimapCarryBasePosition = _viewModel.PlaybackState?.CurrentPosition.TotalSeconds ?? 0;
            _minimapInitialPosition = _minimapCarryBasePosition;
            _minimapPreviousExcess = 0;
            _minimapDragStarted = _minimapDragMode == MinimapDragMode.PointToRange;
            _wasPlayingBeforeMinimapDrag = _viewModel.PlaybackState?.State == PlayState.Playing;
            _viewModel.BeginManualWaveformNavigation();
            // 範囲移動と枠外からの位置指定は解放時に一度だけシークする。押下時に一時停止すると、
            // すばやいドラッグで Pause と Play が競合し、再生が停止したままになる。
            // ビュー自由化のビュー移動では再生を止めない。
            _resumePlaybackAfterMinimapDrag = _minimapDragMode != MinimapDragMode.MoveRange &&
                _minimapDragMode != MinimapDragMode.PointToRange &&
                !_minimapFreeViewDrag &&
                PauseForZoomedDrag(zoomState);
            MinimapCanvas.CaptureMouse();
            if (_minimapDragMode == MinimapDragMode.PointToRange)
                PreviewMinimapPointToRange(clickedTime);
            else if (centerFixedZoom && !_minimapFreeViewDrag)
                DrawPlaybackCursorAtCenter();
            e.Handled = true;
        }

        private void PreviewMinimapPointToRange(double position)
        {
            var zoomState = _viewModel.ZoomState;
            if (zoomState == null)
                return;

            _minimapCarriedPosition = Math.Clamp(position, 0, zoomState.TotalDuration);
            // ビュー自由化の再生バー移動ではビュー範囲に触れない。
            if (_minimapFreeViewDrag)
            {
                DrawPlaybackCursor(TimeSpan.FromSeconds(_minimapCarriedPosition.Value));
                UpdateMinimapCursor();
                return;
            }
            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
            {
                _viewModel.CenterWaveformRangeOnPosition(_minimapCarriedPosition.Value);
                DrawPlaybackCursorAtCenter();
            }
            else
            {
                _viewModel.SetWaveformRangeCentered(_minimapCarriedPosition.Value, zoomState.CurrentZoomLevel);
                DrawPlaybackCursor(TimeSpan.FromSeconds(_minimapCarriedPosition.Value));
            }
            UpdateMinimapCursor();
        }

        private void MinimapCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_minimapDragMode == MinimapDragMode.None || e.LeftButton != MouseButtonState.Pressed)
                return;

            var zoomState = _viewModel.ZoomState;
            if (zoomState == null || MinimapCanvas.ActualWidth <= 0)
                return;

            // リピート範囲の選択中はプレビュー表示だけを更新する。
            if (_minimapDragMode == MinimapDragMode.SelectRepeat)
            {
                double selectX = Math.Clamp(e.GetPosition(MinimapCanvas).X, 0, MinimapCanvas.ActualWidth);
                if (!_minimapDragStarted)
                {
                    if (Math.Abs(selectX - _minimapDragStartX) < SystemParameters.MinimumHorizontalDragDistance)
                        return;
                    _minimapDragStarted = true;
                }
                double currentTime = selectX / MinimapCanvas.ActualWidth * zoomState.TotalDuration;
                PreviewRepeatSelection(_repeatSelectAnchor, currentTime);
                return;
            }

            // 中央固定の範囲移動は、マウスがミニマップ外へ出ても曲端が中央へ来るまで続ける。
            double pointerX = e.GetPosition(MinimapCanvas).X;
            double x = _minimapDragMode == MinimapDragMode.MoveRange &&
                _viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed
                    ? pointerX : Math.Clamp(pointerX, 0, MinimapCanvas.ActualWidth);
            if (!_minimapDragStarted)
            {
                if (Math.Abs(x - _minimapDragStartX) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                _minimapDragStarted = true;
            }
            double time = x / MinimapCanvas.ActualWidth * zoomState.TotalDuration;
            double deltaTime = time - _minimapDragStartTime;
            double initialWidth = _minimapInitialRangeEnd - _minimapInitialRangeStart;

            switch (_minimapDragMode)
            {
                case MinimapDragMode.PointToRange:
                    PreviewMinimapPointToRange(time);
                    break;
                case MinimapDragMode.MoveRange:
                    // ビュー自由化のビュー移動では再生バーをずらさない。
                    if (_minimapFreeViewDrag)
                    {
                        double desiredViewStart = _minimapInitialRangeStart + deltaTime;
                        if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                            _viewModel.PanWaveformRange(desiredViewStart);
                        else
                            _viewModel.MoveWaveformRange(desiredViewStart);
                        RefreshWaveformRange();
                        UpdateMinimapCursor();
                        break;
                    }
                    // リピートモードでは共有UIでリピート範囲を動かす(ビュー不変)。
                    if (_isRepeatEditMode)
                    {
                        MoveRepeatRange(deltaTime, zoomState.TotalDuration);
                        break;
                    }
                    double desiredRangeStart = _minimapInitialRangeStart + deltaTime;
                    double previousRangeStart = _viewModel.ZoomState.VisibleRangeStart;
                    if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                        _viewModel.PanWaveformRange(desiredRangeStart);
                    else
                        _viewModel.MoveWaveformRange(desiredRangeStart);
                    double appliedRangeStart = _viewModel.ZoomState.VisibleRangeStart;
                    double effectivePosition = _minimapCarriedPosition ?? _minimapCarryBasePosition;
                    double moveWidth = zoomState.VisibleRangeDuration;
                    if (moveWidth > 0)
                    {
                        double oldRatio = (effectivePosition - previousRangeStart) / moveWidth;
                        double newRatio = (effectivePosition - appliedRangeStart) / moveWidth;
                        if (Math.Abs(newRatio - 0.5) < Math.Abs(oldRatio - 0.5))
                        {
                            // 中央へ向かう: 範囲のみ動かし、位置は動かさない。
                        }
                        else
                        {
                            // 中央維持・遠ざかる: 剛体として位置も同量動かす。
                            double rigidShift = appliedRangeStart - previousRangeStart;
                            if (rigidShift != 0)
                                _minimapCarriedPosition = Math.Clamp(effectivePosition + rigidShift, 0, zoomState.TotalDuration);
                        }
                    }
                    // 端で範囲が詰まった余剰分は位置へ付け替える。
                    // エンジンへの反映は解放時に1回だけ行う。
                    double rangeExcess = desiredRangeStart - appliedRangeStart;
                    double excessDelta = rangeExcess - _minimapPreviousExcess;
                    _minimapPreviousExcess = rangeExcess;
                    if (excessDelta != 0 && zoomState.TotalDuration > 0)
                        _minimapCarriedPosition = Math.Clamp((_minimapCarriedPosition ?? effectivePosition) + excessDelta, 0, zoomState.TotalDuration);
                    UpdateMinimapCursor();
                    break;
                case MinimapDragMode.ResizeStart:
                    // リピートモードでは共有UIでリピート範囲の始端を動かす。
                    if (_isRepeatEditMode)
                    {
                        ResizeRepeatRange(true, _minimapInitialRangeStart + deltaTime, zoomState.TotalDuration);
                        break;
                    }
                    _viewModel.SetWaveformRangeStart(_minimapInitialRangeStart + deltaTime);
                    break;
                case MinimapDragMode.ResizeEnd:
                    // リピートモードでは共有UIでリピート範囲の終端を動かす。
                    if (_isRepeatEditMode)
                    {
                        ResizeRepeatRange(false, _minimapInitialRangeEnd + deltaTime, zoomState.TotalDuration);
                        break;
                    }
                    _viewModel.SetWaveformRangeEnd(_minimapInitialRangeEnd + deltaTime);
                    break;
                case MinimapDragMode.ZoomAroundCenter:
                    double dragDistance = x - _minimapDragStartX;
                    // 左ハンドルは既存の方向を維持し、右ハンドルは逆方向にする。
                    // これにより、各ハンドルを外側へ動かしたときの表示範囲の変化が
                    // ユーザーがハンドルを広げる／狭める感覚と一致する。
                    double dragDirection = _isZoomingFromRightHandle ? 1 : -1;
                    double zoomExponent = dragDirection * dragDistance / Math.Max(32, MinimapCanvas.ActualWidth / 4);
                    double zoomFactor = Math.Clamp((double)_viewModel.Settings.WaveformZoom.ZoomFactor, 1.1, 10);
                    double newWidth = initialWidth * Math.Pow(zoomFactor, zoomExponent);
                    double pivot;
                    if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                    {
                        _viewModel.ZoomWaveformPreservingRatio(_minimapInitialRangeStart,
                            _minimapInitialRangeEnd, _minimapInitialPosition, newWidth);
                    }
                    else
                    {
                        pivot = (_minimapInitialRangeStart + _minimapInitialRangeEnd) / 2;
                        _viewModel.SetWaveformRangeCentered(pivot, newWidth);
                    }
                    break;
            }

            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                DrawPlaybackCursorAtCenter();
        }

        private void MinimapCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_minimapDragMode == MinimapDragMode.SelectRepeat)
            {
                if (_minimapDragStarted && _viewModel.ZoomState != null && MinimapCanvas.ActualWidth > 0)
                {
                    double x = Math.Clamp(e.GetPosition(MinimapCanvas).X, 0, MinimapCanvas.ActualWidth);
                    CommitRepeatSelection(_repeatSelectAnchor,
                        x / MinimapCanvas.ActualWidth * _viewModel.ZoomState.TotalDuration);
                }
                else
                {
                    DrawMinimap();
                }
                _minimapDragStarted = false;
                _minimapDragMode = MinimapDragMode.None;
                MinimapCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (_minimapDragMode == MinimapDragMode.None)
                return;

            // ビュー自由化のビュー移動では再生バーをずらさない(シーク確定なし)。
            if (_minimapFreeViewDrag && _minimapDragMode == MinimapDragMode.MoveRange)
            {
                _viewModel.EndManualWaveformNavigation();
                _resumePlaybackAfterMinimapDrag = false;
                _wasPlayingBeforeMinimapDrag = false;
                _minimapCarriedPosition = null;
                _minimapDragStarted = false;
                _minimapDragMode = MinimapDragMode.None;
                _minimapFreeViewDrag = false;
                _isZoomingFromRightHandle = false;
                MinimapCanvas.ReleaseMouseCapture();
                DrawMinimap();
                e.Handled = true;
                return;
            }

            if (_minimapDragMode == MinimapDragMode.PointToRange &&
                _viewModel.ZoomState != null && MinimapCanvas.ActualWidth > 0)
            {
                double x = Math.Clamp(e.GetPosition(MinimapCanvas).X, 0, MinimapCanvas.ActualWidth);
                PreviewMinimapPointToRange(x / MinimapCanvas.ActualWidth * _viewModel.ZoomState.TotalDuration);
            }

            var zoomState = _viewModel.ZoomState;
            if (zoomState != null)
            {
                double currentPosition = _viewModel.PlaybackState?.CurrentPosition.TotalSeconds ?? 0;
                // 移動ドラッグ中は剛体で動かした位置が確定済み。リサイズ・ズームでは位置を動かさず、
                // 範囲外に出た場合のみ近い方の端へ寄せる。中央への寄せは行わない。
                double? finalPosition = _minimapDragMode == MinimapDragMode.PointToRange || _minimapDragStarted
                    ? _minimapCarriedPosition : null;
                // ビュー自由化中はビュー操作で再生バーを動かさない。
                if (_minimapDragStarted && finalPosition == null && !_minimapFreeViewDrag &&
                    (currentPosition < zoomState.VisibleRangeStart || currentPosition > zoomState.VisibleRangeEnd))
                    finalPosition = Math.Clamp(currentPosition, zoomState.VisibleRangeStart, zoomState.VisibleRangeEnd);

                if (finalPosition.HasValue && finalPosition.Value != currentPosition)
                {
                    if (_wasPlayingBeforeMinimapDrag)
                        _viewModel.SeekAndPlay(finalPosition.Value);
                    else
                        _viewModel.SetPositionKeepingWaveformRange(finalPosition.Value);
                }
                else if (_resumePlaybackAfterMinimapDrag)
                {
                    _viewModel.PlayKeepingWaveformRange();
                }
            }

            _viewModel.EndManualWaveformNavigation();
            _resumePlaybackAfterMinimapDrag = false;
            _wasPlayingBeforeMinimapDrag = false;
            _minimapCarriedPosition = null;
            _minimapDragStarted = false;
            _minimapDragMode = MinimapDragMode.None;
            _minimapFreeViewDrag = false;
            _isZoomingFromRightHandle = false;
            MinimapCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void MinimapCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_minimapDragMode == MinimapDragMode.SelectRepeat)
            {
                _minimapDragMode = MinimapDragMode.None;
                _minimapDragStarted = false;
                DrawMinimap();
                return;
            }

            if (_minimapDragMode == MinimapDragMode.None)
                return;

            _minimapCarriedPosition = null;
            if (_minimapDragMode == MinimapDragMode.PointToRange)
            {
                if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
                    _viewModel.PanWaveformRange(_minimapInitialRangeStart);
                else
                    _viewModel.MoveWaveformRange(_minimapInitialRangeStart);
            }
            _viewModel.EndManualWaveformNavigation();
            _minimapDragMode = MinimapDragMode.None;
            _minimapDragStarted = false;
            _minimapFreeViewDrag = false;
            _resumePlaybackAfterMinimapDrag = false;
            _wasPlayingBeforeMinimapDrag = false;
            DrawMinimap();
        }

        // ========== 全体波形ビューのリピート範囲 ==========

        private void MinimapModeSwitch_Click(object sender, RoutedEventArgs e)
        {
            _isRepeatEditMode = MinimapModeSwitch?.IsChecked == true;
            if (MinimapCanvas != null)
                MinimapCanvas.Cursor = _isRepeatEditMode ? Cursors.Cross : null;
            UpdateMinimapModeSwitchTooltip();
            DrawMinimap();
        }

        private void UpdateMinimapModeSwitchTooltip()
        {
            if (MinimapModeSwitch == null)
                return;

            MinimapModeSwitch.ToolTip = _languageService.Get(
                _isRepeatEditMode ? "Minimap.ModeRepeat" : "Minimap.ModeView");
        }

        private void RepeatEndBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingRepeatUi || _viewModel?.Settings == null)
                return;

            _viewModel.Settings.RepeatRangeMode = RepeatEndBehaviorCombo?.SelectedIndex == 1
                ? RepeatRangeMode.StopAtEnd
                : RepeatRangeMode.Loop;
            UpdateRepeatRangeUi();
        }

        private void RepeatRangeClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.Settings == null)
                return;

            _viewModel.Settings.RepeatRangeEnabled = false;
            _lastRepeatCheckPosition = double.NaN;
            UpdateRepeatRangeUi();
        }

        private void PreviewRepeatSelection(double anchor, double current)
        {
            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.TotalDuration <= 0 || MinimapCanvas.ActualWidth <= 0)
                return;

            // 共有UIに仮配置する(確定は CommitRepeatSelection で行う)。
            double start = Math.Clamp(Math.Min(anchor, current), 0, zoomState.TotalDuration);
            double end = Math.Clamp(Math.Max(anchor, current), 0, zoomState.TotalDuration);
            if (_minimapRangeOverlay == null)
                DrawMinimap();
            if (_minimapRangeOverlay == null)
                return;

            ApplyMinimapOverlayColors();
            double left = start / zoomState.TotalDuration * MinimapCanvas.ActualWidth;
            _minimapRangeOverlay.Width = Math.Max(1, (end - start) / zoomState.TotalDuration * MinimapCanvas.ActualWidth);
            _minimapRangeOverlay.Height = MinimapCanvas.ActualHeight;
            Canvas.SetLeft(_minimapRangeOverlay, left);
            _minimapRangeOverlay.Visibility = Visibility.Visible;
            UpdateMinimapHandle(ref _minimapLeftHandle, "LeftHandle", left);
            UpdateMinimapHandle(ref _minimapRightHandle, "RightHandle", Math.Clamp(left + _minimapRangeOverlay.Width - 8, 0,
                Math.Max(0, MinimapCanvas.ActualWidth - 8)));
            _minimapLeftHandle.Visibility = Visibility.Visible;
            _minimapRightHandle.Visibility = Visibility.Visible;
            if (RepeatRangeLabel != null)
                RepeatRangeLabel.Text = FormatRepeatRangeText(start, end);
        }

        private void CommitRepeatSelection(double anchor, double current)
        {
            var settings = _viewModel?.Settings;
            var zoomState = _viewModel?.ZoomState;
            if (settings == null || zoomState == null || zoomState.TotalDuration <= 0)
                return;

            double start = Math.Clamp(Math.Min(anchor, current), 0, zoomState.TotalDuration);
            double end = Math.Clamp(Math.Max(anchor, current), 0, zoomState.TotalDuration);
            if (end - start >= 0.1)
            {
                settings.RepeatRangeStart = start;
                settings.RepeatRangeEnd = end;
                settings.RepeatRangeEnabled = true;
            }
            else
            {
                settings.RepeatRangeEnabled = false;
            }
            _lastRepeatCheckPosition = double.NaN;
            UpdateRepeatRangeUi();
        }

        private void UpdateRepeatRangeUi()
        {
            var settings = _viewModel?.Settings;
            if (RepeatRangeLabel == null || RepeatEndBehaviorCombo == null)
                return;

            _isUpdatingRepeatUi = true;
            try
            {
                RepeatEndBehaviorCombo.SelectedIndex =
                    settings?.RepeatRangeMode == RepeatRangeMode.StopAtEnd ? 1 : 0;
            }
            finally
            {
                _isUpdatingRepeatUi = false;
            }

            RepeatRangeLabel.Text = FormatRepeatRangeText();
            DrawMinimap();
        }

        private string FormatRepeatRangeText()
        {
            var settings = _viewModel?.Settings;
            var zoomState = _viewModel?.ZoomState;
            if (settings == null || zoomState == null || zoomState.TotalDuration <= 0 ||
                !settings.RepeatRangeEnabled)
                return _languageService.Get("Repeat.RangeEmpty");

            double start = Math.Clamp(settings.RepeatRangeStart, 0, zoomState.TotalDuration);
            double end = Math.Clamp(settings.RepeatRangeEnd, 0, zoomState.TotalDuration);
            if (end - start < 0.1)
                return _languageService.Get("Repeat.RangeEmpty");

            return FormatRepeatRangeText(start, end);
        }

        private string FormatRepeatRangeText(double start, double end)
        {
            string mode = _viewModel?.Settings?.RepeatRangeMode == RepeatRangeMode.StopAtEnd
                ? _languageService.Get("Repeat.StopAtEnd")
                : _languageService.Get("Repeat.Loop");
            return $"{FormatTime(TimeSpan.FromSeconds(start))} - {FormatTime(TimeSpan.FromSeconds(end))} ({mode})";
        }

        /// <summary>
        /// リピート範囲全体を指定量だけ平行移動する(ビュー範囲・再生位置は不変)。
        /// </summary>
        private void MoveRepeatRange(double deltaTime, double totalDuration)
        {
            var settings = _viewModel?.Settings;
            if (settings == null || totalDuration <= 0)
                return;

            double width = Math.Max(0.1, _minimapInitialRangeEnd - _minimapInitialRangeStart);
            double start = Math.Clamp(_minimapInitialRangeStart + deltaTime, 0, Math.Max(0, totalDuration - width));
            settings.RepeatRangeStart = start;
            settings.RepeatRangeEnd = Math.Min(totalDuration, start + width);
            UpdateRepeatRangeUi();
        }

        /// <summary>
        /// リピート範囲の始端または終端を動かす(ビュー範囲・再生位置は不変)。
        /// </summary>
        private void ResizeRepeatRange(bool isStartEdge, double newEdge, double totalDuration)
        {
            var settings = _viewModel?.Settings;
            if (settings == null || totalDuration <= 0)
                return;

            if (isStartEdge)
                settings.RepeatRangeStart = Math.Clamp(newEdge, 0, settings.RepeatRangeEnd - 0.1);
            else
                settings.RepeatRangeEnd = Math.Clamp(newEdge, settings.RepeatRangeStart + 0.1, totalDuration);
            UpdateRepeatRangeUi();
        }

        /// <summary>
        /// モードに応じて共有範囲UIの色を付ける。ビュー=緑、リピート=青。
        /// </summary>
        private void ApplyMinimapOverlayColors()
        {
            if (_minimapRangeOverlay == null)
                return;

            if (_isRepeatEditMode)
            {
                _minimapRangeOverlay.Fill = (Brush)FindResource("AccentBlueBrush");
                _minimapRangeOverlay.Stroke = (Brush)FindResource("AccentBlueBrush");
                _minimapRangeOverlay.Opacity = 0.35;
            }
            else
            {
                _minimapRangeOverlay.Fill = (Brush)FindResource("RangeOverlayBrush");
                _minimapRangeOverlay.Stroke = (Brush)FindResource("RangeOverlayBorderBrush");
                _minimapRangeOverlay.Opacity = 1.0;
            }
            if (_minimapLeftHandle != null)
                _minimapLeftHandle.Fill = (Brush)FindResource(
                    _isRepeatEditMode ? "AccentBlueBrush" : "RangeOverlayBorderBrush");
            if (_minimapRightHandle != null)
                _minimapRightHandle.Fill = (Brush)FindResource(
                    _isRepeatEditMode ? "AccentBlueBrush" : "RangeOverlayBorderBrush");
        }

        /// <summary>
        /// 再生位置がリピート範囲の終端を横切ったらループ/停止させる。
        /// 境界をまたいだ場合に true を返し、呼び出し側はそのフレームの後続描画を省く。
        /// </summary>
        private bool EnforceRepeatRange(double position)
        {
            var settings = _viewModel?.Settings;
            var zoomState = _viewModel?.ZoomState;
            if (settings == null || zoomState == null || !settings.RepeatRangeEnabled)
                return false;

            // シーク/範囲操作のドラッグ中は再生位置の付け替えと競合するため見送る。
            if (_isDraggingSeekBar || _isDraggingWaveform || _minimapDragMode != MinimapDragMode.None)
            {
                _lastRepeatCheckPosition = position;
                return false;
            }

            double total = zoomState.TotalDuration;
            if (total <= 0)
                return false;

            double start = Math.Clamp(settings.RepeatRangeStart, 0, total);
            double end = Math.Clamp(settings.RepeatRangeEnd, 0, total);
            if (end - start < 0.1)
                return false;

            bool crossed = !double.IsNaN(_lastRepeatCheckPosition) &&
                _lastRepeatCheckPosition < end && position >= end;
            _lastRepeatCheckPosition = position;
            if (!crossed)
                return false;

            if (settings.RepeatRangeMode == RepeatRangeMode.StopAtEnd)
            {
                // ワンショットで停止し、範囲先頭にキューする(範囲選択は維持)。
                // 終端で停止したままにすると、エンジン位置の反映遅れで再開時の
                // 範囲判定が外れて範囲を抜けてしまうため、停止時点で先頭へ戻す。
                // 再生再開時は必ず範囲先頭から再生される。
                _viewModel.Pause();
                _viewModel.SetPosition(start);
                _lastRepeatCheckPosition = start;
                UpdateRepeatRangeUi();
            }
            else
            {
                _viewModel.SeekAndPlay(start);
                _lastRepeatCheckPosition = start;
            }
            return true;
        }

        private void WaveformCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            HotKeyInputType inputType = e.Delta > 0
                ? HotKeyInputType.MouseWheelUp
                : HotKeyInputType.MouseWheelDown;
            var binding = _viewModel.Settings.HotKeyBindings.Find(item =>
                item.InputType == inputType && item.Modifiers == Keyboard.Modifiers);
            if (binding == null || !ExecuteHotKeyAction(binding.Action))
            {
                _viewModel.BeginManualWaveformNavigation();
                double step = Math.Clamp((double)_viewModel.Settings.WaveformZoom.ScrollStepSize, 0.01, 3600);
                _viewModel.ScrollWaveform(e.Delta > 0 ? step : -step);
                _viewModel.EndManualWaveformNavigation();
            }

            e.Handled = true;
        }

        private void UpdateWaveformProgress()
        {
            // エンジン読み込み中はそちらを優先表示する。
            if (_viewModel?.IsEngineLoading == true)
            {
                WaveformProgressText.Visibility = Visibility.Collapsed;
                return;
            }
            double progress = _viewModel.WaveformProgress;
            bool isLoading = progress > 0 && progress < 1;
            WaveformProgressText.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            WaveformProgressText.Text = isLoading ? $"波形を生成中... {(int)(progress * 100)}%" : string.Empty;
        }

        private void UpdateEngineLoadingUi()
        {
            if (EngineLoadingText == null || _viewModel == null)
                return;

            bool loading = _viewModel.IsEngineLoading;
            EngineLoadingText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (!loading)
                return;

            double percent = _viewModel.EngineBufferingPercent;
            string message = _languageService.Get("Loading.Audio");
            EngineLoadingText.Text = percent >= 0 ? $"{message} {(int)percent}%" : message;
        }

        private void WaveformCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var zoomState = _viewModel.ZoomState;
            if (zoomState == null || zoomState.VisibleRangeDuration <= 0)
                return;
            _isCenterWaveformPanMode = _viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed;
            _keepWaveformRangeDuringDrag = _viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.LeftScroll;
            _wasPlayingBeforeWaveformDrag = _viewModel.PlaybackState?.State == PlayState.Playing;

            _viewModel.BeginManualWaveformNavigation();
            _resumePlaybackAfterWaveformDrag = zoomState != null && PauseForZoomedDrag(zoomState);

            if (_isCenterWaveformPanMode)
            {
                _isDraggingWaveform = true;
                _isPotentialWaveformPan = true;
                _isPanningWaveform = false;
                _waveformPointerStartX = e.GetPosition(WaveformCanvas).X;
                _waveformPanInitialRangeStart = zoomState.VisibleRangeStart;
                _waveformPanInitialRangeDuration = zoomState.VisibleRangeDuration;
                WaveformCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (!UpdateWaveformPosition(e))
            {
                _viewModel.EndManualWaveformNavigation();
                return;
            }

            _isDraggingWaveform = true;
            WaveformCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void WaveformCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // ビュー自由化の右ドラッグは再生位置に触れず、表示範囲だけを動かす。
            if (_isFreeViewDragging)
            {
                if (e.RightButton != MouseButtonState.Pressed)
                    return;

                UpdateFreeViewPan(e);
                return;
            }

            if (!_isDraggingWaveform || e.LeftButton != MouseButtonState.Pressed)
                return;

            if (_isPotentialWaveformPan)
            {
                double deltaX = e.GetPosition(WaveformCanvas).X - _waveformPointerStartX;
                if (Math.Abs(deltaX) >= SystemParameters.MinimumHorizontalDragDistance)
                {
                    _isPotentialWaveformPan = false;
                    _isPanningWaveform = true;
                }
            }

            if (_isPanningWaveform)
                UpdateWaveformPan(e);
            else if (!_isPotentialWaveformPan)
                UpdateWaveformPosition(e);
        }

        private void WaveformCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingWaveform)
                return;

            if (_isPanningWaveform)
            {
                UpdateWaveformPan(e);
            }

            if (_isPanningWaveform && _isCenterWaveformPanMode)
            {
                var zoomState = _viewModel.ZoomState;
                if (zoomState != null)
                {
                    double centerPosition = Math.Clamp(
                        zoomState.VisibleRangeStart + zoomState.VisibleRangeDuration / 2,
                        0, zoomState.TotalDuration);
                    if (_wasPlayingBeforeWaveformDrag)
                        _viewModel.SeekAndPlay(centerPosition);
                    else
                        _viewModel.SetPosition(centerPosition);
                }
            }
            else if (_keepWaveformRangeDuringDrag && TryGetWaveformPosition(e, out double leftScrollPosition))
            {
                // 左流しのドラッグ中は描画だけを更新し、マウスを離した時点で一度だけ
                // 再生エンジンへシークする。移動ごとのシークは再生状態イベントを滞留させる。
                if (_resumePlaybackAfterWaveformDrag || _wasPlayingBeforeWaveformDrag)
                    _viewModel.SeekAndPlay(leftScrollPosition);
                else
                    _viewModel.SetPositionKeepingWaveformRange(leftScrollPosition);
            }
            else if (_resumePlaybackAfterWaveformDrag)
            {
                if (TryGetWaveformPosition(e, out double position))
                {
                    if (_keepWaveformRangeDuringDrag)
                        _viewModel.SeekAndPlayKeepingWaveformRange(position);
                    else
                        _viewModel.SeekAndPlay(position);
                }
            }
            else if (_isPotentialWaveformPan)
            {
                // A click seeks as usual; dragging pans the view without moving the playhead.
                UpdateWaveformPosition(e);
            }
            else if (!_isPanningWaveform)
            {
                UpdateWaveformPosition(e);
            }

            _viewModel.EndManualWaveformNavigation();

            _isDraggingWaveform = false;
            _isPotentialWaveformPan = false;
            _isPanningWaveform = false;
            _isCenterWaveformPanMode = false;
            _keepWaveformRangeDuringDrag = false;
            _resumePlaybackAfterWaveformDrag = false;
            _wasPlayingBeforeWaveformDrag = false;
            WaveformCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void WaveformCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isFreeViewDragging)
            {
                _viewModel.EndManualWaveformNavigation();
                _isFreeViewDragging = false;
                WaveformCanvas.Cursor = null;
                DrawWaveform();
                return;
            }

            if (!_isDraggingWaveform)
                return;

            _viewModel.EndManualWaveformNavigation();
            _isDraggingWaveform = false;
            _isPotentialWaveformPan = false;
            _isPanningWaveform = false;
            _resumePlaybackAfterWaveformDrag = false;
            _wasPlayingBeforeWaveformDrag = false;
            DrawWaveform();
        }

        // ========== ビュー自由化: 右ドラッグでビュー範囲を移動 ==========

        private void WaveformCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var zoomState = _viewModel?.ZoomState;
            if (zoomState == null || zoomState.VisibleRangeDuration <= 0 || WaveformCanvas.ActualWidth <= 0)
                return;
            if (!_viewModel.Settings.WaveformZoom.FreeView)
                return;

            // 中央固定は手動のビュー操作と競合するため自動で切る。
            if (_viewModel.Settings.WaveformZoom.CursorMode == CursorDisplayMode.CenterFixed)
            {
                _viewModel.Settings.WaveformZoom.CursorMode = CursorDisplayMode.LeftScroll;
                if (SidebarCenterFixedCheck != null)
                    SidebarCenterFixedCheck.IsChecked = false;
                DrawWaveform();
            }

            _isFreeViewDragging = true;
            _waveformPointerStartX = e.GetPosition(WaveformCanvas).X;
            _waveformPanInitialRangeStart = _viewModel.ZoomState.VisibleRangeStart;
            _waveformPanInitialRangeDuration = _viewModel.ZoomState.VisibleRangeDuration;
            _viewModel.BeginManualWaveformNavigation();
            WaveformCanvas.Cursor = Cursors.ScrollAll;
            WaveformCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void WaveformCanvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isFreeViewDragging)
                return;

            UpdateFreeViewPan(e);
            _viewModel.EndManualWaveformNavigation();
            _isFreeViewDragging = false;
            WaveformCanvas.Cursor = null;
            WaveformCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void UpdateFreeViewPan(MouseEventArgs e)
        {
            if (WaveformCanvas.ActualWidth <= 0)
                return;

            // 内容がカーソルに付いてくる向きに表示範囲をずらす。端では丸める。
            double deltaX = e.GetPosition(WaveformCanvas).X - _waveformPointerStartX;
            double deltaTime = deltaX / WaveformCanvas.ActualWidth * _waveformPanInitialRangeDuration;
            _viewModel.MoveWaveformRange(_waveformPanInitialRangeStart - deltaTime);
            RefreshWaveformRange();
            var state = _viewModel.PlaybackState;
            if (state != null)
                DrawPlaybackCursor(TimeSpan.FromSeconds(state.CurrentPosition.TotalSeconds));
            DrawMinimap();
        }

        private void UpdateWaveformPan(MouseEventArgs e)
        {
            if (WaveformCanvas.ActualWidth <= 0)
                return;

            double deltaX = e.GetPosition(WaveformCanvas).X - _waveformPointerStartX;
            double deltaTime = deltaX / WaveformCanvas.ActualWidth * _waveformPanInitialRangeDuration;
            _viewModel.PanWaveformRange(_waveformPanInitialRangeStart - deltaTime);
            if (_isCenterWaveformPanMode)
                DrawPlaybackCursorAtCenter();
        }

        private void DrawPlaybackCursorAtCenter(double? displayedPosition = null)
        {
            if (WaveformCanvas.ActualWidth <= 0)
                return;

            if (_centerGuideLine == null)
            {
                _centerGuideLine = new Line
                {
                    Y1 = 0,
                    Stroke = (Brush)FindResource("TextSecondaryBrush"),
                    StrokeThickness = 1,
                    Opacity = 0.6,
                    Tag = "CenterGuide",
                    IsHitTestVisible = false
                };
                WaveformCanvas.Children.Add(_centerGuideLine);
            }

            double cursorX = WaveformCanvas.ActualWidth / 2;
            _centerGuideLine.X1 = cursorX;
            _centerGuideLine.X2 = cursorX;
            _centerGuideLine.Y2 = WaveformCanvas.ActualHeight;
            _centerGuideLine.Visibility = Visibility.Visible;

            // ミニマップと同じく、ドラッグ中の仮位置を定期的な再生状態更新より優先する。
            // MouseMove と PlaybackState の描画が交互に走ってもカーソルを往復させない。
            var playbackState = _viewModel.PlaybackState;
            double position = _minimapCarriedPosition ?? displayedPosition ??
                _viewModel.PendingWaveformSeekPosition ??
                (playbackState?.State == PlayState.Playing
                    ? GetInterpolatedPlaybackPosition(playbackState)
                    : playbackState?.CurrentPosition.TotalSeconds) ?? double.NaN;
            if (!double.IsNaN(position))
                DrawPlaybackCursor(TimeSpan.FromSeconds(position));
        }

        private bool UpdateWaveformPosition(MouseEventArgs e)
        {
            if (!TryGetWaveformPosition(e, out double seconds))
                return false;

            var position = TimeSpan.FromSeconds(seconds);
            DrawPlaybackCursor(position);
            if (_keepWaveformRangeDuringDrag)
            {
                SeekBar.Value = seconds;
                CurrentTimeText.Text = FormatTime(position);
                return true;
            }
            else
                _viewModel.SetPosition(position.TotalSeconds);
            return true;
        }

        private bool TryGetWaveformPosition(MouseEventArgs e, out double seconds)
        {
            var zoomState = _viewModel.ZoomState;
            if (zoomState == null || zoomState.VisibleRangeDuration <= 0 || WaveformCanvas.ActualWidth <= 0)
            {
                seconds = 0;
                return false;
            }

            double ratio = Math.Clamp(e.GetPosition(WaveformCanvas).X / WaveformCanvas.ActualWidth, 0, 1);
            seconds = Math.Clamp(zoomState.VisibleRangeStart + zoomState.VisibleRangeDuration * ratio,
                0, zoomState.TotalDuration);
            return true;
        }

        private bool PauseForZoomedDrag(WaveformZoomState zoomState)
        {
            if (zoomState == null || zoomState.CurrentZoomLevel >= zoomState.TotalDuration - 0.001 ||
                _viewModel.PlaybackState?.State != PlayState.Playing)
                return false;

            _viewModel.Pause();
            return true;
        }

    }
}
