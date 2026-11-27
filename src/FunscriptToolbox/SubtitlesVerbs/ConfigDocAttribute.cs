using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    /// <summary>
    /// The documentation of a setting of the configuration (a property), or of a type that can be chosen in the configuration ('$type'),
    /// for the user: shown in the 'Config' tab of the web UI (tooltip of the row, editor of the value, list of the types).
    /// (the '///' comments stay for the developers: they can say how it works inside)
    /// </summary>
    /// <remarks>
    /// On a property: also found on an override of it (Inherited). On a class: read for that class only (SceneConfigView doesn't
    /// look at the base classes: a derived type would show the text of its base class under its own name).
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class ConfigDocAttribute : Attribute
    {
        /// <param name="what">What the setting does (or what the type is).</param>
        public ConfigDocAttribute(string what)
        {
            this.What = what;
        }

        /// <summary>What the setting does (or what the type is).</summary>
        public string What { get; }

        /// <summary>Optional: what we suggest you do with it (the usual values, when to change it, the trade-offs...).</summary>
        public string Advice { get; set; }
    }
}
