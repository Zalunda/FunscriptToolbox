//using FunscriptToolbox.SubtitlesVerbs.Infra;
//using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
//using Newtonsoft.Json;

//namespace FunscriptToolbox.SubtitlesVerbs.Translations
//{
//    public class TranslatorAI : Translator
//    {
//        [JsonProperty(Order = 10)]
//        internal MetadataAggregator Metadatas { get; set; }
//        [JsonProperty(Order = 11)]
//        public int MaxItemsInRequest { get; set; } = 10000;
//        [JsonProperty(Order = 12)]
//        public int IncludePreviousItems { get; set; } = 0;
//        [JsonProperty(Order = 13)]
//        public int OverlapItemsInRequest { get; set; } = 0;


//        [JsonProperty(Order = 20, Required = Required.Always)]
//        public AIEngine Engine { get; set; }
//        [JsonProperty(Order = 21, Required = Required.Always)]
//        public AIOptions Options { get; set; }

//        public override bool IsPrerequisitesMet(
//            SubtitleGeneratorContext context,
//            Transcription transcription,
//            out string reason)
//        {
//            if (Metadatas?.IsPrerequisitesMet(context, out reason) == false)
//            {
//                return false;
//            }

//            reason = null;
//            return true;
//        }

//        public override void Translate(
//            SubtitleGeneratorContext context,
//            Transcription transcription,
//            Translation translation)
//        {
//            var runner = new AIEngineRunner<TranslatedItem>(
//                context,
//                this.Engine,
//                translation);

//            var items = this.Metadatas.GetTimingsWithMetadata<TranslatedItem>(context, translation); // + TODO { ProduceMetaData => null on old transcription }, Add transcription, add meta rules

//            var nbErrors = runner.HandlePreviousFiles();
//            if (nbErrors == 0)
//            {
//                runner.Run(CreateRequests(context, transcription, translation, items));
//            }
//        }
//    }
//}