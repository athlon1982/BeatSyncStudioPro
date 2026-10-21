using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Renders a small waveform thumbnail for an audio clip held in memory.
    /// Supports the formats the playback engine can decode (WAV and MP3).
    /// Anything else gets a flat placeholder instead of an exception, so the
    /// timeline can still draw the block.
    /// </summary>
    public static class WaveformRenderer
    {
        public static BitmapSource GenerateWaveformBitmap(byte[] audioBytes, string soundType, int width, int height)
        {
            if (width <= 0) width = 100;
            if (height <= 0) height = 35;

            float[] peaks = audioBytes != null && audioBytes.Length > 0
                ? ComputePeaks(audioBytes, soundType ?? string.Empty, width)
                : Array.Empty<float>();

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), 1.0);
                double mid = height / 2.0;

                if (peaks.Length == 0)
                {
                    // Placeholder: a single flat center line.
                    dc.DrawLine(pen, new Point(0, mid), new Point(width, mid));
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        double amp = peaks[Math.Min(x, peaks.Length - 1)] * (mid - 1);
                        if (amp < 0.5) amp = 0.5;
                        dc.DrawLine(pen, new Point(x + 0.5, mid - amp), new Point(x + 0.5, mid + amp));
                    }
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Decodes the audio and returns one peak (max absolute sample, 0..1)
        /// per horizontal pixel column.
        /// </summary>
        private static float[] ComputePeaks(byte[] audioBytes, string soundType, int columns)
        {
            try
            {
                using var ms = new MemoryStream(audioBytes);
                using WaveStream reader = soundType.ToLowerInvariant() == "wav"
                    ? new WaveFileReader(ms)
                    : new Mp3FileReader(ms);

                ISampleProvider samples = reader.ToSampleProvider();
                int channels = reader.WaveFormat.Channels;
                if (channels <= 0) channels = 1;

                // Read the whole clip frame by frame, keeping the loudest
                // absolute sample of each frame.
                var framePeaks = new List<float>();
                float[] buffer = new float[8192];
                int read;
                int inFrame = 0;
                float frameMax = 0;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        float v = Math.Abs(buffer[i]);
                        if (v > frameMax) frameMax = v;
                        inFrame++;
                        if (inFrame >= channels)
                        {
                            framePeaks.Add(frameMax);
                            frameMax = 0;
                            inFrame = 0;
                        }
                    }
                }
                if (inFrame > 0) framePeaks.Add(frameMax);
                if (framePeaks.Count == 0) return Array.Empty<float>();

                // Bucket the frames down (or up) to exactly one peak per column.
                var result = new float[columns];
                double framesPerColumn = (double)framePeaks.Count / columns;
                for (int x = 0; x < columns; x++)
                {
                    int start = (int)(x * framesPerColumn);
                    int end = (int)((x + 1) * framesPerColumn);
                    if (end <= start) end = start + 1;
                    if (start >= framePeaks.Count) start = framePeaks.Count - 1;
                    if (end > framePeaks.Count) end = framePeaks.Count;

                    float max = 0;
                    for (int i = start; i < end; i++)
                    {
                        if (framePeaks[i] > max) max = framePeaks[i];
                    }
                    result[x] = Math.Min(1.0f, max);
                }
                return result;
            }
            catch
            {
                return Array.Empty<float>();
            }
        }
    }
}
