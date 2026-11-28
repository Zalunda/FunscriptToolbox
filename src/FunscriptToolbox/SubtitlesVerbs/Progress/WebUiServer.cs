using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        /// <summary>A new one when the first start is refused (a listener that failed to start can't be started again).</summary>
        private HttpListener m_listener = new HttpListener();
        /// <summary>The locks 'VID' / 'ENC' held by the Review editors open in a browser.</summary>
        private readonly ReviewLockLeases r_reviewLeases;
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
            r_reviewLeases = new ReviewLockLeases(output.Locks);
            Port = port;
            Host = host ?? "localhost";
        }

        public void Start()
        {
            try
            {
                StartListener();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5 && IsReachableFromNetwork && OperatingSystem.IsWindows())
            {
                // Access denied: on Windows, listening on the network needs an URL reservation (once, as administrator).
                // Windows is asked for it (UAC prompt), with a firewall rule for the port, then it's started again.
                var manualCommands =
                    $"    {UrlReservationCommand}\n"
                    + $"    {FirewallRuleCommand}\n";
                string refusal;
                try
                {
                    refusal = AddUrlReservationAsAdministrator();
                }
                catch (Exception ex2)
                {
                    refusal = ex2.Message;
                }
                if (refusal != null)
                {
                    throw new Exception(
                        $"Can't start the web UI on '{ListenUrl}': access denied, and the URL reservation couldn't be added ({refusal}).\n"
                        + "To allow it, run this once in a command prompt opened as administrator:\n"
                        + manualCommands, ex);
                }

                try
                {
                    m_listener = new HttpListener();
                    StartListener();
                }
                catch (HttpListenerException ex3)
                {
                    throw new Exception(
                        $"Can't start the web UI on '{ListenUrl}', even after adding the URL reservation: {ex3.Message}\n"
                        + "To allow it, run this once in a command prompt opened as administrator:\n"
                        + manualCommands, ex3);
                }
            }
            catch (HttpListenerException ex)
            {
                throw new Exception($"Can't start the web UI on '{ListenUrl}' (is the port already used?): {ex.Message}", ex);
            }

            m_thread = new Thread(Loop) { IsBackground = true, Name = "WebUiServer" };
            m_thread.Start();
        }

        private void StartListener()
        {
            m_listener.Prefixes.Add(ListenUrl);
            m_listener.Start();
        }

        private string FirewallRuleName => $"FunscriptToolbox {Port}";

        /// <summary>The URL reservation that allows this user to listen on the network (on Windows).</summary>
        private string UrlReservationCommand => $"netsh http add urlacl url={ListenUrl} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"";

        /// <summary>The firewall rule that lets the other machines of the local network (only) open the page.</summary>
        private string FirewallRuleCommand => $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={Port} remoteip=localsubnet";

        /// <summary>
        /// Adds the URL reservation and the firewall rule (replacing the previous one with the same name), in one process run as administrator:
        /// Windows asks the user (UAC prompt). Returns null when done, otherwise why not (ex. refused by the user).
        /// </summary>
        private string AddUrlReservationAsAdministrator()
        {
            Console.WriteLine($"The web UI needs a URL reservation to be reachable from the network ('{ListenUrl}'): asking Windows for it (administrator)...");
            var commands = string.Join(" & ",
                UrlReservationCommand,
                $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" >nul",
                FirewallRuleCommand);
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /s: only the first and the last quotes are removed, the ones of the commands are kept
                Arguments = $"/s /c \"{commands}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return "the command couldn't be started";
                    if (!process.WaitForExit(60000))
                        return "the command didn't end after 60 seconds";
                    // (the exit code is the one of the firewall rule, the last command: the reservation is checked by starting again)
                    if (process.ExitCode != 0)
                        Console.WriteLine($"Warning: the firewall rule '{FirewallRuleName}' couldn't be added (exit code {process.ExitCode}): the other machines may not reach the page.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return "refused in the Windows prompt";
            }
            Console.WriteLine($"URL reservation and firewall rule '{FirewallRuleName}' added.");
            return null;
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
            while (m_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = m_listener.GetContext();
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
                else if (method == "GET" && path.StartsWith("/api/flowconfig/"))
                {
                    // The configuration of a step of the 'Flow' graph, with where each value comes from: /api/flowconfig/<scene node id>?key=<key of the box>
                    var key = context.Request.QueryString["key"];
                    var view = int.TryParse(path.Substring("/api/flowconfig/".Length), out var sceneId) ? r_output.GetConfigView(sceneId) : null;
                    if (view == null) { WriteResponse(context, 404, "text/plain", "Unknown scene, or its configuration is not loaded (yet)."); return; }
                    try { WriteJson(context, view.GetWorkerConfig(key)); }
                    catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't read the configuration: {ex.Message}"); }
                }
                else if (method == "POST" && path.StartsWith("/api/flowconfig/"))
                {
                    // A value of the configuration of a step, written in (or removed from) a '.override.user.config': /api/flowconfig/<scene node id>
                    // (body: see ConfigChangeRequest; answer: { ok, file, warning, config } or { error })
                    var view = int.TryParse(path.Substring("/api/flowconfig/".Length), out var sceneId) ? r_output.GetConfigView(sceneId) : null;
                    if (view == null) { WriteResponse(context, 404, "text/plain", "Unknown scene, or its configuration is not loaded (yet)."); return; }
                    try { WriteJson(context, view.ChangeValue(JObject.Parse(ReadBody(context)).ToObject<ConfigChangeRequest>())); }
                    catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't change the configuration: {ex.Message}"); }
                }
                else if (method == "GET" && path.StartsWith("/api/scenefiles/"))
                {
                    // The files of a scene (the 'Files' tab): /api/scenefiles/<scene node id>; one of them: ?folder=<scene|backup>&name=<its name, as listed>
                    var q = context.Request.QueryString;
                    var view = int.TryParse(path.Substring("/api/scenefiles/".Length), out var sceneId) ? r_output.GetFilesView(sceneId) : null;
                    if (view == null) { WriteResponse(context, 404, "text/plain", "Unknown scene (started again? select it again), or its files are not known yet."); return; }
                    if (q["name"] == null)
                    {
                        try { WriteJson(context, view.List()); }
                        catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't list the files: {ex.Message}"); }
                        return;
                    }
                    SceneFileContent file;
                    try { file = view.Read(q["folder"], q["name"]); }
                    catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't read the file: {ex.Message}"); return; }
                    if (file == null) { WriteResponse(context, 404, "text/plain", "Not a file of the list of this scene (or it can't be shown here)."); return; }
                    WriteBytes(context, 200, file.ContentType, file.Bytes);
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
                        // 1. The one in memory or on disk (kept by the run): always, even when a Review editor is open
                        try { image = r_output.GetFlowImage(sceneId, q["key"], q["field"], start, end, onlyIfAvailable: true); }
                        catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't read the screenshot: {ex.Message}"); return; }
                        // 2. Otherwise, taken with ffmpeg (not for 'cached=1')
                        if (image == null && q["cached"] != "1")
                        {
                            // (a Review editor is open: it holds the lock of ffmpeg on the videos, a new screenshot would wait until it's closed)
                            if (r_reviewLeases.IsHeldByAReview(r_output.Locks.VideoSource))
                            {
                                WriteResponse(context, 503, "text/plain", $"Not kept on disk, and a Review editor is open: it holds the lock '{r_output.Locks.VideoSource.ShortName}' (no ffmpeg on the videos meanwhile). Close it to take the screenshot.");
                                return;
                            }
                            try { image = r_output.GetFlowImage(sceneId, q["key"], q["field"], start, end, onlyIfAvailable: false); }
                            catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't take the screenshot: {ex.Message}"); return; }
                        }
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
                    // (?mode=now|skip: 'Stop now', the 'skip' button of a task, see ITaskNode.SetSkippable;
                    //  'Pause after this request' is 'Pause scene': /api/scenepause)
                    var mode = context.Request.QueryString["mode"];
                    if (mode != "now" && mode != "skip")
                    {
                        WriteResponse(context, 400, "text/plain", "Missing or unknown 'mode' (now or skip).");
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
                else if (method == "POST" && path == "/api/privatekey")
                {
                    // An API key asked by a scene ('needs you'): body { name, value }, written in the .private.config (never sent back to
                    // the page); then 'Rescan': the scenes blocked are checked again (all those that wait for this key continue)
                    var setter = r_output.SetPrivateValue;
                    if (setter == null) { WriteResponse(context, 404, "text/plain", "Not possible here."); return; }
                    try
                    {
                        // (several keys at once, ex. the engines of a collection: body { items: [{ name, value }] }, the empty ones ignored, at least one)
                        var body = JObject.Parse(ReadBody(context));
                        var items = (body["items"] as JArray)?.OfType<JObject>().ToArray() ?? new[] { body };
                        var given = items
                            .Select(item => (Name: (string)item["name"], Value: ((string)item["value"])?.Trim()))
                            .Where(item => !string.IsNullOrEmpty(item.Value))
                            .ToArray();
                        if (given.Length == 0 || given.Any(item => string.IsNullOrEmpty(item.Name))) { WriteResponse(context, 400, "text/plain", "At least one key (its name and its value) is needed."); return; }
                        foreach (var (name, value) in given)
                            setter(name, value);
                        r_output.RequestRescan();
                        WriteResponse(context, 200, "text/plain", "OK");
                    }
                    catch (Exception ex) { WriteResponse(context, 500, "text/plain", $"Can't save it: {ex.Message}"); }
                }
                else if (method == "POST" && path.StartsWith("/api/redostep/"))
                {
                    // 'Redo this step': /api/redostep/<scene node id>?key=<key of the worker, ex. w3> (done by the main loop)
                    var ok = int.TryParse(path.Substring("/api/redostep/".Length), out var nodeId) && r_output.RequestRedoStep(nodeId, context.Request.QueryString["key"]);
                    WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown scene (or the application is closing).");
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
        /// - finalized?review=&lt;id&gt; (POST) : saves which subtitles are finalized by the user
        /// - done?review=&lt;id&gt; (POST)    : the review is done (the scene is rescanned: the worker writes the file)
        /// - gaps?review=&lt;id&gt; (POST)    : saves the parts listened to, to validate the gaps (JSON { ranges: [{ s, e }] }, ms)
        /// - hold?client=&amp;name= (POST)  : the editor is open: the locks VID / ENC are held (JSON: their state), see ReviewLockLeases
        /// - release?client= (POST)     : the editor is closed: its locks are released
        /// - peaks?audio=&amp;s=&amp;e=&amp;n=        : the waveform (binary: min / max, signed 16 bits, for each of the n bins)
        /// - audio.wav?audio=           : the whole audio (WAV, with ranges: the browser only reads what it plays)
        /// - snippet.wav?audio=&amp;s=&amp;e=     : a short part of the audio (WAV), ex. to play the start of a subtitle again and again
        /// - video?part=&lt;index&gt;         : the light video of a part ('.FAST.mp4', with ranges); &amp;original=1: the video itself (if it's there)
        /// </summary>
        private void HandleTiming(HttpListenerContext context, string method, string rest)
        {
            var slash = rest.IndexOf('/');
            var sceneId = slash > 0 && int.TryParse(rest.Substring(0, slash), out var id) ? id : -1;
            // (the locks of an editor: released even when its scene has no editor anymore, ex. it was started again)
            if (method == "POST" && slash > 0 && rest.Substring(slash + 1) == "release")
            {
                r_reviewLeases.Release(context.Request.QueryString["client"]);
                WriteResponse(context, 200, "text/plain", "OK");
                return;
            }
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
                else if (method == "POST" && what == "gaps")
                {
                    // body: { ranges: [{ s, e }] }: the parts listened to, to validate the gaps (ms; they replace the ones saved before)
                    var body = JObject.Parse(ReadBody(context));
                    editor.SaveValidatedGaps(q["review"], body["ranges"]?.ToObject<TimingEditorRange[]>());
                    WriteResponse(context, 200, "text/plain", "OK");
                }
                else if (method == "POST" && what == "hold")
                {
                    // ?client=<id of the page>&name=<scene>: the locks VID / ENC held while the editor is open (called again regularly, see ReviewLockLeases)
                    WriteJson(context, r_reviewLeases.Hold(q["client"], q["name"]));
                }
                else if (method == "POST" && what == "finalized")
                {
                    // body: { items: [{ k: "start ms-end ms", f }] }: the subtitles finalized by the user (known by their timing as saved)
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
                    var header = Inputs.PcmAudio.CreateWavHeader(samplingRate, pcm.Length);
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
                    var header = Inputs.PcmAudio.CreateWavHeader(samplingRate, length);
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
            try { m_listener.Stop(); m_listener.Close(); } catch { }
            r_reviewLeases.Dispose();
        }
    }
}
