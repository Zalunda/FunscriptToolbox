using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FunscriptToolbox
{
    public class FunscriptVault
    {
        private readonly string r_baseVaultFolder;
        private readonly bool r_enabled;

        public FunscriptVault(string baseVaultFolder)
        {
            r_baseVaultFolder = baseVaultFolder;
            r_enabled = !File.Exists("disable.txt");
        }

        public void SaveFunscript(Funscript funscript, string filename)
        {
            Directory.CreateDirectory(r_baseVaultFolder);

            if (File.Exists(filename))
            {
                BackupToVault(filename);
            }
            funscript.Save(filename);
            BackupToVault(filename);
        }

        private void BackupToVault(string filename)
        {
            if (r_enabled)
            {
                if (File.Exists(filename))
                {
                    var md5 = GetMD5(filename);
                    var alreadyExists = Directory.GetFiles(r_baseVaultFolder, $"*.{md5}").Any();
                    if (!alreadyExists)
                    {
                        File.Move(
                            filename,
                            Path.Combine(
                                r_baseVaultFolder,
                                Path.GetFileName(filename) + $".{md5}"));
                    }
                }
            }
        }

        private string GetMD5(string filename)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filename))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
        }
    }
}
