using FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2
{
    public delegate void ProgressUpdateDelegate(string toolName, string toolAction, string message);

    public class SubtitleGeneratorContext : VerbContext
    {
        public SubtitleGeneratorContext(
            ILog log,
            SubtitleGeneratorPrivateConfig privateConfig,
            string prefix,
            bool isVerbose,
            string baseFilePath,
            WorkInProgressSubtitles wipsub,
            FfmpegAudioHelper ffmpegAudioHelper) 
            : base(log, prefix, isVerbose)
        {
            r_logsAndBackupFolder = baseFilePath + "_LogsAndBackup";
            r_privateConfig = privateConfig;
            this.BaseFilePath = baseFilePath;
            this.Wipsub = wipsub;
            FfmpegAudioHelper = ffmpegAudioHelper;
            this.UserTodoList = new List<string>();
        }

        private readonly string r_logsAndBackupFolder;
        private readonly SubtitleGeneratorPrivateConfig r_privateConfig;

        public string BaseFilePath { get; }

        public WorkInProgressSubtitles Wipsub { get; }
        public FfmpegAudioHelper FfmpegAudioHelper { get; }
        public List<string> UserTodoList { get; }

        public string GetPrivateConfig(string itemName)
        {
            return r_privateConfig.GetValue(itemName);
        }

        internal void WriteInfoAlreadyDone(string message = null)
        {
            WriteInfo(message, ConsoleColor.DarkGray);
        }

        internal void DefaultProgressUpdateHandler(string toolName, string toolAction, string message)
        {
            var toolActionString = toolAction == null ? string.Empty : $"[{toolAction}]";
            WriteInfo($"    [{toolName}]{toolActionString} {message}", isProgress: true);
        }

        internal void DefaultUpdateHandler(string toolName, string toolAction, string message)
        {
            var toolActionString = toolAction == null ? string.Empty : $"[{toolAction}]";
            WriteInfo($"    [{toolName}]{toolActionString} {message}", isProgress: false);
        }

        internal void AddUserTodo(string message)
        {
            this.UserTodoList.Add(message);
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

        internal string GetPotentialVerboseFilePath(DateTime processStartTime, string suffixe)
        {
            return Path.Combine(
                        r_logsAndBackupFolder,
                        processStartTime.ToString("yyyyMMddHHmmss") + "-" + suffixe);
        }

        internal void CreateVerboseFile(DateTime processStartTime, string suffixe, string content)
        {
            if (IsVerbose)
            {
                Directory.CreateDirectory(r_logsAndBackupFolder);
                File.WriteAllText(
                    GetPotentialVerboseFilePath(processStartTime, suffixe),
                    content,
                    Encoding.UTF8);
            }
        }

        internal void SoftDelete(string fullpath)
        {
            if (File.Exists(fullpath))
            {
                Directory.CreateDirectory(r_logsAndBackupFolder);
                var crc = ComputeFileHash(fullpath);

                var targetPath = Path.Combine(
                        r_logsAndBackupFolder,
                        $"{Path.GetFileNameWithoutExtension(fullpath)}-{crc}{Path.GetExtension(fullpath)}");
                File.Delete(targetPath);
                File.Move(fullpath, targetPath);
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