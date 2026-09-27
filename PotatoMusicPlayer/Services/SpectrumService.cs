using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Dsp;
using NAudio.Wave;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// 再生中ファイルの実データをFFT解析したスペクトラムを提供する。
    /// 波形生成と同様にNAudioでデコードし、再生位置に対応するフレームを返す。
    /// 解析完了まではレベルを返さない(フェイク表示はしない)。
    /// </summary>
    public sealed class SpectrumService : IDisposable
    {
        public const int BandCount = 48;
        private const int FftSize = 4096;
        private const int FftBits = 12; // 2^12 = 4096
        private const int HopSize = 2048;

        private readonly object _lock = new();
        private string _loadedPath;
        private List<float[]> _frames = new();
        private double _frameSeconds;
        private readonly float[] _displayLevels = new float[BandCount];
        private bool _disposed;
        private string _lastError;

        /// <summary>最後の解析エラーを取得する（デバッグ用）</summary>
        public string LastError => _lastError;

        /// <summary>指定ファイルのスペクトラム解析をバックグラウンドで実行する。</summary>
        public Task LoadFileAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(() => LoadFile(filePath, cancellationToken), cancellationToken);
        }

        private void LoadFile(string filePath, CancellationToken cancellationToken)
        {
            var frames = new List<float[]>();
            double frameSeconds = 0;
            WaveStream reader = null;
            try
            {
                reader = CreateReader(filePath);
                if (reader == null)
                {
                    _lastError = $"CreateReader returned null for: {filePath}";
                    Debug.WriteLine($"[SpectrumService] {_lastError}");
                    return;
                }

                using (reader)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    var sampleProvider = reader.ToSampleProvider();
                    int channels = Math.Max(1, reader.WaveFormat.Channels);
                    int sampleRate = Math.Max(8000, reader.WaveFormat.SampleRate);
                    frameSeconds = (double)HopSize / sampleRate;

                    Debug.WriteLine($"[SpectrumService] Analyzing: {filePath}, Channels={channels}, SampleRate={sampleRate}, FrameSeconds={frameSeconds:F4}");

                    var window = new float[FftSize];
                    int buffered = 0;
                    var readBuffer = new float[HopSize * channels * 2];
                    int read;
                    long totalFramesProcessed = 0;
                    while ((read = sampleProvider.Read(readBuffer, 0, readBuffer.Length)) > 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        int framesInBuffer = read / channels;
                        int consumed = 0;
                        while (consumed < framesInBuffer)
                        {
                            int copy = Math.Min(FftSize - buffered, framesInBuffer - consumed);
                            for (int i = 0; i < copy; i++)
                            {
                                double mono = 0;
                                for (int ch = 0; ch < channels; ch++)
                                    mono += readBuffer[(consumed + i) * channels + ch];
                                window[buffered + i] = (float)(mono / channels);
                            }
                            buffered += copy;
                            consumed += copy;

                            if (buffered >= FftSize)
                            {
                                frames.Add(AnalyzeWindow(window, sampleRate));
                                // ホップ分だけずらして残りを繰り越す。
                                Array.Copy(window, HopSize, window, 0, FftSize - HopSize);
                                buffered = FftSize - HopSize;
                                totalFramesProcessed++;
                            }
                        }
                    }

                    Debug.WriteLine($"[SpectrumService] Analysis complete: {frames.Count} frames generated, TotalFramesProcessed={totalFramesProcessed}");
                }
            }
            catch (Exception ex)
            {
                _lastError = $"Analysis failed: {ex.Message}";
                Debug.WriteLine($"[SpectrumService] {_lastError}");
                Debug.WriteLine(ex.ToString());
                return;
            }
            finally
            {
                if (reader != null)
                {
                    try { reader.Dispose(); } catch { }
                }
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            if (frames.Count == 0)
            {
                _lastError = "No frames generated (file too short or empty)";
                Debug.WriteLine($"[SpectrumService] {_lastError}");
            }

            lock (_lock)
            {
                if (_disposed)
                    return;

                _loadedPath = filePath;
                _frames = frames;
                _frameSeconds = frameSeconds;
                Array.Clear(_displayLevels, 0, _displayLevels.Length);
                Debug.WriteLine($"[SpectrumService] Loaded {_frames.Count} frames for {filePath}");
            }
        }

        /// <summary>
        /// 再生位置に対応するバンドレベル(0..1)を返す。未解析・別ファイル時はnull。
        /// </summary>
        public float[] GetLevels(string filePath, double positionSeconds)
        {
            float[] target;
            lock (_lock)
            {
                if (_disposed || _frames.Count == 0 || _frameSeconds <= 0 ||
                    !string.Equals(_loadedPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
#if DEBUG
                    if (_frames.Count == 0)
                        Debug.WriteLine($"[SpectrumService] GetLevels: No frames (disposed={_disposed}, frameSeconds={_frameSeconds}, pathMatch={string.Equals(_loadedPath, filePath, StringComparison.OrdinalIgnoreCase)})");
#endif
                    return null;
                }

                double framePos = Math.Clamp(positionSeconds / _frameSeconds, 0, _frames.Count - 1);
                int index = (int)framePos;
                double fraction = framePos - index;
                var current = _frames[index];
                target = new float[BandCount];
                if (index + 1 < _frames.Count && fraction > 0)
                {
                    var next = _frames[index + 1];
                    for (int i = 0; i < BandCount; i++)
                        target[i] = (float)(current[i] + (next[i] - current[i]) * fraction);
                }
                else
                {
                    Array.Copy(current, target, BandCount);
                }

                // 表示のちらつきを抑える軽い平滑化(データ駆動。フェイク成分なし)。
                var copy = new float[BandCount];
                for (int i = 0; i < BandCount; i++)
                {
                    float displayed = _displayLevels[i];
                    float goal = target[i];
                    _displayLevels[i] = goal > displayed
                        ? displayed + (goal - displayed) * 0.6f
                        : displayed + (goal - displayed) * 0.35f;
                    copy[i] = _displayLevels[i];
                }
                return copy;
            }
        }

        private static float[] AnalyzeWindow(float[] mono, int sampleRate)
        {
            var data = new Complex[FftSize];
            double windowSum = 0;
            for (int i = 0; i < FftSize; i++)
            {
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                windowSum += window;
                data[i].X = (float)(mono[i] * window);
                data[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftBits, data);

            int usableBins = FftSize / 2;
            double minFreq = 40;
            double maxFreq = Math.Min(16000, sampleRate / 2.0);
            var levels = new float[BandCount];
            if (maxFreq <= minFreq)
                return levels;

            double logMin = Math.Log10(minFreq);
            double logMax = Math.Log10(maxFreq);
            for (int band = 0; band < BandCount; band++)
            {
                double low = Math.Pow(10, logMin + (logMax - logMin) * band / BandCount);
                double high = Math.Pow(10, logMin + (logMax - logMin) * (band + 1) / BandCount);
                int lowBin = Math.Clamp((int)(low * FftSize / sampleRate), 1, usableBins - 1);
                int highBin = Math.Clamp((int)Math.Ceiling(high * FftSize / sampleRate), lowBin + 1, usableBins);
                double powerSum = 0;
                for (int bin = lowBin; bin < highBin; bin++)
                {
                    double magnitude = Math.Sqrt(data[bin].X * data[bin].X + data[bin].Y * data[bin].Y);
                    // A Hann window has a coherent gain of windowSum / FftSize.
                    // Correct for it and for the real FFT's mirrored spectrum.
                    // NAudio の FFT 出力は 1/N 正規化済みのため N を掛けて戻す。
                    double amplitude = magnitude * 2.0 * FftSize / windowSum;
                    powerSum += amplitude * amplitude;
                }

                double rmsAmplitude = Math.Sqrt(powerSum / Math.Max(1, highBin - lowBin));
                double decibels = 20.0 * Math.Log10(Math.Max(rmsAmplitude, 1e-6));
                levels[band] = (float)Math.Clamp((decibels + 72.0) / 54.0, 0.0, 1.0);
            }
            return levels;
        }

        /// <summary>
        /// ファイル形式に応じた WaveStream を生成する(WaveformServiceと同方針)。
        /// </summary>
        private static WaveStream CreateReader(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                Debug.WriteLine($"[SpectrumService] CreateReader: File not found: {filePath}");
                return null;
            }

            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case ".wav":
                    case ".mp3":
                        return new AudioFileReader(filePath);
                    case ".flac":
                    case ".ogg":
                    default:
                        return new MediaFoundationReader(filePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpectrumService] CreateReader primary failed for {ext}: {ex.Message}");
                try
                {
                    return new MediaFoundationReader(filePath);
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"[SpectrumService] CreateReader fallback failed for {ext}: {ex2.Message}");
                    return null;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _frames = new List<float[]>();
                _loadedPath = null;
            }
        }
    }
}
