using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// Surrendered pawns are never threats: colonists and turrets stop shooting at them,
    /// and SettlementDefeatUtility.IsDefeated ignores them.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ThreatDisabled))]
    public static class Patch_Pawn_ThreatDisabled
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (!__result && __instance.MentalStateDef == OA_DefOf.OA_Surrendered)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// Settlement defenders under assault do not panic-flee off the map: they fight until morale breaks, then surrender.
    /// </summary>
    [HarmonyPatch(typeof(LordJob), nameof(LordJob.AddFleeToil), MethodType.Getter)]
    public static class Patch_LordJob_AddFleeToil
    {
        public static void Postfix(LordJob __instance, ref bool __result)
        {
            if (!__result || !(__instance is LordJob_DefendBase))
            {
                return;
            }
            OASettings settings = OAMod.Settings;
            if (!settings.enableCapitulation || !settings.disableSettlementFlee)
            {
                return;
            }
            Map map = __instance.lord?.lordManager?.map;
            if (map != null && SurrenderUtility.IsHostileSettlementMap(map, out _))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Universal guard: no mod (CAI 5000, CE, vanilla duties) may hand a surrendered pawn a new job,
    /// except lying down and involuntary ones.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob
    {
        public static bool Prefix(Job newJob, Pawn ___pawn)
        {
            if (newJob == null || ___pawn == null || ___pawn.MentalStateDef != OA_DefOf.OA_Surrendered || ___pawn.Downed)
            {
                return true;
            }
            JobDef def = newJob.def;
            if (def == OA_DefOf.OA_Surrender || def == JobDefOf.Vomit || def == JobDefOf.Wait_Downed || def == JobDefOf.Wait_MaintainPosture)
            {
                return true;
            }
            OAMod.DebugLog($"Blocked job {def.defName} for surrendered {___pawn}.");
            return false;
        }
    }

    /// <summary>
    /// Getting back up (MakeUndowned) clears the mind, including the surrendered mental state.
    /// A pawn who had capitulated lies back down right away.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeUndowned")]
    public static class Patch_Pawn_HealthTracker_MakeUndowned
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (___pawn != null && ___pawn.Spawned && !___pawn.Downed && SurrenderUtility.HasCapitulated(___pawn))
            {
                SurrenderUtility.Resurrender(___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill
    {
        public static void Prefix(Pawn __instance, DamageInfo? dinfo)
        {
            if (!SurrenderUtility.IsSurrendered(__instance) || dinfo?.Instigator?.Faction != Faction.OfPlayer)
            {
                return;
            }
            MapComponent_SiegeMorale morale = __instance.MapHeld?.GetComponent<MapComponent_SiegeMorale>();
            if (morale != null)
            {
                morale.surrenderedKilled++;
            }
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "AddHumanlikeOrders")]
    public static class Patch_FloatMenuMakerMap_AddHumanlikeOrders
    {
        private static readonly TargetingParameters SurrenderedTargets = new TargetingParameters
        {
            canTargetPawns = true,
            canTargetBuildings = false,
            canTargetItems = false,
            mapObjectTargetsMustBeAutoAttackable = false,
            validator = t => t.Thing is Pawn p && SurrenderUtility.IsSurrendered(p)
        };

        public static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> opts)
        {
            if (pawn == null || !pawn.IsColonistPlayerControlled || pawn.Downed)
            {
                return;
            }
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return;
            }
            foreach (LocalTargetInfo target in GenUI.TargetsAt(clickPos, SurrenderedTargets, thingsOnly: true))
            {
                if (!(target.Thing is Pawn victim) || victim == pawn)
                {
                    continue;
                }
                if (!pawn.CanReach(victim, PathEndMode.Touch, Danger.Deadly))
                {
                    opts.Add(new FloatMenuOption("OA_CannotTakePrisoner".Translate(victim.LabelShort, victim) + ": " + "NoPath".Translate().CapitalizeFirst(), null));
                    continue;
                }
                var option = new FloatMenuOption("OA_TakePrisoner".Translate(victim.LabelShort, victim), () =>
                {
                    Job job = JobMaker.MakeJob(OA_DefOf.OA_TakeSurrenderedPrisoner, victim);
                    job.count = 1;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.High, null, victim);
                opts.Add(FloatMenuUtility.DecoratePrioritizedTask(option, pawn, victim));
            }
        }
    }

    // ---------------------------------------------------------------- CAI 5000

    /// <summary>
    /// CAI skips its whole combat reasoning (scan reactions, aggro, sapping, custom duties) for dead or downed pawns.
    /// Surrendered pawns are treated the same way.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CAI_IsDeadOrDowned
    {
        public static bool Prepare() => CAICompat.Active;

        public static MethodBase TargetMethod() => AccessTools.PropertyGetter(CAICompat.ThingCompCombatAIType, "IsDeadOrDowned");

        public static void Postfix(ThingComp __instance, ref bool __result)
        {
            if (!__result && __instance.parent is Pawn pawn && SurrenderUtility.IsSurrendered(pawn))
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// The getter above may be inlined by the JIT, so the rare tick (aggro, sapping, duty overrides) is also skipped directly.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CAI_CompTickRare
    {
        public static bool Prepare() => CAICompat.Active;

        public static MethodBase TargetMethod() => AccessTools.Method(CAICompat.ThingCompCombatAIType, "CompTickRare");

        public static bool Prefix(ThingComp __instance)
        {
            return !(__instance.parent is Pawn pawn && SurrenderUtility.IsSurrendered(pawn));
        }
    }

    // ---------------------------------------------------------------- Combat Extended

    [HarmonyPatch]
    public static class Patch_CE_AddSuppression
    {
        public static bool Prepare() => CECompat.Active;

        public static MethodBase TargetMethod() => AccessTools.Method(CECompat.CompSuppressableType, "AddSuppression");

        public static bool Prefix(ThingComp __instance)
        {
            return !(__instance.parent is Pawn pawn && SurrenderUtility.IsSurrendered(pawn));
        }
    }
}
