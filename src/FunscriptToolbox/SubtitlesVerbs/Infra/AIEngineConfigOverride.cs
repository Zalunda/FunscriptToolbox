using Newtonsoft.Json;
using System.Dynamic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// Changes merged into the request body of the engine, for one task. The changes go in 'RequestBodyExtension'
    /// (ex. "EngineConfigOverride": { "RequestBodyExtension": { "generationConfig": { ... } } }): any other property is a configuration
    /// error (a property put directly under "EngineConfigOverride" would otherwise be silently ignored).
    /// </summary>
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Error)]
    public class AIEngineConfigOverride
    {
        [ConfigDoc("JSON merged into the RequestBodyExtension of the engine, for the requests of this step only (ex. { \"generationConfig\": { \"maxOutputTokens\": 16384 } }).",
            Advice = "Google: \"temperature\" / \"maxOutputTokens\" must be inside \"generationConfig\" (at the top level, the request is rejected).")]
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