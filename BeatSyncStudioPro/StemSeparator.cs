using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// HTDemucs stem separation via ONNX Runtime (model weights are Meta's
    /// HTDemucs, MIT-licensed; downloaded on demand, never bundled).
    /// The song is processed in 7.8-second segments (the model's training
    /// length) with 25% overlap and linear crossfades, exactly the way the
    /// reference Demucs apply-model works, minus the extra shift passes.
    /// Output order from the model: drums, bass, other, vocals.
    /// </summary>
    public static class StemSeparator
    {
        public const int SegmentSamples = 343980; // 7.8 s at 44.1 kHz
        public static readonly string[] StemNames = { "drums", "bass", "other", "vocals" };

        public static (float[] Left, float[] Right) DecodeBytes(byte[] bytes, string soundType)
        {
            IWaveProvider reader = soundType.ToLower() == "wav"
                ? new WaveFileReader(new MemoryStream(bytes))
                : (IWaveProvider)new Mp3FileReader(new MemoryStream(bytes));
            return ReadAll(reader.ToSampleProvider());
        }

        public static (float[] Left, float[] Right) DecodeFile(string path)
        {
            using var reader = new MediaFoundationReader(path);
            return ReadAll(reader.ToSampleProvider());
        }

        private static (float[] Left, float[] Right) ReadAll(ISampleProvider provider)
        {
            if (provider.WaveFormat.Channels == 1) provider = provider.ToStereo();
            if (provider.WaveFormat.SampleRate != 44100) provider = new WdlResamplingSampleProvider(provider, 44100);
            var all = new List<float>();
            var buffer = new float[44100 * 2];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++) all.Add(buffer[i]);
            }
            int frames = all.Count / 2;
            var left = new float[frames];
            var right = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                left[i] = all[i * 2];
                right[i] = all[i * 2 + 1];
            }
            return (left, right);
        }

        public static async Task SplitAsync(float[] left, float[] right, string modelPath, string outFolder, string baseName,
            IProgress<double>? progress, CancellationToken token)
        {
            await Task.Run(() =>
            {
                int total = left.Length;
                if (total == 0) throw new InvalidOperationException("The source audio is empty.");
                int overlap = SegmentSamples / 4;
                int stride = SegmentSamples - overlap;

                var acc = new float[4][][];
                for (int s = 0; s < 4; s++) acc[s] = new[] { new float[total], new float[total] };
                var weights = new float[total];

                using var session = new InferenceSession(modelPath);
                string inputName = session.InputMetadata.Keys.First();

                for (int offset = 0; offset < total; offset += stride)
                {
                    token.ThrowIfCancellationRequested();
                    int chunkLen = Math.Min(SegmentSamples, total - offset);
                    bool isFirst = offset == 0;
                    bool isLast = offset + chunkLen >= total;

                    // Normalize the chunk by its RMS (Demucs reference behaviour)
                    double sumSq = 0;
                    for (int i = 0; i < chunkLen; i++)
                    {
                        double l = left[offset + i];
                        double r = right[offset + i];
                        sumSq += l * l + r * r;
                    }
                    double std = Math.Sqrt(sumSq / Math.Max(1, chunkLen * 2));
                    if (std < 1e-6) std = 1;

                    var input = new DenseTensor<float>(new[] { 1, 2, SegmentSamples });
                    for (int i = 0; i < chunkLen; i++)
                    {
                        input[0, 0, i] = (float)(left[offset + i] / std);
                        input[0, 1, i] = (float)(right[offset + i] / std);
                    }

                    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
                    using var results = session.Run(inputs);
                    var output = results.First().AsTensor<float>();
                    int outLen = output.Dimensions.Length >= 4 ? output.Dimensions[3] : SegmentSamples;
                    int valid = Math.Min(chunkLen, outLen);

                    for (int i = 0; i < valid; i++)
                    {
                        double w = Weight(i, valid, isFirst, isLast, overlap);
                        weights[offset + i] += (float)w;
                        for (int s = 0; s < 4; s++)
                        {
                            acc[s][0][offset + i] += (float)(output[0, s, 0, i] * std * w);
                            acc[s][1][offset + i] += (float)(output[0, s, 1, i] * std * w);
                        }
                    }

                    progress?.Report((offset + chunkLen) / (double)total);
                    if (isLast) break;
                }

                Directory.CreateDirectory(outFolder);
                for (int s = 0; s < 4; s++)
                {
                    var interleaved = new float[total * 2];
                    for (int i = 0; i < total; i++)
                    {
                        float wsum = weights[i] > 1e-6f ? weights[i] : 1f;
                        interleaved[i * 2] = acc[s][0][i] / wsum;
                        interleaved[i * 2 + 1] = acc[s][1][i] / wsum;
                    }
                    string path = Path.Combine(outFolder, $"{baseName} ({StemNames[s]}).wav");
                    WaveFileWriter.CreateWaveFile16(path, new ArraySampleProvider(interleaved));
                }
                progress?.Report(1.0);
            }, token);
        }

        private static double Weight(int i, int len, bool isFirst, bool isLast, int overlap)
        {
            double w = 1;
            if (!isFirst && i < overlap) w = Math.Min(w, (i + 1.0) / overlap);
            if (!isLast && i >= len - overlap) w = Math.Min(w, (len - i) / (double)overlap);
            return w;
        }
    }
}
