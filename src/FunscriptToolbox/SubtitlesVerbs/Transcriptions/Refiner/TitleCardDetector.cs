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
        public double ChangeRatio { get; init; } = 0.30;   // share of text pixels that are new = new card
        public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(1);

        /// The fade in/out of a card (text brighter/dimmer than BrightLevel, so not detected as a card) is followed through the
        /// brightness of the text, for at most this long on each side. Zero: the fades are not followed.
        public TimeSpan MaxFadeDuration { get; init; } = TimeSpan.FromSeconds(1.5);
    }

    public sealed class TitleCardDetector
    {
        /// How often (in video time) the progress is reported.
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMinutes(1);

        // Fades: the brightness of the text is averaged over this many frames to follow its trend.
        private const int FadeSmoothingFrames = 5;

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

            // Mean brightness of the zone for every frame (see Analyze), to follow the fades
            var levels = new List<double>();
            int maxFadeFrames = (int)Math.Ceiling(_o.MaxFadeDuration.TotalSeconds * fps);
            int smoothing = FadeSmoothingFrames;
            long lastEndFrame = 0;      // the frame where the last segment was closed (a fade-in cannot go before it)
            bool fadingOut = false;     // the card is over, its fade out is being followed (segStart is still open)
            long fadeOutFrame = 0;      // first frame after the card

            TimeSpan TimeAt(long frame) => startAt + TimeSpan.FromSeconds(frame / fps);

            void Close(long frame)
            {
                var end = TimeAt(frame);
                if (segStart is { } s && end > s) raw.Add(new TitleCardSegment(s, end));
                segStart = null;
                lastEndFrame = frame;
            }

            double Mean(long from, long to)
            {
                from = Math.Max(0, from);
                to = Math.Min(levels.Count - 1, to);
                if (to < from) return double.NaN;
                double sum = 0;
                for (long i = from; i <= to; i++) sum += levels[(int)i];
                return sum / (to - from + 1);
            }

            // The text of the card was fading in before 'frame' (its first detected frame): where does the fade start?
            long FadeInStart(long frame)
            {
                long limit = Math.Max(lastEndFrame, frame - maxFadeFrames);
                long start = frame;
                while (start - 1 >= limit)
                {
                    // Still brightening: the average of the frames from j on is above the average of the frames before j
                    long j = start - 1;
                    double after = Mean(j, j + smoothing - 1);
                    double before = Mean(j - smoothing, j - 1);
                    if (double.IsNaN(before) || before >= after)
                        break;
                    start = j;
                }
                return start;
            }

            // The card is over: is the text still dimming at 'frame'? (average of the last frames below the average of the ones before)
            bool FadeOutContinues(long frame)
            {
                if (frame - fadeOutFrame >= maxFadeFrames)
                    return false;

                double older = Mean(frame - 2 * smoothing + 1, frame - smoothing);
                double recent = Mean(frame - smoothing + 1, frame);
                return !double.IsNaN(older) && older > recent;
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

                    // The next card may start with a fade in, going back before 'now' (but not before the last segment)
                    if (segStart == null)
                        boundary = TimeSpan.FromTicks(Math.Min(boundary.Ticks, Math.Max((now - _o.MaxFadeDuration).Ticks, TimeAt(lastEndFrame).Ticks)));
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
                    long frame = index++;
                    var t = TimeAt(frame);

                    Analyze(buf, cur, prev, hasPrev, out bool isCard, out bool changed, out double level);
                    levels.Add(level);

                    if (fadingOut)
                    {
                        if (!isCard && FadeOutContinues(frame))
                        {
                            hasPrev = false;
                            goto frameDone;
                        }
                        fadingOut = false;
                        Close(frame); // the fade is over (or a new card starts)
                    }

                    if (!isCard)
                    {
                        if (segStart != null)
                        {
                            // The card is over: follow its fade out (the trend cannot be seen yet, the card was flat until now)
                            fadeOutFrame = frame;
                            if (maxFadeFrames > 0)
                                fadingOut = true;
                            else
                                Close(frame);
                        }
                        hasPrev = false;
                    }
                    else
                    {
                        if (segStart != null && changed) Close(frame); // new card right after previous one
                        segStart ??= TimeAt(FadeInStart(frame));

                        (prev, cur) = (cur, prev);
                        hasPrev = true;
                    }

                frameDone:

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
            var videoEnd = TimeAt(index);
            Close(index); // video ended during a card

            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg failed: {await errTask}");

            Report(videoEnd, final: true);
            return Cleanup(raw);
        }

        // Single pass: classify the frame, build the text mask, compare with previous mask.
        // Also gives the mean brightness of the zone ('level'), whether or not the frame is a card:
        // a fading text raises it smoothly, even when it is too dim to count as bright pixels.
        private bool Analyze(byte[] f, bool[] mask, bool[] prev, bool hasPrev, out bool isCard, out bool changed, out double level)
        {
            int dark = 0, bright = 0, diff = 0;
            long sum = 0;
            for (int i = 0; i < f.Length; i++)
            {
                byte p = f[i];
                sum += p;
                bool b = p >= _o.BrightLevel;
                mask[i] = b;
                if (p <= _o.DarkLevel) dark++;
                else if (b) bright++;
                if (hasPrev && b && !prev[i]) diff++; // only new text: a text that dims (fade out) is not a new card
            }
            double n = f.Length;
            isCard = dark / n >= _o.MinDarkRatio
                && bright / n >= _o.MinBrightRatio
                && bright / n <= _o.MaxBrightRatio;
            changed = hasPrev && diff > Math.Max(8, bright * _o.ChangeRatio);
            level = sum / n;
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
