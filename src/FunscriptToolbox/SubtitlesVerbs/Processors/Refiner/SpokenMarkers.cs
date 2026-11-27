using FunscriptToolbox.SubtitlesVerbs.Inputs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// The spoken markers of <see cref="TimingRefineEngineSpokenMarkers"/>: short English number words (a synthetic male voice) inserted in
    /// the audio of a clip, at the possible places of a boundary. The AI transcribes the clip with the markers inline ("[20]"), so the
    /// place of each marker relative to the words gives the boundaries.
    ///
    /// The labels are the ones that worked in the tests (scratchbook T07-T16): long words only (~280-310 ms each: 'five' / 'eight' were
    /// not heard inside the speech), no teens with the tens ('thirteen' / 'thirty' sound alike), and the AI always gets them in a
    /// random order (an ordered list is "counted" instead of heard). Embedded resources: 16 kHz, mono, 16 bits, normalized (RMS 1000).
    /// </summary>
    public static class SpokenMarkers
    {
        /// <summary>The labels (the word, and the number the AI writes for it).</summary>
        public static readonly IReadOnlyList<(string Word, int Number)> Labels = new[]
        {
            ("eleven", 11), ("twelve", 12), ("twenty", 20), ("thirty", 30), ("forty", 40), ("fifty", 50),
            ("sixty", 60), ("seventy", 70), ("eighty", 80), ("ninety", 90), ("hundred", 100), ("thousand", 1000),
        };

        public const int SamplingRate = 16000;

        /// <summary>The RMS of the embedded words (they are scaled from it).</summary>
        private const double ResourceRms = 1000.0;

        /// <summary>The level of a marker: this fraction of the RMS of the loud part of the clip (as in the tests).</summary>
        private const double LevelOfSpeech = 0.9;

        /// <summary>A sample above this (16 bits) is in the "loud part" of the clip.</summary>
        private const int LoudSampleThreshold = 500;

        /// <summary>The level used when the clip has almost no loud samples (less than 0.1 s).</summary>
        private const double DefaultSpeechRms = 3000.0;

        private static readonly Lazy<Dictionary<string, short[]>> r_samples = new Lazy<Dictionary<string, short[]>>(LoadAll);

        public static int NumberOf(string word) => Labels.First(l => l.Word == word).Number;

        public static TimeSpan DurationOf(string word)
            => TimeSpan.FromSeconds((double)SamplesOf(word).Length / SamplingRate);

        private static short[] SamplesOf(string word)
            => r_samples.Value.TryGetValue(word, out var samples)
                ? samples
                : throw new InvalidOperationException($"Unknown spoken marker '{word}'.");

        private static Dictionary<string, short[]> LoadAll()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var result = new Dictionary<string, short[]>();
            foreach (var (word, _) in Labels)
            {
                var name = $"FunscriptToolbox.SubtitlesVerbs.Processors.Refiner.Markers.{word}.wav";
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The spoken marker '{name}' is not embedded in the application.");
                result[word] = ReadWav(stream, name);
            }
            return result;
        }

        /// <summary>A plain PCM .wav: 16 kHz, mono, 16 bits (anything else is an error: the resources are made that way).</summary>
        private static short[] ReadWav(Stream stream, string name)
        {
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "RIFF")
                throw new InvalidOperationException($"'{name}' is not a .wav file.");
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
                throw new InvalidOperationException($"'{name}' is not a .wav file.");

            bool formatOk = false;
            while (stream.Position < stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();
                if (chunkId == "fmt ")
                {
                    var format = reader.ReadInt16();
                    var channels = reader.ReadInt16();
                    var rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    var bits = reader.ReadInt16();
                    if (chunkSize > 16)
                        reader.ReadBytes(chunkSize - 16);
                    if (format != 1 || channels != 1 || rate != SamplingRate || bits != 16)
                        throw new InvalidOperationException($"'{name}' must be PCM, mono, {SamplingRate} Hz, 16 bits.");
                    formatOk = true;
                }
                else if (chunkId == "data")
                {
                    if (!formatOk)
                        throw new InvalidOperationException($"'{name}': no 'fmt ' chunk before the data.");
                    var bytes = reader.ReadBytes(chunkSize);
                    var samples = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
                    return samples;
                }
                else
                {
                    reader.ReadBytes(chunkSize);
                }
            }
            throw new InvalidOperationException($"'{name}': no data in the .wav file.");
        }

        /// <summary>
        /// The audio of a clip with its pieces: [LeadingPieces] + [Start, End] with each marker of Inserts INSERTED at its time
        /// + [TrailingPieces]. The markers are scaled to the loudness of the speech of the clip.
        /// </summary>
        public static PcmAudio BuildClipAudio(PcmAudio source, TimingClipOption clip)
        {
            if (clip.Inserts.Count > 0 && source.SamplingRate != SamplingRate)
                throw new InvalidOperationException(
                    $"The spoken markers are {SamplingRate} Hz, the audio is {source.SamplingRate} Hz: they can't be inserted.");

            var parts = new List<PcmAudio>();
            parts.AddRange(clip.LeadingPieces.Select(p => source.ExtractSnippet(p.From, p.To)));

            var main = source.ExtractSnippet(clip.Start, clip.End);
            if (clip.Inserts.Count == 0)
            {
                parts.Add(main);
            }
            else
            {
                var gain = SpeechRms(main) * LevelOfSpeech / ResourceRms;
                var from = clip.Start;
                foreach (var insert in clip.Inserts.OrderBy(i => i.At))
                {
                    var at = insert.At < clip.Start ? clip.Start : insert.At > clip.End ? clip.End : insert.At;
                    if (at > from)
                        parts.Add(source.ExtractSnippet(from, at));
                    parts.Add(Scaled(insert.Label, gain));
                    from = at;
                }
                if (clip.End > from)
                    parts.Add(source.ExtractSnippet(from, clip.End));
            }

            parts.AddRange(clip.TrailingPieces.Select(p => source.ExtractSnippet(p.From, p.To)));
            return new PcmAudio(parts);
        }

        /// <summary>The RMS of the loud samples of the audio (DefaultSpeechRms when there are almost none).</summary>
        private static double SpeechRms(PcmAudio audio)
        {
            var data = audio.Data;
            double sum = 0;
            int count = 0;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                int sample = (short)(data[i] | (data[i + 1] << 8));
                if (Math.Abs(sample) > LoudSampleThreshold)
                {
                    sum += (double)sample * sample;
                    count++;
                }
            }
            return count > SamplingRate / 10 ? Math.Sqrt(sum / count) : DefaultSpeechRms;
        }

        private static PcmAudio Scaled(string word, double gain)
        {
            var samples = SamplesOf(word);
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var value = (int)Math.Round(samples[i] * gain);
                var clamped = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
                bytes[i * 2] = (byte)(clamped & 0xFF);
                bytes[i * 2 + 1] = (byte)((clamped >> 8) & 0xFF);
            }
            return new PcmAudio(SamplingRate, bytes);
        }
    }
}
