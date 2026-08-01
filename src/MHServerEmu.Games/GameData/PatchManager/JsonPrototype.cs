using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using System.Text.Json;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class JsonPrototype : ValueBase
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly PrototypeId _parentRef;
        private readonly List<Field> _fields = new();

        private Prototype _instance;

        public override ValueType ValueType { get => ValueType.Prototype; }

        public JsonPrototype(JsonElement jsonElement)
        {
            _parentRef = (PrototypeId)jsonElement.GetProperty("ParentDataRef").GetUInt64();

            Type classType = GameDatabase.DataDirectory.GetPrototypeClassType(_parentRef);
            if (!Verify.IsNotNull(classType)) return;

            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {
                string fieldName = jsonProperty.Name;

                // "ParentDataRef" is consumed above. "Description" isn't a real field on any prototype
                // class - patch authors use it as an inline human-readable comment on individual array
                // elements (see e.g. PatchDataMod_Vendors_OftH_VendorItems.json), the same way the outer
                // PrototypePatchEntry has its own top-level Description. Skip both silently rather than
                // failing the GetProperty() lookup below and logging a Verify warning for something that's
                // working as intended.
                if (fieldName == "ParentDataRef" || fieldName == "Description")
                    continue;

                System.Reflection.PropertyInfo fieldInfo = classType.GetProperty(fieldName);
                if (!Verify.IsNotNull(fieldInfo))
                    continue;

                Type fieldType = fieldInfo.PropertyType;

                // Array-typed fields need their own handling: ParseJsonElement() has no
                // JsonValueKind.Array case, so it falls through to value.ToString() and the array
                // arrives as a raw JSON string, which then fails the cast in ConvertValue(). That
                // failure is swallowed as a "Can't convert" warning and the field silently keeps
                // whatever ParentDataRef had - which is why a loot node's own nested Choices[]/
                // Modifiers[] stopped applying after upstream merge 34df4088b dropped mtzimas92's
                // ParseJsonArray() (b27e81dbe).
                object fieldValue = fieldType.IsArray && jsonProperty.Value.ValueKind == JsonValueKind.Array
                    ? ParseArrayField(jsonProperty.Value, fieldType)
                    : PatchEntryConverter.ParseJsonElement(jsonProperty.Value, fieldType);

                Field field = new(fieldName, fieldValue, fieldType);
                _fields.Add(field);
            }
        }

        /// <summary>
        /// Builds the value for an array-typed field. Prototype-element arrays are deferred: we keep a
        /// <see cref="JsonPrototypeArray"/> and materialize it in <see cref="GetValue()"/>, because
        /// actually constructing a prototype (AllocatePrototype/CopyPrototypeDataRefFields) requires
        /// GameDatabase to be initialized, and this runs during LoadPatchDataFromDisk() - which on this
        /// fork happens BEFORE Globals are loaded (see GameDatabase's static ctor). Doing it eagerly here
        /// throws NullReferenceException on lootGlobalsProto/blueprint and fails the whole patch file.
        /// Element types that are plain values (PrototypeId, AssetId, ints, ...) touch no game data, so
        /// they are safe to build immediately.
        /// </summary>
        private static object ParseArrayField(JsonElement jsonElement, Type fieldType)
        {
            Type elementType = fieldType.GetElementType();
            if (elementType == null)
                return PatchEntryConverter.ParseJsonElement(jsonElement, fieldType);

            if (typeof(Prototype).IsAssignableFrom(elementType))
                return new JsonPrototypeArray(jsonElement);

            JsonElement[] jsonArray = jsonElement.EnumerateArray().ToArray();
            Array result = Array.CreateInstance(elementType, jsonArray.Length);

            for (int i = 0; i < jsonArray.Length; i++)
            {
                object element = PatchEntryConverter.ParseJsonElement(jsonArray[i], elementType);
                result.SetValue(PrototypePatchManager.ConvertValue(element, elementType), i);
            }

            return result;
        }

        public override object GetValue()
        {
            if (!Verify.IsTrue(_parentRef != PrototypeId.Invalid)) return null;

            if (_instance == null)
            {
                Type classType = GameDatabase.DataDirectory.GetPrototypeClassType(_parentRef);
                if (!Verify.IsNotNull(classType)) return null;

                Prototype instance = GameDatabase.PrototypeClassManager.AllocatePrototype(classType);
                if (!Verify.IsNotNull(instance)) return null;

                CalligraphySerializer.CopyPrototypeDataRefFields(instance, _parentRef);

                foreach (Field field in _fields)
                {
                    System.Reflection.PropertyInfo fieldInfo = classType.GetProperty(field.Name);
                    if (!Verify.IsNotNull(fieldInfo))
                        continue;

                    try
                    {
                        // Materialize a deferred prototype-element array now that GameDatabase is ready.
                        // JsonPrototypeArray.GetValue() returns Prototype[]; ConvertValue() narrows it to
                        // the field's concrete element type (e.g. LootNodePrototype[]). Nested levels
                        // recurse naturally: each element is itself a JsonPrototype whose own array fields
                        // were deferred the same way.
                        object rawValue = field.Value is JsonPrototypeArray deferredArray
                            ? deferredArray.GetValue()
                            : field.Value;

                        object convertedValue = PrototypePatchManager.ConvertValue(rawValue, field.Type);
                        fieldInfo.SetValue(instance, convertedValue);
                    }
                    catch (Exception e)
                    {
                        // An array-typed field reaching here means ParseArrayField() did not handle it and
                        // the value arrived as a raw JSON string. That is always a regression, never normal
                        // patch data - it silently leaves the field at ParentDataRef's value, which is how
                        // every nested Choices[]/Modifiers[] in every patch file stopped applying for a day
                        // without a single error (upstream merge 34df4088b, 2026-07-30). Log it loudly so it
                        // is caught in minutes rather than by noticing loot has quietly gone missing.
                        if (field.Type.IsArray)
                            Logger.Error($"Can't convert ARRAY field {field.Name} in {classType.Name} - {e.Message}. " +
                                         $"Nested array support has regressed - see JsonPrototype.ParseArrayField().");
                        else
                            Logger.Warn($"Can't convert {field.Name} in {classType.Name} - {e.Message}");
                    }
                }

                _instance = instance;
            }

            return _instance;
        }

        private readonly struct Field(string name, object value, Type type)
        {
            public readonly string Name = name;
            public readonly object Value = value;
            public readonly Type Type = type;
        }
    }

    public class JsonPrototypeArray : ValueBase
    {
        private readonly JsonPrototype[] _jsonPrototypes;
        private Prototype[] _instances;

        public override ValueType ValueType { get => ValueType.PrototypeArray; }

        public JsonPrototypeArray(JsonElement jsonElement)
        {
            if (jsonElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Json element is not array");

            JsonElement[] jsonArray = jsonElement.EnumerateArray().ToArray();
            if (jsonArray.Length == 0)
            {
                _jsonPrototypes = [];
                _instances = [];
                return;
            }

            _jsonPrototypes = new JsonPrototype[jsonArray.Length];
            for (int i = 0; i < jsonArray.Length; i++)
                _jsonPrototypes[i] = new(jsonArray[i]);
        }

        public override object GetValue()
        {
            if (_instances == null)
            {
                Prototype[] instances = new Prototype[_jsonPrototypes.Length];
                for (int i = 0; i < _jsonPrototypes.Length; i++)
                    instances[i] = (Prototype)_jsonPrototypes[i].GetValue();
                _instances = instances;
            }

            return _instances;
        }
    }
}
