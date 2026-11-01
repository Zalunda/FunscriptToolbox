using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public sealed record TitleCardSegment(TimeSpan StartTime, TimeSpan EndTime);

    public sealed class TitleCardDetectorOptions
    {
        /// false: the title cards are not detected (the other settings are not used).
        public bool Enabled { get; init; } = true;

        public double? SampleFps { get; init; }
        public string FfmpegFilter { get; init; } = "crop=iw*0.20:ih*0.06:iw*0.15:ih*0.47";

        public byte DarkLevel { get; init; } = 24;         // pixel <= this is "black"
        public byte BrightLevel { get; init; } = 140;      // pixel >= this is "text"
        public double MinDarkRatio { get; init; } = 0.95;  // share of the zone that must be black
        public double MinBrightRatio { get; init; } = 0.0005;
        public double MaxBrightRatio { get; init; } = 0.30; // zone is tight, text fills more of it
        public double ChangeRatio { get; init; } = 0.30;   // share of text pixels that differ = new card
        public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(1);
    }

    public sealed class TitleCardDetector
    {
        /// How often (in video time) the progress is reported.
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMinutes(1);

        private readonly string _ffmpegPath;
        private readonly TitleCardDetectorOptions _o;
        private readonly string _videoFilter;

        /// <param name="ffmpegPath">The ffmpeg executable (see FfmpegHelper.FfmpegPath).</param>
        public TitleCardDetector(string ffmpegPath, TitleCardDetectorOptions options = null)
        {
            _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
            _o = options ?? new();
            _videoFilter = _o.FfmpegFilter; // fails now, not in the middle of the detection
        }

        /// <summary>
        /// Detects the title cards of a video, starting at 'startAt' (to resume).
        /// Times are in the video (not in the timeline of all the parts).
        /// </summary>
        /// <param name="onProgress">
        /// About every minute of video: the cards that are now final (not reported before) and the time up to which everything is final.
        /// Resuming at that time gives the same result as not stopping (a card still in progress is detected again from its start).
        /// </param>
        /// <returns>All the cards found by this call.</returns>
        public async Task<List<TitleCardSegment>> DetectAsync(
            string videoPath,
            TimeSpan startAt,
            Action<IReadOnlyList<TitleCardSegment>, TimeSpan> onProgress,
            CancellationToken ct = default)
        {
            var info = await FFmpeg.GetMediaInfo(videoPath, ct);
            var video = info.VideoStreams.FirstOrDefault()
                ?? throw new InvalidOperationException("No video stream found.");

            double fps = _o.SampleFps ?? video.Framerate;
            if (fps <= 0 || double.IsNaN(fps)) fps = 25;

            var (w, h) = await GetFilteredSizeAsync(videoPath, ct);
            int frameSize = w * h;

            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (startAt > TimeSpan.Zero)
            {
                args.Add("-ss");
                args.Add(startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            args.AddRange(new[] {
                "-i", videoPath,
                "-vf", string.Create(CultureInfo.InvariantCulture, $"fps={fps},{_videoFilter},format=gray"),
                "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "gray", "-" });
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var errTask = proc.StandardError.ReadToEndAsync(); // prevents pipe deadlock

            var raw = new List<TitleCardSegment>();
            var buf = new byte[frameSize];
            var prev = new bool[frameSize];
            var cur = new bool[frameSize];
            bool hasPrev = false;
            TimeSpan? segStart = null;
            long index = 0;
            int reported = 0;
            var nextReport = startAt + ProgressInterval;

            void Close(TimeSpan end)
            {
                if (segStart is { } s && end > s) raw.Add(new TitleCardSegment(s, end));
                segStart = null;
            }

            // The cards that cannot change anymore, and the time up to which everything is final.
            void Report(TimeSpan now, bool final)
            {
                if (onProgress == null) return;

                var settled = new List<TitleCardSegment>(raw);
                var boundary = final ? now : (segStart ?? now);
                if (!final)
                {
                    // A short fragment touching the card in progress will be merged into it (see Cleanup): not final yet
                    while (settled.Count > 0
                        && settled[^1].EndTime == boundary
                        && settled[^1].EndTime - settled[^1].StartTime < _o.MinDuration)
                    {
                        boundary = settled[^1].StartTime;
                        settled.RemoveAt(settled.Count - 1);
                    }
                }

                var cleaned = Cleanup(settled);
                var fresh = cleaned.Skip(reported).ToList();
                reported = cleaned.Count;
                onProgress(fresh, boundary);
            }

            var stream = proc.StandardOutput.BaseStream;
            try
            {
                while (await ReadFullAsync(stream, buf, ct))
                {
                    var t = startAt + TimeSpan.FromSeconds(index++ / fps);

                    if (!Analyze(buf, cur, prev, hasPrev, out bool isCard, out bool changed))
                    { /* unreachable, kept simple */ }

                    if (!isCard)
                    {
                        Close(t);
                        hasPrev = false;
                    }
                    else
                    {
                        if (segStart != null && changed) Close(t); // new card right after previous one
                        segStart ??= t;

                        (prev, cur) = (cur, prev);
                        hasPrev = true;
                    }

                    if (t >= nextReport)
                    {
                        Report(t, final: false);
                        nextReport = t + ProgressInterval;
                    }
                }
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                throw;
            }
            var videoEnd = startAt + TimeSpan.FromSeconds(index / fps);
            Close(videoEnd); // video ended during a card

            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg failed: {await errTask}");

            Report(videoEnd, final: true);
            return Cleanup(raw);
        }

        // Single pass: classify the frame, build the text mask, compare with previous mask.
        private bool Analyze(byte[] f, bool[] mask, bool[] prev, bool hasPrev, out bool isCard, out bool changed)
        {
            int dark = 0, bright = 0, diff = 0;
            for (int i = 0; i < f.Length; i++)
            {
                byte p = f[i];
                bool b = p >= _o.BrightLevel;
                mask[i] = b;
                if (p <= _o.DarkLevel) dark++;
                else if (b) bright++;
                if (hasPrev && b != prev[i]) diff++;
            }
            double n = f.Length;
            isCard = dark / n >= _o.MinDarkRatio
                && bright / n >= _o.MinBrightRatio
                && bright / n <= _o.MaxBrightRatio;
            changed = hasPrev && diff > Math.Max(8, bright * _o.ChangeRatio);
            return true;
        }

        // Short fragments touching the next segment (text fading in) are merged into it;
        // other short ones (flashes) are dropped.
        private List<TitleCardSegment> Cleanup(List<TitleCardSegment> raw)
        {
            var result = new List<TitleCardSegment>();
            TimeSpan? carryStart = null;
            for (int i = 0; i < raw.Count; i++)
            {
                var s = raw[i];
                var start = carryStart ?? s.StartTime;
                carryStart = null;

                if (s.EndTime - s.StartTime >= _o.MinDuration && s.EndTime - start >= _o.MinDuration)
                {
                    result.Add(new TitleCardSegment(start, s.EndTime));
                }
                else if (i + 1 < raw.Count && raw[i + 1].StartTime == s.EndTime)
                {
                    carryStart = start; // adjacent: extend the next card backwards
                }
            }
            return result;
        }

        // Runs the user's filter on 1 frame via Xabe and reads the resulting image size.
        private async Task<(int w, int h)> GetFilteredSizeAsync(string videoPath, CancellationToken ct)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"tc_{Guid.NewGuid():N}.png");
            try
            {
                await FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-i \"{videoPath}\" -frames:v 1 -vf \"{_videoFilter}\"")
                    .SetOutput(tmp)
                    .Start(ct);

                var img = await FFmpeg.GetMediaInfo(tmp, ct);
                var v = img.VideoStreams.First();
                return (v.Width, v.Height);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static async Task<bool> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(read), ct);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }
    }
}
