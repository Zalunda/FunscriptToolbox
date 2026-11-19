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

namespace FunscriptToolbox.Progress
{
    /// <summary>
    /// A tiny local web server (HttpListener, no extra dependency) that serves the progress page.
    /// Only listens on localhost.
    /// </summary>
    public class WebUiServer : IDisposable
    {
        private const string PageResourceName = "FunscriptToolbox.Progress.ui.html";

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
                else if (method == "POST" && path.StartsWith("/api/cancel/"))
                {
                    var ok = int.TryParse(path.Substring("/api/cancel/".Length), out var nodeId) && r_output.RequestCancel(nodeId);
                    WriteResponse(context, ok ? 200 : 404, "text/plain", ok ? "OK" : "Unknown node.");
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
