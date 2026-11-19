using FunscriptToolbox.Progress;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public delegate void ProgressUpdateDelegate(string toolName, string toolAction, string message);

    public class SubtitleGeneratorContext : VerbContext
    {
        public FfmpegHelper FfmpegHelper { get; }
        private readonly SubtitleGeneratorPrivateConfig r_privateConfig;

        public List<string> UserTodoList { get; }

        public SubtitleGeneratorConfig Config { get; private set; }
        public WorkInProgressSubtitles WIP { get; private set; }

        /// <summary>The locks shared by all the sequences processed at the same time.</summary>
        public SharedLocks Locks { get; }

        private readonly Dictionary<object, ITaskNode> r_plannedNodes = new Dictionary<object, ITaskNode>();

        public SubtitleGeneratorContext(
            ILog log,
            bool isVerbose,
            FfmpegHelper ffmpegAudioHelper,
            SubtitleGeneratorPrivateConfig privateConfig,
            IUserOutput output,
            SharedLocks locks)
            : base(log, isVerbose, null, output)
        {
            this.FfmpegHelper = ffmpegAudioHelper;
            r_privateConfig = privateConfig;
            this.Locks = locks;

            this.UserTodoList = new List<string>();
            this.Config = null;
            this.WIP = null;
        }

        public void ChangeCurrentFile(
            SubtitleGeneratorConfig config,
            WorkInProgressSubtitles wipsub)
        {
            this.Config = config;
            this.WIP = wipsub;
            this.ChangePrefix((wipsub != null)
                ? Path.GetFileNameWithoutExtension(wipsub.OriginalFilePath) + ": "
                : string.Empty);
        }

        public void ForgetCurrentFile()
        {
            this.WIP = null;
            this.ChangePrefix(string.Empty);
        }

        public string GetPrivateConfig(string itemName)
        {
            return r_privateConfig.GetValue(itemName);
        }

        public string GetValidatedPrivateConfig(string itemName)
        {
            return r_privateConfig.GetValidatedValue(itemName);
        }

        internal void WriteInfoAlreadyDone(string message = null)
        {
            WriteInfo(message, ConsoleColor.DarkGray);
        }

        internal void WriteNumeredPoint(int number, string message, ConsoleColor? color = null)
        {
            var split = message.Split('\n');
            WriteInfo($"{number,3}. {split[0]}", color);
            for (int i = 1; i < split.Length; i++)
            {
                WriteInfo($"     {split[i]}", color);
            }
        }

        internal void DefaultProgressUpdateHandler(string toolName, string toolAction, string message)
        {
            var toolActionString = toolAction == null ? string.Empty : $"[{toolAction}]";
            WriteInfo($"    [{toolName}]{toolActionString} {message}", isProgress: true);
        }

        internal void ClearProgressUpdate()
        {
            WriteInfo(string.Empty, isProgress: true);
        }

        internal void DefaultUpdateHandler(string toolName, string toolAction, string message)
        {
            var toolActionString = toolAction == null ? string.Empty : $"[{toolAction}]";
            WriteInfo($"    [{toolName}]{toolActionString} {message}", isProgress: false);
        }

        /// <summary>
        /// A safe point (between two steps: what was received is already saved, no lock is held): when the application is closing,
        /// the scene pauses here, until the application closes, or until the user cancels the close (then it continues normally).
        /// </summary>
        internal void PauseIfClosing()
        {
            var reason = this.Output.PauseAtSafePointReason;
            if (reason == null)
                return;

            var node = TaskScope.Current;
            node.SetState(TaskState.WaitingForLock, reason);
            using (this.Output.EnterPauseForClose())
            {
                string currentReason;
                while ((currentReason = this.Output.PauseAtSafePointReason) != null)
                {
                    // (ex. 'Pause all', then 'Close': the reason shown changes)
                    if (currentReason != reason)
                    {
                        reason = currentReason;
                        node.SetState(TaskState.WaitingForLock, reason);
                    }
                    Thread.Sleep(250);
                }
            }
            node.SetState(TaskState.Running);
        }

        /// <summary>The workers that could not run yet (prerequisites not met). A blocked scene can be checked again ('Rescan' in the web UI).</summary>
        public List<string> BlockedWorkers { get; } = new List<string>();

        internal void AddBlockedWorker(string workerName)
        {
            lock (this.BlockedWorkers)
            {
                this.BlockedWorkers.Add(workerName);
            }
        }

        internal void AddUserTodo(string message)
        {
            lock (this.UserTodoList)
            {
                this.UserTodoList.Add(this.Prefix + message);
            }
        }

        // ---- Task tree (only shown in --ui mode; does nothing in console mode) ----

        /// <summary>Starts a sub-task of the current task. Dispose it when done.</summary>
        internal ITaskNode BeginTask(string kind, string name)
        {
            var node = TaskScope.Current.AddPendingChild(kind, name);
            node.Start();
            return node;
        }

        /// <summary>Starts a step of the current task (even an instant one: the tree then shows every step that was done). Dispose it when done.</summary>
        internal ITaskNode BeginStep(string name) => BeginTask("Step", name);

        /// <summary>Runs a step: completed when the work returns, Failed on an exception, Cancelled when stopped by the user.</summary>
        internal T RunStep<T>(string name, Func<ITaskNode, T> work)
        {
            using var node = BeginStep(name);
            try
            {
                return work(node);
            }
            catch (UserStoppedWorkerException)
            {
                node.SetState(TaskState.Cancelled, "stopped by the user");
                throw;
            }
            catch (Exception ex)
            {
                // Not Fail(): the error is already logged where it happened (an error line on each nested step would count it many times)
                node.SetState(TaskState.Failed, ex.Message);
                throw;
            }
        }

        /// <inheritdoc cref="RunStep{T}(string, Func{ITaskNode, T})"/>
        internal void RunStep(string name, Action<ITaskNode> work)
        {
            RunStep<object>(name, node => { work(node); return null; });
        }

        /// <summary>
        /// Creates a step that will run in the background, beside the current work. It is NOT started:
        /// the background code must call Start() and Dispose() on it itself (so the 'current task' of the main work is not changed).
        /// </summary>
        internal ITaskNode PlanBackgroundStep(string name)
        {
            return TaskScope.Current.AddPendingChild("Background", name, isBackground: true);
        }

        /// <summary>Shows a worker as 'pending' in the task tree, until it is started with <see cref="BeginWorkerTask"/>.</summary>
        internal void PlanWorkerTask(object worker, string name)
        {
            r_plannedNodes[worker] = TaskScope.Current.AddPendingChild("Worker", name);
        }

        /// <summary>The node planned for a worker by <see cref="PlanWorkerTask"/> (until it's started), or null.</summary>
        internal ITaskNode GetPlannedWorkerTask(object worker) => r_plannedNodes.TryGetValue(worker, out var node) ? node : null;

        internal ITaskNode BeginWorkerTask(object worker, string name)
        {
            if (r_plannedNodes.Remove(worker, out var planned))
            {
                planned.Start();
                return planned;
            }
            return BeginTask("Worker", name);
        }

        internal void WriteStream(string text) => this.Output.WriteStream(text);

        internal void WriteStreamLine(string text = "") => this.Output.WriteStreamLine(text);

        /// <summary>True once each time the user asks to stop what is currently done (Q key in console mode, 'Stop' button in --ui mode).</summary>
        internal bool PollStopRequest() => this.Output.PollStopRequest();

        /// <summary>Only one prompt/dialog at a time (the others wait, and show as 'waiting for user'). The wait can be cancelled in --ui mode.</summary>
        internal IDisposable EnterUserInteraction(string description)
        {
            return this.Locks.UserInteraction.Acquire(description, cancellable: this.Output.IsWebUi);
        }

        /// <summary>Only one process using the local machine (local transcription tool, local AI server) at a time. (The ffmpeg work has its own, finer limits: see SharedLocks.)</summary>
        internal IDisposable UseLocalResource(string description)
        {
            return this.Locks.LocalResource.Acquire(description);
        }

        internal string GetPotentialVerboseFilePath(string suffixe, DateTime? processStartTime = null)
        {
            Directory.CreateDirectory(this.WIP.BackupFolder);
            return processStartTime == null
                ? Path.Combine(
                    this.WIP.BackupFolder,
                    suffixe)
                : Path.Combine(
                        this.WIP.BackupFolder,
                        processStartTime.Value.ToString("yyyyMMddHHmmss") + "_" + suffixe);
        }

        internal void CreateVerboseTextFile(string suffixe, string content, DateTime? processStartTime = null)
        {
            if (this.Output.IsWebUi)
            {
                // What was sent/received can be inspected in the UI, even when not in verbose mode.
                TaskScope.Current.SetDetail(suffixe, content);
            }

            if (IsVerbose)
            {
                File.WriteAllText(
                    GetPotentialVerboseFilePath(suffixe, processStartTime),
                    content,
                    Encoding.UTF8);
            }
        }

        internal void CreateVerboseBinaryFile(string suffixe, byte[] content, DateTime? processStartTime = null)
        {
            if (IsVerbose)
            {
                File.WriteAllBytes(
                    GetPotentialVerboseFilePath(suffixe, processStartTime),
                    content);
            }
        }

        internal void SoftDelete(string fullpath)
        {
            if (File.Exists(fullpath))
            {
                Directory.CreateDirectory(this.WIP.BackupFolder);
                var crc = ComputeFileHash(fullpath);

                var targetPath = Path.Combine(
                        this.WIP.BackupFolder,
                        $"{Path.GetFileNameWithoutExtension(fullpath)}-{crc}{Path.GetExtension(fullpath)}");
                if (File.Exists(targetPath))
                {
                    File.Delete(fullpath);
                }
                else
                {
                    File.Move(fullpath, targetPath);
                }
            }
        }

        static private string ComputeFileHash(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 12).ToLower();
        }
    }
}