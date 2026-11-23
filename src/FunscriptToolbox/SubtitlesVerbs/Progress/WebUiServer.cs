using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// A tiny local web server (HttpListener, no extra dependency) that serves the progress page.
    /// Only listens on localhost.
    /// </summary>
    public class WebUiServer : IDisposable
    {
        private const string PageResourceName = "FunscriptToolbox.SubtitlesVerbs.Progress.ui.html";

        private static readonly JsonSerializerSettings rs_jsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None
        };

        private readonly WebUiOutput r_output;
        private readonly HttpListener r_listener = new HttpListener();
        private Thread m_thread;

        public int Port { get; }

        /// <summary>'localhost' (only this machine), '*' (all the network interfaces), or a name / address of this machine.</summary>
        public string Host { get; }

        /// <summary>The address to open the page on this machine.</summary>
        public string Url => $"http://localhost:{Port}/";

        /// <summary>What the server listens to (ex. "http://*:5055/").</summary>
        public string ListenUrl => $"http://{Host}:{Port}/";

        /// <summary>True when the page can be opened from another machine.</summary>
        public bool IsReachableFromNetwork => !string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) && Host != "127.0.0.1";

        public WebUiServer(WebUiOutput output, int port, string host = null)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentException($"Invalid port '{port}' for --ui (must be between 1 and 65535).");
            if (host != null && (host.Length == 0 || host.IndexOfAny(new[] { '/', ':', ' ' }) >= 0))
                throw new ArgumentException($"Invalid host '{host}' for --runasserver (ex. 'localhost:5055', '*:5055', a name or an address of this machine).");

            r_output = output;
            Port = port;
            Host = host ?? "localhost";
        }

        public void Start()
        {
            r_listener.Prefixes.Add(ListenUrl);
            try
            {
                r_listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5 && IsReachableFromNetwork)
            {
                // Access denied: on Windows, listening on the network needs an URL reservation (once, as administrator)
                throw new Exception(
                    $"Can't start the web UI on '{ListenUrl}': access denied. To allow it, run this once in a command prompt opened as administrator:\n"
                    + $"    netsh http add urlacl url={ListenUrl} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"\n"
                    + $"(and allow the port {Port} in the firewall, for the other machines)", ex);
            }
            catch (HttpListenerException ex)
            {
                throw new Exception($"Can't start the web UI on '{ListenUrl}' (is the port already used?): {ex.Message}", ex);
            }

            m_thread = new Thread(Loop) { IsBackground = true, Name = "WebUiServer" };
            m_thread.Start();
        }

        public void OpenBrowser() => OpenBrowser(Url);

        public static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Can't open the browser automatically ({ex.Message}). Open '{url}' manually.");
            }
        }

        /// <summary>True when a FunscriptToolbox page answers on this address (ex. an instance already running in the background).</summary>
        public static bool IsAnswering(string url)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = client.GetAsync(url.TrimEnd('/') + "/api/state").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- Is the page open somewhere? ----

        private int m_hasClient;

        /// <summary>True once a page has asked for the state (i.e. the progress page is open in a browser).</summary>
        public bool HasClient => Volatile.Read(ref m_hasClient) == 1;

        /// <summary>
        /// When nobody opens the page: a warning in the console after 15 seconds, then a red line after 30 seconds
        /// (the browser is not opened automatically, unless --openbrowser).
        /// </summary>
        public void StartClientWatcher()
        {
            var watcher = new Thread(() =>
            {
                var steps = new (int delaySeconds, ConsoleColor color, string text)[]
                {
                    (15, ConsoleColor.Yellow, $"Nobody is looking at the progress page yet: open {Url} in your browser (or use --openbrowser)."),
                    (15, ConsoleColor.Red, $"Still nobody on the progress page: open {Url} in your browser to follow the work, answer the prompts and close the application."),
                };
                foreach (var (delaySeconds, color, text) in steps)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
                    if (HasClient)
                        return;
                    Console.ForegroundColor = color;
                    Console.WriteLine(text);
                    Console.ResetColor();
                }
            })
            { IsBackground = true, Name = "WebUiClientWatcher" };
            watcher.Start();
        }

        private int m_consoleListenerStarted;

        /// <summary>From now on, pressing ENTER in the console is like 'Close' in the page. Only one listener at a time.</summary>
        public void StartConsoleExitListener()
        {
            if (Console.IsInputRedirected || Interlocked.Exchange(ref m_consoleListenerStarted, 1) == 1)
                return;

            var consoleTask = new Thread(() =>
            {
                try { Console.ReadLine(); } catch { }
                Interlocked.Exchange(ref m_consoleListenerStarted, 0);
                r_output.RequestClose(force: false);
            })
            { IsBackground = true };
            consoleTask.Start();
        }

        private void Loop()
        {
            while (r_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = r_listener.GetContext();
                }
                catch (Exception)
                {
                    return; // listener stopped
                }

                // Requests are small and fast: handled one after the other is enough, but never let a failure kill the server.
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath;
                var method = context.Request.HttpMethod;

                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    WriteResponse(context, 200, "text/html; charset=utf-8", LoadPage());
                }
                else if (method == "GET" && path == "/api/state")
                {
                    if (Interlocked.Exchange(ref m_hasClient, 1) == 0)
                    {
                        // (so it's clear why there is no warning: ex. a tab of a previous run reconnected by itself)
                        Console.WriteLine($"The progress page is open in a browser ({Url}).");
                    }
                    WriteJson(context, r_output.CreateSnapshot());
                }
                else if (method == "GET" && path.StartsWith("/api/node/"))
                {
                    if (int.TryParse(path.Substring("/api/node/".Length), out var nodeId))
                    {
                        var detail = r_output.CreateNodeDetail(nodeId);
                        if (detail != null) { WriteJson(context, detail); return; }
                    }
                    WriteResponse(context, 404, "text/plain", "Unknown node.");
                }
                else if (method == "GET" && path.StartsWith("/api/flowinspect/"))
                {
                    // The items of a box of the 'Flow' graph: /api/flowinspect/<scene node id>?key=<key of the box>
                    var key = context.Request.QueryString["key"];
                    if (int.TryParse(path.Substring("/api/flowinspect/".Length), out var sceneId) && !string.IsNullOrEmpty(key))
                    {
                        var inspection = r_output.InspectFlow(sceneId, key);
                        if (inspection != null) { WriteJson(context, inspection); return; }
                    }
                    WriteResponse(context, 404, "text/plain", "Unknown scene, or nothing to inspect.");
                }
                else if (method == "GET" && path.StartsWith("/api/flowimage/"))
                {
                    // The screenshot of an item: /api/flowimage/<scene node id>?key=<worker>&field=<extractor>&s=<start>&e=<end> (times: hh:mm:ss.fff)
                    var q = context.Request.QueryString;
                    byte[] image = null;
                    if (int.TryParse(path.Substring("/api/flowimage/".Length), out var sceneId)
                        && TimeSpan.TryParse(q["s"], System.Globalization.CultureInfo.InvariantCulture, out var start)
                        && TimeSpan.TryParse(q["e"], System.Globalization.CultureInfo.InvariantCulture, out var end)
                        && !string.IsNullOrEmpty(q["key"]) && !string.IsNullOrEmpty(q["field"]))
                    {
                        try { image = r_output.GetFlowImage(sceneId, q["key"], q["field"], start, end, q["cached"] == "1"); }
                        catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't take the screenshot: {ex.Message}"); return; }
                    }
                    if (image == null) { WriteResponse(context, 404, "text/plain", "No screenshot for this item."); return; }
                    // (the same item: the same image, the browser can keep it)
                    context.Response.Headers["Cache-Control"] = "max-age=3600";
                    WriteBytes(context, 200, "image/jpeg", image);
                }
                else if (path.StartsWith("/api/timing/"))
                {
                    HandleTiming(context, method, path.Substring("/api/timing/".Length));
                }
                else if (method == "POST" && path.StartsWith("/api/cancel/"))
                {
                    // (?mode=now|after|undo|skip: 'Stop now', 'Stop after this request', undo 'Stop after this request',
                    //  the 'skip' button of a task, see ITaskNode.SetSkippable)
                    var mode = context.Request.QueryString["mode"];
                    if (mode != "now" && mode != "after" && mode != "undo" && mode != "skip")
                    {
                        WriteResponse(context, 400, "text/plain", "Missing or unknown 'mode' (now, after, undo or skip).");
                    }
                    else
                    {
                        var ok = int.TryParse(path.Substring("/api/cancel/".Length), out var nodeId) && r_output.RequestCancel(nodeId, mode);
                        WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown node, or not possible in its current state.");
                    }
                }
                else if (method == "POST" && path.StartsWith("/api/answer/"))
                {
                    var body = ReadBody(context);
                    var json = JObject.Parse(body);
                    var ok = int.TryParse(path.Substring("/api/answer/".Length), out var promptId)
                        && r_output.AnswerPrompt(promptId, (string)json["choice"], (string)json["text"]);
                    WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown prompt.");
                }
                else if (method == "POST" && path.StartsWith("/api/exit") && !r_output.AllowCloseFromPage)
                {
                    // --runasserver: the page can't close the application
                    WriteResponse(context, 403, "text/plain", "Closing from the page is disabled (--runasserver).");
                }
                else if (method == "POST" && path.StartsWith("/api/scenepause/"))
                {
                    // 'Pause scene' / 'Resume scene': /api/scenepause/<scene node id>?paused=1|0 (the scene pauses at its next safe point)
                    var ok = int.TryParse(path.Substring("/api/scenepause/".Length), out var nodeId)
                        && r_output.SetScenePaused(nodeId, context.Request.QueryString["paused"] == "1");
                    WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown scene.");
                }
                else if (method == "POST" && (path == "/api/pause" || path == "/api/resume"))
                {
                    // 'Pause all' / 'Resume all' (server): the scenes pause at their next safe point, the application stays open
                    r_output.SetPausedAll(path == "/api/pause");
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && path == "/api/exit")
                {
                    // Closes when the running scenes are finished ('closing' mode). ?force=1: closes right away, even if scenes are running.
                    WriteResponse(context, 200, "text/plain", "OK");
                    r_output.RequestClose(force: context.Request.QueryString["force"] == "1");
                }
                else if (method == "POST" && path == "/api/exit/cancel")
                {
                    r_output.CancelClose();
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && path == "/api/rescan")
                {
                    r_output.RequestRescan();
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && path.StartsWith("/api/retry/"))
                {
                    var ok = int.TryParse(path.Substring("/api/retry/".Length), out var nodeId) && r_output.RequestRetry(nodeId);
                    WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown scene.");
                }
                else
                {
                    WriteResponse(context, 404, "text/plain", "Not found.");
                }
            }
            catch (Exception ex)
            {
                try { WriteResponse(context, 500, "text/plain", ex.Message); } catch { }
            }
        }

        /// <summary>
        /// The 'Timing' editor of a scene: /api/timing/&lt;scene node id&gt;/&lt;what&gt;?... (times in seconds)
        /// - info                       : what can be edited and played (JSON)
        /// - subs?review=&lt;id&gt;           : the subtitles of a review (JSON, 404 when the review hasn't started yet)
        /// - terrain?audio=&lt;id&gt;         : where the sound is (JSON, { computing: true } while it's computed)
        /// - save?review=&lt;id&gt; (POST)    : saves the subtitles of a review (in the .wipsubtitles)
        /// - finalized?review=&lt;id&gt; (POST) : saves the edges finalized by the user
        /// - done?review=&lt;id&gt; (POST)    : the review is done (the scene is rescanned: the worker writes the file)
        /// - peaks?audio=&amp;s=&amp;e=&amp;n=        : the waveform (binary: min / max, signed 16 bits, for each of the n bins)
        /// - audio.wav?audio=           : the whole audio (WAV, with ranges: the browser only reads what it plays)
        /// - snippet.wav?audio=&amp;s=&amp;e=     : a short part of the audio (WAV), ex. to play the start of a subtitle again and again
        /// - video?part=&lt;index&gt;         : the light video of a part ('.FAST.mp4', with ranges); &amp;original=1: the video itself (if it's there)
        /// </summary>
        private void HandleTiming(HttpListenerContext context, string method, string rest)
        {
            var slash = rest.IndexOf('/');
            var sceneId = slash > 0 && int.TryParse(rest.Substring(0, slash), out var id) ? id : -1;
            var editor = sceneId >= 0 ? r_output.GetTimingEditor(sceneId) : null;
            if (editor == null)
            {
                WriteResponse(context, 404, "text/plain", "Unknown scene, or no Review editor for it (yet).");
                return;
            }
            var what = rest.Substring(slash + 1);
            var q = context.Request.QueryString;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            TimeSpan Seconds(string name) => double.TryParse(q[name], System.Globalization.NumberStyles.Float, culture, out var v) && v >= 0
                ? TimeSpan.FromSeconds(v)
                : throw new TimingEditorException($"Invalid or missing '{name}'.");

            try
            {
                if (method == "GET" && what == "info")
                {
                    WriteJson(context, editor.GetInfo());
                }
                else if (method == "GET" && what == "subs")
                {
                    var subs = editor.LoadSubtitles(q["review"]);
                    if (subs == null) WriteResponse(context, 404, "text/plain", $"The review '{q["review"]}' hasn't started yet: the scene starts it when the steps before it are done.");
                    else WriteJson(context, subs);
                }
                else if (method == "POST" && what == "save")
                {
                    // body: { stamp, force, subtitles: [{ s, e, lines, fs, fe }], speakers: [{ name, alias }] } => { stamp } (409: saved from somewhere else since it was loaded)
                    var body = JObject.Parse(ReadBody(context));
                    var subtitles = body["subtitles"]?.ToObject<TimingEditorSubtitle[]>();
                    var speakers = body["speakers"]?.ToObject<TimingEditorSpeaker[]>();
                    try
                    {
                        var newStamp = editor.SaveSubtitles(q["review"], (string)body["stamp"], (bool?)body["force"] == true, subtitles, speakers);
                        WriteJson(context, new { stamp = newStamp });
                    }
                    catch (TimingEditorConflictException ex)
                    {
                        WriteResponse(context, 409, "text/plain", ex.Message);
                    }
                }
                else if (method == "POST" && what == "finalized")
                {
                    // body: { items: [{ k: "start ms-end ms", s, e }] }: the edges finalized by the user (the subtitles known by their timing as saved)
                    var body = JObject.Parse(ReadBody(context));
                    editor.SaveFinalized(q["review"], body["items"]?.ToObject<TimingEditorFinalized[]>());
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && what == "activetime")
                {
                    // body: { ms }: time of active work in the editor since the last time
                    var body = JObject.Parse(ReadBody(context));
                    editor.AddActiveTime(q["review"], (long?)body["ms"] ?? 0);
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && what == "done")
                {
                    // (the worker writes the file at its next run: only this scene is started again, when it's not running anymore)
                    editor.MarkReviewDone(q["review"]);
                    var restarted = r_output.RequestRetry(sceneId);
                    WriteJson(context, new { restarted });
                }
                else if (method == "GET" && what == "terrain")
                {
                    WriteJson(context, editor.GetTerrain(q["audio"]));
                }
                else if (method == "GET" && what == "peaks")
                {
                    var nbBins = int.TryParse(q["n"], out var n) ? n : throw new TimingEditorException("Invalid or missing 'n'.");
                    var peaks = editor.GetPeaks(q["audio"], Seconds("s"), Seconds("e"), nbBins);
                    context.Response.Headers["Cache-Control"] = "no-store";
                    WriteBytes(context, 200, "application/octet-stream", peaks);
                }
                else if (method == "GET" && what == "audio.wav")
                {
                    var (samplingRate, pcm) = editor.GetAudio(q["audio"]);
                    var header = AudioExtractions.PcmAudio.CreateWavHeader(samplingRate, pcm.Length);
                    WriteRanged(context, "audio/wav", header.Length + pcm.LongLength, (position, buffer, count) =>
                    {
                        // (the header, then the samples)
                        var nbRead = 0;
                        while (nbRead < count && position < header.Length)
                            buffer[nbRead++] = header[position++];
                        var fromPcm = (int)Math.Min(count - nbRead, pcm.LongLength - (position - header.Length));
                        if (fromPcm > 0)
                            Array.Copy(pcm, position - header.Length, buffer, nbRead, fromPcm);
                        return nbRead + Math.Max(0, fromPcm);
                    });
                }
                else if (method == "GET" && what == "snippet.wav")
                {
                    var (samplingRate, pcm) = editor.GetAudio(q["audio"]);
                    var start = Math.Min(pcm.LongLength, 2 * (long)(Seconds("s").TotalSeconds * samplingRate));
                    var end = Math.Min(pcm.LongLength, 2 * (long)(Seconds("e").TotalSeconds * samplingRate));
                    var length = (int)Math.Max(0, Math.Min(end - start, 2L * samplingRate * 60));   // (at most a minute)
                    var header = AudioExtractions.PcmAudio.CreateWavHeader(samplingRate, length);
                    var bytes = new byte[header.Length + length];
                    Array.Copy(header, bytes, header.Length);
                    Array.Copy(pcm, start, bytes, header.Length, length);
                    context.Response.Headers["Cache-Control"] = "max-age=60";
                    WriteBytes(context, 200, "audio/wav", bytes);
                }
                else if (method == "GET" && what == "video")
                {
                    // (by the index of the part: never a path given by the page)
                    var videoPath = int.TryParse(q["part"], out var partIndex) ? editor.GetVideoPath(partIndex, q["original"] == "1") : null;
                    if (videoPath == null)
                    {
                        WriteResponse(context, 404, "text/plain", "No video for this part.");
                        return;
                    }
                    // (the light video is always a .mp4; the original video can be something else: the browser plays it, or not)
                    var videoType = Path.GetExtension(videoPath).ToLowerInvariant() switch
                    {
                        ".webm" => "video/webm",
                        ".mkv" => "video/x-matroska",
                        ".mov" => "video/quicktime",
                        ".m4v" => "video/mp4",
                        _ => "video/mp4"
                    };
                    using var file = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    WriteRanged(context, videoType, file.Length, (position, buffer, count) =>
                    {
                        file.Position = position;
                        return file.Read(buffer, 0, count);
                    });
                }
                else
                {
                    WriteResponse(context, 404, "text/plain", "Not found.");
                }
            }
            catch (TimingEditorException ex)
            {
                WriteResponse(context, 400, "text/plain", ex.Message);
            }
        }

        /// <summary>
        /// Sends some data, or only the part asked by the browser ('Range' header: the video / audio elements read what they play, and seek).
        /// readAt(position, buffer, count) reads from 'position' and returns the number of bytes read.
        /// </summary>
        private static void WriteRanged(HttpListenerContext context, string contentType, long totalLength, Func<long, byte[], int, int> readAt)
        {
            var response = context.Response;
            long from = 0, to = totalLength - 1;
            var isPartial = false;
            var range = context.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                // (only the first range: the browsers only ask for one)
                var spec = range.Substring("bytes=".Length).Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var startText = spec.Substring(0, dash);
                    var endText = spec.Substring(dash + 1);
                    if (startText.Length == 0 && long.TryParse(endText, out var suffixLength))
                    {
                        from = Math.Max(0, totalLength - suffixLength);
                        isPartial = true;
                    }
                    else if (long.TryParse(startText, out var start))
                    {
                        from = start;
                        if (endText.Length > 0 && long.TryParse(endText, out var end))
                            to = Math.Min(end, totalLength - 1);
                        isPartial = true;
                    }
                }
                if (isPartial && (from > to || from >= totalLength))
                {
                    response.StatusCode = 416;
                    response.Headers["Content-Range"] = $"bytes */{totalLength}";
                    response.ContentLength64 = 0;
                    response.OutputStream.Close();
                    return;
                }
            }

            response.StatusCode = isPartial ? 206 : 200;
            response.ContentType = contentType;
            response.Headers["Accept-Ranges"] = "bytes";
            if (isPartial)
                response.Headers["Content-Range"] = $"bytes {from}-{to}/{totalLength}";
            response.ContentLength64 = to - from + 1;
            try
            {
                var buffer = new byte[64 * 1024];
                var position = from;
                while (position <= to)
                {
                    var nbRead = readAt(position, buffer, (int)Math.Min(buffer.Length, to - position + 1));
                    if (nbRead <= 0)
                        break;
                    response.OutputStream.Write(buffer, 0, nbRead);
                    position += nbRead;
                }
                response.OutputStream.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is IOException || ex is ObjectDisposedException)
            {
                // The browser doesn't need the rest anymore (ex. it seeks somewhere else): normal, not an error.
                try { response.Abort(); } catch { }
            }
        }

        private static string LoadPage()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PageResourceName)
                ?? throw new Exception($"Embedded resource '{PageResourceName}' not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string ReadBody(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            return reader.ReadToEnd();
        }

        private static void WriteJson(HttpListenerContext context, object value)
        {
            WriteResponse(context, 200, "application/json; charset=utf-8", JsonConvert.SerializeObject(value, rs_jsonSettings));
        }

        private static void WriteBytes(HttpListenerContext context, int statusCode, string contentType, byte[] bytes)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void WriteResponse(HttpListenerContext context, int statusCode, string contentType, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            try { r_listener.Stop(); r_listener.Close(); } catch { }
        }
    }
}
