using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public delegate void ProgressUpdateDelegate(string toolName, string toolAction, string message);

    public class SubtitleGeneratorContext : VerbContext
    {
        public FfmpegAudioHelper FfmpegAudioHelper { get; }
        public SubtitleGeneratorConfig Config { get; internal set; }
        private readonly SubtitleGeneratorPrivateConfig r_privateConfig;
        public Language OverrideSourceLanguage { get; }

        public List<string> UserTodoList { get; }

        public WorkInProgressSubtitles WIP { get; private set; }

        public SubtitleGeneratorContext(
            ILog log,
            bool isVerbose,
            FfmpegAudioHelper ffmpegAudioHelper,
            SubtitleGeneratorConfig config,
            SubtitleGeneratorPrivateConfig privateConfig,
            Language overrideSourceLanguage) 
            : base(log, isVerbose, null)
        {
            this.FfmpegAudioHelper = ffmpegAudioHelper;
            this.Config = config;
            r_privateConfig = privateConfig;
            this.OverrideSourceLanguage = overrideSourceLanguage;

            this.UserTodoList = new List<string>();
            this.WIP = null;
        }

        public string GetPrivateConfig(string itemName)
        {
            return r_privateConfig.GetValue(itemName);
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

        internal void AddUserTodo(string message)
        {
            this.UserTodoList.Add(this.Prefix + message);
        }

        public void ChangeCurrentFile(
            WorkInProgressSubtitles wipsub)
        {
            this.WIP = wipsub;
            this.ChangePrefix(Path.GetFileNameWithoutExtension(wipsub.OriginalFilePath) + ": ");
        }

        public void ForgetCurrentFile()
        {
            this.WIP = null;
            this.ChangePrefix(string.Empty);
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