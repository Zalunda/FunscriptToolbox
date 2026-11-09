namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIRequestPart
    {
        public abstract string Modality { get; }

        public abstract BinaryDataType? AssociatedDataType { get; }

        public AIRequestPart(AIRequestSection section) 
        {
            this.Section = section;
        }

        public AIRequestSection Section { get; }
        public abstract string ForSimplifiedFullPrompt();
        public abstract double Units { get; }
        public abstract string UnitName { get; }
        public abstract double EstimatedTokens { get; }
        /// <summary>
        /// Approximate number of bytes this part will take in the JSON request body (e.g. base64 for binary data, JSON wrapper overhead).
        /// </summary>
        public abstract long EstimatedRequestBytes { get; }
        protected const int JsonPartOverheadBytes = 100; // e.g. {"inlineData":{"mimeType":"audio/wav","data":"..."}},
    }
}