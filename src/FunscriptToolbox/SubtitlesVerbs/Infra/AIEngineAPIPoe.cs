using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [ConfigDoc("Poe's API (https://api.poe.com/v1): many models (GPT, Gemini, Claude, Grok...) with one subscription / one balance of points.",
        Advice = "Good as a fallback in an AIEngineCollection. Not for the steps that need exact audio timings (ex. 'full-ai'): the audio can be shortened (silences removed) before it reaches the model.")]
    public class AIEngineAPIPoe : AIEngineAPIOpenAICompatible
    {
        public override string ToolName { get; } = "Poe-API";

        protected override dynamic ConvertPart(AIRequestPart part)
        {
            if (part is AIRequestPartAudio partAudio)
            {
                return new
                    {
                        type = "file",
                        file = new
                        {
                            filename = partAudio.FileName,
                            file_data = $"data:audio/mp3;base64,{Convert.ToBase64String(partAudio.Content)}"
                        }
                    };
            }
            else if (part is AIRequestPartImage partImage)
            {
                return new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = $"data:image/jpeg;base64,{Convert.ToBase64String(partImage.Content)}"
                        }
                    };
            }
            else if (part is AIRequestPartText partText)
            {
                return new
                {
                    type = "text",
                    text = partText.Content
                };
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}