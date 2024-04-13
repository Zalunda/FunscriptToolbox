using System.IO;

namespace FunscriptToolbox.Core.Infra
{
    public static class PathExtension
    {
        public static string SafeGetDirectoryName(string path)
        {
            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory)
                ? "."
                : directory;
        }
    }
}