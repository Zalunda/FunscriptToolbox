using CommandLine;
using FunscriptToolbox.Core;
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
    internal class VerbMotionVectorsOFSPluginServer : VerbMotionVectors
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("motionvectors.ofspluginserver", aliases: new[] { "mvs.ofsps" }, HelpText = "Starts a server to respond to plugin request.")]
        public class Options : OptionsBase
        {
            [Option('c', "channelbasefilepath", Required = true, HelpText = "Channel (i.e. file prefix) for the communication.")]
            public string ChannelBaseFilePath { get; set; }

            [Option('l', "channellockfilepath", Required = true, HelpText = "Channel Lock for the communication.")]
            public string ChannelLockFilePath { get; set; }            

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
            Directory.CreateDirectory(r_responseFolder);

            using (var channelLock = new FileStream(r_options.ChannelLockFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
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
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.StartsWith(r_options.ChannelBaseFilePath))
            {
                WriteVerbose($"Server: Received file '{Path.GetFileName(e.FullPath)}', ignoring since it's not starting with our base path '{Path.GetFileName(r_options.ChannelBaseFilePath)}'", ConsoleColor.DarkGray);
                return;
            }

            if (File.Exists(e.FullPath))
            {
                PluginResponse response;

                try
                {
                    WaitUntilFileIsReadable(e.FullPath, TimeSpan.FromSeconds(30));
                    var content = File.ReadAllText(e.FullPath);
                    var request = JsonConvert.DeserializeObject<PluginRequest>(content, r_jsonSetting);
                    if (!r_options.DebugMode)
                    {
                        File.Delete(e.FullPath);
                    }

                    WriteInfo($"Server: Received {request.GetType().Name} ({e.Name})");
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
                        var mvsReader = GetMvsReader(createRulesRequest.MvsFullPath, createRulesRequest.MaximumMemoryUsageInMB);
                        var learnFromActionsSettings = new LearnFromActionsSettings
                        {
                            NbFramesToIgnoreAroundAction = createRulesRequest.LearnFromAction_NbFramesToIgnoreAroundAction
                        };
                        var generateActionsSettings = new GenerateActionsSettings
                        {
                            MaximumStrokesDetectedPerSecond = createRulesRequest.GenerateActions_MaximumStrokesDetectedPerSecond,
                            PercentageOfFramesToKeep = createRulesRequest.GenerateActions_PercentageOfFramesToKeep
                        };

                        var learningActions = createRulesRequest.SelectedActions.Length > 2
                            ? createRulesRequest.SelectedActions
                            : createRulesRequest.Actions.Length > 2
                                ? createRulesRequest.Actions
                                : null;

                        if (learningActions == null)
                        {
                            var rules = new List<BlocAnalyserRule>();
                            for (ushort i = 0; i < mvsReader.FrameLayout.NbCellsTotalPerFrame; i++)
                            {
                                rules.Add(new BlocAnalyserRule(i, 6));
                            }
                            var unit = new FrameAnalyserUnit(mvsReader.FrameLayout, rules.ToArray());
                            var tempAnalyser = new FrameAnalyser(mvsReader.FrameLayout, unit, unit, unit);
                            learningActions = tempAnalyser.GenerateActions(
                                mvsReader, 
                                createRulesRequest.CurrentVideoTimeAsTimeSpan, 
                                createRulesRequest.CurrentVideoTimeAsTimeSpan + TimeSpan.FromSeconds(10),
                                generateActionsSettings)
                                .Take(7)  // 3 strokes
                                .ToArray();
                        }
                        m_currentFrameAnalyser = FrameAnalyserGenerator.CreateFromScriptSequence(
                                    mvsReader,
                                    learningActions,
                                    learnFromActionsSettings);

                        if (createRulesRequest.LearnFromAction_ShowUI)
                        {
                            m_currentFrameAnalyser = Test.TestAnalyser(
                                TakeSnapshot(
                                    createRulesRequest.VideoFullPath,
                                    createRulesRequest.CurrentVideoTimeAsTimeSpan),
                                mvsReader,
                                m_currentFrameAnalyser,
                                createRulesRequest);
                        }
                        else
                        {
                            m_currentFrameAnalyser = m_currentFrameAnalyser
                                .Filter(
                                    createRulesRequest.LearnFromAction_DefaultActivityFilter,
                                    createRulesRequest.LearnFromAction_DefaultQualityFilter,
                                    createRulesRequest.LearnFromAction_DefaultMinimumPercentageFilter);
                        }

                        response = new CreateRulesPluginResponse
                        {
                            FrameDurationInMs = mvsReader.FrameDurationInMs,
                            CurrentVideoTime = createRulesRequest.CurrentVideoTime,
                            ScriptIndex = createRulesRequest.ScriptIndex,
                            Actions = m_currentFrameAnalyser?.GenerateActions(
                                mvsReader,
                                createRulesRequest.CurrentVideoTimeAsTimeSpan,
                                createRulesRequest.CurrentVideoTimeAsTimeSpan + TimeSpan.FromSeconds(createRulesRequest.GenerateActions_DurationToGenerateInSeconds),
                                generateActionsSettings)
                        };
                    }
                    else
                    {
                        WriteInfo($"Server: Unsupport request type '{request.GetType()}'.");
                        response = null;
                    }
                }
                catch (Exception ex)
                {
                    WriteError(ex.ToString());
                    response = new ErrorPluginResponse(ex);
                }

                if (response != null)
                {
                    var responseFullPath = Path.Combine(r_responseFolder, e.Name);

                    using (var stream = new FileStream(responseFullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(JsonConvert.SerializeObject(response));
                    }
                }

                WriteInfo($"Server: Done.");
            }

            r_semaphore.Release();
        }

        private MotionVectorsFileReader GetMvsReader(string mvsFullPath, int maximumMemoryUsageInMB)
        {
            if (m_currentMvsReader?.FilePath == mvsFullPath && m_currentMvsReader?.MaximumMemoryUsageInMB == maximumMemoryUsageInMB)
            {
                return m_currentMvsReader;
            }
            else
            {
                m_currentMvsReader?.Dispose();
                m_currentMvsReader = new MotionVectorsFileReader(mvsFullPath, maximumMemoryUsageInMB);
                return m_currentMvsReader;
            }
        }

        private async Task<byte[]> TakeSnapshot(string videoFullPath, TimeSpan time)
        {
            var tempFile = Path.GetTempFileName() + ".jpg";
            try
            {
                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(videoFullPath, tempFile, time);
                var result = await conversion.Start();
                return File.ReadAllBytes(tempFile);
            }
            catch(Exception ex)
            {
                rs_log.Error("Error while taking snapshot", ex);
                return null;
            }
            finally
            {
                File.Delete(tempFile);
            }
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

