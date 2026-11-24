using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyLyricsWallpaper.Services;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Captures Windows system audio via WASAPI Loopback and computes a 128-band stereo FFT spectrum
/// (64 left + 64 right) matching Wallpaper Engine's wallpaperRegisterAudioListener format.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private const int FftLength = 1024;
    private const int FftM = 10; // log2(1024)
    private const int BandsPerChannel = 64;

    private readonly object _lock = new();
    private readonly float[] _ringL = new float[FftLength];
    private readonly float[] _ringR = new float[FftLength];
    private int _ringPos;
    private long _lastDataTicks;

    private readonly float[] _hann = new float[FftLength];
    private readonly Complex[] _fftL = new Complex[FftLength];
    private readonly Complex[] _fftR = new Complex[FftLength];
    private readonly float[] _bands = new float[BandsPerChannel * 2];
    private readonly float[] _smooth = new float[BandsPerChannel * 2];
    private readonly int[] _binStart = new int[BandsPerChannel];
    private readonly int[] _binEnd = new int[BandsPerChannel];

    private WasapiLoopbackCapture? _capture;
    private System.Threading.Timer? _emitTimer;
    private int _sampleRate = 48000;
    private string _deviceId = "";
    private bool _paused;
    private bool _disposed;

    public event Action<float[]>? SpectrumReady;

    public AudioCaptureService()
    {
        for (int i = 0; i < FftLength; i++)
        {
            _hann[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftLength - 1)));
        }
        RebuildBandMap(_sampleRate);
    }

    public static List<AudioDeviceInfo> ListOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(dev.ID, dev.FriendlyName, dev.ID == defaultId));
                dev.Dispose();
            }
        }
        catch { }
        return list;
    }

    public void Start(string? deviceId = null, int fps = 30)
    {
        Stop();
        _deviceId = deviceId ?? "";

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(_deviceId))
            {
                try { device = enumerator.GetDevice(_deviceId); } catch { device = null; }
            }
            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _capture = new WasapiLoopbackCapture(device);
            _sampleRate = _capture.WaveFormat.SampleRate;
            RebuildBandMap(_sampleRate);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch
        {
            // No audio endpoint available; timer will still emit zeros gracefully
        }

        int intervalMs = Math.Clamp(1000 / Math.Clamp(fps, 15, 60), 16, 66);
        _emitTimer = new System.Threading.Timer(EmitSpectrum, null, intervalMs, intervalMs);
    }

    public void SetPaused(bool paused) => _paused = paused;

    public void Stop()
    {
        _emitTimer?.Dispose();
        _emitTimer = null;

        if (_capture != null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.StopRecording();
            }
            catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }

    private void RebuildBandMap(int sampleRate)
    {
        // Map 64 bands logarithmically from ~30 Hz to ~16000 Hz
        double minFreq = 30.0;
        double maxFreq = Math.Min(16000.0, sampleRate * 0.45);
        int maxBin = FftLength / 2 - 1;

        for (int b = 0; b < BandsPerChannel; b++)
        {
            double f0 = minFreq * Math.Pow(maxFreq / minFreq, (double)b / BandsPerChannel);
            double f1 = minFreq * Math.Pow(maxFreq / minFreq, (double)(b + 1) / BandsPerChannel);
            int i0 = Math.Clamp((int)Math.Floor(f0 * FftLength / sampleRate), 1, maxBin);
            int i1 = Math.Clamp((int)Math.Ceiling(f1 * FftLength / sampleRate), i0, maxBin);
            _binStart[b] = i0;
            _binEnd[b] = i1;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture == null) return;
        var fmt = _capture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample != 4 || fmt.Encoding != WaveFormatEncoding.IeeeFloat) return;

        int frameSize = channels * 4;
        int frames = e.BytesRecorded / frameSize;

        lock (_lock)
        {
            int offset = 0;
            for (int f = 0; f < frames; f++)
            {
                float l = BitConverter.ToSingle(e.Buffer, offset);
                float r = channels > 1 ? BitConverter.ToSingle(e.Buffer, offset + 4) : l;
                _ringL[_ringPos] = l;
                _ringR[_ringPos] = r;
                _ringPos = (_ringPos + 1) & (FftLength - 1);
                offset += frameSize;
            }
            _lastDataTicks = Environment.TickCount64;
        }
    }

    private void EmitSpectrum(object? state)
    {
        if (_disposed || _paused) return;
        var handler = SpectrumReady;
        if (handler == null) return;

        bool hasRecentAudio;
        lock (_lock)
        {
            hasRecentAudio = (Environment.TickCount64 - _lastDataTicks) < 250;
            if (hasRecentAudio)
            {
                int pos = _ringPos;
                for (int i = 0; i < FftLength; i++)
                {
                    int idx = (pos + i) & (FftLength - 1);
                    float w = _hann[i];
                    _fftL[i].X = _ringL[idx] * w;
                    _fftL[i].Y = 0;
                    _fftR[i].X = _ringR[idx] * w;
                    _fftR[i].Y = 0;
                }
            }
        }

        if (hasRecentAudio)
        {
            FastFourierTransform.FFT(true, FftM, _fftL);
            FastFourierTransform.FFT(true, FftM, _fftR);

            for (int b = 0; b < BandsPerChannel; b++)
            {
                int i0 = _binStart[b];
                int i1 = _binEnd[b];
                float peakL = 0, peakR = 0;
                for (int i = i0; i <= i1; i++)
                {
                    float ml = MathF.Sqrt(_fftL[i].X * _fftL[i].X + _fftL[i].Y * _fftL[i].Y);
                    float mr = MathF.Sqrt(_fftR[i].X * _fftR[i].X + _fftR[i].Y * _fftR[i].Y);
                    if (ml > peakL) peakL = ml;
                    if (mr > peakR) peakR = mr;
                }

                // Slight tilt boost for higher frequencies & perceptual curve matching Wallpaper Engine
                float tilt = 1.0f + (b / (float)BandsPerChannel) * 2.2f;
                float vL = MathF.Min(1.35f, MathF.Pow(peakL * 14f * tilt, 0.72f));
                float vR = MathF.Min(1.35f, MathF.Pow(peakR * 14f * tilt, 0.72f));

                _smooth[b] = vL > _smooth[b] ? _smooth[b] * 0.35f + vL * 0.65f : _smooth[b] * 0.78f + vL * 0.22f;
                _smooth[BandsPerChannel + b] = vR > _smooth[BandsPerChannel + b]
                    ? _smooth[BandsPerChannel + b] * 0.35f + vR * 0.65f
                    : _smooth[BandsPerChannel + b] * 0.78f + vR * 0.22f;
            }
        }
        else
        {
            // Smooth decay to zero during silence
            for (int i = 0; i < _smooth.Length; i++)
            {
                _smooth[i] *= 0.75f;
                if (_smooth[i] < 0.001f) _smooth[i] = 0f;
            }
        }

        Array.Copy(_smooth, _bands, _bands.Length);
        handler(_bands);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
