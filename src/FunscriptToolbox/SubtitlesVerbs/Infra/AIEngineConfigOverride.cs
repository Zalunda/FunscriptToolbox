using Newtonsoft.Json;
using System.Dynamic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIEngineConfigOverride
    {
        public ExpandoObject RequestBodyExtension { get; set; }

        public AIEngineConfigOverride() { }

        public AIEngineConfigOverride(ExpandoObject requestBodyExtension)
        {
            this.RequestBodyExtension = requestBodyExtension;
        }

        public static AIEngineConfigOverride FromObject(object anonymousOrObject)
        {
            if (anonymousOrObject == null) return null;
            if (anonymousOrObject is ExpandoObject expando) return new AIEngineConfigOverride(expando);

            var json = JsonConvert.SerializeObject(anonymousOrObject);
            return new AIEngineConfigOverride(JsonConvert.DeserializeObject<ExpandoObject>(json));
        }
    }
}