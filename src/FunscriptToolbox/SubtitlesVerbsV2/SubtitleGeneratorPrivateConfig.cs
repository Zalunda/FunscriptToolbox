using FunscriptToolbox.SubtitlesVerbsV2.Outputs;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbV2
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
            r_privateItems = privateItems;
        }

        private readonly string r_filepath;
        private readonly Dictionary<string, string> r_privateItems;

        internal string GetValue(string itemName)
        {
            if (r_privateItems == null)
            {
                if (!File.Exists(r_filepath))
                {
                    using (var writer = new StreamWriter(r_filepath, false, Encoding.UTF8))
                    {
                        var newConfig = new ExpandoObject();
                        ((IDictionary<string,object>)newConfig)[itemName] = null;
                        rs_serializer.Serialize(writer, newConfig);
                    }
                }
                throw new Exception($"Can't resolve the value for the private config '{itemName}'. A new file '{r_filepath}' as been created, you need to set the value for this private config in that file.");
            }
            else if (! r_privateItems.TryGetValue(itemName, out var value) || string.IsNullOrEmpty(value))
            {
                throw new Exception($"Can't resolve the value for the private config '{itemName}'. You need to set the value for this private config in '{r_filepath}'.");
            }
            else 
            { 
                return value; 
            }
        }
    }
}