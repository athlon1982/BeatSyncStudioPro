using System;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Live microphone channel for the DJ window (v3.31). Captures the chosen
    /// input device (44.1kHz / 16-bit / mono) into a small lock-protected ring
    /// buffer; the channel itself is the sample provider the mixer pulls from,
    /// returning silence whenever the mic is off or the ring runs dry.
    /// Latency is announcement-grade (capture buffers + output), which is
    /// what talk-over DJ work needs. Level is a decaying peak for the meter
    /// and the auto-duck detector.
    /// </summary>
    public sealed class MicChannel : ISampleProvider, IDisposable
    {
        private const int RingFrames = 44100;   // one second of stereo
        private readonly float[] _ring = new float[RingFrames * 2];
        private readonly object _lock = new object();
        private int _writePos, _readPos, _count;
        private WaveInEvent? _waveIn;
        private volatile float _level;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        /// <summary>Voice-tuned defaults; the DJ window's Mic FX button edits this live.</summary>
        public TrackFxSettings Fx { get; } = new TrackFxSettings
        {
            EqOn = true, LowDb = -4, MidDb = 2, HighDb = 3,
            DelayOn = false,
            ReverbOn = true, ReverbSize = 0.5, ReverbDamp = 0.6, ReverbMix = 0.18
        };

        public bool IsLive => _waveIn != null;
        public float Level => _level;

        public static int DeviceCount => WaveInEvent.DeviceCount;

        public static string DeviceName(int device)
        {
            try { return WaveInEvent.GetCapabilities(device).ProductName; }
            catch { return "Microphone " + (device + 1); }
        }

        public void Start(int device)
        {
            Stop();
            var waveIn = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };
            waveIn.DataAvailable += OnDataAvailable;
            _waveIn = waveIn;
            waveIn.StartRecording();
        }

        public void Stop()
        {
            // v3.54 (audit #22): detach the callback and clear the field
            // BEFORE disposing, so a DataAvailable already in flight on the
            // capture thread can't run against a disposed device.
            var waveIn = _waveIn;
            if (waveIn != null)
            {
                _waveIn = null;
                waveIn.DataAvailable -= OnDataAvailable;
                try { waveIn.StopRecording(); } catch { }
                try { waveIn.Dispose(); } catch { }
            }
            lock (_lock) { _count = 0; _readPos = _writePos; }
            _level = 0;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            float peak = 0;
            int frames = e.BytesRecorded / 2;
            lock (_lock)
            {
                for (int i = 0; i < frames; i++)
                {
                    short s = BitConverter.ToInt16(e.Buffer, i * 2);
                    float f = s / 32768f;
                    float af = Math.Abs(f);
                    if (af > peak) peak = af;
                    _ring[_writePos * 2] = f;
                    _ring[_writePos * 2 + 1] = f;
                    _writePos = (_writePos + 1) % RingFrames;
                    if (_count < RingFrames) _count++;
                    else _readPos = (_readPos + 1) % RingFrames;   // drop oldest on overflow
                }
            }
            _level = peak > _level ? peak : Math.Max(peak, _level * 0.92f);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int frames = count / 2;
                for (int f = 0; f < frames; f++)
                {
                    if (_count > 0)
                    {
                        buffer[offset + f * 2] = _ring[_readPos * 2];
                        buffer[offset + f * 2 + 1] = _ring[_readPos * 2 + 1];
                        _readPos = (_readPos + 1) % RingFrames;
                        _count--;
                    }
                    else
                    {
                        buffer[offset + f * 2] = 0;
                        buffer[offset + f * 2 + 1] = 0;
                    }
                }
            }
            return count;
        }

        public void Dispose() => Stop();
    }
}
