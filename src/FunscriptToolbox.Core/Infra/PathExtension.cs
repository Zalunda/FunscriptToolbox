using System;
using System.IO;

namespace FunscriptToolbox.Core.Infra
{
    public static class PathExtension
    {
        public static string SafeGetDirectoryName(string path, string defaultValue = ".")
        {
            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory)
                ? defaultValue
                : directory;
        }

        public static object GetRelativePath(string rootPath, string fullPath)
        {
            // 1. Convert to Uri
            Uri fullUri = new Uri(fullPath);

            // IMPORTANT: The root path must end with a slash, otherwise Uri 
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                rootPath += Path.DirectorySeparatorChar;
            }
            Uri rootUri = new Uri(rootPath);

            // 2. Make relative Uri
            Uri relativeUri = rootUri.MakeRelativeUri(fullUri);

            // 3. Convert back to string string (handling URL encoding like %20 for spaces)
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            // 4. Fix separators (Uri uses '/', Windows uses '\')
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}