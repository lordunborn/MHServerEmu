using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace LootTableDumper
{
    internal class Program
    {
        private static readonly HashSet<PrototypeId> VisitedInChain = new();

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
                bool alreadyShown = VisitedInChain.Contains(table.DataRef);

                Console.WriteLine($"{indent}[LootTable] {name} (Ref={(ulong)table.DataRef}) PickMethod={table.PickMethod} NoDropPercent={table.NoDropPercent} NumChoices={table.Choices?.Length ?? 0}{(alreadyShown ? "  <-- ALREADY VISITED ABOVE IN THIS CHAIN" : "")}");

                if (alreadyShown)
                    return;

                VisitedInChain.Add(table.DataRef);

                if (table.Choices != null)
                {
                    foreach (LootNodePrototype choice in table.Choices)
                        PrintNode(choice, depth + 1);
                }

                VisitedInChain.Remove(table.DataRef);
            }
            else if (proto is LootNodePrototype node)
            {
                string name = node.DataRef != PrototypeId.Invalid ? SafeGetName(node.DataRef) : "(anonymous)";
                Console.WriteLine($"{indent}[{node.GetType().Name}] {name} Weight={node.Weight}");
            }
            else
            {
                Console.WriteLine($"{indent}[{proto.GetType().Name}] {SafeGetName(proto.DataRef)} (Ref={(ulong)proto.DataRef})");
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
