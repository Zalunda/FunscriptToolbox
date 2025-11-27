using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataContainer
    {
        public static object ReplacePlaceholdersWithAPICompatibleNodes(object node, APIFormat format)
        {
            if (node == null) return null;

            // --- 1. THE GOAL: Swap BinaryDataContainer ---
            if (node is BinaryDataContainer binaryContainer)
            {
                // This executes your specific logic (Poe vs OpenAI vs Gemini)
                return binaryContainer.GetAPICompatibleNode(format);
            }

            // --- 2. Handle Strings/Value Types (Stop recursion) ---
            if (node is string || node.GetType().IsValueType)
            {
                return node;
            }

            // --- 3. Handle Dictionaries (ExpandoObject matches this) ---
            if (node is IDictionary<string, object> dict)
            {
                var newDict = new ExpandoObject() as IDictionary<string, object>;
                foreach (var kvp in dict)
                {
                    newDict[kvp.Key] = ReplacePlaceholdersWithAPICompatibleNodes(kvp.Value, format);
                }
                return newDict;
            }

            // --- 4. Handle Lists/Arrays ---
            if (node is IEnumerable list && !(node is IDictionary<string, object>))
            {
                var newList = new List<object>();
                foreach (var item in list)
                {
                    newList.Add(ReplacePlaceholdersWithAPICompatibleNodes(item, format));
                }
                return newList;
            }

            // --- 5. Handle Anonymous Types / POCOs ---
            // If you used "new { role = ... }" in your code, it lands here.
            // We convert it to an ExpandoObject to make it standardized.
            var properties = node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (properties.Length > 0)
            {
                var expando = new ExpandoObject() as IDictionary<string, object>;
                foreach (var prop in properties)
                {
                    var val = prop.GetValue(node);
                    expando[prop.Name] = ReplacePlaceholdersWithAPICompatibleNodes(val, format);
                }
                return expando;
            }

            return node;
        }

        public string FileName { get; }
        public BinaryDataType DataType { get; }
        public byte[] Content { get; }

        public BinaryDataContainer(string fileName, BinaryDataType dataType, byte[] content)
        {
            this.FileName = fileName;
            this.DataType = dataType;
            this.Content = content;
        }

        public dynamic GetAPICompatibleNode(APIFormat format)
        {
            var base64 = Convert.ToBase64String(this.Content);

            switch (this.DataType)
            {
                case BinaryDataType.Audio:
                    switch (format)
                    {
                        case APIFormat.OpenAI:
                            return new
                            {
                                type = "input_audio",
                                input_audio = new
                                {
                                    data = base64,
                                    format = "wav"
                                }
                            };

                        case APIFormat.Poe:
                            return new
                            {
                                type = "file",
                                file = new
                                {
                                    filename = this.FileName,
                                    file_data = $"data:audio/mp3;base64,{base64}"
                                }
                            };
                    }
                    break;

                case BinaryDataType.Image:
                    return new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = $"data:image/jpeg;base64,{base64}"
                        }
                    };
            }
            throw new NotImplementedException();
        }
    }
}