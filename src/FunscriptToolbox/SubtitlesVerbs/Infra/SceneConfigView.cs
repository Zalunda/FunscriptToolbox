using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// The 'Config' tab of the inspector of the 'Flow' view (see ISceneConfigView): the configuration of a step, read again from the files
    /// (SubtitleGeneratorConfigLoader.LoadHierarchicallyWithSources), as a tree. Each value says where it was written (its level, see
    /// ConfigSource) and the values it replaced; a value written nowhere is the default of the code; a value that no class has is
    /// flagged as not used (it is ignored when the configuration is read).
    /// </summary>
    public class SceneConfigView : ISceneConfigView
    {
        private readonly SubtitleGeneratorContext r_context;
        private readonly string r_baseConfigPath;
        private readonly string r_targetFullPath;

        public SceneConfigView(SubtitleGeneratorContext context, string baseConfigPath, string targetFullPath)
        {
            r_context = context;
            r_baseConfigPath = baseConfigPath;
            r_targetFullPath = targetFullPath;
        }

        /// <summary>The key of the whole configuration of the scene (all its steps, its shared objects...), instead of a step: read-only.</summary>
        public const string WholeConfigKey = "all";

        public object GetWorkerConfig(string workerKey)
        {
            if (workerKey == WholeConfigKey)
            {
                if (r_context.Config == null)
                    return new { error = "The scene is not loaded (yet)." };
                return ToResult(LoadWholeTree(r_baseConfigPath, r_targetFullPath), r_context.Config.MergedConfigHash);
            }
            var (runningWorker, error) = FindRunningWorker(workerKey);
            if (runningWorker == null)
                return new { error };
            return Describe(r_baseConfigPath, r_targetFullPath, runningWorker, r_context.Config.MergedConfigHash);
        }

        private (SubtitleWorker Worker, string Error) FindRunningWorker(string workerKey)
        {
            var running = r_context.Config;
            if (running == null)
                return (null, "The scene is not loaded (yet).");
            if (workerKey == null || !workerKey.StartsWith("w") || !int.TryParse(workerKey.Substring(1), out var index))
                return (null, "Only the steps (the workers) have a configuration.");
            var runningWorkers = SceneFlowGraph.GetWorkers(running);
            if (index < 0 || index >= runningWorkers.Length)
                return (null, "Unknown worker.");
            return (runningWorkers[index], null);
        }

        /// <summary>
        /// The configuration of a worker (the one that runs), read again from the files, as a tree.
        /// runningHash: the hash of the configuration the scene uses (to say when the files changed since it was read).
        /// </summary>
        public static object Describe(string baseConfigPath, string targetFullPath, SubtitleWorker runningWorker, string runningHash)
        {
            return ToResult(LoadTree(baseConfigPath, targetFullPath, runningWorker), runningHash);
        }

        /// <summary>The configuration of a worker read again from the files: its tree, the files read, its id in the files.</summary>
        private class LoadedTree
        {
            public string Title { get; set; }
            public SubtitleGeneratorConfigWithSources Loaded { get; set; }
            public object[] Sources { get; set; }
            public Node Tree { get; set; }
            /// <summary>The id of the worker in the files (its 'Id': the key of its entry in 'Workers').</summary>
            public string WorkerId { get; set; }
            public string Error { get; set; }
            /// <summary>The objects that can be chosen for the properties of the tree (see Node.ObjectChoices).</summary>
            public Dictionary<string, ObjectChoiceList> Choices { get; set; }
        }

        private static object ToResult(LoadedTree loaded, string runningHash)
        {
            if (loaded.Tree == null)
                return new { title = loaded.Title, error = loaded.Error, sources = loaded.Sources };
            return new
            {
                title = loaded.Title,
                error = loaded.Error,
                // (the scene uses the configuration it read when it started: a change is used after a 'Retry')
                changed = loaded.Loaded.Config != null && runningHash != null && loaded.Loaded.Config.MergedConfigHash != runningHash,
                sources = loaded.Sources,
                tree = loaded.Tree,
                // (a list, not a dictionary: the web server writes the keys of a dictionary in camelCase)
                choices = loaded.Choices?.Values.ToList()
            };
        }

        /// <summary>The configuration read again from the files, and the list of the files read (no tree yet).</summary>
        private static LoadedTree LoadSources(string baseConfigPath, string targetFullPath, string title)
        {
            var loaded = SubtitleGeneratorConfigLoader.LoadHierarchicallyWithSources(baseConfigPath, targetFullPath);
            var baseConfigDir = Path.GetDirectoryName(Path.GetFullPath(baseConfigPath));
            return new LoadedTree
            {
                Title = title,
                Loaded = loaded,
                Error = loaded.Error,
                Sources = loaded.Sources
                    .GroupBy(s => s.Level)
                    .Select(g => g.First())
                    .Select(s => (object)new
                    {
                        level = s.Level,
                        kind = s.Kind,
                        file = Path.GetRelativePath(baseConfigDir, s.FilePath),
                        // (where it applies: the folder, relative to the main configuration)
                        folder = s.Folder,
                        exists = s.Exists
                    })
                    .ToArray()
            };
        }

        /// <summary>The whole configuration of the scene read again from the files (all its steps, its shared objects...), as one tree.</summary>
        private static LoadedTree LoadWholeTree(string baseConfigPath, string targetFullPath)
        {
            var result = LoadSources(baseConfigPath, targetFullPath, "The whole configuration of the scene");
            if (result.Loaded.Merged == null)
                return result;
            var builder = new TreeBuilder(result.Loaded.Merged);
            result.Tree = builder.Build("Configuration", result.Loaded.Merged, result.Loaded.Config, typeof(SubtitleGeneratorConfig), 0);
            result.Choices = builder.Choices;
            return result;
        }

        private static LoadedTree LoadTree(string baseConfigPath, string targetFullPath, SubtitleWorker runningWorker)
        {
            var result = LoadSources(baseConfigPath, targetFullPath, runningWorker.DisplayName);
            var loaded = result.Loaded;
            if (loaded.Merged == null)
                return result;

            // The same worker, in the configuration read again (the files may have changed since the scene started)
            var mergedWorkers = loaded.Merged.Property(nameof(SubtitleGeneratorConfig.Workers), StringComparison.OrdinalIgnoreCase)?.Value as JArray;
            JObject workerJson = null;
            SubtitleWorker workerObject = null;
            var loadedWorkers = loaded.Config?.Workers;
            if (loadedWorkers != null && mergedWorkers != null && loadedWorkers.Length == mergedWorkers.Count)
            {
                // (by its Id: its name in the web UI can change with the files, ex. 'Produces')
                var found = Array.FindIndex(loadedWorkers, w => w != null && w.GetType() == runningWorker.GetType() && w.Id == runningWorker.Id);
                if (found >= 0)
                {
                    workerObject = loadedWorkers[found];
                    workerJson = mergedWorkers[found] as JObject;
                }
            }
            else if (mergedWorkers != null)
            {
                // (the configuration can't be read: the worker is found in the merged files by its Id)
                workerJson = mergedWorkers.OfType<JObject>().FirstOrDefault(w => GetItemLabel(w) == runningWorker.Id);
            }
            if (workerJson == null)
            {
                result.Error = loaded.Error ?? "This step is not in the configuration files anymore.";
                return result;
            }

            result.WorkerId = GetItemLabel(workerJson);
            var builder = new TreeBuilder(loaded.Merged);
            result.Tree = builder.Build(runningWorker.DisplayName, workerJson, workerObject, runningWorker.GetType(), 0);
            result.Choices = builder.Choices;
            return result;
        }

        // (one change at a time, for all the scenes: they can share the same files)
        private static readonly object rs_writeLock = new object();

        /// <summary>
        /// Writes (or removes) a value of the configuration of a step in a '.override.user.config' (only those: the other files are replaced
        /// by a new version), then reads the configuration again: refused (the file is put back) when it can't be read anymore. Returns the
        /// configuration read again, and a warning when the value written is not the one used (ex. a deeper file has another value).
        /// </summary>
        public object ChangeValue(ConfigChangeRequest request)
        {
            // (the whole configuration of the scene: the 'parent' of the value is the item it's in (a step, a shared object), or the root)
            Func<LoadedTree> load;
            if (request?.Key == WholeConfigKey)
            {
                if (r_context.Config == null)
                    return new { error = "The scene is not loaded (yet)." };
                load = () => LoadWholeTree(r_baseConfigPath, r_targetFullPath);
            }
            else
            {
                var (runningWorker, error) = FindRunningWorker(request?.Key);
                if (runningWorker == null)
                    return new { error };
                load = () => LoadTree(r_baseConfigPath, r_targetFullPath, runningWorker);
            }
            try
            {
                lock (rs_writeLock)
                {
                    return SceneConfigWriter.Apply(load, request.Key == WholeConfigKey, r_context.Config.MergedConfigHash, request);
                }
            }
            catch (ConfigChangeException ex)
            {
                return new { error = ex.Message };
            }
        }

        /// <summary>Writes a change in a '.override.user.config' (see ChangeValue).</summary>
        private static class SceneConfigWriter
        {
            private static readonly string[] rs_workerIdKeys = { "Id" };

            /// <param name="load">Reads the tree again (the one of the step, or the whole configuration).</param>
            /// <param name="isWhole">The whole configuration of the scene: the path starts at its root.</param>
            public static object Apply(Func<LoadedTree> load, bool isWhole, string runningHash, ConfigChangeRequest request)
            {
                var before = load();
                if (before.Tree == null)
                    throw new ConfigChangeException(before.Error ?? "The configuration can't be read.");

                // The value, and the containers on its way (from the step)
                var chain = FindChain(before.Tree, request.Path);
                var leaf = chain[chain.Count - 1];
                var isObjectProperty = leaf.ObjectChoices != null;
                if (leaf.Kind != "val" && !isObjectProperty)
                    throw new ConfigChangeException("Only a value, or the object of a property that has choices, can be changed (not a list).");
                if (request.IsObjectChange && !isObjectProperty)
                    throw new ConfigChangeException("This property has no objects to choose from.");
                if (leaf.Kind != "val" && !request.IsObjectChange && !request.IsNull && !request.Remove)
                    throw new ConfigChangeException("Choose an object (a shared object, or a new one).");
                if (leaf.Unused == true && !request.Remove)
                    throw new ConfigChangeException("This setting is not used (no class has it): it can only be removed.");

                // The file: only a '.override.user.config'
                var source = before.Loaded.Sources.FirstOrDefault(s => s.Level == request.Level && s.Kind == ConfigSource.KindUserOverride)
                    ?? throw new ConfigChangeException($"'{request.Level}' is not a '.override.user.config' of this scene: only those can be changed (the other files are replaced by a new version).");

                // Where it goes: in the step ('Workers'), or in the shared object ('SharedObjects') for every step that uses it
                // (the whole configuration: in the item it's in, a step or a shared object, or at the root; the 'step' is that item)
                var containers = chain.Take(chain.Count - 1).ToList();
                var (stepPlace, firstInside) = isWhole
                    ? GetWholePlace(before, containers)
                    : (before.WorkerId == null ? null : new Place(
                        nameof(SubtitleGeneratorConfig.Workers), "Id", before.WorkerId, containers.Skip(1).ToList(), shared: false), 0);   // (the first one is the step itself)
                var sharedPlaces = Enumerable.Range(firstInside, containers.Count - firstInside).Reverse()
                    .Where(i => containers[i].Reference != null)
                    .Select(i => new Place(nameof(SubtitleGeneratorConfig.SharedObjects), "$id", containers[i].Reference, containers.Skip(i + 1).ToList(), shared: true))
                    .ToList();

                JObject newSharedObject = null;
                var value = request.Remove ? null
                    : request.IsNull ? JValue.CreateNull()
                    : request.IsObjectChange ? BuildObjectValue(before, leaf, request, out newSharedObject)
                    : ParseValue(leaf, request.Value);

                // The file, changed
                var filePath = source.FilePath;
                var previousText = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
                var file = previousText == null ? new JObject() : SubtitleGeneratorConfigLoader.ReadConfigFile(filePath);
                if (request.Remove)
                {
                    // (removed from where it is written in this file: the place asked first (the step / the innermost shared object), then the other ones,
                    // so 'Revert' works whatever way it was written)
                    var places = (request.Shared ? sharedPlaces.Concat(new[] { stepPlace }) : new[] { stepPlace }.Concat(sharedPlaces))
                        .Where(p => p != null && !p.Path.Any(n => n.Kind == "arr"));
                    if (!places.Any(p => TryRemove(file, p, leaf.Name)))
                        throw new ConfigChangeException($"This setting is not written in {request.Level}.");
                }
                else
                {
                    var place = request.Shared
                        ? sharedPlaces.FirstOrDefault() ?? throw new ConfigChangeException("This value is not in a shared object.")
                        : stepPlace ?? throw new ConfigChangeException("The id of this step is not known.");
                    if (place.Path.Any(n => n.Kind == "arr"))
                        throw new ConfigChangeException(request.Shared
                            ? "This value is in a list: a list is replaced as a whole by an override, it can't be changed here (yet)."
                            : "This value is in a list: a list is replaced as a whole by an override, it can't be changed here (yet). If it's in a shared object, change it for every step that uses it.");
                    if (newSharedObject != null)
                    {
                        // (a new shared object: defined in the same file, at the end of 'SharedObjects', before it's used)
                        var sharedArray = file.Property(nameof(SubtitleGeneratorConfig.SharedObjects), StringComparison.OrdinalIgnoreCase)?.Value as JArray;
                        if (sharedArray == null)
                        {
                            sharedArray = new JArray();
                            file.AddFirst(new JProperty(nameof(SubtitleGeneratorConfig.SharedObjects), sharedArray));
                        }
                        sharedArray.Add(newSharedObject);
                    }
                    Set(file, place, leaf.Name, value);
                }

                var newText = SubtitleGeneratorConfigLoader.ToHybridConfigText(file,
                    "// Your own changes (see the comment at the top of the '.override.config'). Also written by the web UI ('Config' tab of a step):" + Environment.NewLine
                    + "// the comments are not kept, the file before the last change is in '.bak'.");
                if (previousText != null)
                    File.WriteAllText(filePath + ".bak", previousText, new System.Text.UTF8Encoding(true));
                File.WriteAllText(filePath, newText, new System.Text.UTF8Encoding(true));

                // Read again: refused when the configuration can't be read anymore
                var after = load();
                if (after.Tree == null || (after.Error != null && before.Error == null))
                {
                    if (previousText == null) File.Delete(filePath); else File.WriteAllText(filePath, previousText, new System.Text.UTF8Encoding(true));
                    throw new ConfigChangeException($"Not saved: with this change, the configuration can't be read anymore: {after.Error}");
                }

                // The value used now: the one written? (ex. a deeper file, or the step itself, has another value)
                string warning = null;
                var now = TryFindChain(after.Tree, request.Path)?.LastOrDefault();
                if (!request.Remove && request.IsObjectChange)
                {
                    // (the object used now: the one chosen?)
                    var wantedRef = request.Ref ?? request.NewId;
                    var ok = wantedRef != null
                        ? now?.Reference == wantedRef
                        : now != null && now.Reference == null && now.Type == ((JObject)value)["$type"]?.ToString();
                    if (!ok)
                        warning = $"Written in {request.Level}, but the step uses another object: {now?.Reference ?? now?.Type ?? now?.Value ?? "(none)"}"
                            + (now?.Level != null ? $" (written in {now.Level}: a file read after {request.Level} has this setting too)." : ".");
                }
                else if (!request.Remove)
                {
                    var expected = new Node();
                    TreeBuilder.SetLeaf(expected, value);
                    static string Normalized(string text) => text?.Replace("\r\n", "\n").Trim();
                    if (now == null || Normalized(now.Value) != Normalized(expected.Value))
                        warning = $"Written in {request.Level}, but the step uses another value: {now?.Value ?? "(none)"}"
                            + (now?.Level != null ? $" (written in {now.Level}: a file read after {request.Level} has this setting too)." : ".");
                }
                return new
                {
                    ok = true,
                    file = Path.GetFileName(filePath),
                    warning,
                    config = ToResult(after, runningHash)
                };
            }

            private static readonly System.Text.RegularExpressions.Regex rs_itemIndex = new System.Text.RegularExpressions.Regex(@"^\[(\d+)\]");

            /// <summary>
            /// The whole configuration: where a value is written, from its 'parent'. In a step ('Workers') or a shared object ('SharedObjects'):
            /// that item, found by its id. Otherwise: the root of the file (ex. 'Global'). firstInside: the first container inside the parent.
            /// </summary>
            private static (Place Place, int FirstInside) GetWholePlace(LoadedTree before, List<Node> containers)
            {
                // containers: [root, top property, (item, ...)]
                var top = containers.Count > 1 ? containers[1] : null;
                foreach (var (arrayName, rootKey) in new[] { (nameof(SubtitleGeneratorConfig.Workers), "Id"), (nameof(SubtitleGeneratorConfig.SharedObjects), "$id") })
                {
                    if (top == null || !string.Equals(top.Name, arrayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (containers.Count < 3)
                        throw new ConfigChangeException($"A whole item of '{arrayName}' can't be replaced here: change its values.");
                    var item = containers[2];
                    var match = rs_itemIndex.Match(item.Name ?? "");
                    var mergedArray = before.Loaded.Merged.Property(arrayName, StringComparison.OrdinalIgnoreCase)?.Value as JArray;
                    var index = match.Success ? int.Parse(match.Groups[1].Value) : -1;
                    var itemJson = mergedArray != null && index >= 0 && index < mergedArray.Count ? mergedArray[index] as JObject : null;
                    var id = itemJson == null ? null : rootKey == "$id" ? itemJson.Property("$id")?.Value?.ToString() : GetItemLabel(itemJson);
                    if (string.IsNullOrEmpty(id))
                        throw new ConfigChangeException($"The id of this item of '{arrayName}' is not known: it can't be changed here.");
                    return (new Place(arrayName, rootKey, id, containers.Skip(3).ToList(), shared: false), 3);
                }
                return (new Place(null, null, null, containers.Skip(1).ToList(), shared: false), 1);
            }

            private static List<Node> FindChain(Node tree, string[] path)
            {
                return TryFindChain(tree, path) ?? throw new ConfigChangeException("This setting is not in the configuration anymore (the files changed?): reload it.");
            }

            private static List<Node> TryFindChain(Node tree, string[] path)
            {
                if (path == null || path.Length == 0)
                    return null;
                var chain = new List<Node> { tree };
                var current = tree;
                foreach (var name in path)
                {
                    current = current.Children?.FirstOrDefault(c => c.Name == name);
                    if (current == null)
                        return null;
                    chain.Add(current);
                }
                return chain;
            }

            /// <summary>The item of 'Workers' / 'SharedObjects' of the file (created when asked). No rootArray: the file itself (its root).</summary>
            private static JObject FindRoot(JObject file, string rootArray, string rootKey, string rootId, bool create)
            {
                if (rootArray == null)
                    return file;
                var array = file.Property(rootArray, StringComparison.OrdinalIgnoreCase)?.Value as JArray;
                var keys = rootKey == "$id" ? new[] { "$id" } : rs_workerIdKeys;
                var item = array?.OfType<JObject>().FirstOrDefault(o => keys.Any(k =>
                    o.Property(k, StringComparison.OrdinalIgnoreCase)?.Value is JValue v && v.Type == JTokenType.String
                    && string.Equals((string)v, rootId, StringComparison.OrdinalIgnoreCase)));
                if (item != null || !create)
                    return item;
                if (array == null)
                {
                    array = new JArray();
                    file[rootArray] = array;
                }
                item = new JObject { [rootKey] = rootId };
                array.Add(item);
                return item;
            }

            /// <summary>Where a value is written in a file: the item of 'Workers' / 'SharedObjects', and the objects from it to the value.</summary>
            private sealed class Place
            {
                public Place(string rootArray, string rootKey, string rootId, List<Node> path, bool shared)
                {
                    RootArray = rootArray; RootKey = rootKey; RootId = rootId; Path = path; Shared = shared;
                }
                public string RootArray { get; }
                public string RootKey { get; }
                public string RootId { get; }
                public List<Node> Path { get; }
                public bool Shared { get; }
            }

            private static void Set(JObject file, Place place, string leafName, JToken value)
            {
                var useReferences = !place.Shared;
                var container = FindRoot(file, place.RootArray, place.RootKey, place.RootId, create: true);
                foreach (var node in place.Path)
                {
                    var property = container.Property(node.Name, StringComparison.OrdinalIgnoreCase);
                    var child = property?.Value as JObject;
                    if (child == null)
                    {
                        child = new JObject();
                        if (property != null) property.Value = child; else container.Add(new JProperty(node.Name, child));
                    }
                    // (a shared object, changed for this step only: a '$ref' with other properties)
                    if (useReferences && node.Reference != null)
                    {
                        child.Remove("$ref");
                        child.AddFirst(new JProperty("$ref", node.Reference));
                    }
                    container = child;
                }
                var leafProperty = container.Property(leafName, StringComparison.OrdinalIgnoreCase);
                if (leafProperty != null) leafProperty.Value = value; else container.Add(new JProperty(leafName, value));
            }

            /// <summary>Removes the value from this place of the file. Returns false when it's not written there.</summary>
            private static bool TryRemove(JObject file, Place place, string leafName)
            {
                var root = FindRoot(file, place.RootArray, place.RootKey, place.RootId, create: false);
                if (root == null)
                    return false;
                var containers = new List<JObject> { root };
                foreach (var node in place.Path)
                {
                    var child = containers[containers.Count - 1].Property(node.Name, StringComparison.OrdinalIgnoreCase)?.Value as JObject;
                    if (child == null)
                        return false;
                    containers.Add(child);
                }
                var leafProperty = containers[containers.Count - 1].Property(leafName, StringComparison.OrdinalIgnoreCase);
                if (leafProperty == null)
                    return false;
                leafProperty.Remove();

                // (the objects left with nothing to change: removed, up to the item of the step / shared object)
                for (var i = containers.Count - 1; i >= 1; i--)
                {
                    if (containers[i].Properties().Any(p => p.Name != "$ref"))
                        break;
                    containers[i].Parent.Remove();
                }
                if (place.RootArray == null)
                    return true; // (the root of the file: stays)
                var identityKeys = place.RootKey == "$id" ? new[] { "$id" } : rs_workerIdKeys;
                if (!root.Properties().Any(p => !identityKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    var array = (JArray)root.Parent;
                    root.Remove();
                    if (array.Count == 0)
                        array.Parent.Remove();
                }
                return true;
            }

            private static readonly System.Text.RegularExpressions.Regex rs_validId = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.\-]+$");

            /// <summary>
            /// The object chosen for a property: a '$ref' to a shared object, or a new object (its '$type', or a copy of a shared object).
            /// A new object with an id: returned as a '$ref' to it, and newSharedObject is its definition (for 'SharedObjects').
            /// </summary>
            private static JToken BuildObjectValue(LoadedTree before, Node leaf, ConfigChangeRequest request, out JObject newSharedObject)
            {
                newSharedObject = null;
                if (before.Choices == null || !before.Choices.TryGetValue(leaf.ObjectChoices, out var choices))
                    throw new ConfigChangeException("This property has no objects to choose from.");

                if (request.Ref != null)
                {
                    if (!choices.Shared.Any(s => s.Id == request.Ref))
                        throw new ConfigChangeException($"'{request.Ref}' is not a shared object of type '{choices.Declared}'.");
                    return new JObject(new JProperty("$ref", request.Ref));
                }

                JObject content;
                if (request.CopyFrom != null)
                {
                    if (!choices.Shared.Any(s => s.Id == request.CopyFrom))
                        throw new ConfigChangeException($"'{request.CopyFrom}' is not a shared object of type '{choices.Declared}'.");
                    var sharedArray = before.Loaded.Merged.Property(nameof(SubtitleGeneratorConfig.SharedObjects), StringComparison.OrdinalIgnoreCase)?.Value as JArray;
                    var definition = sharedArray?.OfType<JObject>().FirstOrDefault(o => (o.Property("$id")?.Value as JValue)?.Value as string == request.CopyFrom)
                        ?? throw new ConfigChangeException($"The shared object '{request.CopyFrom}' is not in the configuration anymore (the files changed?): reload it.");
                    // (its values, as used: without any '$id' (defined once, by the original))
                    content = (JObject)definition.DeepClone();
                    foreach (var idProperty in content.DescendantsAndSelf().OfType<JObject>().Select(o => o.Property("$id")).Where(p => p != null).ToList())
                        idProperty.Remove();
                    // ('$type' first, to be read as the type of the object)
                    var typeProperty = content.Property("$type");
                    if (typeProperty != null && content.Properties().First() != typeProperty)
                    {
                        typeProperty.Remove();
                        content.AddFirst(typeProperty);
                    }
                }
                else
                {
                    if (!choices.Types.Contains(request.NewType, StringComparer.Ordinal))
                        throw new ConfigChangeException($"'{request.NewType}' is not a type of '{choices.Declared}'.");
                    content = new JObject(new JProperty("$type", request.NewType));
                }

                var newId = request.NewId?.Trim();
                if (string.IsNullOrEmpty(newId))
                    return content;
                if (!rs_validId.IsMatch(newId))
                    throw new ConfigChangeException($"'{newId}' can't be an id: only letters, digits, '_', '-' and '.'.");
                if (before.Loaded.Merged.DescendantsAndSelf().OfType<JObject>().Any(o => (o.Property("$id")?.Value as JValue)?.Value as string == newId))
                    throw new ConfigChangeException($"An object with the id '{newId}' already exists.");
                content.AddFirst(new JProperty("$id", newId));
                newSharedObject = content;
                return new JObject(new JProperty("$ref", newId));
            }

            /// <summary>The value typed by the user, checked against the type of the setting.</summary>
            private static JToken ParseValue(Node leaf, string text)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                text = text ?? string.Empty;
                var trimmed = text.Trim();
                switch (leaf.EditType)
                {
                    case "bool":
                        if (bool.TryParse(trimmed, out var b)) return new JValue(b);
                        throw new ConfigChangeException("Expected: true or false.");
                    case "int":
                        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, culture, out var l)) return new JValue(l);
                        throw new ConfigChangeException("Expected: a whole number.");
                    case "num":
                        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, culture, out var d)) return new JValue(d);
                        throw new ConfigChangeException("Expected: a number (with '.' for the decimals).");
                    case "enum":
                        var option = leaf.Options?.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase));
                        if (option != null) return new JValue(option);
                        throw new ConfigChangeException($"Expected one of: {string.Join(", ", leaf.Options ?? Array.Empty<string>())}.");
                    case "time":
                        if (TimeSpan.TryParse(trimmed, culture, out _)) return new JValue(trimmed);
                        throw new ConfigChangeException("Expected a duration: hh:mm:ss.fff (ex. 00:00:01.500).");
                    case "any":
                        // (no type known, ex. in a 'RequestBodyExtension': a number, true / false or null as such, otherwise a text)
                        try
                        {
                            if (JToken.Parse(trimmed) is JValue parsed && parsed.Type != JTokenType.String)
                                return parsed;
                        }
                        catch (JsonException)
                        {
                        }
                        return new JValue(text.Replace("\r\n", "\n"));
                    default:
                        return new JValue(text.Replace("\r\n", "\n"));
                }
            }
        }

        /// <summary>The name of an item of an array (a worker, an extractor...), from its id.</summary>
        private static string GetItemLabel(JObject item)
        {
            foreach (var key in new[] { "Id", "OutputFieldName", "$id", "$ref" })
            {
                var value = item.Property(key, StringComparison.OrdinalIgnoreCase)?.Value;
                if (value != null && value.Type == JTokenType.String && !string.IsNullOrEmpty((string)value))
                    return (string)value;
            }
            return null;
        }

        /// <summary>A node of the tree sent to the page (short names: a configuration can have thousands of values).</summary>
        public class Node
        {
            /// <summary>The name of the property (or "[2] full-whisper" for an item of an array).</summary>
            [JsonProperty("n")]
            public string Name { get; set; }

            /// <summary>"obj", "arr" or "val".</summary>
            [JsonProperty("k")]
            public string Kind { get; set; }

            /// <summary>The value (a leaf), as text.</summary>
            [JsonProperty("v", NullValueHandling = NullValueHandling.Ignore)]
            public string Value { get; set; }

            /// <summary>The kind of value of a leaf: "str", "num", "bool", "null".</summary>
            [JsonProperty("vt", NullValueHandling = NullValueHandling.Ignore)]
            public string ValueType { get; set; }

            /// <summary>Where it was written (see ConfigSource.Level). Null: written nowhere (Default), or not tracked.</summary>
            [JsonProperty("lv", NullValueHandling = NullValueHandling.Ignore)]
            public string Level { get; set; }

            /// <summary>Written nowhere: the default value of the code.</summary>
            [JsonProperty("d", NullValueHandling = NullValueHandling.Ignore)]
            public bool? Default { get; set; }

            /// <summary>No class has this property: it is ignored when the configuration is read.</summary>
            [JsonProperty("u", NullValueHandling = NullValueHandling.Ignore)]
            public bool? Unused { get; set; }

            /// <summary>The type of an object (ex. "AIEngineAPIGoogle").</summary>
            [JsonProperty("ty", NullValueHandling = NullValueHandling.Ignore)]
            public string Type { get; set; }

            /// <summary>A shared object (the id of the '$ref'): the values under it are the ones of the shared object.</summary>
            [JsonProperty("ref", NullValueHandling = NullValueHandling.Ignore)]
            public string Reference { get; set; }

            /// <summary>The '$ref' has other properties: the shared object with these values changed, for this step only.</summary>
            [JsonProperty("refo", NullValueHandling = NullValueHandling.Ignore)]
            public bool? ReferenceWithOverrides { get; set; }

            /// <summary>The object is defined here with an '$id' (it can be used elsewhere with a '$ref').</summary>
            [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
            public string Id { get; set; }

            /// <summary>The values it replaced, the oldest first.</summary>
            [JsonProperty("h", NullValueHandling = NullValueHandling.Ignore)]
            public List<HistoryItem> History { get; set; }

            [JsonProperty("c", NullValueHandling = NullValueHandling.Ignore)]
            public List<Node> Children { get; set; }

            /// <summary>How a value is edited (from the type of the setting): "bool", "int", "num", "enum", "time", "str" or "any".</summary>
            [JsonProperty("t", NullValueHandling = NullValueHandling.Ignore)]
            public string EditType { get; set; }

            /// <summary>The choices of an "enum".</summary>
            [JsonProperty("opts", NullValueHandling = NullValueHandling.Ignore)]
            public string[] Options { get; set; }

            /// <summary>The setting can be null.</summary>
            [JsonProperty("nl", NullValueHandling = NullValueHandling.Ignore)]
            public bool? CanBeNull { get; set; }

            /// <summary>
            /// A property whose value is an object that can be chosen (another shared object, or a new object): the key of its choices
            /// in the result ('choices': the shared objects and the types that fit the declared type of the property).
            /// </summary>
            [JsonProperty("oc", NullValueHandling = NullValueHandling.Ignore)]
            public string ObjectChoices { get; set; }

            /// <summary>The documentation of the setting (ConfigDocAttribute.What of the property).</summary>
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)]
            public string Doc { get; set; }

            /// <summary>What we suggest (ConfigDocAttribute.Advice of the property).</summary>
            [JsonProperty("adv", NullValueHandling = NullValueHandling.Ignore)]
            public string Advice { get; set; }

            /// <summary>An object: the documentation of its type (ConfigDocAttribute.What of the class).</summary>
            [JsonProperty("tdoc", NullValueHandling = NullValueHandling.Ignore)]
            public string TypeDoc { get; set; }

            /// <summary>An object: what we suggest about its type (ConfigDocAttribute.Advice of the class).</summary>
            [JsonProperty("tadv", NullValueHandling = NullValueHandling.Ignore)]
            public string TypeAdvice { get; set; }
        }

        /// <summary>The documentation of a type ('$type') that can be chosen (see ObjectChoiceList.TypeDocs).</summary>
        public class TypeDocItem
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)]
            public string Doc { get; set; }

            [JsonProperty("adv", NullValueHandling = NullValueHandling.Ignore)]
            public string Advice { get; set; }
        }

        /// <summary>The objects that can be chosen for a property of a type (see Node.ObjectChoices).</summary>
        public class ObjectChoiceList
        {
            /// <summary>The key (Node.ObjectChoices): the name of the declared type.</summary>
            [JsonProperty("key")]
            public string Key { get; set; }

            /// <summary>The name of the declared type, as in the files when it has one (ex. "AIEngine").</summary>
            [JsonProperty("declared")]
            public string Declared { get; set; }

            /// <summary>The shared objects ('SharedObjects') of this type.</summary>
            [JsonProperty("shared")]
            public List<SharedObjectChoice> Shared { get; set; }

            /// <summary>The types ('$type') a new object can have.</summary>
            [JsonProperty("types")]
            public string[] Types { get; set; }

            /// <summary>The documentation of the types of 'Types' and of the shared objects (only the ones that have one).</summary>
            [JsonProperty("typeDocs", NullValueHandling = NullValueHandling.Ignore)]
            public List<TypeDocItem> TypeDocs { get; set; }
        }

        public class SharedObjectChoice
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("type")]
            public string Type { get; set; }

            /// <summary>Where it is defined.</summary>
            [JsonProperty("lv", NullValueHandling = NullValueHandling.Ignore)]
            public string Level { get; set; }
        }

        public class HistoryItem
        {
            [JsonProperty("lv")]
            public string Level { get; set; }

            [JsonProperty("v")]
            public string Value { get; set; }
        }

        /// <summary>Builds the tree of a step: the merged JSON (where the values come from) walked with the objects read (their default values).</summary>
        private class TreeBuilder
        {
            private const int MaxDepth = 30;

            // (the contracts of the serializer of the configuration: the same properties, in the same order, with the same names)
            private static readonly IContractResolver rs_contracts = SubtitleGeneratorConfigLoader.rs_serializer.ContractResolver;

            // (the default values, as text: enums by name like in the files; its own serializer, without the references of the configuration)
            private static readonly JsonSerializer rs_valueSerializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                Converters = { new StringEnumConverter() },
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });

            private readonly Dictionary<string, JObject> r_definitions;
            private readonly HashSet<object> r_objectsInPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
            private readonly List<(string Id, Type Type, string TypeName, string Level)> r_sharedObjects;

            /// <summary>The objects that can be chosen for the properties of the tree (see Node.ObjectChoices), by the name of their declared type.</summary>
            public Dictionary<string, ObjectChoiceList> Choices { get; } = new Dictionary<string, ObjectChoiceList>(StringComparer.Ordinal);

            public TreeBuilder(JObject merged)
            {
                // The shared objects ('$id'), for the '$ref' (the first one when the same id is defined twice: an error of the files)
                r_definitions = new Dictionary<string, JObject>(StringComparer.Ordinal);
                foreach (var definition in merged.DescendantsAndSelf().OfType<JObject>())
                {
                    if (definition.Property("$id")?.Value is JValue id && id.Type == JTokenType.String && !r_definitions.ContainsKey((string)id))
                        r_definitions[(string)id] = definition;
                }
                r_sharedObjects = GetSharedObjects(merged);
            }

            /// <summary>The types that are never offered as a choice: placeholders (ex. the engine of the main configuration, AIEngineNeedToBeOverriden).</summary>
            private static bool IsPlaceholderType(Type type) => type == typeof(AIEngineNeedToBeOverriden);

            /// <summary>The objects of 'SharedObjects', with their type (not the placeholders, ex. 'AIEngineNeedToBeOverriden').</summary>
            internal static List<(string Id, Type Type, string TypeName, string Level)> GetSharedObjects(JObject merged)
            {
                var list = new List<(string, Type, string, string)>();
                var array = merged.Property(nameof(SubtitleGeneratorConfig.SharedObjects), StringComparison.OrdinalIgnoreCase)?.Value as JArray;
                foreach (var item in array?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!(item.Property("$id")?.Value is JValue id) || id.Type != JTokenType.String
                        || !(item.Property("$type")?.Value is JValue typeName) || typeName.Type != JTokenType.String)
                        continue;
                    Type type = null;
                    try { type = SubtitleGeneratorConfigLoader.rs_serializer.SerializationBinder.BindToType(null, (string)typeName); }
                    catch (Exception) { /* (an unknown type: not offered) */ }
                    if (type != null && !IsPlaceholderType(type))
                        list.Add(((string)id, type, (string)typeName, id.Annotation<ConfigSource>()?.Level));
                }
                return list;
            }

            // (the types a new object can have: the classes with a name in the files ('$type'), by declared type)
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string[]> rs_concreteTypes =
                new System.Collections.Concurrent.ConcurrentDictionary<Type, string[]>();

            internal static string[] GetConcreteTypes(Type declaredType)
            {
                return rs_concreteTypes.GetOrAdd(declaredType, t =>
                {
                    var binder = SubtitleGeneratorConfigLoader.rs_serializer.SerializationBinder;
                    return t.Assembly.GetTypes()
                        .Where(x => t.IsAssignableFrom(x) && !x.IsAbstract && !x.IsGenericTypeDefinition && !IsPlaceholderType(x))
                        .Select(x => { binder.BindToName(x, out var assemblyName, out var typeName); return assemblyName == null ? typeName : null; })
                        .Where(x => x != null)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                });
            }

            /// <summary>The key of the choices of a property of this type (see Node.ObjectChoices), or null when there is nothing to choose.</summary>
            private string RegisterChoices(Type declaredType)
            {
                if (declaredType == null || !declaredType.IsClass || declaredType == typeof(string) || declaredType == typeof(object)
                    || typeof(IEnumerable).IsAssignableFrom(declaredType) || typeof(JToken).IsAssignableFrom(declaredType)
                    || !(rs_contracts.ResolveContract(declaredType) is JsonObjectContract))
                    return null;
                var key = declaredType.Name;
                if (Choices.ContainsKey(key))
                    return key;
                var shared = r_sharedObjects
                    .Where(s => declaredType.IsAssignableFrom(s.Type))
                    .Select(s => new SharedObjectChoice { Id = s.Id, Type = s.TypeName, Level = s.Level })
                    .ToList();
                var types = GetConcreteTypes(declaredType);
                if (shared.Count == 0 && types.Length <= 1)
                    return null;
                var binder = SubtitleGeneratorConfigLoader.rs_serializer.SerializationBinder;
                var typeDocs = types
                    .Concat(shared.Select(s => s.Type))
                    .Distinct(StringComparer.Ordinal)
                    .Select(typeName =>
                    {
                        Type type = null;
                        try { type = binder.BindToType(null, typeName); }
                        catch (Exception) { /* (an unknown type: no documentation) */ }
                        var doc = GetDoc(type);
                        return doc == null ? null : new TypeDocItem { Type = typeName, Doc = doc.What, Advice = doc.Advice };
                    })
                    .Where(d => d != null)
                    .ToList();
                Choices[key] = new ObjectChoiceList { Key = key, Declared = declaredType.Name, Shared = shared, Types = types, TypeDocs = typeDocs.Count > 0 ? typeDocs : null };
                return key;
            }

            /// <summary>The documentation of a type: of its own class only (not inherited: a derived type would show the text of its base class under its own name).</summary>
            internal static ConfigDocAttribute GetDoc(Type type)
            {
                return type == null ? null : (ConfigDocAttribute)Attribute.GetCustomAttribute(type, typeof(ConfigDocAttribute), inherit: false);
            }

            /// <summary>The documentation of a property (also when it's declared, or overridden, in a base class).</summary>
            private static ConfigDocAttribute GetDoc(JsonProperty property)
            {
                return property.AttributeProvider?.GetAttributes(typeof(ConfigDocAttribute), inherit: true).OfType<ConfigDocAttribute>().FirstOrDefault();
            }

            private static Node WithDoc(Node node, JsonProperty property)
            {
                var doc = GetDoc(property);
                if (doc != null)
                {
                    node.Doc = doc.What;
                    node.Advice = doc.Advice;
                }
                return node;
            }

            public Node Build(string name, JToken json, object obj, Type declaredType, int depth)
            {
                var node = new Node { Name = name };
                SetSource(node, json);
                if (depth > 0)
                    node.ObjectChoices = RegisterChoices(declaredType);

                // A shared object: its values are the ones of the shared object (the '$ref' itself: where it was written)
                if (json is JObject reference && reference.Count == 1 && reference.Property("$ref")?.Value is JValue refValue)
                {
                    var id = (string)refValue;
                    node.Reference = id;
                    node.Level = refValue.Annotation<ConfigSource>()?.Level ?? node.Level;
                    // (the same '$ref' written again by a later file: the values the '$ref' itself replaced, after the ones the object replaced)
                    var refHistory = refValue.Annotation<ConfigSourceHistory>();
                    if (refHistory != null)
                        node.History = (node.History ?? new List<HistoryItem>())
                            .Concat(refHistory.Overridden.Select(o => new HistoryItem { Level = o.Source.Level, Value = $"{{\"$ref\":\"{o.Value}\"}}" }))
                            .ToList();
                    if (id == null || !r_definitions.TryGetValue(id, out var definition))
                    {
                        node.Kind = "val";
                        node.Value = $"(no shared object '{id}')";
                        return node;
                    }
                    json = definition;
                }
                else if (json?.Annotation<ConfigExpandedReference>() is ConfigExpandedReference expanded)
                {
                    // (a '$ref' with other properties: a copy of the shared object, these properties changed in it)
                    node.Reference = expanded.Id;
                    node.ReferenceWithOverrides = true;
                    node.Level = expanded.ReferenceSource.Level;
                }
                else if (json is JObject withId && withId.Property("$id")?.Value is JValue idValue && idValue.Type == JTokenType.String)
                {
                    node.Id = (string)idValue;
                }

                // (a value that is already JSON, ex. in a dynamic object: walked as JSON)
                if (obj is JToken objAsJson)
                {
                    obj = null;
                    declaredType = null;
                    if (json == null)
                    {
                        BuildFromJson(node, objAsJson, depth);
                        return node;
                    }
                }

                // (no object, ex. the configuration can't be read: the class of its '$type', like the serializer)
                if (obj == null && json is JObject typed && typed.Property("$type")?.Value is JValue typeName && typeName.Type == JTokenType.String)
                {
                    try
                    {
                        declaredType = SubtitleGeneratorConfigLoader.rs_serializer.SerializationBinder.BindToType(null, (string)typeName) ?? declaredType;
                    }
                    catch (Exception)
                    {
                        // (an unknown type: the declared one)
                    }
                }

                var type = obj?.GetType() ?? declaredType;
                if (obj != null && !(obj is string) && !type.IsValueType)
                {
                    if (depth > MaxDepth || !r_objectsInPath.Add(obj))
                    {
                        node.Kind = "val";
                        node.Value = "(…)";
                        return node;
                    }
                }
                try
                {
                    if (json is JValue jsonValue)
                    {
                        SetLeaf(node, jsonValue);
                        SetEditType(node, declaredType ?? type, jsonValue);
                        return node;
                    }
                    var contract = type == null ? null : rs_contracts.ResolveContract(Nullable.GetUnderlyingType(type) ?? type);
                    if (contract is JsonObjectContract objectContract && (obj != null || json is JObject))
                        BuildObject(node, json as JObject, obj, objectContract, depth);
                    else if (contract is JsonDictionaryContract dictionaryContract && (obj != null || json is JObject))
                        BuildDictionary(node, json as JObject, obj as IDictionary, dictionaryContract, depth);
                    else if (contract is JsonArrayContract arrayContract && (obj != null || json is JArray))
                        BuildArray(node, json as JArray, obj as IEnumerable, arrayContract, depth);
                    else if (json is JContainer)
                        BuildFromJson(node, json, depth);
                    else if (obj == null || obj is string || type.IsPrimitive || type.IsEnum || obj is TimeSpan || obj is decimal || obj is DateTime)
                    {
                        var leafValue = obj == null ? JValue.CreateNull() : JToken.FromObject(obj, rs_valueSerializer);
                        SetLeaf(node, leafValue);
                        SetEditType(node, declaredType ?? type, leafValue);
                    }
                    else
                    {
                        // (ex. a dynamic object written nowhere: its value, from the code)
                        var asJson = JToken.FromObject(obj, rs_valueSerializer);
                        if (asJson is JContainer)
                            BuildFromJson(node, asJson, depth);
                        else
                            SetLeaf(node, asJson);
                    }
                }
                catch (Exception ex)
                {
                    node.Kind = "val";
                    node.Value = $"(can't read it: {ex.Message})";
                    node.Children = null;
                }
                finally
                {
                    if (obj != null)
                        r_objectsInPath.Remove(obj);
                }
                return node;
            }

            private static void SetSource(Node node, JToken json)
            {
                if (json == null)
                {
                    node.Default = true;
                    return;
                }
                node.Level = json.Annotation<ConfigSource>()?.Level;
                var history = json.Annotation<ConfigSourceHistory>();
                if (history != null)
                    node.History = history.Overridden.Select(o => new HistoryItem { Level = o.Source.Level, Value = o.Value }).ToList();
            }

            /// <summary>How the value can be edited: from the type of the setting (or, without type, from the value itself).</summary>
            private static void SetEditType(Node node, Type declaredType, JToken value)
            {
                var underlying = declaredType == null ? null : (Nullable.GetUnderlyingType(declaredType) ?? declaredType);
                node.CanBeNull = declaredType == null || !declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) != null ? true : (bool?)null;
                if (underlying == typeof(bool))
                    node.EditType = "bool";
                else if (underlying != null && underlying.IsEnum)
                {
                    node.EditType = "enum";
                    node.Options = Enum.GetNames(underlying);
                }
                else if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte)
                    || underlying == typeof(uint) || underlying == typeof(ulong) || underlying == typeof(ushort))
                    node.EditType = "int";
                else if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
                    node.EditType = "num";
                else if (underlying == typeof(TimeSpan))
                    node.EditType = "time";
                else if (underlying == typeof(string))
                    node.EditType = "str";
                else
                {
                    // (no type, ex. in a 'RequestBodyExtension', or a type read from a text, ex. a language: from the value)
                    switch (value?.Type)
                    {
                        case JTokenType.Boolean: node.EditType = "bool"; break;
                        case JTokenType.Integer: node.EditType = "int"; break;
                        case JTokenType.Float: node.EditType = "num"; break;
                        case JTokenType.String: node.EditType = "str"; break;
                        default: node.EditType = "any"; break;
                    }
                    if (underlying == null || underlying == typeof(object))
                        node.CanBeNull = true;
                }
            }

            internal static void SetLeaf(Node node, JToken value)
            {
                node.Kind = "val";
                node.Children = null;
                switch (value?.Type ?? JTokenType.Null)
                {
                    case JTokenType.Null:
                    case JTokenType.Undefined:
                        node.Value = "null";
                        node.ValueType = "null";
                        break;
                    case JTokenType.String:
                        node.Value = (string)value;
                        node.ValueType = "str";
                        break;
                    case JTokenType.Boolean:
                        node.Value = value.ToString(Formatting.None);
                        node.ValueType = "bool";
                        break;
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        node.Value = value.ToString(Formatting.None);
                        node.ValueType = "num";
                        break;
                    default:
                        // (ex. a TimeSpan: its text, like in the files)
                        node.Value = value is JValue other
                            ? Convert.ToString(other.Value, System.Globalization.CultureInfo.InvariantCulture)
                            : value.ToString(Formatting.None);
                        node.ValueType = "str";
                        break;
                }
            }

            private void BuildObject(Node node, JObject json, object obj, JsonObjectContract contract, int depth)
            {
                node.Kind = "obj";
                node.Type = obj?.GetType().Name ?? (string)json?["$type"];
                var typeDoc = GetDoc(obj?.GetType() ?? contract.UnderlyingType);
                if (typeDoc != null)
                {
                    node.TypeDoc = typeDoc.What;
                    node.TypeAdvice = typeDoc.Advice;
                }
                node.Children = new List<Node>();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in contract.Properties)
                {
                    if (property.Ignored)
                        continue;
                    var jsonProperty = json?.Property(property.PropertyName, StringComparison.OrdinalIgnoreCase);
                    if (jsonProperty != null)
                        used.Add(jsonProperty.Name);
                    if (!property.Readable)
                    {
                        // (a property that can only be written, ex. an old name kept for the old files: used, but it has no value of its own)
                        if (jsonProperty != null)
                            node.Children.Add(WithDoc(Build(jsonProperty.Name, jsonProperty.Value, null, property.PropertyType, depth + 1), property));
                        continue;
                    }
                    // (written nowhere: only when there is an object to read its default value from)
                    if (jsonProperty == null && obj == null)
                        continue;
                    if (jsonProperty == null && property.ShouldSerialize != null && !property.ShouldSerialize(obj))
                        continue;
                    var value = obj == null ? null : property.ValueProvider.GetValue(obj);
                    node.Children.Add(WithDoc(Build(jsonProperty?.Name ?? property.PropertyName, jsonProperty?.Value, value, property.PropertyType, depth + 1), property));
                }
                AddUnusedProperties(node, json, used, depth);
            }

            private void BuildDictionary(Node node, JObject json, IDictionary obj, JsonDictionaryContract contract, int depth)
            {
                node.Kind = "obj";
                node.Children = new List<Node>();
                var objKeys = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (obj != null)
                    foreach (var key in obj.Keys)
                        objKeys[Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)] = key;
                var names = (json?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>())
                    .Concat(objKeys.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // (values of any type, ex. 'RequestBodyExtension', an ExpandoObject sent as is to an AI: only their JSON, nothing in it is 'not used')
                var anyValue = contract.DictionaryValueType == null || contract.DictionaryValueType == typeof(object);
                foreach (var name in names)
                {
                    var jsonProperty = json?.Property(name, StringComparison.OrdinalIgnoreCase);
                    var value = obj != null && objKeys.TryGetValue(name, out var key) ? obj[key] : null;
                    node.Children.Add(anyValue && jsonProperty != null
                        ? Build(name, jsonProperty.Value, null, null, depth + 1)
                        : Build(name, jsonProperty?.Value, value, contract.DictionaryValueType, depth + 1));
                }
            }

            private void BuildArray(Node node, JArray json, IEnumerable obj, JsonArrayContract contract, int depth)
            {
                node.Kind = "arr";
                node.Children = new List<Node>();
                var objItems = obj?.Cast<object>().ToList() ?? new List<object>();
                var count = Math.Max(objItems.Count, json?.Count ?? 0);
                for (var i = 0; i < count; i++)
                {
                    var itemJson = json != null && i < json.Count ? json[i] : null;
                    var itemObj = i < objItems.Count ? objItems[i] : null;
                    var label = itemObj is SubtitleWorker worker
                        ? worker.DisplayName
                        : (itemJson is JObject itemObject ? GetItemLabel(itemObject) : null);
                    node.Children.Add(Build($"[{i}]" + (label == null ? string.Empty : $" {label}"), itemJson, itemObj, contract.CollectionItemType, depth + 1));
                }
            }

            /// <summary>Only the JSON (no class to read it with, ex. the 'RequestBodyExtension' sent as is to an AI).</summary>
            private void BuildFromJson(Node node, JToken json, int depth)
            {
                if (json is JArray array)
                {
                    node.Kind = "arr";
                    node.Children = array.Select((item, i) => Build($"[{i}]", item, null, null, depth + 1)).ToList();
                }
                else if (json is JObject obj)
                {
                    node.Kind = "obj";
                    node.Type = node.Type ?? (string)obj["$type"];
                    node.Children = obj.Properties()
                        .Where(p => !p.Name.StartsWith("$"))
                        .Select(p => Build(p.Name, p.Value, null, null, depth + 1))
                        .ToList();
                }
                else
                {
                    SetLeaf(node, json as JValue);
                }
            }

            /// <summary>The properties written in the files that the class doesn't have: ignored when the configuration is read.</summary>
            private void AddUnusedProperties(Node node, JObject json, HashSet<string> used, int depth)
            {
                if (json == null)
                    return;
                foreach (var property in json.Properties().Where(p => !p.Name.StartsWith("$") && !used.Contains(p.Name)))
                {
                    var child = Build(property.Name, property.Value, null, null, depth + 1);
                    child.Unused = true;
                    node.Children.Add(child);
                }
            }
        }
    }
}
