using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{    
    public class TranslatorAI : Translator
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public AIEngine Engine { get; set; }

        [JsonProperty(Order = 11, Required = Required.Always)]
        public AIMessagesHandler MessagesHandler { get; set; }

        public override bool IsPrerequisitesMet(
            Transcription transcription,
            out string reason)
        {
            return this.MessagesHandler.IsReadyToStart(transcription, out reason);
        }

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            this.Engine.Execute(context, this.MessagesHandler, transcription, translation);
        }
    }
}