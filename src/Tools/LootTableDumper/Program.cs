using MHServerEmu.Core.Extensions;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

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

            // One-off: check whether TrainingRoomSHIELDRegion (and its areas) carry the CosmicGameMode region keyword
            // that gates SharedPatrolHightownBossesAll's Cosmic vs non-Cosmic branch selection.
            PrototypeId trainingRoomRef = (PrototypeId)12181996598405306634;
            RegionPrototype trainingRoomProto = GameDatabase.GetPrototype<RegionPrototype>(trainingRoomRef);
            Console.WriteLine("==================== TrainingRoomSHIELDRegion.prototype Keywords ====================");
            if (trainingRoomProto == null)
            {
                Console.WriteLine("  Could not load RegionPrototype.");
            }
            else
            {
                Console.WriteLine($"  HasKeywords={trainingRoomProto.HasKeywords}");
                if (trainingRoomProto.Keywords != null)
                {
                    foreach (PrototypeId kw in trainingRoomProto.Keywords)
                        Console.WriteLine($"  Keyword: {SafeGetName(kw)} (Ref={(ulong)kw})");
                }
            }

            if (args.Length > 0 && args[0] == "--search")
            {
                string pattern = args.Length > 1 ? args[1] : "";
                SearchLootTables(pattern);
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

                Console.WriteLine($"{indent}[LootTable] {name} (Ref={(ulong)table.DataRef}) PickMethod={table.PickMethod} NoDropPercent={table.NoDropPercent} NumChoices={table.Choices?.Length ?? 0}{(alreadyShown ? "  <-- CYCLE, SAME OBJECT IS ITS OWN ANCESTOR" : "")}");
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
                Console.WriteLine($"{indent}[LootDropItemPrototype] Item={SafeGetName(dropItem.Item)} (Ref={(ulong)dropItem.Item}) NumMin={dropItem.NumMin} NumMax={dropItem.NumMax} Weight={dropItem.Weight}");
                PrintModifiers(dropItem, indent);
            }
            else if (proto is LootNodePrototype node)
            {
                string name = node.DataRef != PrototypeId.Invalid ? SafeGetName(node.DataRef) : "(anonymous)";
                Console.WriteLine($"{indent}[{node.GetType().Name}] {name} Weight={node.Weight}");
                PrintModifiers(node, indent);
            }
            else
            {
                Console.WriteLine($"{indent}[{proto.GetType().Name}] {SafeGetName(proto.DataRef)} (Ref={(ulong)proto.DataRef})");
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
                    _ => ""
                };

                Console.WriteLine($"{indent}  (Modifier) {modifier.GetType().Name} {detail}");
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
