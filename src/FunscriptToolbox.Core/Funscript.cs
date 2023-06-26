using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace FunscriptToolbox.Core
{
    public class Funscript
    {
        public const string AudioSignatureExtension = ".asig";
        public const string FunscriptExtension = ".funscript";

        private readonly static JsonSerializer rs_serializer = JsonSerializer
            .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.None,
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

        public static Funscript FromFile(string filename)
        {
            using (var reader = File.OpenText(filename))
            using (var jsonReader = new JsonTextReader(reader))
            {
                var content = rs_serializer.Deserialize<dynamic>(jsonReader);
                return new Funscript(content);
            }
        }


        public static TimeSpan FromChapterTime(dynamic time)
        {
            if (TimeSpan.TryParse((string)time, out var timespanTime))
                return timespanTime;
            else
                return TimeSpan.Zero;
        }

        public static string ToChapterTime(TimeSpan time)
        {
            return $"{time:hh}:{time:mm}:{time:ss}.{time:fff}";
        }

        public dynamic r_content;
        public List<FunscriptAction> m_actionsDelayed;
        public List<dynamic> m_chaptersDelayed;

        public Funscript(dynamic content = null)
        {
            r_content = content ?? new JObject();
            m_actionsDelayed = null;
            m_chaptersDelayed = null;
        }

        public FunscriptAction[] Actions
        {
            get
            {
                UpdateDelayed();
                return r_content.actions?.ToObject<FunscriptAction[]>();
            }
            set
            {
                r_content.actions = new JArray(value.Select(f => JObject.FromObject(f)).ToArray());
            }
        }

        public FunscriptAudioSignature AudioSignature { 
            get 
            {
                return r_content.audioSignature?.ToObject<FunscriptAudioSignature>();
            }
            set 
            {
                r_content.audioSignature = JObject.FromObject(value);
            }
        }

        public int Duration
        {
            get
            {
                return r_content.metadata?.duration?.ToObject<int>();
            }
            set
            {
                r_content.metadata = r_content.metadata ?? JObject.FromObject(new { });
                r_content.metadata.duration = value;
            }
        }

        public void AddNotes(string newNote)
        {
            r_content.metadata = r_content.metadata ?? JObject.FromObject(new { notes = string.Empty });

            var originalNotes = r_content.metadata.notes;
            if (string.IsNullOrWhiteSpace(originalNotes?.Value))
            {
                r_content.metadata.notes = newNote;
            }
            else
            {
                r_content.metadata.notes = originalNotes + "\n" + newNote;
            }
        }

        public Funscript Clone()
        {
            return new Funscript(JsonConvert.DeserializeObject<dynamic>(JsonConvert.SerializeObject(r_content)));
        }

        public Funscript CloneWithoutActionsOrChapters()
        {
            var clone = Clone();
            clone.m_actionsDelayed = new List<FunscriptAction>();
            clone.m_chaptersDelayed = new List<dynamic>();
            return clone;
        }

        public IEnumerable<dynamic> GetClonedChapters()
        {
            var chapters = Clone().r_content.metadata?.chapters;
            if (chapters?.HasValues == true)
            {
                foreach (var chapter in chapters)
                {
                    yield return chapter;
                }
            }
        }

        public void AddActionDelayed(FunscriptAction action)
        {
            m_actionsDelayed = m_actionsDelayed ?? new List<FunscriptAction>();
            m_actionsDelayed.Add(action);
        }

        public void AddChapterDelayed(dynamic chapter)
        {
            m_chaptersDelayed = m_chaptersDelayed ?? new List<dynamic>();
            m_chaptersDelayed.Add(chapter);
        }

        private void UpdateDelayed()
        {
            if (m_actionsDelayed != null)
            {
                r_content.actions = new JArray(m_actionsDelayed.Select(f => JObject.FromObject(f)).ToArray());
            }
            if (m_chaptersDelayed != null)
            {
                r_content.metadata = r_content.metadata ?? JObject.FromObject(new { });
                r_content.metadata.chapters = r_content.metadata.chapters ?? JObject.FromObject(new { });
                r_content.metadata.chapters = new JArray(m_chaptersDelayed.Select(f => JObject.FromObject(f)).ToArray());
            }
        }

        public void TransformChaptersTime(Func<TimeSpan, TimeSpan> transformTimeFunc)
        {
            var chapters = r_content?.metadata?.chapters;
            if (chapters?.HasValues == true)
            {
                foreach (var item in chapters)
                {
                    if (TimeSpan.TryParse((string)item.startTime, out var startTime))
                    {
                        var newStartTime = transformTimeFunc(startTime);
                        item.startTime = $"{newStartTime:hh}:{newStartTime:mm}:{newStartTime:ss}.{newStartTime:fff}";
                    }
                    if (TimeSpan.TryParse((string)item.endTime, out var endTime))
                    {
                        var newEndTime = transformTimeFunc(endTime);
                        item.endTime = $"{newEndTime:hh}:{newEndTime:mm}:{newEndTime:ss}.{newEndTime:fff}";
                    }
                }
            }
        }

        public void Save(string filename)
        {
            using (var writer = File.CreateText(filename))
            {
                UpdateDelayed();
                rs_serializer.Serialize(writer, r_content);
            }
        }
    }
}
