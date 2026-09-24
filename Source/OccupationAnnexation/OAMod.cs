using HarmonyLib;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    public class OAMod : Mod
    {
        public const string HarmonyId = "tmine.OccupationAnnexation";

        public static OASettings Settings;

        public OAMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OASettings>();
            new Harmony(HarmonyId).PatchAll(typeof(OAMod).Assembly);
            if (CECompat.Active)
            {
                Log.Message("[Occupation & Annexation] Combat Extended detected: suppression affects defender morale.");
            }
            if (CAICompat.Active)
            {
                Log.Message("[Occupation & Annexation] CAI 5000 detected: surrendered pawns are excluded from its combat AI.");
            }
        }

        public override string SettingsCategory() => "OA_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect) => Settings.DoWindowContents(inRect);

        public static void DebugLog(string text)
        {
            if (Settings != null && Settings.debugLogging)
            {
                Log.Message("[Occupation & Annexation] " + text);
            }
        }
    }
}
