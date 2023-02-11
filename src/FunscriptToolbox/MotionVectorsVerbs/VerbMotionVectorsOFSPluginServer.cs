using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.Core.MotionVectors;
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

        [Verb("motionvectors.ofspluginserver", aliases: new[] { "mvs.ofss" }, HelpText = "TODO")]
        public class Options : OptionsBase
        {
            [Option('c', "channelbasefilepath", Required = true, HelpText = "TODO")]
            public string ChannelBaseFilePath { get; set; }

            [Option('t', "timeout", Required = false, HelpText = "Timeout before the server stop byitself", Default = 300)]
            public int TimeOutInSeconds { get; set; }

            public TimeSpan TimeOut => TimeSpan.FromSeconds(this.TimeOutInSeconds);
        }

        private readonly Options r_options;
        private readonly Semaphore r_semaphore;
        private readonly JsonSerializerSettings r_jsonSetting;

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
                        typeof(ServerRequestCreateRulesFromScriptActions)
                    }
                }
            };

            m_currentMvsReader = null;
            m_currentFrameAnalyser = null;
        }

        public int Execute()
        {
            UpdateFfmpeg();

            var parentFolder = Path.GetDirectoryName(r_options.ChannelBaseFilePath);
            var watcher = new FileSystemWatcher();
            watcher.Path = parentFolder;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
            watcher.Created += OnCreated;
            watcher.Changed += OnCreated;
            watcher.EnableRaisingEvents = true;

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
            
            if (File.Exists(e.FullPath))
            {
                WaitUntilFileIsReadable(e.FullPath, TimeSpan.FromSeconds(30));
                var content = File.ReadAllText(e.FullPath);
                var request = JsonConvert.DeserializeObject<ServerRequest>(content, r_jsonSetting);
                File.Delete(e.FullPath);
                if (request is ServerRequestCreateRulesFromScriptActions createRulesFromScriptActions)
                {
                    WriteInfo($"Server: Received {createRulesFromScriptActions.GetType().Name}");
                    var mvsReader = GetMvsReader(createRulesFromScriptActions.MvsFullPath, createRulesFromScriptActions.MaximumMemoryUsageInMB);

                    if (createRulesFromScriptActions.Actions.Length > 0)
                    {
                        var snapshotTask = TakeSnapshot(createRulesFromScriptActions.VideoFullPath, createRulesFromScriptActions.CurrentVideoTimeAsTimeSpan);
                        m_currentFrameAnalyser = FrameAnalyserGenerator.CreateFromScriptSequence(mvsReader, createRulesFromScriptActions.Actions);

                        if (createRulesFromScriptActions.ShowUI)
                        {
                            m_currentFrameAnalyser = Test.TestAnalyser(
                                snapshotTask.Result,
                                createRulesFromScriptActions.CurrentVideoTimeAsTimeSpan,
                                mvsReader,
                                m_currentFrameAnalyser);
                        }
                        var response = new ServerRequestCreateRulesFromScriptActions.Response
                        {
                            Actions = m_currentFrameAnalyser.CreateActions(
                                mvsReader,
                                createRulesFromScriptActions.CurrentVideoTimeAsTimeSpan,
                                createRulesFromScriptActions.CurrentVideoTimeAsTimeSpan + TimeSpan.FromSeconds(createRulesFromScriptActions.DurationToGenerateInSeconds),
                                TimeSpan.FromMilliseconds(createRulesFromScriptActions.MinimumActionDurationInMilliseconds))
                        };
                        var responseFile = Path.ChangeExtension(e.FullPath, ".response.json");
                        File.WriteAllText(
                            responseFile + ".BACKUP.json",
                            JsonConvert.SerializeObject(response));
                        File.WriteAllText(
                            responseFile + ".TEMP",
                            JsonConvert.SerializeObject(response));
                        File.Move(responseFile + ".TEMP", responseFile);
                    }
                }
                else
                {

                }
                WriteInfo($"Server: Done.");
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

        internal class ServerRequest
        {
            public string VideoFullPath { get; set; }
            public string MvsFullPath { get; set; }
            public int CurrentVideoTime { get; set; }
            public int MaximumMemoryUsageInMB { get; set; }

            public TimeSpan CurrentVideoTimeAsTimeSpan => TimeSpan.FromMilliseconds(CurrentVideoTime);
        }

        internal class ServerRequestCreateRulesFromScriptActions : ServerRequest
        {
            public FunscriptAction[] Actions { get; set; }
            public int DurationToGenerateInSeconds { get; set; }
            public int MinimumActionDurationInMilliseconds { get; set; }
            public bool ShowUI { get; set; }

            public class Response
            {
                public FunscriptAction[] Actions { get; set; }
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

