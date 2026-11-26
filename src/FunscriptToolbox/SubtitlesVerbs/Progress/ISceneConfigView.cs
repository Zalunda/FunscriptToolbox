using System;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// The 'Config' tab of the inspector of the 'Flow' view: the configuration of a step of a scene, as a tree, with where each value
    /// comes from (the main configuration, an override file of a folder...). Called from the threads of the web server.
    /// </summary>
    public interface ISceneConfigView
    {
        /// <summary>The configuration of a worker of the graph (its key, ex. "w3"), read again from the files.</summary>
        object GetWorkerConfig(string workerKey);

        /// <summary>
        /// Writes (or removes) a value of the configuration of a step in a '.override.user.config', then returns the configuration
        /// read again ({ ok, file, warning, config }), or { error } (nothing changed).
        /// </summary>
        object ChangeValue(ConfigChangeRequest request);
    }

    /// <summary>A change of a value in the 'Config' tab (see ISceneConfigView.ChangeValue).</summary>
    public class ConfigChangeRequest
    {
        /// <summary>The key of the step in the graph (ex. "w3").</summary>
        public string Key { get; set; }

        /// <summary>The names of the nodes of the tree, from the step to the value (ex. ["TitleCardDetection", "Enabled"]).</summary>
        public string[] Path { get; set; }

        /// <summary>The level of the '.override.user.config' to write in (ex. "1-U").</summary>
        public string Level { get; set; }

        /// <summary>The value, as typed (checked against the type of the setting).</summary>
        public string Value { get; set; }

        /// <summary>Writes null instead of Value.</summary>
        public bool IsNull { get; set; }

        /// <summary>Removes the value from the file (instead of writing it).</summary>
        public bool Remove { get; set; }

        /// <summary>
        /// The value is in a shared object ('$ref'): true = written in the shared object (every step that uses it),
        /// false = for this step only (a '$ref' with other properties, in the entry of the step).
        /// </summary>
        public bool Shared { get; set; }

        // --- A property whose value is an object (see SceneConfigView.Node.ObjectChoices): another object chosen ---

        /// <summary>Another shared object: the property gets a '$ref' to it.</summary>
        public string Ref { get; set; }

        /// <summary>A new object of this type ('$type'), with the defaults of the code (or a copy of CopyFrom).</summary>
        public string NewType { get; set; }

        /// <summary>
        /// The new object is a shared object with this id: defined in 'SharedObjects' of the same file, and the property gets a '$ref' to it
        /// (other steps can use it). Null: defined in place, only for this property.
        /// </summary>
        public string NewId { get; set; }

        /// <summary>The new object is a copy of this shared object (its values, and its type).</summary>
        public string CopyFrom { get; set; }

        public bool IsObjectChange => Ref != null || NewType != null || CopyFrom != null;
    }

    /// <summary>A change refused, with a message for the user (nothing was changed).</summary>
    public class ConfigChangeException : Exception
    {
        public ConfigChangeException(string message) : base(message) { }
    }
}
