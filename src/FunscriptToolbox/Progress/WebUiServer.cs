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
        private readonly ManualResetEventSlim r_exitRequested = new ManualResetEventSlim(false);
        private Thread m_thread;

        public int Port { get; }
        public string Url => $"http://localhost:{Port}/";

        public WebUiServer(WebUiOutput output, int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentException($"Invalid port '{port}' for --ui (must be between 1 and 65535).");

            r_output = output;
            Port = port;
        }

        public void Start()
        {
            r_listener.Prefixes.Add(Url);
            try
            {
                r_listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new Exception($"Can't start the web UI on '{Url}' (is the port already used?): {ex.Message}", ex);
            }

            m_thread = new Thread(Loop) { IsBackground = true, Name = "WebUiServer" };
            m_thread.Start();
        }

        public void OpenBrowser()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Can't open the browser automatically ({ex.Message}). Open '{Url}' manually.");
            }
        }

        /// <summary>Blocks until the user clicks 'Close' in the page, or presses ENTER in the console.</summary>
        public void WaitForExitRequest()
        {
            if (!Console.IsInputRedirected)
            {
                var consoleTask = new Thread(() =>
                {
                    try { Console.ReadLine(); } catch { }
                    r_exitRequested.Set();
                })
                { IsBackground = true };
                consoleTask.Start();
            }
            r_exitRequested.Wait();
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
                else if (method == "POST" && path == "/api/exit")
                {
                    WriteResponse(context, 200, "text/plain", "OK");
                    r_exitRequested.Set();
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
