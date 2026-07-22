using Gazillion;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Missions.Actions
{
    public class MissionActionShowTeamSelectDialog : MissionAction
    {
        private MissionActionShowTeamSelectDialogPrototype _proto;

        public MissionActionShowTeamSelectDialog(IMissionActionOwner owner, MissionActionPrototype prototype) : base(owner, prototype)
        {
            // NotInGame CivilWarEventProgressionMissionPhase02
            _proto = prototype as MissionActionShowTeamSelectDialogPrototype;
        }

        public override void Run()
        {
            using var playersHandle = ListPool<Player>.Instance.Get(out List<Player> players);
            if (GetDistributors(DistributionType.Participants, players) == false) return;

            var message = NetMessageTeamSelectDialog.CreateBuilder()
                .SetPublicEventProtoId((ulong)_proto.PublicEvent)
                .Build();

            foreach (Player player in players)
                player.SendMessage(message);
        }
    }
}
