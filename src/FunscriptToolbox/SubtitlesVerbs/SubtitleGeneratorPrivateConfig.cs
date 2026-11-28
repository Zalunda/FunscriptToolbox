using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleGeneratorPrivateConfig
    {
        public static readonly JsonSerializer rs_serializer = JsonSerializer
                .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented
                });
        public static SubtitleGeneratorPrivateConfig FromFile(string filepath)
        {
            if (File.Exists(filepath))
            {
                try
                {
                    using var reader = File.OpenText(filepath);
                    using var jsonReader = new JsonTextReader(reader);
                    var content = rs_serializer.Deserialize<ExpandoObject>(jsonReader);
                    return new SubtitleGeneratorPrivateConfig(
                        filepath, 
                        content.ToDictionary(
                            kp => kp.Key, 
                            kp => kp.Value?.ToString(),
                            StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error while parsing file '{filepath}': {ex.Message}", ex);
                }
            }
            else
            {
                return new SubtitleGeneratorPrivateConfig(filepath);
            }
        }

        private SubtitleGeneratorPrivateConfig(string filepath, Dictionary<string, string> privateItems = null)
        {
            r_filepath = filepath;
            m_privateItems = privateItems;
        }

        private readonly string r_filepath;
        // (replaced as a whole when a value is set: the scenes that read it at the same time see the old or the new one)
        private volatile Dictionary<string, string> m_privateItems;
        private readonly object r_writeLock = new object();

        internal string GetValue(string itemName)
        {
            if (m_privateItems?.TryGetValue(itemName, out var value) == true)
            {
                return value;
            }
            return null;
        }

        /// <summary>
        /// The value, or NeedsUserException when it's not there (the scene waits for the user: 'needs you', the web UI asks for it; without
        /// the web UI: written in the file by the user, then Retry). The file is created (with this name, without value) when it doesn't exist.
        /// </summary>
        internal string GetValidatedValue(string itemName)
        {
            var value = GetValue(itemName);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
            throw CreateNeedsAnyOf(new[] { itemName });
        }

        /// <summary>
        /// NeedsUserException for several API keys, at least one needed (ex. the engines of an AIEngineCollection: none has its key). The names
        /// not in the file yet are written in it, without value (the file is created when needed; the other values kept).
        /// </summary>
        internal NeedsUserException CreateNeedsAnyOf(IReadOnlyList<string> itemNames)
        {
            lock (r_writeLock)
            {
                var items = File.Exists(r_filepath)
                    ? FromFile(r_filepath).m_privateItems ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var missingInFile = itemNames.Where(name => !items.ContainsKey(name)).ToArray();
                if (missingInFile.Length > 0 || !File.Exists(r_filepath))
                {
                    foreach (var name in missingInFile)
                        items[name] = null;
                    WriteFile(items);
                }
            }
            var fileName = Path.GetFileName(r_filepath);
            return new NeedsUserException(
                itemNames.Count == 1
                    ? $"The API key '{itemNames[0]}' is needed (in '{fileName}'): enter it in the web UI (button of the scene), or in that file, then Retry."
                    : $"One of the API keys {string.Join(", ", itemNames.Select(name => $"'{name}'"))} is needed (at least one; the engines are tried in this order; in '{fileName}'): " +
                      "enter it in the web UI (button of the scene), or in that file, then Retry.",
                NeedsUserException.PrivateKeyActionPrefix + string.Join(",", itemNames));
        }

        /// <summary>Sets a value (ex. an API key entered in the web UI): in the file (created when needed; the other values kept) and for the scenes running.</summary>
        internal void SetValue(string itemName, string value)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                throw new ArgumentException("The name of the value is missing.");
            lock (r_writeLock)
            {
                // (the file as it is now: it may have been changed by hand since it was read)
                var items = File.Exists(r_filepath)
                    ? FromFile(r_filepath).m_privateItems ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                items[itemName] = value;
                WriteFile(items);
                m_privateItems = items;
            }
        }

        private void WriteFile(Dictionary<string, string> items)
        {
            using var writer = new StreamWriter(r_filepath, false, Encoding.UTF8);
            rs_serializer.Serialize(writer, items);
        }
    }
}