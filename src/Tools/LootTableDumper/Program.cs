using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.PatchManager;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Prototypes.Markers;
using MHServerEmu.Games.Locales;
using MHServerEmu.Games.Properties;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LootTableDumper
{
    internal class Program
    {
        private static readonly HashSet<object> VisitedInChain = new(ReferenceEqualityComparer.Instance);

        private static LogCollectorTarget _bootCollector;

        static void Main(string[] args)
        {
            // Attached before GameDatabase is ever touched below, so --tracepatch can see
            // PrototypePatchManager activity from the eager LoadAllPrototypes pass too, not just
            // whatever happens to run after this Main() body starts dispatching on args.
            _bootCollector = new LogCollectorTarget();
            LogManager.Enabled = true;
            LogManager.AttachTarget(_bootCollector);

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

            if (args.Length > 0 && args[0] == "--patchstatus")
            {
                PatchStatus();
                return;
            }

            if (args.Length > 0 && args[0] == "--validatepatches")
            {
                ValidatePatches();
                return;
            }

            if (args.Length > 1 && args[0] == "--tracepatch")
            {
                TracePatchTrace(args[1]);
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

            if (args.Length > 0 && args[0] == "--checkenumcollisions")
            {
                CheckLootTableEnumCollisions();
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

            if (args.Length > 0 && args[0] == "--findprotoref")
            {
                if (args.Length > 1 && ulong.TryParse(args[1], out ulong targetRefVal))
                {
                    int maxDepth = args.Length > 2 && int.TryParse(args[2], out int fd) ? fd : 6;
                    FindPrototypeRef((PrototypeId)targetRefVal, maxDepth);
                }
                return;
            }

            if (args.Length > 0 && args[0] == "--regionaudit")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                RegionAssetAudit(pattern);
                return;
            }

            if (args.Length > 0 && args[0] == "--findavatarswapoff")
            {
                FindRegionsWithAvatarSwapDisabled();
                return;
            }

            if (args.Length > 0 && args[0] == "--findunusednpcs")
            {
                string pathPrefix = args.Length > 1 ? args[1] : "Entity/Characters/NPCs/";
                FindUnusedNpcs(pathPrefix);
                return;
            }

            if (args.Length > 0 && args[0] == "--findunusedconsumables")
            {
                string pathPrefix = args.Length > 1 ? args[1] : "Entity/Items/Consumables/";
                int maxDepth = args.Length > 2 && int.TryParse(args[2], out int ucd) ? ucd : 10;
                FindUnusedConsumables(pathPrefix, maxDepth);
                return;
            }

            if (args.Length > 1 && args[0] == "--findbyicon")
            {
                string[] iconNames = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string pathPrefix = args.Length > 2 ? args[2] : "Entity/Items/";
                FindByIcon(iconNames, pathPrefix);
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
        /// Re-parses every PatchData*.json file under Data/Game/Patches (same file discovery as
        /// PrototypePatchManager.LoadPatchDataFromDisk) and, for every Enabled entry, independently
        /// resolves the "Prototype" name and force-loads that prototype to exercise the real patch
        /// application pipeline. PrototypePatchManager silently skips entries whose Prototype name
        /// fails to resolve (no log line at all) - this catches that case directly instead of relying
        /// on log-scraping. Application failures (missing field, type mismatch) still surface as Warn/
        /// WarnException through a collector log target, since those DO log but easy to miss by eye
        /// across hundreds of entries.
        /// </summary>
        private static void ValidatePatches()
        {
            var collector = new LogCollectorTarget();
            LogManager.Enabled = true;
            LogManager.AttachTarget(collector);

            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            int totalEnabled = 0;
            int propertiesEntries = 0;
            var unresolved = new List<(string File, string Prototype, string Path)>();

            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, "PatchData", "json"))
            {
                string fileName = Path.GetFileName(filePath);
                PrototypePatchEntry[] entries = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                if (entries == null)
                {
                    Console.WriteLine($"[PARSE FAILED] {fileName}");
                    continue;
                }

                foreach (PrototypePatchEntry entry in entries)
                {
                    if (entry.Enabled == false) continue;
                    totalEnabled++;

                    if (entry.Value.ValueType == MHServerEmu.Games.GameData.PatchManager.ValueType.Properties)
                    {
                        // Applied via a separate mechanism (CheckProperties), not the CheckAndUpdate
                        // pipeline below - just confirm the prototype name still resolves.
                        propertiesEntries++;
                    }

                    PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(entry.Prototype);
                    if (protoRef == PrototypeId.Invalid)
                    {
                        unresolved.Add((fileName, entry.Prototype, entry.Path));
                        continue;
                    }

                    // Touching the prototype triggers PrototypeClassManager's PreCheck/PostOverride,
                    // which runs every pending patch entry queued against this PrototypeId.
                    GameDatabase.GetPrototype<Prototype>(protoRef);
                }
            }

            Console.WriteLine($"==================== Patch validation: {totalEnabled} enabled entries checked ({propertiesEntries} Properties-type, applied via a separate path) ====================");

            if (unresolved.Count > 0)
            {
                Console.WriteLine($"\n[UNRESOLVED PROTOTYPE NAME] {unresolved.Count} entries - PrototypePatchManager silently skips these, no log line ever printed:");
                foreach (var (file, proto, path) in unresolved)
                    Console.WriteLine($"  {file}: '{proto}' (Path={path})");
            }
            else
            {
                Console.WriteLine("\nAll entries resolved to a valid prototype name.");
            }

            var failures = collector.Messages.Where(m => m.Level >= LoggingLevel.Warn).ToList();
            if (failures.Count > 0)
            {
                Console.WriteLine($"\n[APPLY FAILURES] {failures.Count} Warn/Error messages from PrototypePatchManager while force-loading patched prototypes:");
                foreach (var msg in failures)
                    Console.WriteLine($"  {msg}");
            }
            else
            {
                Console.WriteLine("No Warn/Error output from PrototypePatchManager while applying patches.");
            }
        }

        /// <summary>
        /// Force-loads every enabled patch entry's target prototype (same as ValidatePatches), then
        /// reports how many of the entries PrototypePatchManager actually registered ended up with
        /// Patched == true vs still false after the full pass - i.e. entries that resolved a valid
        /// prototype name but never found a matching path/field to apply to. This is a DIFFERENT
        /// failure mode than ValidatePatches' "unresolved prototype name" check: those never even
        /// make it into _patchDict, while these are registered but silently never matched.
        /// </summary>
        private static void PatchStatus()
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, "PatchData", "json"))
            {
                PrototypePatchEntry[] entries = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                if (entries == null) continue;

                foreach (PrototypePatchEntry entry in entries)
                {
                    if (entry.Enabled == false) continue;
                    PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(entry.Prototype);
                    if (protoRef == PrototypeId.Invalid) continue;
                    GameDatabase.GetPrototype<Prototype>(protoRef);
                }
            }

            var allEntries = PrototypePatchManager.Instance.EnumerateAllEntries().ToList();
            int patchedTrue = allEntries.Count(e => e.Entry.Patched);
            int patchedFalse = allEntries.Count - patchedTrue;

            Console.WriteLine($"==================== Patch status: {allEntries.Count} entries registered in PrototypePatchManager ====================");
            Console.WriteLine($"Patched=true:  {patchedTrue}");
            Console.WriteLine($"Patched=false: {patchedFalse}");

            if (patchedFalse > 0)
            {
                Console.WriteLine("\n[NEVER MATCHED] registered but never found a matching path/field:");
                foreach (var (protoRef, entry) in allEntries.Where(e => e.Entry.Patched == false))
                    Console.WriteLine($"  {entry.Prototype} (Path={entry.Path})");
            }
        }

        private static void TracePatchTrace(string searchTerm)
        {
            // Also force-touch the prototype in case it wasn't part of the eager LoadAllPrototypes
            // pass (harmless no-op if it's already loaded and cached).
            PrototypeId protoRef = GameDatabase.GetPrototypeRefByName(searchTerm);
            if (protoRef != PrototypeId.Invalid)
                GameDatabase.GetPrototype<Prototype>(protoRef);

            Console.WriteLine($"==================== All PrototypePatchManager log messages captured since process start (filtered by '{searchTerm}') ====================");
            Console.WriteLine($"Total captured messages: {_bootCollector.Messages.Count}");
            foreach (var msg in _bootCollector.Messages)
            {
                string text = msg.ToString();
                if (text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"[{msg.Level}] {text}");
            }
        }

        private sealed class LogCollectorTarget : LogTarget
        {
            public List<LogMessage> Messages { get; } = new();

            public LogCollectorTarget() : base(new LogTargetSettings
            {
                IncludeTimestamps = false,
                MinimumLevel = LoggingLevel.Trace,
                MaximumLevel = LoggingLevel.Fatal,
                Channels = LogChannels.All
            })
            { }

            public override void ProcessLogMessage(in LogMessage message)
            {
                if (message.Logger == nameof(PrototypePatchManager) || message.Logger == nameof(JsonPrototype))
                    Messages.Add(message);
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

        /// <summary>
        /// Reverse-reference search: which top-level prototypes contain a PrototypeId field
        /// (anywhere in their nested object graph - Population/Cluster/Selector chains included)
        /// matching the given ref. Used to answer "is this boss/loot table entity shared with
        /// other content" before boosting it via LiveTuning, since eWETV_/eLTTV_ settings apply
        /// globally to every place a prototype is used, not just the context you're tuning for.
        /// </summary>
        private static void FindPrototypeRef(PrototypeId targetRef, int maxDepth)
        {
            Console.WriteLine($"==================== Searching all prototypes for PrototypeId reference {(ulong)targetRef} ({SafeGetName(targetRef)}) ====================");

            int count = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                if (protoRef == targetRef) continue; // skip the target referencing itself trivially

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                VisitedInChain.Clear();
                List<string> paths = new();
                FindProtoRefInGraph(proto, targetRef, "", 0, maxDepth, paths);

                if (paths.Count > 0)
                {
                    count++;
                    Console.WriteLine($"  {SafeGetName(protoRef)} [{proto.GetType().Name}] (Ref={(ulong)protoRef})");
                    foreach (string p in paths)
                        Console.WriteLine($"      .{p}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"-- {count} top-level prototypes reference it --");
        }

        private static void FindProtoRefInGraph(object obj, PrototypeId targetRef, string path, int depth, int maxDepth, List<string> hits)
        {
            if (obj == null || depth > maxDepth) return;

            Type type = obj.GetType();
            if (type.IsValueType == false)
            {
                if (VisitedInChain.Contains(obj)) return;
                VisitedInChain.Add(obj);
            }

            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                object value;
                try { value = prop.GetValue(obj); }
                catch { continue; }
                if (value == null) continue;

                string childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";

                if (value is PrototypeId pid)
                {
                    if (pid == targetRef) hits.Add(childPath);
                }
                else if (value is PrototypeId[] pidArr)
                {
                    for (int i = 0; i < pidArr.Length; i++)
                        if (pidArr[i] == targetRef) hits.Add($"{childPath}[{i}]");
                }
                else if (value is Prototype nestedProto)
                {
                    FindProtoRefInGraph(nestedProto, targetRef, childPath, depth + 1, maxDepth, hits);
                }
                else if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    int i = 0;
                    foreach (var item in enumerable)
                    {
                        if (item is Prototype itemProto)
                            FindProtoRefInGraph(itemProto, targetRef, $"{childPath}[{i}]", depth + 1, maxDepth, hits);
                        i++;
                    }
                }
            }
        }

        /// <summary>
        /// Finds concrete item prototypes under a given path prefix (default: Entity/Items/Consumables/)
        /// that are never referenced anywhere inside any loot table's data graph - candidates for reuse,
        /// same "confirmed unused" check used for NPCs/portals but scoped to loot table references only
        /// (vendor stock, crafting recipes, etc. are NOT loot tables and are not checked here).
        /// </summary>
        private static void FindUnusedConsumables(string pathPrefix, int maxDepth)
        {
            Console.WriteLine($"==================== Finding item prototypes under '{pathPrefix}' NOT referenced in any loot table ====================");

            HashSet<PrototypeId> referencedRefs = new();
            int lootTableCount = 0;
            foreach (PrototypeId lootTableRef in DataDirectory.Instance.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(lootTableRef);
                if (proto == null) continue;

                lootTableCount++;
                VisitedInChain.Clear();
                CollectPrototypeIdRefsInGraph(proto, 0, maxDepth, referencedRefs);
            }

            Console.WriteLine($"Scanned {lootTableCount} loot tables, collected {referencedRefs.Count} distinct PrototypeId references (depth<={maxDepth}).");
            Console.WriteLine();

            int checkedCount = 0;
            List<(string Name, PrototypeId Ref)> unused = new();

            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (name == "(unnamed)" || name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) == false) continue;
                if (name.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) == false) continue;

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto is not ItemPrototype) continue;

                checkedCount++;
                if (referencedRefs.Contains(protoRef) == false)
                    unused.Add((name, protoRef));
            }

            foreach (var (name, protoRef) in unused.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {name} (Ref={(ulong)protoRef}) [{GameDatabase.GetPrototype<Prototype>(protoRef).GetType().Name}]");

            Console.WriteLine();
            Console.WriteLine($"-- {checkedCount} item prototype(s) checked under '{pathPrefix}', {unused.Count} NOT referenced in any loot table --");
        }

        /// <summary>Finds every prototype under pathPrefix that has an AssetId-typed field (IconPath,
        /// IconPathHiRes, StoreIconPath, UnrealClass, etc.) resolving to one of the given icon base names
        /// (matched case-insensitively against the asset's own name, ignoring any "Type." prefix).</summary>
        private static void FindByIcon(string[] iconNames, string pathPrefix)
        {
            Console.WriteLine($"==================== Searching prototypes under '{pathPrefix}' for icon/asset matches ====================");

            HashSet<string> targets = new(iconNames.Select(StripExtensionAndPrefix), StringComparer.OrdinalIgnoreCase);
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string target in targets)
                results[target] = new List<string>();

            int checkedCount = 0;

            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (name == "(unnamed)" || name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) == false) continue;
                if (name.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) == false) continue;

                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto == null) continue;

                checkedCount++;

                foreach (System.Reflection.PropertyInfo prop in proto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.PropertyType != typeof(AssetId)) continue;

                    AssetId assetId;
                    try { assetId = (AssetId)prop.GetValue(proto); }
                    catch { continue; }

                    if (assetId == AssetId.Invalid) continue;

                    string assetName = StripExtensionAndPrefix(GameDatabase.GetAssetName(assetId));
                    if (results.TryGetValue(assetName, out var list))
                        list.Add($"{name} [{prop.Name}] (Ref={(ulong)protoRef})");
                }
            }

            foreach (string target in targets.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                var matches = results[target];
                Console.WriteLine($"  {target}: {matches.Count} match(es)");
                foreach (string match in matches.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"    {match}");
            }

            Console.WriteLine();
            Console.WriteLine($"-- Checked {checkedCount} prototype(s) under '{pathPrefix}' against {targets.Count} icon name(s) --");
        }

        /// <summary>Strips a file extension (e.g. ".png") and any "Type."-style dotted prefix, leaving
        /// just the bare asset name for case-insensitive comparison.</summary>
        private static string StripExtensionAndPrefix(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            int dotExt = value.LastIndexOf('.');
            if (dotExt != -1 && dotExt > value.LastIndexOf('/') && value.Length - dotExt <= 5)
                value = value[..dotExt];

            int dotPrefix = value.LastIndexOf('.');
            if (dotPrefix != -1)
                value = value[(dotPrefix + 1)..];

            return value;
        }

        private static void CollectPrototypeIdRefsInGraph(object obj, int depth, int maxDepth, HashSet<PrototypeId> refs)
        {
            if (obj == null || depth > maxDepth) return;

            Type type = obj.GetType();
            if (type.IsValueType == false)
            {
                if (VisitedInChain.Contains(obj)) return;
                VisitedInChain.Add(obj);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                object value;
                try { value = prop.GetValue(obj); }
                catch { continue; }
                if (value == null) continue;

                if (value is PrototypeId pid)
                {
                    if (pid != PrototypeId.Invalid) refs.Add(pid);
                }
                else if (value is PrototypeId[] pidArr)
                {
                    foreach (PrototypeId p in pidArr)
                        if (p != PrototypeId.Invalid) refs.Add(p);
                }
                else if (value is Prototype nestedProto)
                {
                    CollectPrototypeIdRefsInGraph(nestedProto, depth + 1, maxDepth, refs);
                }
                else if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is Prototype itemProto)
                            CollectPrototypeIdRefsInGraph(itemProto, depth + 1, maxDepth, refs);
                    }
                }
            }
        }

        private static void FindRegionsWithAvatarSwapDisabled()
        {
            Console.WriteLine("==================== Regions with EnableAvatarSwap=False ====================");

            int total = 0;
            int disabled = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                RegionPrototype proto = GameDatabase.GetPrototype<Prototype>(protoRef) as RegionPrototype;
                if (proto == null) continue;

                total++;
                if (proto.EnableAvatarSwap == false)
                {
                    disabled++;
                    Console.WriteLine($"  {SafeGetName(protoRef)} (Behavior={proto.Behavior})");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"-- {disabled} of {total} region prototypes have EnableAvatarSwap=False --");
        }

        /// <summary>
        /// Finds every AgentPrototype under pathPrefix (default Entity/Characters/NPCs/) that has zero
        /// existing cell-marker placements anywhere in the game - candidates safe to repurpose as a
        /// standing dialog NPC (same reuse pattern as Misty Knight/Cloak/Doctor Strange/Domino). Does NOT
        /// check client UPK model availability - that can only be confirmed by looking at the client
        /// assets directly, not from server-side data alone (confirmed via Manifold/Songbird, whose
        /// server data looked identical to working candidates but had no cooked model client-side).
        /// </summary>
        private static void FindUnusedNpcs(string pathPrefix)
        {
            Console.WriteLine($"==================== Unused NPC candidates under '{pathPrefix}' (zero cell-marker placements) ====================");

            var placedRefs = new HashSet<PrototypeId>();
            foreach (PrototypeId cellRef in DataDirectory.Instance.IteratePrototypesInHierarchy<CellPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                CellPrototype cell = GameDatabase.GetPrototype<CellPrototype>(cellRef);
                if (cell == null) continue;

                CollectPlacedEntityRefs(cell.MarkerSet, placedRefs);
                CollectPlacedEntityRefs(cell.InitializeSet, placedRefs);
            }

            int checkedCount = 0;
            int unusedCount = 0;
            foreach (PrototypeId protoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                string name = SafeGetName(protoRef);
                if (name.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) == false) continue;

                AgentPrototype proto = GameDatabase.GetPrototype<Prototype>(protoRef) as AgentPrototype;
                if (proto == null) continue;

                checkedCount++;
                if (placedRefs.Contains(protoRef)) continue;

                unusedCount++;
                AssetId unrealClass = proto.UnrealClass;
                string unrealClassName = unrealClass != AssetId.Invalid ? GameDatabase.GetAssetName(unrealClass) : "none";
                Console.WriteLine($"  {name} (Ref={(ulong)protoRef}) Allegiance={proto.Allegiance} DesignState={proto.DesignState} UnrealClass={unrealClassName}");
            }

            Console.WriteLine();
            Console.WriteLine($"-- {unusedCount} of {checkedCount} checked NPC prototype(s) have zero cell-marker placements --");
            Console.WriteLine("-- Model presence in the client UPKs is NOT verified here - check each candidate against the client assets before using it --");
        }

        private static void CollectPlacedEntityRefs(MarkerSetPrototype markerSet, HashSet<PrototypeId> placedRefs)
        {
            if (markerSet?.Markers == null) return;

            foreach (var marker in markerSet.Markers)
            {
                if (marker is not EntityMarkerPrototype entityMarker) continue;
                if (entityMarker.EntityGuid == PrototypeGuid.Invalid) continue;

                PrototypeId entityRef = GameDatabase.GetDataRefByPrototypeGuid(entityMarker.EntityGuid);
                if (entityRef != PrototypeId.Invalid)
                    placedRefs.Add(entityRef);
            }
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
        /// Iterates every LootTablePrototype in the game, groups them by their resolved
        /// LootTablePrototypeEnumValue, and reports any enum value shared by more than one
        /// distinct PrototypeId - direct proof (or disproof) of a LiveTuning array-index collision
        /// between unrelated loot tables.
        /// </summary>
        private static void CheckLootTableEnumCollisions()
        {
            Console.WriteLine("==================== Checking LootTablePrototypeEnumValue collisions ====================");

            Dictionary<int, List<PrototypeId>> enumToProtoRefs = new();
            int total = 0;

            foreach (PrototypeId protoRef in DataDirectory.Instance.IterateAllPrototypes(PrototypeIterateFlags.NoAbstract))
            {
                Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef);
                if (proto is not LootTablePrototype lootTableProto) continue;

                total++;
                int enumVal = lootTableProto.LootTablePrototypeEnumValue;

                if (enumToProtoRefs.TryGetValue(enumVal, out List<PrototypeId> list) == false)
                {
                    list = new();
                    enumToProtoRefs[enumVal] = list;
                }
                list.Add(protoRef);
            }

            Console.WriteLine($"Total LootTablePrototype instances checked: {total}");

            int collisionGroups = 0;
            foreach (var kvp in enumToProtoRefs)
            {
                if (kvp.Value.Count <= 1) continue;

                // enum value 0 is the well-known "invalid/unset" bucket - every table that never
                // got a real enum value assigned lands here together, which is not a real collision.
                if (kvp.Key == 0) continue;

                collisionGroups++;
                Console.WriteLine($"  COLLISION at enum value {kvp.Key}:");
                foreach (PrototypeId protoRef in kvp.Value)
                    Console.WriteLine($"    {SafeGetName(protoRef)} (Ref={(ulong)protoRef})");
            }

            Console.WriteLine($"  -- {collisionGroups} colliding enum value(s) found (excluding enum 0) --");
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
            PrototypeId protoRef = ulong.TryParse(path, out ulong rawRef)
                ? (PrototypeId)rawRef
                : GameDatabase.GetPrototypeRefByName(path);

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
                    if (string.IsNullOrEmpty(str)) continue;
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

        /// <summary>
        /// Screens unreachable/target-only regions (repurposing candidates from --regions) for the
        /// specific client-asset failure mode confirmed 3-for-3 in prior investigation (Civil War
        /// Bazaar/Airport, Brooklyn Winter, Midtown Xmas): the region's Area(s) point at a District/
        /// CellSet/ClientMap resource that is either an orphaned duplicate nobody else uses, or a
        /// stale reference into a completely unrelated zone family. Both patterns produced a clean,
        /// error-free server-side region generation followed by an indefinite client-side hang -
        /// nothing in the server log flags it, so this has to be caught by data comparison instead.
        /// Not a guarantee (can't see the client's actual asset bundles), just a pre-screen so
        /// candidates don't have to be tested in-game one at a time.
        /// </summary>
        private static void RegionAssetAudit(string pattern)
        {
            try
            {
                LiveTuningManager.Instance.Initialize();
            }
            catch (Exception e)
            {
                Console.WriteLine($"LiveTuning failed to load - eRTV values will be defaults (1). {e.Message}");
            }

            DataDirectory dataDirectory = DataDirectory.Instance;

            // ---- Reachability plumbing (same approach as --regions) ----
            Dictionary<PrototypeId, PrototypeId> targetToRegion = new();
            HashSet<PrototypeId> regionsWithTarget = new();
            foreach (PrototypeId targetRef in dataDirectory.IteratePrototypesInHierarchy<RegionConnectionTargetPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var target = GameDatabase.GetPrototype<RegionConnectionTargetPrototype>(targetRef);
                if (target == null || target.Region == PrototypeId.Invalid) continue;
                targetToRegion[targetRef] = target.Region;
                regionsWithTarget.Add(target.Region);
            }

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

            HashSet<PrototypeId> regionsWithOpenWaypoint = new();
            foreach (PrototypeId wpRef in dataDirectory.IteratePrototypesInHierarchy<WaypointPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var wp = GameDatabase.GetPrototype<WaypointPrototype>(wpRef);
                if (wp == null || wp.Destination == PrototypeId.Invalid) continue;
                if (wp.StartLocked || waypointsInGraphs.Contains(wpRef) == false) continue;

                if (targetToRegion.TryGetValue(wp.Destination, out PrototypeId regionRef) == false)
                {
                    if (GameDatabase.GetPrototype<RegionPrototype>(wp.Destination) != null)
                        regionRef = wp.Destination;
                    else
                        continue;
                }
                regionsWithOpenWaypoint.Add(regionRef);
            }

            HashSet<PrototypeId> regionsWithTransition = new();
            foreach (PrototypeId trRef in dataDirectory.IteratePrototypesInHierarchy<TransitionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var tr = GameDatabase.GetPrototype<TransitionPrototype>(trRef);
                if (tr == null || tr.DirectTarget == PrototypeId.Invalid) continue;
                if (targetToRegion.TryGetValue(tr.DirectTarget, out PrototypeId regionRef) == false) continue;
                regionsWithTransition.Add(regionRef);
            }

            List<(string Name, RegionPrototype Proto)> regions = new();
            foreach (PrototypeId regionRef in dataDirectory.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var proto = GameDatabase.GetPrototype<RegionPrototype>(regionRef);
                if (proto == null) continue;
                regions.Add((SafeGetName(regionRef), proto));
            }

            // A region only counts as reachable through a genuine entry channel: an open waypoint,
            // matchmaking queue, or an in-world transition pointing at it. Being listed in another
            // region's AltRegions[] is NOT by itself proof of reachability - that other region might
            // itself be nothing but a dead RegionConnectionTarget stub (confirmed real case: Civil War's
            // Bazaar/Airport "Band" regions are TARGETONLY with zero real entry point, yet their own
            // Region10/25/50 difficulty-tier AltRegions siblings would otherwise "vouch" for each other
            // in a closed loop and get miscounted as live). AltRegions membership only propagates
            // reachability from a base that is ITSELF genuinely reachable through one of those channels.
            bool HasGenuineEntryChannel(RegionPrototype proto) =>
                regionsWithOpenWaypoint.Contains(proto.DataRef) ||
                proto.IsQueueRegion ||
                regionsWithTransition.Contains(proto.DataRef);

            HashSet<PrototypeId> reachableViaLiveBase = new();
            foreach ((string _, RegionPrototype proto) in regions)
            {
                if (proto.AltRegions.IsNullOrEmpty()) continue;
                if (HasGenuineEntryChannel(proto) == false) continue;
                foreach (PrototypeId altRef in proto.AltRegions)
                    if (altRef != PrototypeId.Invalid && altRef != proto.DataRef)
                        reachableViaLiveBase.Add(altRef);
            }

            bool IsReachable(RegionPrototype proto) =>
                HasGenuineEntryChannel(proto) ||
                reachableViaLiveBase.Contains(proto.DataRef);

            // ---- Build the live-asset pool from every reachable region's resolved district/cellset/clientmap assets ----
            Dictionary<AssetId, List<string>> assetToLiveRegions = new();
            foreach ((string name, RegionPrototype proto) in regions)
            {
                if (IsReachable(proto) == false) continue;

                HashSet<AssetId> fingerprint = new();
                GetRegionResourceFingerprint(proto, fingerprint);
                foreach (AssetId assetId in fingerprint)
                {
                    if (assetToLiveRegions.TryGetValue(assetId, out List<string> list) == false)
                    {
                        list = new();
                        assetToLiveRegions[assetId] = list;
                    }
                    if (list.Count < 3) list.Add(Path.GetFileNameWithoutExtension(name));
                }
            }

            Console.WriteLine($"==================== Region Asset-Reuse Audit (filter: '{pattern}') ====================");
            Console.WriteLine($"Live/reachable regions contribute {assetToLiveRegions.Count} distinct district/cellset/clientmap assets.");
            Console.WriteLine();

            int safeCount = 0, riskyCount = 0, skipCount = 0;
            List<string> riskyReport = new();

            foreach ((string name, RegionPrototype proto) in regions)
            {
                if (IsReachable(proto)) continue; // only auditing repurposing candidates
                if (string.IsNullOrEmpty(pattern) == false && name.Contains(pattern, StringComparison.OrdinalIgnoreCase) == false) continue;

                bool isTargetOnly = regionsWithTarget.Contains(proto.DataRef);

                HashSet<AssetId> fingerprint = new();
                GetRegionResourceFingerprint(proto, fingerprint);

                if (fingerprint.Count == 0)
                {
                    skipCount++;
                    Console.WriteLine($"[SKIP]  {Path.GetFileNameWithoutExtension(name)} | no resolvable district/cellset/clientmap assets - can't assess");
                    continue;
                }

                List<string> sharedWith = new();
                List<string> orphanAssets = new();
                foreach (AssetId assetId in fingerprint)
                {
                    string assetName = GameDatabase.GetAssetName(assetId);
                    if (assetToLiveRegions.TryGetValue(assetId, out List<string> liveRegionNames))
                        sharedWith.Add($"{assetName} (also used by {string.Join(",", liveRegionNames)})");
                    else
                        orphanAssets.Add(assetName);
                }

                bool nameFamilyMismatch = orphanAssets.Count > 0 && HasTokenOverlap(name, orphanAssets) == false;

                if (sharedWith.Count > 0 && orphanAssets.Count == 0)
                {
                    safeCount++;
                    Console.WriteLine($"[SAFE]  {Path.GetFileNameWithoutExtension(name)}{(isTargetOnly ? " (TARGETONLY)" : "")} | shares: {string.Join("; ", sharedWith)}");
                }
                else
                {
                    riskyCount++;
                    string flag = nameFamilyMismatch ? "RISKY+NAME-MISMATCH" : "RISKY";
                    string line = $"[{flag}] {Path.GetFileNameWithoutExtension(name)}{(isTargetOnly ? " (TARGETONLY)" : "")} | orphaned/unverified: {string.Join("; ", orphanAssets)}" +
                        (sharedWith.Count > 0 ? $" | also shares: {string.Join("; ", sharedWith)}" : "");
                    Console.WriteLine(line);
                    riskyReport.Add(line);
                }
            }

            Console.WriteLine();
            Console.WriteLine("==================== SUMMARY ====================");
            Console.WriteLine($"SAFE (shares a live district/cellset/clientmap - low risk): {safeCount}");
            Console.WriteLine($"RISKY (orphaned/unverified resource - matches confirmed dead-zone pattern): {riskyCount}");
            Console.WriteLine($"SKIP (nothing resolvable to check): {skipCount}");
        }

        /// <summary>Collects every District/CellSet/Cell/ClientMap AssetId a region's generator+areas depend on.</summary>
        private static void GetRegionResourceFingerprint(RegionPrototype region, HashSet<AssetId> fingerprint)
        {
            if (region.ClientMap != AssetId.Invalid) fingerprint.Add(region.ClientMap);

            if (region.RegionGenerator is SingleCellRegionGeneratorPrototype singleCellRegionGen)
            {
                if (singleCellRegionGen.Cell != AssetId.Invalid) fingerprint.Add(singleCellRegionGen.Cell);
                return;
            }

            HashSet<PrototypeId> areaRefs = new();
            region.RegionGenerator?.GetAreasInGenerator(areaRefs);

            foreach (PrototypeId areaRef in areaRefs)
            {
                AreaPrototype area = GameDatabase.GetPrototype<AreaPrototype>(areaRef);
                if (area == null) continue;
                if (area.ClientMap != AssetId.Invalid) fingerprint.Add(area.ClientMap);

                switch (area.Generator)
                {
                    case DistrictAreaGeneratorPrototype districtGen:
                        if (districtGen.District != AssetId.Invalid) fingerprint.Add(districtGen.District);
                        break;
                    case SingleCellAreaGeneratorPrototype singleCellAreaGen:
                        if (singleCellAreaGen.Cell != AssetId.Invalid) fingerprint.Add(singleCellAreaGen.Cell);
                        break;
                    case BaseGridAreaGeneratorPrototype gridGen:
                        if (gridGen.CellSets.HasValue())
                            foreach (CellSetEntryPrototype cellSet in gridGen.CellSets)
                                if (cellSet != null && cellSet.CellSet != AssetId.Invalid)
                                    fingerprint.Add(cellSet.CellSet);
                        break;
                }
            }
        }

        private static readonly Regex TokenSplitRegex = new(@"[A-Z]+(?![a-z])|[A-Z][a-z]*|[0-9]+|[a-z]+", RegexOptions.Compiled);

        /// <summary>Splits a Calligraphy-style PascalCase/underscored identifier into lowercase word tokens.</summary>
        private static HashSet<string> ExtractTokens(string identifier)
        {
            HashSet<string> tokens = new();
            foreach (Match m in TokenSplitRegex.Matches(identifier))
                if (m.Value.Length >= 4)
                    tokens.Add(m.Value.ToLowerInvariant());
            return tokens;
        }

        /// <summary>True if the region name shares at least one 4+ char word token with any of the given asset names.</summary>
        private static bool HasTokenOverlap(string regionName, List<string> assetNames)
        {
            HashSet<string> regionTokens = ExtractTokens(Path.GetFileNameWithoutExtension(regionName));
            foreach (string assetName in assetNames)
            {
                HashSet<string> assetTokens = ExtractTokens(assetName);
                if (regionTokens.Overlaps(assetTokens)) return true;
            }
            return false;
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
                PrototypeId itemRef = dropItem.Item?.DataRef ?? PrototypeId.Invalid;
                Console.WriteLine($"{indent}[LootDropItemPrototype] Item={SafeGetName(itemRef)} (Ref={(ulong)itemRef}) ParentDataRef={(ulong)dropItem.ParentDataRef} NumMin={dropItem.NumMin} NumMax={dropItem.NumMax} Weight={dropItem.Weight}");
                PrintModifiers(dropItem, indent);
            }
            else if (proto is LootDropItemFilterPrototype itemFilter)
            {
                Console.WriteLine($"{indent}[LootDropItemFilterPrototype] ParentDataRef={(ulong)itemFilter.ParentDataRef} NumMin={itemFilter.NumMin} NumMax={itemFilter.NumMax} ItemRank={itemFilter.ItemRank} UISlot={itemFilter.UISlot} Weight={itemFilter.Weight}");
                PrintModifiers(itemFilter, indent);
            }
            else if (proto is LootDropAgentPrototype agentDrop)
            {
                PrototypeId agentRef = agentDrop.Agent?.DataRef ?? PrototypeId.Invalid;
                Console.WriteLine($"{indent}[LootDropAgentPrototype] Agent={SafeGetName(agentRef)} (Ref={(ulong)agentRef}) ParentDataRef={(ulong)agentDrop.ParentDataRef} NumMin={agentDrop.NumMin} NumMax={agentDrop.NumMax} Weight={agentDrop.Weight}");
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
