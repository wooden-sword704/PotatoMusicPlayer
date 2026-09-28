using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LibVLCSharp.Shared;
using PotatoMusicPlayer.Models;
using TagLib;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// LibVLCSharp を使用したメディア再生制御サービス
    /// </summary>
    public class MediaService : IDisposable
    {
        private readonly LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private bool _isInitialized = false;
        private readonly SynchronizationContext _syncContext;
        // ファイル未読み込み状態でも音量設定を保持する（再生開始時に適用される）
        private float _desiredVolume = 0.8f;
        private float _desiredPlaybackSpeed = 1.0f;
        private bool _positionSetAfterEnd;
        private long? _pendingSeekPosition;
        private TimeSpan _lastDuration = TimeSpan.Zero;

        // イベント
        public event EventHandler<TimeSpan> PositionChanged;
        public event EventHandler<TimeSpan> DurationChanged;
        public event EventHandler PlaybackStateChanged;
        public event EventHandler MediaEnded;
        public event EventHandler<string> ErrorOccurred;
        // バッファリング進捗(0 ~ 100)。再生パイプラインの読み込み表示用。
        public event EventHandler<float> BufferingChanged;

        private void Raise(Action action)
        {
            if (_syncContext != null)
            {
                _syncContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }

        public MediaService()
        {
            _syncContext = SynchronizationContext.Current;
            try
            {
                // LibVLCの初期化
                Core.Initialize();
                _libVLC = new LibVLC();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"LibVLC initialization failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// ファイルを開いて再生準備
        /// </summary>
        public async Task<bool> LoadFileAsync(string filePath)
        {
            if (!_isInitialized)
                return false;

            try
            {
                // 古いメディアを破棄（UIスレッドで）
                _currentMedia?.Dispose();

                // メディアのパースは LibVLC 側で重い場合があるためバックグラウンドで行う
                var newMedia = await Task.Run(() =>
                {
                    var m = new Media(_libVLC, filePath, FromType.FromPath);
                    m.Parse(MediaParseOptions.ParseLocal);
                    return m;
                });

                // イベントは UI スレッド側で登録する
                _currentMedia = newMedia;
                _currentMedia.ParsedChanged += OnMediaParsedChanged;
                _currentMedia.StateChanged += OnMediaStateChanged;

                if (_mediaPlayer == null)
                {
                    _mediaPlayer = new MediaPlayer(_libVLC);
                    _mediaPlayer.EndReached += OnMediaEnded;
                    _mediaPlayer.Playing += OnMediaPlaying;
                    _mediaPlayer.TimeChanged += OnMediaTimeChanged;
                    _mediaPlayer.LengthChanged += OnMediaLengthChanged;
                    _mediaPlayer.Buffering += OnMediaBuffering;
                    _mediaPlayer.EncounteredError += OnMediaEncounteredError;
                    // プレイヤー生成前に設定されていた音量を適用
                    _mediaPlayer.Volume = (int)(_desiredVolume * 100);
                    _mediaPlayer.SetRate(_desiredPlaybackSpeed);
                }

                _mediaPlayer.Stop();
                _mediaPlayer.Media = _currentMedia;
                _pendingSeekPosition = null;
                _positionSetAfterEnd = false;

                return true;
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// 再生パイプラインが安定する(実際に音声時刻が進み始める)のを待つ。
        /// 読み込み直後の即時シークによる音の途切れを防ぐためのゲート。
        /// キャンセル・エラー時は false を返す(例外は投げない)。
        /// </summary>
        public Task<bool> WaitForStablePlaybackAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_mediaPlayer != null && _mediaPlayer.IsPlaying && _mediaPlayer.Time > 0)
                    return Task.FromResult(true);
            }
            catch
            {
                // 状態取得に失敗した場合は下の待機フローへ進む。
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int settled = 0;
            EventHandler<TimeSpan> timeHandler = null;
            EventHandler<float> bufferingHandler = null;
            EventHandler<string> errorHandler = null;
            CancellationTokenRegistration registration = default;

            void Cleanup()
            {
                PositionChanged -= timeHandler;
                BufferingChanged -= bufferingHandler;
                ErrorOccurred -= errorHandler;
                registration.Dispose();
            }

            bool TrySettle(bool result)
            {
                if (System.Threading.Interlocked.Exchange(ref settled, 1) != 0)
                    return false;
                Cleanup();
                tcs.TrySetResult(result);
                return true;
            }

            // 時刻が進み始めた = デコード〜出力が回っている。
            timeHandler = (s, t) =>
            {
                if (t.TotalMilliseconds > 0)
                    TrySettle(true);
            };
            bufferingHandler = (s, percent) =>
            {
                if (percent >= 100)
                    TrySettle(true);
            };
            errorHandler = (s, message) => TrySettle(false);

            PositionChanged += timeHandler;
            BufferingChanged += bufferingHandler;
            ErrorOccurred += errorHandler;
            registration = cancellationToken.Register(() => TrySettle(false));
            return tcs.Task;
        }

        /// <summary>
        /// 再生開始
        /// </summary>
        public void Play()
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    // EndReached後のLibVLCはEnded状態を保持することがある。
                    // Playだけでは再開できないため、停止ボタンと同じく先頭へ戻してから再生する。
                    if ((_mediaPlayer.State == VLCState.Ended ||
                        (_mediaPlayer.Length > 0 && _mediaPlayer.Time >= _mediaPlayer.Length)) &&
                        !_positionSetAfterEnd)
                    {
                        _mediaPlayer.Stop();
                        _mediaPlayer.Time = 0;
                    }
                    _positionSetAfterEnd = false;
                    _mediaPlayer.Play();
                    Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"Play failed: {ex.Message}"));
            }
        }

        /// <summary>指定位置へ移動して、その位置から再生する。</summary>
        public void PlayFromPosition(long milliseconds)
        {
            try
            {
                if (_mediaPlayer == null)
                    return;

                long targetPosition = Math.Max(0, milliseconds);
                if (_mediaPlayer.State == VLCState.Stopped ||
                    _mediaPlayer.State == VLCState.Ended ||
                    (_mediaPlayer.Length > 0 && _mediaPlayer.Time >= _mediaPlayer.Length))
                {
                    // LibVLC ignores Time changes while stopped/ended. Start first,
                    // then apply the requested position from the Playing event.
                    _pendingSeekPosition = targetPosition;
                    _positionSetAfterEnd = true;
                    _mediaPlayer.Play();
                    Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
                    return;
                }

                bool wasPlaying = _mediaPlayer.IsPlaying;
                // 一時停止状態からの再開では Time がすぐに確定しない場合がある。
                // Playing 通知後にも目標位置を適用する。
                _pendingSeekPosition = wasPlaying ? null : targetPosition;
                _mediaPlayer.Time = targetPosition;
                _positionSetAfterEnd = !wasPlaying;
                if (!wasPlaying)
                    _mediaPlayer.Play();
                Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"PlayFromPosition failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 一時停止
        /// </summary>
        public void Pause()
        {
            try
            {
                if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Pause();
                    Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"Pause failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 再生/一時停止の切り替え
        /// </summary>
        public void TogglePlayPause()
        {
            if (_mediaPlayer == null)
                return;

            if (_mediaPlayer.IsPlaying)
                Pause();
            else
                Play();
        }

        /// <summary>
        /// 停止（先頭に戻す）
        /// </summary>
        public void Stop()
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Time = 0;
                    _positionSetAfterEnd = false;
                    _pendingSeekPosition = null;
                    Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"Stop failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 現在位置を設定（ミリ秒）
        /// </summary>
        public void SetPosition(long milliseconds)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    // Stopped/Ended では Time の変更が無視される。次の Playing で適用する。
                    long targetPosition = Math.Max(0, milliseconds);
                    if (_mediaPlayer.State == VLCState.Ended ||
                        _mediaPlayer.State == VLCState.Stopped ||
                        (_mediaPlayer.Length > 0 && _mediaPlayer.Time >= _mediaPlayer.Length))
                    {
                        _pendingSeekPosition = targetPosition;
                        _positionSetAfterEnd = true;
                    }
                    else
                    {
                        // 一時停止中に受け付けた位置も、再開直後にもう一度確定する。
                        _pendingSeekPosition = _mediaPlayer.State == VLCState.Paused
                            ? targetPosition : null;
                        _positionSetAfterEnd = _pendingSeekPosition.HasValue;
                        _mediaPlayer.Time = targetPosition;
                    }
                    Raise(() => PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(targetPosition)));
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"SetPosition failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 相対位置を移動（秒）
        /// </summary>
        public void SkipRelative(float seconds)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    long currentMs = _mediaPlayer.Time;
                    long newMs = currentMs + (long)(seconds * 1000);
                    newMs = Math.Max(0, newMs);  // 負にならないように
                    SetPosition(newMs);
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"SkipRelative failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 音量を設定（0.0 ~ maxMultiplier。1.0 = 100%）
        /// LibVLC の Volume プロパティは 100 を超える値（音量ブースト）も受け付ける
        /// </summary>
        public void SetVolume(float volume, float maxMultiplier = 1.0f)
        {
            try
            {
                volume = Math.Clamp(volume, 0.0f, maxMultiplier);
                // 常に希望音量を記録しておき、プレイヤー未生成の間も状態取得で返せるようにする
                _desiredVolume = volume;
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Volume = (int)(volume * 100);
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"SetVolume failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// 再生速度を設定
        /// </summary>
        public void SetPlaybackSpeed(float speed)
        {
            try
            {
                speed = Math.Max(0.25f, speed);  // 0.25倍以上
                _desiredPlaybackSpeed = speed;
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.SetRate(speed);
                }
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"SetPlaybackSpeed failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// メディア情報を取得（非同期）
        /// </summary>
        public async Task<MediaFile> GetMediaInfoAsync(string filePath)
        {
            var info = new MediaFile
            {
                FilePath = filePath,
                FileName = System.IO.Path.GetFileName(filePath),
                FileFormat = System.IO.Path.GetExtension(filePath).TrimStart('.')
            };

            try
            {
                // 重いパース処理はバックグラウンドで行う
                var duration = await Task.Run(() =>
                {
                    using (var media = new Media(_libVLC, filePath, FromType.FromPath))
                    {
                        media.Parse(MediaParseOptions.ParseLocal);
                        return TimeSpan.FromMilliseconds(media.Duration);
                    }
                });

                info.Duration = duration;
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"GetMediaInfo failed: {ex.Message}"));
            }

            // タグ情報は再生用のLibVLC解析とは独立して取得する。
            try
            {
                await Task.Run(() =>
                {
                    using (var tagFile = TagLib.File.Create(filePath))
                    {
                        info.Title = tagFile.Tag.Title;
                        info.Artist = tagFile.Tag.Performers?.FirstOrDefault();
                        info.Album = tagFile.Tag.Album;
                        info.Genre = tagFile.Tag.FirstGenre;
                        info.Bitrate = tagFile.Properties.AudioBitrate;
                        info.SampleRate = tagFile.Properties.AudioSampleRate;
                        info.Channels = tagFile.Properties.AudioChannels;

                        if (info.Duration <= TimeSpan.Zero)
                            info.Duration = tagFile.Properties.Duration;
                    }
                });
            }
            catch (Exception ex)
            {
                // タグが読めないファイルでも再生とファイル名表示は継続する。
                System.Diagnostics.Debug.WriteLine($"Metadata read failed: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// 現在の再生状態を取得
        /// </summary>
        public PlaybackState GetPlaybackState()
        {
            var state = new PlaybackState();

            if (_mediaPlayer == null)
            {
                // ファイル未読み込みでも、ユーザー設定済みの音量を反映して返す
                state.Volume = _desiredVolume;
                state.PlaybackSpeed = _desiredPlaybackSpeed;
                return state;
            }

            try
            {
                state.State = _mediaPlayer.IsPlaying ? PlayState.Playing :
                    _mediaPlayer.State == VLCState.Stopped || _mediaPlayer.State == VLCState.Ended
                        ? PlayState.Stopped : PlayState.Paused;
                state.CurrentPosition = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time));
                var playerDuration = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
                state.Duration = playerDuration > TimeSpan.Zero ? playerDuration : _lastDuration;
                // Stop()直後など、LibVLCが一時的に0を返す場合も希望音量を維持する。
                state.Volume = _desiredVolume;
                state.PlaybackSpeed = _mediaPlayer.Rate;
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"GetPlaybackState failed: {ex.Message}"));
            }

            return state;
        }

        /// <summary>
        /// 再生中かどうか
        /// </summary>
        public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

        // ========== Private Event Handlers ==========

        private void OnMediaParsedChanged(object sender, EventArgs e)
        {
            if (_currentMedia != null)
            {
                Raise(() => DurationChanged?.Invoke(this, TimeSpan.FromMilliseconds(_currentMedia.Duration)));
            }
        }

        private void OnMediaStateChanged(object sender, MediaStateChangedEventArgs e)
        {
            Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
        }

        private void OnMediaTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            Raise(() => PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Time)));
            Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
        }

        private void OnMediaLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            _lastDuration = TimeSpan.FromMilliseconds(e.Length);
            Raise(() => DurationChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Length)));
            Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
        }

        private void OnMediaBuffering(object sender, MediaPlayerBufferingEventArgs e)
        {
            Raise(() => BufferingChanged?.Invoke(this, e.Cache));
        }

        private void OnMediaEnded(object sender, EventArgs e)
        {
            Raise(() => MediaEnded?.Invoke(this, EventArgs.Empty));
        }

        private void OnMediaPlaying(object sender, EventArgs e)
        {
            // LibVLC のイベントスレッドからプレイヤーを操作しない。
            // Stop がイベント終了を待つ間に Time の設定が走ると停止処理が固まり得る。
            if (_syncContext != null)
                _syncContext.Post(_ => ApplyPendingSeekAfterPlaying(), null);
            else
                ThreadPool.QueueUserWorkItem(_ => ApplyPendingSeekAfterPlaying());
        }

        private void ApplyPendingSeekAfterPlaying()
        {
            if (!_pendingSeekPosition.HasValue || _mediaPlayer == null)
                return;

            long position = _pendingSeekPosition.Value;
            _pendingSeekPosition = null;
            try
            {
                _mediaPlayer.Time = position;
                _positionSetAfterEnd = false;
                Raise(() => PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(position)));
                Raise(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
            }
            catch (Exception ex)
            {
                Raise(() => ErrorOccurred?.Invoke(this, $"Seek after playing failed: {ex.Message}"));
            }
        }

        private void OnMediaEncounteredError(object sender, EventArgs e)
        {
            Raise(() => ErrorOccurred?.Invoke(this, "Media playback error."));
        }

        public void Dispose()
        {
            _currentMedia?.Dispose();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }
    }
}
