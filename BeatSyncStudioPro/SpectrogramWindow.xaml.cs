using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Dsp;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Spectrogram of a clip: STFT magnitudes (2048-point FFT, Hann window)
    /// rendered as a heat map — a quick way to SEE hiss, hum and harshness.
    /// </summary>
    public partial class SpectrogramWindow : Window
    {
        private const int FftSize = 2048;
        private const int Cols = 864;
        private const int Rows = 300;

        public SpectrogramWindow(FixedBlock block)
        {
            InitializeComponent();
            TitleText.Text = $"🌈 Spectrogram — {block.Filename}";
            Loaded += async (s, e) =>
            {
                try
                {
                    var bitmap = await Task.Run(() => Compute(block));
                    SpecImage.Source = bitmap;
                    StatusText.Text = "Done. Tip: a constant horizontal line = hum or a steady tone; sparkle at the top = hats and sibilance.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Could not compute the spectrogram: " + ex.Message;
                }
            };
        }

        private static WriteableBitmap Compute(FixedBlock block)
        {
            var (l, r) = StemSeparator.DecodeBytes(block.RawBytes!, block.SoundType);
            int frames = l.Length;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++) mono[i] = (l[i] + r[i]) * 0.5f;

            var pixels = new byte[Cols * Rows * 4];
            var fft = new Complex[FftSize];
            int hop = Math.Max(FftSize / 2, frames / Cols);

            for (int col = 0; col < Cols; col++)
            {
                int start = col * hop;
                for (int i = 0; i < FftSize; i++)
                {
                    double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                    double v = start + i < frames ? mono[start + i] : 0;
                    fft[i].X = (float)(v * window);
                    fft[i].Y = 0;
                }
                FastFourierTransform.FFT(true, 11, fft);

                for (int row = 0; row < Rows; row++)
                {
                    double frac = 1.0 - row / (double)(Rows - 1); // 0 = bottom (low freq)
                    int bin = (int)(Math.Pow(frac, 2.0) * (FftSize / 2 - 1));
                    int binNext = (int)(Math.Pow(Math.Min(1, frac + 1.0 / Rows), 2.0) * (FftSize / 2 - 1));
                    double mag = 0;
                    for (int b = bin; b <= Math.Max(bin, binNext) && b < FftSize / 2; b++)
                    {
                        double re = fft[b].X, im = fft[b].Y;
                        mag = Math.Max(mag, Math.Sqrt(re * re + im * im));
                    }
                    double db = 20 * Math.Log10(mag + 1e-9);
                    double intensity = Math.Clamp((db + 75) / 55, 0, 1); // -75 dB floor .. -20 dB hot
                    var (cr, cg, cb) = HeatColor(intensity);
                    int idx = (row * Cols + col) * 4;
                    pixels[idx] = (byte)cb;
                    pixels[idx + 1] = (byte)cg;
                    pixels[idx + 2] = (byte)cr;
                    pixels[idx + 3] = 255;
                }
            }

            var bitmap = new WriteableBitmap(Cols, Rows, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, Cols, Rows), pixels, Cols * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Black → purple → red → orange → white heat gradient.</summary>
        private static (int R, int G, int B) HeatColor(double t)
        {
            double r, g, b;
            if (t < 0.25) { double k = t / 0.25; r = 40 * k; g = 0; b = 90 * k; }
            else if (t < 0.5) { double k = (t - 0.25) / 0.25; r = 40 + 160 * k; g = 0; b = 90 - 40 * k; }
            else if (t < 0.75) { double k = (t - 0.5) / 0.25; r = 200 + 55 * k; g = 140 * k; b = 50 - 50 * k; }
            else { double k = (t - 0.75) / 0.25; r = 255; g = 140 + 115 * k; b = 200 * k; }
            return ((int)r, (int)g, (int)b);
        }
    }
}
