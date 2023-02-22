using CommandLine;
using FunscriptToolbox.Core.MotionVectors;
using FunscriptToolbox.Core.MotionVectors.PluginMessages;
using FunscriptToolbox.InstallationFiles;
using FunscriptToolbox.UI;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    // TODO LATER:
    // mask in learn from script

    internal class VerbMotionVectorsOFSPluginServer : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.ofspluginserver", aliases: new[] { "mvs.ofsps" }, HelpText = "Starts a server to respond to plugin request.")]
        public class Options : OptionsBase
        {
            [Option('c', "channelbasefilepath", Required = true, HelpText = "Channel (i.e. file prefix) for the communication.")]
            public string ChannelBaseFilePath { get; set; }

            [Option('t', "timeout", Required = false, HelpText = "Timeout before the server stop byitself. Plugin need to send keepalive to keep it alive if no request is send during that time.", Default = 300)]
            public int TimeOutInSeconds { get; set; }

            [Option('d', "debugmode", Hidden = true, Required = false, HelpText = "Debug mode.", Default = false)]
            public bool DebugMode { get; set; }

            public TimeSpan TimeOut => TimeSpan.FromSeconds(this.TimeOutInSeconds);
        }

        private readonly Options r_options;
        private readonly Semaphore r_semaphore;
        private readonly JsonSerializerSettings r_jsonSetting;
        private readonly string r_requestFolder;
        private readonly string r_responseFolder;
        private FileSystemWatcher m_watcher;
        private MotionVectorsFileReader m_currentMvsReader;
        private FrameAnalyser m_currentFrameAnalyser;

        public VerbMotionVectorsOFSPluginServer(Options options)
            : base(rs_log, options)
        {
            r_options = options;
            r_semaphore = new Semaphore(0, int.MaxValue);
            r_jsonSetting = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                SerializationBinder = new KnownTypesBinder
                {
                    KnownTypes = new List<Type>
                    {
                        typeof(CheckVersionPluginRequest),
                        typeof(CreateRulesPluginRequest),
                        typeof(KeepAlivePluginRequest),
                    }
                }
            };
            r_requestFolder = Path.GetDirectoryName(r_options.ChannelBaseFilePath);
            r_responseFolder = Path.Combine(r_requestFolder, "Responses");

            m_currentMvsReader = null;
            m_currentFrameAnalyser = null;
            m_watcher = null;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            Directory.CreateDirectory(r_responseFolder);

            m_watcher = new FileSystemWatcher();
            m_watcher.Path = r_requestFolder;
            m_watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
            m_watcher.Changed += OnCreated;
            m_watcher.EnableRaisingEvents = true;

            foreach (var file in Directory.GetFiles(r_requestFolder))
            {
                OnCreated(m_watcher, new FileSystemEventArgs(WatcherChangeTypes.Created, r_requestFolder, Path.GetFileName(file)));
            }

            while (true)
            {
                if (!r_semaphore.WaitOne(r_options.TimeOut))
                {
                    WriteInfo($"Server: Time out reached ({r_options.TimeOut}). Server is closing down.");
                    return 0;
                }
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.StartsWith(r_options.ChannelBaseFilePath))
            {
                WriteVerbose($"Server: Received file '{Path.GetFileName(e.FullPath)}', ignoring since it's not starting with our base path '{Path.GetFileName(r_options.ChannelBaseFilePath)}'", ConsoleColor.DarkGray);
                return;
            }

            try
            {
                if (File.Exists(e.FullPath))
                {
                    WaitUntilFileIsReadable(e.FullPath, TimeSpan.FromSeconds(30));
                    var content = File.ReadAllText(e.FullPath);
                    var request = JsonConvert.DeserializeObject<PluginRequest>(content, r_jsonSetting);
                    if (!r_options.DebugMode)
                    {
                        File.Delete(e.FullPath);
                    }

                    PluginResponse response;
                    if (request is CheckVersionPluginRequest checkVersionRequest)
                    {
                        response = new CheckVersionPluginResponse()
                            {
                                LastestVersion = PluginClient.Version
                            };
                    }
                    else if (request is KeepAlivePluginRequest keepAliveRequest)
                    {
                        response = new KeepAlivePluginResponse();
                    }
                    else if (request is CreateRulesPluginRequest createRulesRequest)
                    {
                        WriteInfo($"Server: Received {createRulesRequest.GetType().Name} ({e.Name})");

                        var mvsReader = GetMvsReader(createRulesRequest.MvsFullPath, createRulesRequest.SharedConfig.MaximumMemoryUsageInMB);
                        var snapshotTask = TakeSnapshot(createRulesRequest.VideoFullPath, createRulesRequest.CurrentVideoTimeAsTimeSpan);

                        if (createRulesRequest.ShowUI)
                        {
                            m_currentFrameAnalyser = Test.TestAnalyser(snapshotTask, mvsReader, createRulesRequest);
                        }
                        else
                        {
                            m_currentFrameAnalyser = createRulesRequest
                                .CreateInitialFrameAnalyser(mvsReader)
                                .Filter(createRulesRequest.SharedConfig.DefaultActivityFilter, createRulesRequest.SharedConfig.DefaultQualityFilter);
                        }

                        response = new CreateRulesPluginResponse
                        {
                            FrameDurationInMs = mvsReader.FrameDurationInMs,
                            Actions = m_currentFrameAnalyser?.CreateActions(
                                mvsReader,
                                createRulesRequest.CurrentVideoTimeAsTimeSpan,
                                createRulesRequest.CurrentVideoTimeAsTimeSpan + TimeSpan.FromSeconds(createRulesRequest.DurationToGenerateInSeconds),
                                createRulesRequest.SharedConfig.MaximumNbStrokesDetectedPerSecond)
                        };
                    }
                    else
                    {
                        WriteInfo($"Server: Unsupport request type '{request.GetType()}'.");
                        response = null;
                    }

                    if (response != null)
                    {
                        var responseFullPath = Path.Combine(r_responseFolder, e.Name);
                        File.WriteAllText(responseFullPath, JsonConvert.SerializeObject(response));
                    }

                    WriteInfo($"Server: Done.");
                }
            }
            catch(Exception ex)
            {
                WriteError(ex.ToString());
            }

            r_semaphore.Release();
        }

        private async Task<byte[]> TakeSnapshot(string videoFullPath, TimeSpan time)
        {
            var tempFile = Path.GetTempFileName() + ".png";
            try
            {
                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(videoFullPath, tempFile, time);
                var result = await conversion.Start();
                return File.ReadAllBytes(tempFile);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        private MotionVectorsFileReader GetMvsReader(string mvsFullPath, int maximumMemoryUsageInMB)
        {
            return m_currentMvsReader?.FilePath == mvsFullPath && m_currentMvsReader?.MaximumMemoryUsageInMB == maximumMemoryUsageInMB
                ? m_currentMvsReader
                : new MotionVectorsFileReader(mvsFullPath, maximumMemoryUsageInMB);
        }

        private bool WaitUntilFileIsReadable(string fullPath, TimeSpan maxWaitDuration)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                try
                {
                    using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(10);
                }

            } while (watch.Elapsed < maxWaitDuration);

            return false;
        }

        public class KnownTypesBinder : ISerializationBinder
        {
            public IList<Type> KnownTypes { get; set; }

            public Type BindToType(string assemblyName, string typeName)
            {
                return KnownTypes.SingleOrDefault(t => t.Name == typeName);
            }

            public void BindToName(Type serializedType, out string assemblyName, out string typeName)
            {
                assemblyName = null;
                typeName = serializedType.Name;
            }
        }
    }
}

