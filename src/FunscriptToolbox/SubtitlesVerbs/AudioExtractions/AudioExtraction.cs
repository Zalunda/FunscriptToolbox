using Newtonsoft.Json;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public class AudioExtraction
    {
        [JsonProperty(Order = 1)]
        public string Id { get; }

        [JsonProperty(Order = 2)]
        public PcmAudio PcmAudio { get; private set; }

        public bool IsFinished => PcmAudio != null;

        public AudioExtraction(string id, PcmAudio pcmAudio = null)
        {
            this.Id = id;
            this.PcmAudio = pcmAudio;
        }

        public void SetPcmAudio(SubtitleGeneratorContext context, PcmAudio pcmAudio)
        {
            this.PcmAudio = pcmAudio;
            File.WriteAllBytes(
                context.WIP.BaseFilePath + $".{this.Id}.pcm",
                pcmAudio.Data);
        }

        internal void FinalizeLoad(string wipBasePath)
        {
            var pcmFilePath = wipBasePath + $".{this.Id}.pcm";
            if (File.Exists(pcmFilePath))
            {
                this.PcmAudio?.RegisterLoadPcmFunc(() => File.ReadAllBytes(pcmFilePath));
            }
            else
            {
                this.PcmAudio = null; // Force reload
            }
        }
    }
}