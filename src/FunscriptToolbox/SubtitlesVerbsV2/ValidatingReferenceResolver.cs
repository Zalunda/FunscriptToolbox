using Newtonsoft.Json.Serialization;

namespace FunscriptToolbox.SubtitlesVerbsV2
{
    internal class ValidatingReferenceResolver : IReferenceResolver
    {
        private readonly IReferenceResolver r_parent;

        public ValidatingReferenceResolver(IReferenceResolver parentResolver)
        {
            r_parent = parentResolver;
        }

        public void AddReference(object context, string reference, object value)
        {
            r_parent.AddReference(context, reference, value);
        }

        public string GetReference(object context, object value)
        {
            return r_parent.GetReference(context, value);
        }

        public bool IsReferenced(object context, object value)
        {
            return r_parent.IsReferenced(context, value);
        }

        public object ResolveReference(object context, string reference)
        {
            var value = r_parent.ResolveReference(context, reference);
            if (value == null)
            {
                throw new System.Exception($"Reference {reference} cannot be resolved");
            }
            return value;
        }
    }
}