using System.Collections.Generic;

namespace MHServerEmu.Games.RoguesGallery
{
    public class IncursionPowerOverrideEntry
    {
        public string Power { get; set; }
        public float? DamageScale { get; set; }
        public bool? Enabled { get; set; }
    }

    public class IncursionHeroPowerOverrides
    {
        public string Hero { get; set; }
        public List<IncursionPowerOverrideEntry> Powers { get; set; } = new();
    }

    public class IncursionPowerOverrideData
    {
        public List<IncursionHeroPowerOverrides> Heroes { get; set; } = new();
    }
}
