using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Prototypes.Markers;
using MHServerEmu.Games.Locales;
using MHServerEmu.Games.Properties;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LootTableDumper
{
    internal class Program
    {
        private static readonly HashSet<object> VisitedInChain = new(ReferenceEqualityComparer.Instance);

        static void Main(string[] args)
        {
            if (PakFileSystem.Instance.Initialize() == false)
            {
                Console.WriteLine("PakFileSystem failed to initialize.");
                return;
            }

            // Touch GameDatabase to trigger its static initializer (loads Calligraphy.sip / mu_cdata.sip / Patches / LiveTuning)
            if (GameDatabase.IsInitialized == false)
            {
                Console.WriteLine("GameDatabase failed to initialize.");
                return;
            }

            if (args.Length > 0 && args[0] == "--regions")
            {
                DumpRegionInventory();
                return;
            }

            if (args.Length > 0 && args[0] == "--search")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                SearchLootTables(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--namesearch")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                NameSearch(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--dump")
            {
                string path = args.Length > 1 ? args[1] : "";
                int maxDepth = args.Length > 2 && int.TryParse(args[2], out int d) ? d : 4;
                DumpGeneric(path, maxDepth);
                return;
            }

            if (args.Length > 0 && args[0] == "--cellsearch")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                CellMarkerSearch(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--designstatesweep")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                DesignStateSweep(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--assetsearch")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                AssetSearch(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--clientmapsearch")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                RegionClientMapSearch(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--findunrealclass")
            {
                if (args.Length > 1 && ulong.TryParse(args[1], out ulong assetIdVal))
                    FindUnrealClass((AssetId)assetIdVal);
                return;
            }

            if (args.Length > 0 && args[0] == "--findtrackable")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                FindTrackableEntities(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--getguid")
            {
                if (args.Length > 1 && ulong.TryParse(args[1], out ulong refVal))
                {
                    PrototypeId protoRef = (PrototypeId)refVal;
                    PrototypeGuid guid = GameDatabase.GetPrototypeGuid(protoRef);
                    Console.WriteLine($"Ref={refVal} ({SafeGetName(protoRef)}) -> Guid={(ulong)guid}");
                }
                return;
            }

            if (args.Length > 0 && args[0] == "--findbytype")
            {
                string typeName = args.Length > 1 ? args[1] : "";
                FindByCSharpType(typeName);
                return;
            }

            if (args.Length > 0 && args[0] == "--findteleportnpc")
            {
                FindTeleportInteractMissions();
                return;
            }

            if (args.Length > 0 && args[0] == "--dumplootprops")
            {
                string path = args.Length > 1 ? args[1] : "";
                DumpLootTableProps(path);
                return;
            }

            if (args.Length > 0 && args[0] == "--dumpallprops")
            {
                string path = args.Length > 1 ? args[1] : "";
                DumpAllProps(path);
                return;
            }

            if (args.Length > 0 && args[0] == "--resolveleaderboards")
            {
                string path = args.Length > 1 ? args[1] : "";
                ResolveLeaderboardSchedule(path);
                return;
            }

            if (args.Length > 0 && args[0] == "--convertbisjson")
            {
                string path = args.Length > 1 ? args[1] : "";
                ConvertBisJsonRefsToPaths(path);
                return;
            }

            if (args.Length > 0 && args[0] == "--dumpstrings")
            {
                string locoDir = args.Length > 1 ? args[1] : "";
                string searchPattern = args.Length > 2 ? args[2] : "";
                DumpStrings(locoDir, searchPattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--findlocalestringref")
            {
                if (args.Length > 1 && ulong.TryParse(args[1], out ulong localeStringVal))
                    FindLocaleStringRef((LocaleStringId)localeStringVal);
                return;
            }

            if (args.Length > 0 && args[0] == "--lookupstring")
            {
                string locoDir = args.Length > 1 ? args[1] : "";
                string idStr = args.Length > 2 ? args[2] : "";
                LookupString(locoDir, idStr);
                return;
            }

            if (args.Length > 0 && args[0] == "--resolveguid")
            {
                if (args.Length > 1 && ulong.TryParse(args[1], out ulong guidVal))
                {
                    PrototypeGuid guid = (PrototypeGuid)guidVal;
                    PrototypeId resolvedRef = GameDatabase.GetDataRefByPrototypeGuid(guid);
                    if (resolvedRef == PrototypeId.Invalid)
                        Console.WriteLine($"Guid={guidVal} does NOT resolve to any current prototype.");
                    else
                        Console.WriteLine($"Guid={guidVal} -> {SafeGetName(resolvedRef)} (Ref={(ulong)resolvedRef})");
                }
                return;
            }

            string[] tablePaths = args.Length > 0 ? args : new[]
            {
                "Loot/Tables/Mob/Bosses/PatrolHightown/CrossbonesHightownTable.prototype",
                "Loot/Tables/Mob/Bosses/PatrolHightown/Subtable/SharedPatrolHightownBosses.prototype",
                "Loot/Tables/Mob/Bosses/PatrolHightown/Subtable/SharedPatrolHightownBossesAll.prototype",
                "Loot/Tables/Mob/Bosses/PatrolHightown/Subtable/SharedPatrolHightownBossesCosmic.prototype",
                "Loot/Tables/Mob/Bosses/PatrolHightown/Subtable/SharedPatrolHightownBossesCosmicSub.prototype",
            };

            foreach (string path in tablePaths)
            {
                Console.WriteLine();
                Console.WriteLine($"==================== {path} ====================");

                PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(path);
                if (protoRef == PrototypeId.Invalid)
                {
                    Console.WriteLine("  Could not resolve prototype name.");
                    continue;
                }

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null)
                {
                    Console.WriteLine("  Could not load prototype.");
                    continue;
                }

                VisitedInChain.Clear();
                PrintNode(proto, 0);
            }
        }

        /// <summary>
        /// Searches every CellPrototype's MarkerSet for EntityMarker entries whose resolved entity name
        /// (or LastKnownEntityName, baked into the marker itself) contains the given pattern. Useful for
        /// finding where content is ACTUALLY placed in the world, as opposed to guessing/patching new markers.
        /// </summary>
        private static void CellMarkerSearch(string pattern)
        {
            Console.WriteLine($"==================== Searching all cell markers for entities matching '{pattern}' ====================");

            int cellCount = 0, matchCount = 0;
            foreach (PrototypeId cellRef in DataDirectory.Instance.IteratePrototypesInHierarchy<CellPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                cellCount++;
                CellPrototype cell = GameDatabase.GetPrototype<CellPrototype>(cellRef);
                if (cell == null) continue;

                List<string> hits = new();
                CollectMarkerHits(cell.MarkerSet, "MarkerSet", pattern, hits);
                CollectMarkerHits(cell.InitializeSet, "InitializeSet", pattern, hits);

                if (hits.Count > 0)
                {
                    matchCount++;
                    Console.WriteLine($"[Cell] {SafeGetName(cellRef)} (Ref={(ulong)cellRef})");
                    foreach (string hit in hits)
                        Console.WriteLine($"  {hit}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"-- Searched {cellCount} cells, {matchCount} contained a match --");
        }

        private static void CollectMarkerHits(MarkerSetPrototype markerSet, string setName, string pattern, List<string> hits)
        {
            if (markerSet?.Markers == null) return;

            for (int i = 0; i < markerSet.Markers.Length; i++)
            {
                if (markerSet.Markers[i] is not EntityMarkerPrototype entityMarker) continue;

                string entityName = null;
                if (entityMarker.EntityGuid != PrototypeGuid.Invalid)
                {
                    PrototypeId entityRef = GameDatabase.GetDataRefByPrototypeGuid(entityMarker.EntityGuid);
                    if (entityRef != PrototypeId.Invalid)
                        entityName = SafeGetName(entityRef);
                }
                bool stale = entityName == null;
                entityName ??= entityMarker.LastKnownEntityName;
                if (string.IsNullOrEmpty(entityName)) continue;

                if (entityName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{setName}.Markers[{i}] {entityName}{(stale ? " (STALE LastKnownEntityName, GUID unresolved)" : "")} @ {entityMarker.Position} (Guid={(ulong)entityMarker.EntityGuid})");
            }
        }

        /// <summary>
        /// Sweeps every prototype whose name matches pattern and, if it has a "DesignState" property
        /// (Missions, WorldEntities, MetaStates, Powers, etc. each declare their own), reports its value.
        /// Used to find every cut/NotInGame entity belonging to a content pack in one pass.
        /// </summary>
        private static void FindTrackableEntities(string pattern)
        {
            Console.WriteLine($"==================== Searching for entities with working ObjectiveInfo.EdgeEnabled + a real UnrealClass, name filter '{pattern}' ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (pattern.Length > 0 && name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false) continue;

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                var objectiveInfoProp = proto.GetType().GetProperty("ObjectiveInfo");
                var unrealClassProp = proto.GetType().GetProperty("UnrealClass");
                var designStateProp = proto.GetType().GetProperty("DesignState");
                if (objectiveInfoProp == null || unrealClassProp == null) continue;

                object objectiveInfo;
                object unrealClassVal;
                try
                {
                    objectiveInfo = objectiveInfoProp.GetValue(proto);
                    unrealClassVal = unrealClassProp.GetValue(proto);
                }
                catch { continue; }

                if (objectiveInfo == null || unrealClassVal is not AssetId unrealClass || unrealClass == AssetId.Invalid) continue;

                var edgeEnabledProp = objectiveInfo.GetType().GetProperty("EdgeEnabled");
                if (edgeEnabledProp == null) continue;

                object edgeEnabledVal;
                try { edgeEnabledVal = edgeEnabledProp.GetValue(objectiveInfo); }
                catch { continue; }
                if (edgeEnabledVal is not bool edgeEnabled || edgeEnabled == false) continue;

                string designState = "?";
                if (designStateProp != null)
                {
                    try { designState = designStateProp.GetValue(proto)?.ToString() ?? "?"; }
                    catch { }
                }

                count++;
                Console.WriteLine($"  {name} [{proto.GetType().Name}] DesignState={designState} UnrealClass={GameDatabase.GetAssetName(unrealClass)} (Ref={(ulong)protoRef})");
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        private static void FindUnrealClass(AssetId targetAssetId)
        {
            Console.WriteLine($"==================== Searching all prototypes for UnrealClass={(ulong)targetAssetId} ({GameDatabase.GetAssetName(targetAssetId)}) ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                var unrealClassProp = proto.GetType().GetProperty("UnrealClass");
                if (unrealClassProp == null) continue;

                object value;
                try { value = unrealClassProp.GetValue(proto); }
                catch { continue; }
                if (value is not AssetId assetId || assetId != targetAssetId) continue;

                count++;
                Console.WriteLine($"  {SafeGetName(protoRef)} [{proto.GetType().Name}] (Ref={(ulong)protoRef})");
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        private static void FindByCSharpType(string typeName)
        {
            Console.WriteLine($"==================== All prototypes whose C# type is exactly '{typeName}' ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;
                if (proto.GetType().Name != typeName) continue;

                count++;
                Console.WriteLine($"  {SafeGetName(protoRef)} (Ref={(ulong)protoRef})");
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        private static void DumpLootTableProps(string path)
        {
            PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(path);
            if (protoRef == PrototypeId.Invalid)
            {
                Console.WriteLine($"Could not resolve prototype name '{path}'.");
                return;
            }

            Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
            var propsField = proto.GetType().GetProperty("Properties");
            if (propsField == null || propsField.GetValue(proto) is not PropertyCollection properties)
            {
                Console.WriteLine("This prototype has no Properties collection.");
                return;
            }

            Console.WriteLine($"==================== LootTablePrototype properties on {path} ====================");
            foreach (var kvp in properties)
            {
                PropertyId id = kvp.Key;
                if (id.Enum != PropertyEnum.LootTablePrototype) continue;

                Property.FromParam(id, 0, out AssetId param0);
                Property.FromParam(id, 1, out int param1);
                Property.FromParam(id, 2, out AssetId param2);
                PrototypeId value = kvp.Value;

                Console.WriteLine($"  [{GameDatabase.GetAssetName(param0)}={(ulong)param0}] [{param1}] [{GameDatabase.GetAssetName(param2)}={(ulong)param2}] = {SafeGetName(value)} ({(ulong)value})");
            }
        }

        private static void DumpAllProps(string path)
        {
            PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(path);
            if (protoRef == PrototypeId.Invalid) { Console.WriteLine($"Could not resolve prototype name '{path}'."); return; }

            Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
            var propsField = proto.GetType().GetProperty("Properties");
            if (propsField == null || propsField.GetValue(proto) is not PropertyCollection properties)
            { Console.WriteLine("This prototype has no Properties collection."); return; }

            Console.WriteLine($"==================== ALL properties on {path} ====================");
            foreach (var kvp in properties)
                Console.WriteLine($"  {kvp.Key} = {(int)kvp.Value} (int) / {(float)kvp.Value} (float)");
        }

        /// <summary>
        /// Loads the client's real *.string files (e.g. Data/Game/Loco/eng.all) directly via Locale.ImportStringStream,
        /// bypassing LocaleManager/full locale setup entirely, and either dumps everything or filters by substring.
        /// This does not touch the running server's own Data/Config - it's a standalone read-only tool.
        /// </summary>
        private static void DumpStrings(string locoDir, string searchPattern)
        {
            if (Directory.Exists(locoDir) == false)
            {
                Console.WriteLine($"Directory not found: {locoDir}");
                return;
            }

            Locale locale = new(LocaleManager.Instance, Path.Combine(locoDir, "dummy.locale"), "English",
                LocaleLanguage.English, "English", LocaleRegion.All, "Everywhere", "eng.all");

            int fileCount = 0;
            foreach (string filePath in Directory.GetFiles(locoDir, "*.string"))
            {
                using FileStream fs = File.OpenRead(filePath);
                if (locale.ImportStringStream(filePath, fs))
                    fileCount++;
                else
                    Console.WriteLine($"Failed to import {filePath}");
            }

            var field = typeof(Locale).GetField("_stringMap", BindingFlags.NonPublic | BindingFlags.Instance);
            var stringMap = (Dictionary<LocaleStringId, LocaleDefaultString>)field.GetValue(locale);

            Console.WriteLine($"Loaded {fileCount} .string files, {stringMap.Count} total strings from {locoDir}");

            if (string.IsNullOrEmpty(searchPattern))
            {
                Console.WriteLine("(pass a search term as the 2nd argument to filter by substring, e.g. --dumpstrings <dir> \"threat\")");
                return;
            }

            Console.WriteLine($"==================== Strings containing '{searchPattern}' ====================");
            int matches = 0;
            foreach (var kvp in stringMap)
            {
                if (kvp.Value.String.Contains(searchPattern, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                Console.WriteLine($"  {(ulong)kvp.Key} = \"{kvp.Value.String}\"");
                matches++;
            }
            Console.WriteLine($"-- {matches} matches --");
        }

        /// <summary>
        /// Scans every prototype's public properties (including nested Prototype-typed fields/arrays, one level deep)
        /// for a LocaleStringId field matching the given value. Used to find which field on which prototype actually
        /// carries a given string id, when it's not obvious from a targeted --dump.
        /// </summary>
        private static void FindLocaleStringRef(LocaleStringId targetId)
        {
            Console.WriteLine($"==================== Searching all prototypes for LocaleStringId={(ulong)targetId} ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                foreach (var prop in proto.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object value;
                    try { value = prop.GetValue(proto); }
                    catch { continue; }

                    if (value is LocaleStringId lsid && lsid == targetId)
                    {
                        count++;
                        Console.WriteLine($"  {SafeGetName(protoRef)} [{proto.GetType().Name}].{prop.Name} (Ref={(ulong)protoRef})");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        private static void LookupString(string locoDir, string idStr)
        {
            if (Directory.Exists(locoDir) == false || ulong.TryParse(idStr, out ulong idVal) == false)
            {
                Console.WriteLine("Usage: --lookupstring <locoDir> <numeric LocaleStringId>");
                return;
            }

            Locale locale = new(LocaleManager.Instance, Path.Combine(locoDir, "dummy.locale"), "English",
                LocaleLanguage.English, "English", LocaleRegion.All, "Everywhere", "eng.all");

            foreach (string filePath in Directory.GetFiles(locoDir, "*.string"))
            {
                using FileStream fs = File.OpenRead(filePath);
                locale.ImportStringStream(filePath, fs);
            }

            string text = locale.GetLocaleString((LocaleStringId)idVal);
            Console.WriteLine(string.IsNullOrEmpty(text) ? $"{idVal} = (not found)" : $"{idVal} = \"{text}\"");
        }

        private static void FindTeleportInteractMissions()
        {
            Console.WriteLine("==================== Missions containing BOTH a MissionConditionEntityInteractPrototype AND a MissionActionPlayerTeleportPrototype ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<MissionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                VisitedInChain.Clear();
                bool hasInteract = ContainsTypeInGraph(proto, "MissionConditionEntityInteractPrototype", 0, 6);
                VisitedInChain.Clear();
                bool hasTeleport = ContainsTypeInGraph(proto, "MissionActionPlayerTeleportPrototype", 0, 6);

                if (hasInteract && hasTeleport)
                {
                    count++;
                    Console.WriteLine($"  {SafeGetName(protoRef)} (Ref={(ulong)protoRef})");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        private static bool ContainsTypeInGraph(object obj, string typeName, int depth, int maxDepth)
        {
            if (obj == null || depth > maxDepth) return false;

            Type type = obj.GetType();
            if (type.IsValueType == false)
            {
                if (VisitedInChain.Contains(obj)) return false;
                VisitedInChain.Add(obj);
            }

            if (obj is Prototype && type.Name == typeName)
                return true;

            if (obj is Prototype)
            {
                foreach (var prop in type.GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object value;
                    try { value = prop.GetValue(obj); }
                    catch { continue; }
                    if (value == null) continue;

                    if (value is Prototype childProto)
                    {
                        if (childProto.GetType().Name == typeName) return true;
                        if (ContainsTypeInGraph(childProto, typeName, depth + 1, maxDepth)) return true;
                    }
                    else if (value is System.Collections.IEnumerable enumerable && value is not string)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is Prototype itemProto)
                            {
                                if (itemProto.GetType().Name == typeName) return true;
                                if (ContainsTypeInGraph(itemProto, typeName, depth + 1, maxDepth)) return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static void DesignStateSweep(string pattern)
        {
            Console.WriteLine($"==================== DesignState sweep for prototypes matching '{pattern}' ====================");

            int total = 0;
            var byState = new Dictionary<string, List<string>>();

            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false) continue;

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                var designStateProp = proto.GetType().GetProperty("DesignState");
                if (designStateProp == null) continue;

                object value;
                try { value = designStateProp.GetValue(proto); }
                catch { continue; }
                if (value == null) continue;

                total++;
                string state = value.ToString();
                if (byState.TryGetValue(state, out List<string> list) == false)
                {
                    list = new();
                    byState[state] = list;
                }
                list.Add($"{name} [{proto.GetType().Name}]");
            }

            foreach (var kvp in byState.OrderBy(k => k.Key))
            {
                Console.WriteLine();
                Console.WriteLine($"-- DesignState={kvp.Key}: {kvp.Value.Count} --");
                foreach (string entry in kvp.Value.OrderBy(e => e))
                    Console.WriteLine($"  {entry}");
            }

            Console.WriteLine();
            Console.WriteLine($"-- {total} prototypes with a DesignState field matched '{pattern}' --");
        }

        /// <summary>Searches asset names across all asset types for the given substring.</summary>
        private static void AssetSearch(string pattern)
        {
            Console.WriteLine($"==================== Searching all asset names for '{pattern}' ====================");

            int count = 0;
            foreach (AssetId assetId in GameDatabase.SearchAssets(pattern, DataFileSearchFlags.IgnoreCase | DataFileSearchFlags.SortMatchesByName))
            {
                string name = GameDatabase.GetAssetName(assetId);
                Console.WriteLine($"  {name} (AssetId={(ulong)assetId})");
                count++;
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} matches --");
        }

        /// <summary>Prints every non-abstract RegionPrototype's name alongside its resolved ClientMap asset name.</summary>
        private static void RegionClientMapSearch(string pattern)
        {
            Console.WriteLine($"==================== Region ClientMap survey (filter: '{pattern}') ====================");

            foreach (PrototypeId regionRef in DataDirectory.Instance.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(regionRef);
                if (string.IsNullOrEmpty(pattern) == false && name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false) continue;

                RegionPrototype proto = GameDatabase.GetPrototype<RegionPrototype>(regionRef);
                if (proto == null) continue;

                string clientMapName = proto.ClientMap != AssetId.Invalid ? GameDatabase.GetAssetName(proto.ClientMap) : "(none)";
                Console.WriteLine($"{name} -> ClientMap: {clientMapName}");
            }
        }

        /// <summary>Searches ALL prototype names (any type), useful for locating door/gate/blocker/kismet entities by keyword.</summary>
        private static void NameSearch(string pattern)
        {
            Console.WriteLine($"==================== Searching ALL prototype names for '{pattern}' ====================");

            // GameDatabase.SearchPrototypes() defaults parentBlueprintId to BlueprintId.Invalid, which
            // resolves to a null blueprint and an always-empty iterator - use IterateAllPrototypes() instead.
            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false) continue;

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                string typeName = proto != null ? proto.GetType().Name : "?";
                Console.WriteLine($"  {name} (Ref={(ulong)protoRef}) [{typeName}]");
                count++;
            }
            Console.WriteLine($"  -- {count} matches --");
        }

        /// <summary>
        /// Reads a LeaderboardSchedule.json and, for each entry, resolves its string path to the
        /// exact (signed-long) PrototypeGuid and placeholder ActiveInstanceId the engine's own
        /// LeaderboardScheduler/GenerateTables would compute for it - used to hand-seed missing
        /// Leaderboards table rows without guessing at the id math ourselves. Placeholder
        /// ActiveInstanceId is one less than Leaderboard.GenerateInitialInstanceId's real value
        /// (top 32 bits of the guid, bottom 32 zeroed instead of =1), so that LeaderboardDatabase.
        /// LoadSchedule's own "IsEnabled False->True" branch - which does ActiveInstanceId + 1 -
        /// lands exactly on the canonical initial instance id when it activates each leaderboard
        /// for real on next server start, instead of us trying to fabricate DBLeaderboardInstance
        /// rows (and their activation-date math) by hand.
        /// Output: one line per entry, pipe-separated: guid|placeholderActiveInstanceId|prototypeName|leaderboardIdPath
        /// </summary>
        private static void ResolveLeaderboardSchedule(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path) == false)
            {
                Console.WriteLine("Usage: --resolveleaderboards <path to LeaderboardSchedule.json>");
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            int resolved = 0, unresolved = 0;

            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                string idPath = entry.GetProperty("LeaderboardId").GetString();
                PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(idPath);
                if (protoRef == PrototypeId.Invalid)
                {
                    Console.WriteLine($"UNRESOLVED|{idPath}");
                    unresolved++;
                    continue;
                }

                PrototypeGuid guid = GameDatabase.GetPrototypeGuid(protoRef);
                long guidSigned = unchecked((long)(ulong)guid);
                ulong placeholderActiveInstance = (ulong)guid & 0xFFFFFFFF00000000UL;
                long placeholderActiveInstanceSigned = unchecked((long)placeholderActiveInstance);
                string name = protoRef.GetNameFormatted();

                Console.WriteLine($"{guidSigned}|{placeholderActiveInstanceSigned}|{name}|{idPath}");
                resolved++;
            }

            Console.WriteLine($"-- resolved {resolved}, unresolved {unresolved} --");
        }

        /// <summary>
        /// Rewrites PhantomBiSGear.json in place so every slot entry carries a human-readable
        /// full prototype "path" (e.g. "Armor/UniquePrototypes/Avatars/WinterSoldier/Unique385.prototype")
        /// resolved from its hex "ref", instead of only the hex ref + short display name. The hex
        /// form is opaque to hand-editing - the path is exactly what community build guides and our
        /// own data files already use, so this makes the file directly manually-editable without
        /// needing a hex lookup for every change. "ref" is kept alongside "path" (re-resolved from
        /// path, not copied) as a cheap load-time integrity check in PhantomBiSData.cs.
        /// </summary>
        private static void ConvertBisJsonRefsToPaths(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path) == false)
            {
                Console.WriteLine("Usage: --convertbisjson <path to PhantomBiSGear.json>");
                return;
            }

            JsonNode root = JsonNode.Parse(File.ReadAllText(path));
            if (root is not JsonObject rootObj)
            {
                Console.WriteLine("Root of the file is not a JSON object.");
                return;
            }

            int converted = 0, unresolved = 0, heroes = 0;
            var unresolvedDetails = new List<string>();

            foreach (var heroKvp in rootObj)
            {
                if (heroKvp.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                if (heroKvp.Value is not JsonObject heroObj) continue;
                if (heroObj["slots"] is not JsonObject slotsObj) continue;

                heroes++;
                foreach (var slotKvp in slotsObj)
                {
                    if (slotKvp.Value is not JsonObject slotObj) continue;

                    string hex = slotObj["ref"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(hex)) continue;

                    string hexDigits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
                    if (ulong.TryParse(hexDigits, System.Globalization.NumberStyles.HexNumber, null, out ulong refVal) == false)
                    {
                        unresolved++;
                        unresolvedDetails.Add($"{heroKvp.Key}.{slotKvp.Key}: ref '{hex}' is not valid hex");
                        continue;
                    }

                    PrototypeId protoRef = (PrototypeId)refVal;
                    string fullPath = SafeGetName(protoRef);
                    if (fullPath == "(unnamed)")
                    {
                        unresolved++;
                        unresolvedDetails.Add($"{heroKvp.Key}.{slotKvp.Key}: ref 0x{refVal:X16} does not resolve to any current prototype");
                        continue;
                    }

                    slotObj["path"] = fullPath;
                    converted++;
                }
            }

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"Converted {converted} slot refs to paths across {heroes} heroes. Rewrote: {path}");
            if (unresolved > 0)
            {
                Console.WriteLine($"-- {unresolved} refs could NOT be resolved (left without a 'path' field, still have their original 'ref'): --");
                foreach (string detail in unresolvedDetails)
                    Console.WriteLine($"  {detail}");
            }
        }

        /// <summary>
        /// Generic reflection-based dump of any prototype's public properties, recursing into nested
        /// Prototype objects and arrays up to maxDepth. PrototypeId-typed fields get their name resolved.
        /// </summary>
        private static void DumpGeneric(string path, int maxDepth)
        {
            PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(path);
            if (protoRef == PrototypeId.Invalid)
            {
                Console.WriteLine($"Could not resolve prototype name: {path}");
                return;
            }

            Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
            if (proto == null)
            {
                Console.WriteLine($"Could not load prototype: {path}");
                return;
            }

            Console.WriteLine($"==================== {path} (Ref={(ulong)protoRef}) ====================");
            VisitedInChain.Clear();
            DumpReflect(proto, 0, maxDepth);
        }

        private static void DumpReflect(object obj, int depth, int maxDepth)
        {
            if (obj == null) return;
            string indent = new string(' ', depth * 2);

            if (obj is Prototype proto)
            {
                if (VisitedInChain.Contains(proto))
                {
                    Console.WriteLine($"{indent}[{proto.GetType().Name}] {SafeGetName(proto.DataRef)}  <-- already shown, skipping to avoid cycle");
                    return;
                }
                VisitedInChain.Add(proto);
            }

            var type = obj.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                object value;
                try { value = prop.GetValue(obj); }
                catch { continue; }

                if (value == null) { continue; }

                if (value is PrototypeId protoIdVal)
                {
                    if (protoIdVal == PrototypeId.Invalid) continue;
                    Console.WriteLine($"{indent}{prop.Name}: {SafeGetName(protoIdVal)} (Ref={(ulong)protoIdVal})");
                }
                else if (value is AssetId assetIdVal)
                {
                    if (assetIdVal == AssetId.Invalid) continue;
                    string assetName = GameDatabase.GetAssetName(assetIdVal);
                    Console.WriteLine($"{indent}{prop.Name}: {(string.IsNullOrEmpty(assetName) ? "(UNRESOLVED)" : assetName)} (AssetId={(ulong)assetIdVal})");
                }
                else if (value is PrototypeId[] protoIdArr)
                {
                    if (protoIdArr.Length == 0) continue;
                    Console.WriteLine($"{indent}{prop.Name}[{protoIdArr.Length}]: {string.Join(", ", protoIdArr.Select(SafeGetName))}");
                }
                else if (value is Prototype nestedProto)
                {
                    Console.WriteLine($"{indent}{prop.Name}: [{nestedProto.GetType().Name}]");
                    if (depth < maxDepth)
                        DumpReflect(nestedProto, depth + 1, maxDepth);
                }
                else if (value is Array arr && arr.Length > 0 && typeof(Prototype).IsAssignableFrom(arr.GetType().GetElementType()))
                {
                    Console.WriteLine($"{indent}{prop.Name}[{arr.Length}]:");
                    if (depth < maxDepth)
                    {
                        int arrIndex = 0;
                        foreach (var item in arr)
                        {
                            Console.WriteLine($"{indent}  [{arrIndex}]");
                            DumpReflect(item, depth + 1, maxDepth);
                            arrIndex++;
                        }
                    }
                }
                else if (value is Array simpleArr)
                {
                    if (simpleArr.Length == 0) continue;
                    var items = new List<string>();
                    foreach (var item in simpleArr) items.Add(item?.ToString() ?? "null");
                    Console.WriteLine($"{indent}{prop.Name}[{simpleArr.Length}]: {string.Join(", ", items)}");
                }
                else
                {
                    string str = value.ToString();
                    if (string.IsNullOrEmpty(str) || str == "0" || str == "False") continue;
                    Console.WriteLine($"{indent}{prop.Name}: {str}");
                }
            }

            if (obj is Prototype p2)
                VisitedInChain.Remove(p2);
        }

        private static void SearchLootTables(string pattern)
        {
            Console.WriteLine($"==================== Searching LootTablePrototype names for '{pattern}' ====================");

            foreach (PrototypeId protoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = SafeGetName(protoRef);
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                LootTablePrototype table = GameDatabase.GetPrototype<LootTablePrototype>(protoRef);
                if (table == null) continue;

                VisitedInChain.Clear();
                int totalDrops = CountDrops(table, 0);
                Console.WriteLine($"{name} (Ref={(ulong)protoRef}) PickMethod={table.PickMethod} NoDropPercent={table.NoDropPercent} " +
                                   $"NumChoices={table.Choices?.Length ?? 0} ApproxMaxDropsIfAllHit={totalDrops}");
            }
        }

        /// <summary>
        /// Broad inventory of every non-abstract RegionPrototype and MetaGamePrototype:
        /// what gates each one (LiveTuning eRTV_Enabled, eval/access checks) and whether
        /// anything actually leads there (waypoints, transitions, match queue).
        /// </summary>
        private static void DumpRegionInventory()
        {
            // Load LiveTuning so eRTV values reflect the live data files (Game.Current is null here,
            // so LiveTuningManager falls back to its own Instance data).
            try
            {
                LiveTuningManager.Instance.Initialize();
                Console.WriteLine("LiveTuning loaded.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"LiveTuning failed to load - eRTV values will be defaults (1). {e.Message}");
            }

            DataDirectory dataDirectory = DataDirectory.Instance;

            // ---- Connection targets: target -> region ----
            Dictionary<PrototypeId, PrototypeId> targetToRegion = new();
            HashSet<PrototypeId> regionsWithTarget = new();
            foreach (PrototypeId targetRef in dataDirectory.IteratePrototypesInHierarchy<RegionConnectionTargetPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var target = GameDatabase.GetPrototype<RegionConnectionTargetPrototype>(targetRef);
                if (target == null || target.Region == PrototypeId.Invalid) continue;
                targetToRegion[targetRef] = target.Region;
                regionsWithTarget.Add(target.Region);
            }

            // ---- Waypoint graph membership (what the travel UI can actually show) ----
            HashSet<PrototypeId> waypointsInGraphs = new();
            foreach (PrototypeId graphRef in dataDirectory.IteratePrototypesInHierarchy<WaypointGraphPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var graph = GameDatabase.GetPrototype<WaypointGraphPrototype>(graphRef);
                if (graph?.Chapters == null) continue;
                foreach (WaypointChapterPrototype chapter in graph.Chapters)
                {
                    if (chapter?.Waypoints == null) continue;
                    foreach (PrototypeId wpRef in chapter.Waypoints)
                        waypointsInGraphs.Add(wpRef);
                }
            }

            // ---- Waypoints: region -> waypoint summaries ----
            Dictionary<PrototypeId, List<string>> regionWaypoints = new();
            foreach (PrototypeId wpRef in dataDirectory.IteratePrototypesInHierarchy<WaypointPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var wp = GameDatabase.GetPrototype<WaypointPrototype>(wpRef);
                if (wp == null || wp.Destination == PrototypeId.Invalid) continue;

                if (targetToRegion.TryGetValue(wp.Destination, out PrototypeId regionRef) == false)
                {
                    // Some waypoints may point at a region directly
                    if (GameDatabase.GetPrototype<RegionPrototype>(wp.Destination) != null)
                        regionRef = wp.Destination;
                    else
                        continue;
                }

                List<string> flags = new();
                if (wp.StartLocked) flags.Add("LOCKED");
                if (waypointsInGraphs.Contains(wpRef) == false) flags.Add("NOGRAPH");
                if (wp.RequiresItem != PrototypeId.Invalid) flags.Add("NEEDITEM");
                if (wp.EvalShouldDisplay != null) flags.Add("EVALSHOW");
                if (wp.IsCheckpoint) flags.Add("CHECKPOINT");

                string summary = Path.GetFileNameWithoutExtension(SafeGetName(wpRef));
                if (flags.Count > 0) summary += $"({string.Join(",", flags)})";

                if (regionWaypoints.TryGetValue(regionRef, out List<string> list) == false)
                {
                    list = new();
                    regionWaypoints[regionRef] = list;
                }
                list.Add(summary);
            }

            // ---- Transition prototypes (in-world portals) targeting each region ----
            Dictionary<PrototypeId, int> regionTransitionCount = new();
            foreach (PrototypeId trRef in dataDirectory.IteratePrototypesInHierarchy<TransitionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var tr = GameDatabase.GetPrototype<TransitionPrototype>(trRef);
                if (tr == null || tr.DirectTarget == PrototypeId.Invalid) continue;
                if (targetToRegion.TryGetValue(tr.DirectTarget, out PrototypeId regionRef) == false) continue;
                regionTransitionCount.TryGetValue(regionRef, out int count);
                regionTransitionCount[regionRef] = count + 1;
            }

            // ---- Region pass ----
            Dictionary<PrototypeId, List<string>> metagameRegions = new();
            List<string> unreachable = new();
            List<string> lockedOnly = new();
            List<string> rtvDisabled = new();

            Console.WriteLine();
            Console.WriteLine("==================== REGION INVENTORY ====================");

            List<(string Name, RegionPrototype Proto)> regions = new();
            foreach (PrototypeId regionRef in dataDirectory.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var proto = GameDatabase.GetPrototype<RegionPrototype>(regionRef);
                if (proto == null) continue;
                regions.Add((SafeGetName(regionRef), proto));
            }
            regions.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            // Level-banded/cosmic variants listed in a base region's AltRegions[] are entered
            // through their base region, so they inherit its reachability.
            Dictionary<PrototypeId, List<string>> altToBases = new();
            foreach ((string name, RegionPrototype proto) in regions)
            {
                if (proto.AltRegions.IsNullOrEmpty()) continue;
                foreach (PrototypeId altRef in proto.AltRegions)
                {
                    if (altRef == PrototypeId.Invalid || altRef == proto.DataRef) continue;
                    if (altToBases.TryGetValue(altRef, out List<string> baseList) == false)
                    {
                        baseList = new();
                        altToBases[altRef] = baseList;
                    }
                    baseList.Add(Path.GetFileNameWithoutExtension(name));
                }
            }

            foreach ((string name, RegionPrototype proto) in regions)
            {
                float rtvEnabled = LiveTuningManager.GetLiveRegionTuningVar(proto, RegionTuningVar.eRTV_Enabled);

                // Reachability
                List<string> reach = new();
                bool hasOpenWaypoint = false;
                if (regionWaypoints.TryGetValue(proto.DataRef, out List<string> wpList))
                {
                    foreach (string wp in wpList)
                        if (wp.Contains("LOCKED") == false && wp.Contains("NOGRAPH") == false)
                            hasOpenWaypoint = true;
                    reach.Add($"WP[{string.Join("; ", wpList)}]");
                }
                if (proto.IsQueueRegion) reach.Add("QUEUE");
                if (regionTransitionCount.TryGetValue(proto.DataRef, out int trCount)) reach.Add($"TRANS({trCount})");
                if (reach.Count == 0 && altToBases.TryGetValue(proto.DataRef, out List<string> baseList)) reach.Add($"ALT-of[{string.Join(",", baseList.Distinct())}]");
                if (reach.Count == 0 && regionsWithTarget.Contains(proto.DataRef)) reach.Add("TARGETONLY");
                string reachStr = reach.Count > 0 ? string.Join(" ", reach) : "NONE";

                // Gates
                List<string> gates = new();
                if (rtvEnabled != 1f) gates.Add($"eRTV_Enabled={rtvEnabled}");
                if (proto.EvalAccessRestriction != null) gates.Add("EvalRestrict");
                if (proto.AccessChecks.HasValue())
                {
                    foreach (RegionAccessCheckPrototype check in proto.AccessChecks)
                    {
                        if (check is LevelAccessCheckPrototype levelCheck)
                            gates.Add($"Level({levelCheck.LevelMin}-{levelCheck.LevelMax})");
                        else if (check != null)
                            gates.Add(check.GetType().Name);
                    }
                }
                if (proto.AccessDifficulties.HasValue()) gates.Add($"Diff({proto.AccessDifficulties.Length})");
                if (proto.RestrictedRoster.HasValue()) gates.Add($"Roster({proto.RestrictedRoster.Length})");
                string gateStr = gates.Count > 0 ? string.Join(" ", gates) : "-";

                string generator = proto.RegionGenerator != null ? proto.RegionGenerator.GetType().Name.Replace("RegionGeneratorPrototype", "") : "NULL";

                // MetaGames
                string metagames = "-";
                if (proto.MetaGames.HasValue())
                {
                    List<string> mgNames = new();
                    foreach (PrototypeId mgRef in proto.MetaGames)
                    {
                        mgNames.Add(Path.GetFileNameWithoutExtension(SafeGetName(mgRef)));
                        if (metagameRegions.TryGetValue(mgRef, out List<string> mgRegionList) == false)
                        {
                            mgRegionList = new();
                            metagameRegions[mgRef] = mgRegionList;
                        }
                        mgRegionList.Add(Path.GetFileNameWithoutExtension(name));
                    }
                    metagames = string.Join(",", mgNames);
                }

                Console.WriteLine($"[Region] {name} | Behavior={proto.Behavior} Level={proto.Level} PlayerLimit={proto.PlayerLimit} Gen={generator} | Reach: {reachStr} | Gates: {gateStr} | MetaGames: {metagames}");

                // Buckets for the summary
                if (rtvEnabled == 0f) rtvDisabled.Add(name);
                if (reachStr == "NONE" || reachStr == "TARGETONLY") unreachable.Add(name);
                else if (hasOpenWaypoint == false && proto.IsQueueRegion == false && regionTransitionCount.ContainsKey(proto.DataRef) == false
                         && altToBases.ContainsKey(proto.DataRef) == false) lockedOnly.Add(name);
            }

            // ---- MetaGame pass ----
            Console.WriteLine();
            Console.WriteLine("==================== METAGAME INVENTORY ====================");
            foreach (PrototypeId mgRef in dataDirectory.IteratePrototypesInHierarchy<MetaGamePrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var mg = GameDatabase.GetPrototype<MetaGamePrototype>(mgRef);
                if (mg == null) continue;

                // Aggregate DesignState across the MetaStates its modes apply -
                // Dev/NotInGame states are refused at runtime by MetaStatePrototype.CanApplyState()
                int statesLive = 0, statesDev = 0, statesNotInGame = 0, statesNone = 0;
                int modeCount = 0;
                if (mg.GameModes.HasValue())
                {
                    foreach (PrototypeId modeRef in mg.GameModes)
                    {
                        var mode = GameDatabase.GetPrototype<MetaGameModePrototype>(modeRef);
                        if (mode == null) continue;
                        modeCount++;
                        if (mode.ApplyStates.IsNullOrEmpty()) continue;
                        foreach (PrototypeId stateRef in mode.ApplyStates)
                        {
                            var state = GameDatabase.GetPrototype<MetaStatePrototype>(stateRef);
                            if (state == null) continue;
                            switch (state.DesignState)
                            {
                                case DesignWorkflowState.Live: statesLive++; break;
                                case DesignWorkflowState.DevelopmentOnly: statesDev++; break;
                                case DesignWorkflowState.NotInGame: statesNotInGame++; break;
                                default: statesNone++; break;
                            }
                        }
                    }
                }

                string regionsUsing = metagameRegions.TryGetValue(mgRef, out List<string> usingList)
                    ? string.Join(",", usingList.Distinct())
                    : "NO-REGION";

                if (mg is MatchMetaGamePrototype matchMg && matchMg.StartRegion != PrototypeId.Invalid)
                    regionsUsing += $" StartRegion={Path.GetFileNameWithoutExtension(SafeGetName(matchMg.StartRegion))}";

                Console.WriteLine($"[MetaGame] {SafeGetName(mgRef)} | Type={mg.GetType().Name.Replace("Prototype", "")} Modes={modeCount} | States: Live={statesLive} Dev={statesDev} NotInGame={statesNotInGame} None={statesNone} | Regions: {regionsUsing}");
            }

            // ---- Public events (Civil War-style server-driven events) ----
            Console.WriteLine();
            Console.WriteLine("==================== PUBLIC EVENT INVENTORY ====================");
            foreach (PrototypeId peRef in dataDirectory.IteratePrototypesInHierarchy<PublicEventPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var pe = GameDatabase.GetPrototype<PublicEventPrototype>(peRef);
                if (pe == null) continue;
                Console.WriteLine($"[PublicEvent] {SafeGetName(peRef)} | DefaultEnabled={pe.DefaultEnabled} Teams={(pe.Teams != null ? pe.Teams.Length : 0)}");
            }

            // ---- Summary ----
            Console.WriteLine();
            Console.WriteLine("==================== SUMMARY ====================");
            Console.WriteLine($"Total regions: {regions.Count}");
            Console.WriteLine();
            Console.WriteLine($"-- Regions with NO reachability (no waypoint, no transition, no queue, no connection target): {unreachable.Count} --");
            foreach (string name in unreachable) Console.WriteLine($"  {name}");
            Console.WriteLine();
            Console.WriteLine($"-- Regions reachable only via locked/hidden waypoints: {lockedOnly.Count} --");
            foreach (string name in lockedOnly) Console.WriteLine($"  {name}");
            Console.WriteLine();
            Console.WriteLine($"-- Regions disabled via LiveTuning eRTV_Enabled=0: {rtvDisabled.Count} --");
            foreach (string name in rtvDisabled) Console.WriteLine($"  {name}");
        }

        /// <summary>Rough upper-bound count of individual item/agent drops reachable under a table if every gate passed.</summary>
        private static int CountDrops(Prototype proto, int depth)
        {
            if (depth > 20) return 0;

            if (proto is LootTablePrototype table)
            {
                if (VisitedInChain.Contains(table)) return 0;
                VisitedInChain.Add(table);

                int sum = 0;
                if (table.Choices != null)
                {
                    if (table.PickMethod == PickMethod.PickWeight)
                    {
                        // Only one choice is actually picked - use the largest branch as the estimate.
                        int max = 0;
                        foreach (LootNodePrototype choice in table.Choices)
                            max = Math.Max(max, CountDrops(choice, depth + 1));
                        sum = max;
                    }
                    else
                    {
                        foreach (LootNodePrototype choice in table.Choices)
                            sum += CountDrops(choice, depth + 1);
                    }
                }

                VisitedInChain.Remove(table);
                return sum;
            }

            if (proto is LootDropItemPrototype dropItem)
                return Math.Max(1, (int)dropItem.NumMax);

            if (proto is LootDropAgentPrototype || proto is LootDropCreditsPrototype)
                return 1;

            return 0;
        }

        private static void PrintNode(Prototype proto, int depth)
        {
            string indent = new string(' ', depth * 2);

            if (depth > 20)
            {
                Console.WriteLine($"{indent}[max depth reached, stopping]");
                return;
            }

            if (proto is LootTablePrototype table)
            {
                string name = SafeGetName(table.DataRef);
                bool alreadyShown = VisitedInChain.Contains(table);

                Console.WriteLine($"{indent}[LootTable] {name} (Ref={(ulong)table.DataRef}) ParentDataRef={(ulong)table.ParentDataRef} PickMethod={table.PickMethod} NoDropPercent={table.NoDropPercent} Weight={table.Weight} NumChoices={table.Choices?.Length ?? 0}{(alreadyShown ? "  <-- CYCLE, SAME OBJECT IS ITS OWN ANCESTOR" : "")}");
                PrintModifiers(table, indent);

                if (alreadyShown)
                    return;

                VisitedInChain.Add(table);

                if (table.Choices != null)
                {
                    foreach (LootNodePrototype choice in table.Choices)
                        PrintNode(choice, depth + 1);
                }

                VisitedInChain.Remove(table);
            }
            else if (proto is LootDropItemPrototype dropItem)
            {
                Console.WriteLine($"{indent}[LootDropItemPrototype] Item={SafeGetName(dropItem.Item)} (Ref={(ulong)dropItem.Item}) ParentDataRef={(ulong)dropItem.ParentDataRef} NumMin={dropItem.NumMin} NumMax={dropItem.NumMax} Weight={dropItem.Weight}");
                PrintModifiers(dropItem, indent);
            }
            else if (proto is LootDropItemFilterPrototype itemFilter)
            {
                Console.WriteLine($"{indent}[LootDropItemFilterPrototype] ParentDataRef={(ulong)itemFilter.ParentDataRef} NumMin={itemFilter.NumMin} NumMax={itemFilter.NumMax} ItemRank={itemFilter.ItemRank} UISlot={itemFilter.UISlot} Weight={itemFilter.Weight}");
                PrintModifiers(itemFilter, indent);
            }
            else if (proto is LootDropAgentPrototype agentDrop)
            {
                Console.WriteLine($"{indent}[LootDropAgentPrototype] Agent={SafeGetName(agentDrop.Agent)} (Ref={(ulong)agentDrop.Agent}) ParentDataRef={(ulong)agentDrop.ParentDataRef} NumMin={agentDrop.NumMin} NumMax={agentDrop.NumMax} Weight={agentDrop.Weight}");
                PrintModifiers(agentDrop, indent);
            }
            else if (proto is LootDropCharacterTokenPrototype charToken)
            {
                Console.WriteLine($"{indent}[LootDropCharacterTokenPrototype] ParentDataRef={(ulong)charToken.ParentDataRef} AllowedTokenType={charToken.AllowedTokenType} FilterType={charToken.FilterType} Weight={charToken.Weight} OnTokenUnavailable={(charToken.OnTokenUnavailable == null ? "null" : charToken.OnTokenUnavailable.GetType().Name)}");
                PrintModifiers(charToken, indent);
                if (charToken.OnTokenUnavailable != null)
                {
                    Console.WriteLine($"{indent}  OnTokenUnavailable ->");
                    PrintNode(charToken.OnTokenUnavailable, depth + 2);
                }
            }
            else if (proto is LootNodePrototype node)
            {
                string name = node.DataRef != PrototypeId.Invalid ? SafeGetName(node.DataRef) : "(anonymous)";
                Console.WriteLine($"{indent}[{node.GetType().Name}] {name} ParentDataRef={(ulong)node.ParentDataRef} Weight={node.Weight}");
                PrintModifiers(node, indent);
            }
            else
            {
                Console.WriteLine($"{indent}[{proto.GetType().Name}] {SafeGetName(proto.DataRef)} (Ref={(ulong)proto.DataRef}) ParentDataRef={(ulong)proto.ParentDataRef}");
            }
        }

        private static void PrintModifiers(LootNodePrototype node, string indent)
        {
            if (node.Modifiers.IsNullOrEmpty())
                return;

            foreach (LootRollModifierPrototype modifier in node.Modifiers)
            {
                string detail = modifier switch
                {
                    LootRollRequireDifficultyTierPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollRequireConditionKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollForbidConditionKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollRequireRegionKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollForbidRegionKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollRequireDropperKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollForbidDropperKeywordPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    LootRollSetUsablePrototype t => $"Usable={t.Usable}",
                    LootRollSetRarityPrototype t => $"Choices=[{string.Join(", ", (t.Choices ?? Array.Empty<PrototypeId>()).Select(SafeGetName))}]",
                    _ => ""
                };

                Console.WriteLine($"{indent}  (Modifier) {modifier.GetType().Name} ParentDataRef={(ulong)modifier.ParentDataRef} {detail}");
            }
        }

        private static string SafeGetName(PrototypeId prototypeId)
        {
            try
            {
                string name = GameDatabase.GetPrototypeName(prototypeId);
                return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            }
            catch
            {
                return "(unnamed)";
            }
        }
    }
}
