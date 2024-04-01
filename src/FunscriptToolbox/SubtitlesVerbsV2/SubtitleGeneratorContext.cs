using log4net;
using System;
using System.IO;
using System.Security.Cryptography;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SubtitleGeneratorContext : ProcessContext
    {
        public SubtitleGeneratorContext(
            ILog log, 
            bool isVerbose, 
            string baseFilePath,
            WorkInProgressSubtitles wipsub) 
            : base(log, isVerbose, baseFilePath)
        {
            r_logsAndBackupFolder = baseFilePath + "_LogsAndBackup";
            Wipsub = wipsub;
        }

        private readonly string r_logsAndBackupFolder;

        public WorkInProgressSubtitles Wipsub { get; }

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