using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData.Prototypes;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {

        private static readonly Logger Logger = LogManager.CreateLogger();
        private Stack<PrototypeId> _protoStack = new();
        private readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();
        private Dictionary<Prototype, string> _pathDict = new ();
        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        /// <summary>
        /// Loads patches before Globals are loaded.
        /// </summary>
        public void PreInitialize(bool enablePatchManager)
        {
            if (enablePatchManager == false) return;

            // Set this before loading rather than after: a patch entry that appends an existing
            // PrototypeId into a Prototype-typed array (GetElementValue() below) force-constructs
            // that target prototype immediately, as a side effect, while this loop is still parsing
            // OTHER patch files. A prototype's own PostProcess()/PreCheck()/PostOverride() sequence
            // only ever runs once (at first construction, then cached) - if that first construction
            // happens to land inside this same loop, PreCheck() needs _initialized already true to
            // find and apply whatever is already registered in _patchDict for it, even though the
            // overall load pass isn't finished yet. Confirmed via a real case (Tahiti's SSB removal
            // patch): SHIELDSupplyBoost.prototype got eagerly constructed here as a side effect of an
            // unrelated loot-table entry appending its own PrototypeId elsewhere, permanently and
            // silently skipping SHIELDSupplyBoost's own ActionsTriggeredOnItemEvent.Choices[0].Power
            // patch - no warning, no trace, since PreCheck() bailed on _initialized instead of ever
            // consulting _patchDict. This flag now covers both the PrePatchData pass here and the
            // PatchData pass in Initialize() below, since it's set once and never cleared.
            _initialized = true;
            LoadPatchDataFromDisk("PrePatchData");
            ReapplyPatchesToAlreadyConstructedPrototypes();
        }

        /// <summary>
        /// Loads patches after Globals are loaded.
        /// </summary>
        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager == false) return;

            LoadPatchDataFromDisk("PatchData");
            ReapplyPatchesToAlreadyConstructedPrototypes();
        }

        /// <summary>
        /// A patch entry can permanently miss its own PreCheck()/PostOverride() window regardless of
        /// file processing order: ParseJsonPrototype() (used for "Prototype"/"Prototype[]" values that
        /// embed an existing PrototypeId, e.g. adding an existing item into another table's Choices[])
        /// force-constructs that referenced prototype immediately, during raw JSON deserialization of
        /// whatever OTHER patch file happens to contain it - which can construct still more prototypes
        /// transitively (e.g. the referenced item's own native LootTable field). A prototype's
        /// PreCheck()/PostOverride() sequence only ever runs once, at first construction, so if that
        /// first construction happens before its own dedicated patch file is even parsed, its entries
        /// are stuck unpatched forever - no warning, no trace, since nothing ever revisits it.
        ///
        /// Confirmed via a real case (Tahiti's ICPLeader.json "Add SSB" entries, which reference
        /// BonusItemFindBox.prototype - whose own native data uses BonusItemFindTable.prototype as its
        /// loot table): this eagerly built BonusItemFindTable ~40 files before SSBOptimization.json/
        /// SSBRemovalPatch.json ever got parsed, permanently losing their entries despite the
        /// _initialized-ordering fix above (which only covers the reverse case: the target's own
        /// patches already registered before something else force-constructs it).
        ///
        /// Runs once, after every patch file has been parsed and _patchDict is fully populated. For any
        /// prototype that still has unpatched entries, GetPrototype() below is a harmless no-op if nothing
        /// has touched it yet (normal lazy construction then proceeds through the standard, now-correct
        /// PreCheck()/PostOverride() flow) - the only case where entries can STILL be unpatched afterward
        /// is a prototype that was already built earlier, which is exactly the case this needs to catch.
        /// Forcing PostProcessContainedPrototypes() directly (rather than PostProcess()) re-runs only the
        /// patch-application traversal, not whatever extra one-time setup a subclass's PostProcess()
        /// override does for itself - though that does still run again for every CONTAINED prototype
        /// touched during the retraversal, since those go through their own ordinary PostProcess() calls.
        /// </summary>
        private void ReapplyPatchesToAlreadyConstructedPrototypes()
        {
            foreach (var kvp in _patchDict)
            {
                if (NotPatched(kvp.Value) == false) continue;

                Prototype prototype = GameDatabase.GetPrototype<Prototype>(kvp.Key);
                if (prototype == null) continue;

                if (NotPatched(kvp.Value))
                    GameDatabase.PrototypeClassManager.PostProcessContainedPrototypes(prototype);
            }
        }

        private bool LoadPatchDataFromDisk(string prefix)
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (Directory.Exists(patchDirectory) == false)
            {
                Logger.Warn("LoadPatchDataFromDisk(): Game data directory not found");
                return false;
            }

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            // Read all .json files that start with the specified prefix
            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, prefix, "json"))
            {
                string fileName = Path.GetFileName(filePath);

                PrototypePatchEntry[] updateValues = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                if (updateValues == null)
                {
                    Logger.Warn($"LoadPatchDataFromDisk(): Failed to parse {fileName}, skipping");
                    continue;
                }

                foreach (PrototypePatchEntry value in updateValues)
                {
                    if (value.Enabled == false) continue;
                    PrototypeId prototypeId = GameDatabase.GetPrototypeRefByName(value.Prototype);
                    if (prototypeId == PrototypeId.Invalid) continue;
                    AddPatchValue(prototypeId, value);
                    count++;
                }

                Logger.Trace($"Parsed patch data from {fileName}");
            }

            if (count == 0)
                return false;

            Logger.Info($"Loaded {count} {prefix} patches");
            return true;
        }

        private void AddPatchValue(PrototypeId prototypeId, in PrototypePatchEntry value)
        {
            if (_patchDict.TryGetValue(prototypeId, out var patchList) == false)
            {
                patchList = [];
                _patchDict[prototypeId] = patchList;
            }
            patchList.Add(value);
        }

        public bool CheckProperties(PrototypeId protoRef, out Properties.PropertyCollection prop)
        {
            prop = null;
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
                foreach (var entry in list)
                    if (entry.Value.ValueType == ValueType.Properties)
                    {
                        prop = entry.Value.GetValue() as Properties.PropertyCollection;
                        return prop != null;
                    }

            return false;
        }

        public bool PreCheck(PrototypeId protoRef)
        {
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
            {
                if (NotPatched(list))
                    _protoStack.Push(protoRef);
            }

            return _protoStack.Count > 0;
        }

        private static bool NotPatched(List<PrototypePatchEntry> list)
        {
            foreach (var entry in list)
                if (entry.Patched == false) return true;
            return false;
        }

        public void PostOverride(Prototype prototype)
        {
            if (_protoStack.Count == 0) return;

            string currentPath = string.Empty;
            if (prototype.DataRef == PrototypeId.Invalid
                && _pathDict.TryGetValue(prototype, out currentPath) == false) return;

            PrototypeId patchProtoRef = _protoStack.Peek();
            if (prototype.DataRef != PrototypeId.Invalid)
            {
                if (prototype.DataRef != patchProtoRef) return;
                if (_patchDict.ContainsKey(prototype.DataRef))
                    patchProtoRef = _protoStack.Pop();
            }

            if (_patchDict.TryGetValue(patchProtoRef, out var list) == false) return;

            foreach (var entry in list)
                if (entry.Patched == false)
                    CheckAndUpdate(entry, prototype, currentPath);

            if (_protoStack.Count == 0)
                _pathDict.Clear();
        }

        private static bool CheckAndUpdate(PrototypePatchEntry entry, Prototype prototype, string currentPath)
        {
            if (currentPath.StartsWith('.')) currentPath = currentPath[1..];
            if (entry.СlearPath != currentPath) return false;

            var fieldInfo = prototype.GetType().GetProperty(entry.FieldName);
            if (fieldInfo == null)
            {
                Logger.Warn($"CheckAndUpdate: {entry.FieldName} not found for {entry.Prototype}");
                return false;
            }

            UpdateValue(prototype, fieldInfo, entry);
            Logger.Trace($"Patch Prototype: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()}");

            return true;
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            if (targetType.IsInstanceOfType(rawValue))
                return rawValue;

            // Handle array types - convert to correct element type if needed
            if (targetType.IsArray && rawValue.GetType().IsArray)
            {
                Type targetElementType = targetType.GetElementType();
                Type sourceElementType = rawValue.GetType().GetElementType();

                // If element types are compatible, create new array with correct type
                if (targetElementType != sourceElementType &&
                    (targetElementType.IsAssignableFrom(sourceElementType) || sourceElementType.IsAssignableFrom(targetElementType)))
                {
                    Array sourceArray = (Array)rawValue;
                    Array targetArray = Array.CreateInstance(targetElementType, sourceArray.Length);

                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        object element = sourceArray.GetValue(i);
                        if (element != null && targetElementType.IsInstanceOfType(element))
                            targetArray.SetValue(element, i);
                        else if (element != null)
                            targetArray.SetValue(ConvertValue(element, targetElementType), i);
                        else
                            targetArray.SetValue(null, i);
                    }

                    return targetArray;
                }

                return rawValue;
            }

            if (targetType.IsSubclassOf(typeof(Prototype)))
            {
                switch (rawValue)
                {
                    case PrototypeId protoRef:
                        return GameDatabase.GetPrototype<Prototype>(protoRef);

                    case ulong dataId:
                        return GameDatabase.GetPrototype<Prototype>((PrototypeId)dataId);
                }
            }

            // MarkerPrototype.Rotation is an Orientation (Yaw/Pitch/Roll), not a Vector3, but there's no
            // dedicated "Orientation" ValueType in patch data - JSON authors reuse ValueType:Vector3 for it
            // (same 3-float array shape), which used to fail here since Vector3 doesn't implement
            // IConvertible and there's no TypeConverter between the two distinct structs.
            if (targetType == typeof(Orientation) && rawValue is Vector3 vec)
                return new Orientation(vec.X, vec.Y, vec.Z);

            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                return converter.ConvertFrom(rawValue);

            return Convert.ChangeType(rawValue, targetType);
        }

        private static void UpdateValue(Prototype prototype, PropertyInfo fieldInfo, PrototypePatchEntry entry)
        {
            try
            {
                Type fieldType = fieldInfo.PropertyType;
                
                if (entry.ArrayValue)
                {
                    if (entry.ArrayIndex != -1)
                        SetIndexValue(prototype, fieldInfo, entry.ArrayIndex, entry.Value);
                    else
                        InsertValue(prototype, fieldInfo, entry.Value);
                }
                else
                {
                    object convertedValue = ConvertValue(entry.Value.GetValue(), fieldType);
                    fieldInfo.SetValue(prototype, convertedValue);
                }
                entry.Patched = true;
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed UpdateValue: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
            }
        }

        private static void SetIndexValue(Prototype prototype, PropertyInfo fieldInfo, int index, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");

            Array array = (Array)fieldInfo.GetValue(prototype);
            if (array == null || index < 0 || index >= array.Length)
                throw new IndexOutOfRangeException($"Invalid index {index} for array {fieldInfo.Name}.");

            object valueEntry = value.GetValue();

            var entryType = valueEntry.GetType();
            Type elementType = fieldType.GetElementType();

            if (elementType == null || IsTypeCompatible(elementType, entryType, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable to {elementType?.Name}.");

            object converted = GetElementValue(valueEntry, elementType);
            array.SetValue(converted, index);
        }

        private static void InsertValue(Prototype prototype, PropertyInfo fieldInfo, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType; 
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");     

            var valueEntry = value.GetValue();

            var entryType = valueEntry.GetType();
            Type elementType = fieldType.GetElementType(); 

            if (elementType == null || IsTypeCompatible(elementType, entryType, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable for {elementType?.Name}.");

            var currentArray = (Array)fieldInfo.GetValue(prototype);

            int newLength = CalcNewLength(currentArray, valueEntry);
            var newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentArray.Length);

            AddElements(newArray, elementType, valueEntry, currentArray.Length);

            fieldInfo.SetValue(prototype, newArray);
        }

        private static int CalcNewLength(Array currentArray, object valueEntry)
        {
            int currentLength = currentArray?.Length ?? 0;
            int valuesCount = 1;
            if (valueEntry is Array array)
            {
                int length = array.Length;
                if (length > 1) valuesCount = length;
            }
            return currentLength + valuesCount;
        }

        private static bool IsTypeCompatible(Type baseType, Type entryType, ValueType valueType)
        {
            if (entryType.IsArray) entryType = entryType.GetElementType();
            if (valueType == ValueType.PrototypeDataRef || valueType == ValueType.PrototypeDataRefArray) 
                entryType = typeof(Prototype);
            return baseType.IsAssignableFrom(entryType) || entryType.IsAssignableFrom(baseType);
        }

        private static void AddElements(Array newArray, Type elementType, object valueEntry, int lastIndex)
        {
            if (valueEntry is Array array)
            {
                foreach (var entry in array)
                {
                    object elementValue = GetElementValue(entry, elementType);
                    newArray.SetValue(elementValue, lastIndex++);
                }
            }
            else
            {
                object elementValue = GetElementValue(valueEntry, elementType);
                newArray.SetValue(elementValue, lastIndex);
            }
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
            if (elementType.IsClass && valueEntry is PrototypeId dataRef)
            {
                var prototype = GameDatabase.GetPrototype<Prototype>(dataRef) 
                    ?? throw new InvalidOperationException($"DataRef {dataRef} is not Prototype.");
                valueEntry = prototype;
            }

            // If valueEntry is already the correct type (e.g., Prototype), return it directly
            if (elementType.IsInstanceOfType(valueEntry))
                return valueEntry;

            return ConvertValue(valueEntry, elementType);            
        }

        public void SetPath(Prototype parent, Prototype child, string fieldName)
        {
            string parentPath = _pathDict.TryGetValue(parent, out var path) ? path : string.Empty;
            if (parent.DataRef != PrototypeId.Invalid && _patchDict.ContainsKey(parent.DataRef))
                parentPath = string.Empty;
            _pathDict[child] = $"{parentPath}.{fieldName}";
        }

        public void SetPathIndex(Prototype parent, Prototype child, string fieldName, int index)
        {
            string parentPath = _pathDict.TryGetValue(parent, out var path) ? path : string.Empty;
            if (parent.DataRef != PrototypeId.Invalid && _patchDict.ContainsKey(parent.DataRef))
                parentPath = string.Empty;
            _pathDict[child] = $"{parentPath}.{fieldName}[{index}]";
        }

        /// <summary>
        /// Returns every registered patch entry across all resolved prototypes, for external tooling
        /// (e.g. LootTableDumper's --patchstatus) to report on Patched status after a full load pass.
        /// Not used by any runtime game logic.
        /// </summary>
        public IEnumerable<(PrototypeId ProtoRef, PrototypePatchEntry Entry)> EnumerateAllEntries()
        {
            foreach (var kvp in _patchDict)
                foreach (PrototypePatchEntry entry in kvp.Value)
                    yield return (kvp.Key, entry);
        }
    }
}


