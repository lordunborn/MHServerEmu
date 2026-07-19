using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Prototypes.Markers;

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
