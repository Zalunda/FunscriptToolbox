using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    [JsonObject(ItemTypeNameHandling = TypeNameHandling.All)]
    public abstract class WhisperTranscriber : Transcriber
    {
        public PurfviewWhisperConfig WhisperConfig { get; }

        protected PurfviewWhisperHelper WhisperHelper { get; private set; }

        public WhisperTranscriber(
            string transcriptionId,
            IEnumerable<Translator> translators,
            PurfviewWhisperConfig whisperConfig)
            : base(transcriptionId, translators)
        {
            this.WhisperConfig = whisperConfig;
            this.WhisperHelper = new PurfviewWhisperHelper(whisperConfig);
        }
    }
}