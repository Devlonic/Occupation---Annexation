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
    /// except lying down, involuntary ones, putting out fires and tending the wounded during a ceasefire.
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
            if (def == OA_DefOf.OA_Surrender || def == JobDefOf.Vomit || def == JobDefOf.Wait_Downed || def == JobDefOf.Wait_MaintainPosture
                || def == JobDefOf.ExtinguishSelf)
            {
                return true;
            }
            if (def == JobDefOf.BeatFire && newJob.targetA.Thing is Fire && SurrenderFireUtility.MayFightFiresNow(___pawn))
            {
                return true;
            }
            if (SurrenderMedicUtility.IsMedicJob(def) && SurrenderMedicUtility.MayTendNow(___pawn))
            {
                return true;
            }
            if (OAMod.Settings.debugLogging)
            {
                int key = Gen.HashCombineInt(___pawn.thingIDNumber, def.shortHash);
                int now = Find.TickManager.TicksGame;
                if (!lastLogged.TryGetValue(key, out int tick) || now - tick > GenDate.TicksPerHour)
                {
                    lastLogged[key] = now;
                    OAMod.DebugLog($"Blocked job {def.defName} ({newJob.targetA}) for surrendered {___pawn} at {___pawn.Position}, current job {___pawn.CurJobDef?.defName}.");
                }
            }
            return false;
        }

        private static readonly Dictionary<int, int> lastLogged = new Dictionary<int, int>();
    }

    /// <summary>
    /// Vanilla drops whatever a pawn holds when a job ends. A surrendered medic pockets the medicine left over after
    /// tending instead (finish actions run before that drop).
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob_Medic
    {
        public static void Postfix(Pawn_JobTracker __instance, Job newJob, Pawn ___pawn)
        {
            if (newJob?.def == JobDefOf.TendPatient && __instance.curJob == newJob && __instance.curDriver != null && SurrenderUtility.IsSurrendered(___pawn))
            {
                __instance.curDriver.AddFinishAction(condition => SurrenderMedicUtility.StowCarriedMedicine(___pawn));
            }
        }
    }

    /// <summary>
    /// Vanilla treats every hostile pawn as a wall. Those who surrendered and the player's people pass each other:
    /// a colonist standing in a doorway would otherwise stop a medic carrying the wounded for good, and someone lying
    /// face down in a corridor would stop colonists.
    /// </summary>
    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.PawnBlockingPathAt))]
    public static class Patch_PawnUtility_PawnBlockingPathAt
    {
        public static void Postfix(IntVec3 c, Pawn forPawn, ref Pawn __result)
        {
            if (__result == null || !PassEachOther(forPawn, __result))
            {
                return;
            }
            // Anyone else hostile in that cell still blocks.
            List<Thing> things = c.GetThingList(forPawn.Map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn other && other != forPawn && other != __result && !other.Downed && other.HostileTo(forPawn) && !PassEachOther(forPawn, other))
                {
                    __result = other;
                    return;
                }
            }
            __result = null;
        }

        private static bool PassEachOther(Pawn a, Pawn b)
        {
            return (SurrenderUtility.IsSurrendered(a) && b.Faction == Faction.OfPlayer) || (SurrenderUtility.IsSurrendered(b) && a.Faction == Faction.OfPlayer);
        }
    }

    /// <summary>
    /// Those who surrendered never plan a way through walls or locked doors: they may not break anything. CAI 5000
    /// lets hostile pawns dig through walls, and a medic carrying the wounded stood before a town wall for good.
    /// Runs after CAI's own prefix, which sets that mode.
    /// </summary>
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPath),
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode), typeof(PathFinderCostTuning) })]
    public static class Patch_PathFinder_FindPath
    {
        [HarmonyPriority(Priority.Last)]
        public static void Prefix(ref TraverseParms traverseParms)
        {
            Pawn pawn = traverseParms.pawn;
            if (pawn == null || !SurrenderUtility.IsSurrendered(pawn))
            {
                return;
            }
            TraverseMode mode = traverseParms.mode;
            if (mode == TraverseMode.PassAllDestroyableThings || mode == TraverseMode.PassAllDestroyableThingsNotWater
                || mode == TraverseMode.PassAllDestroyablePlayerOwnedThings)
            {
                traverseParms.mode = TraverseMode.ByPawn;
            }
            traverseParms.canBashDoors = false;
            traverseParms.canBashFences = false;
        }
    }

    /// <summary>
    /// A medic whose way is blocked by a colonist does not attack them (vanilla's reaction to a blocked path);
    /// it gives up that errand instead and thinks again.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.CanAttackWhenPathingBlocked), MethodType.Getter)]
    public static class Patch_Pawn_CanAttackWhenPathingBlocked
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__result && SurrenderUtility.IsSurrendered(__instance))
            {
                __result = false;
            }
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
            // Burning to death in a fire left from the fight is not a killing by the player.
            if (!SurrenderUtility.IsSurrendered(__instance) || dinfo?.Instigator?.Faction != Faction.OfPlayer || SurrenderFireUtility.DealingFireDamage)
            {
                return;
            }
            MapComponent_SiegeMorale morale = __instance.MapHeld?.GetComponent<MapComponent_SiegeMorale>();
            if (morale != null)
            {
                morale.surrenderedKilled++;
            }
            SurrenderMedicUtility.Notify_PlayerAttack(__instance.MapHeld, __instance);
            SurrenderConsequencesUtility.Notify_SurrenderedKilled(__instance);
        }
    }

    /// <summary>
    /// Vanilla tends people who lie on the ground from their own cell (ClosestTouch), and only the downed are handled
    /// consistently. Someone lying face down who is not downed is tended from next to them instead.
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_TendPatient), nameof(JobDriver_TendPatient.Notify_Starting))]
    public static class Patch_JobDriver_TendPatient_Notify_Starting
    {
        public static void Postfix(JobDriver_TendPatient __instance, ref PathEndMode ___pathEndMode)
        {
            Pawn patient = __instance.job.targetA.Pawn;
            if (patient != null && patient != __instance.pawn && !patient.Downed && !patient.InBed() && SurrenderUtility.IsSurrendered(patient))
            {
                ___pathEndMode = PathEndMode.Touch;
            }
        }
    }

    /// <summary>
    /// The future town remembers every time the player's doctors tended those who capitulated.
    /// </summary>
    [HarmonyPatch(typeof(TendUtility), nameof(TendUtility.DoTend))]
    public static class Patch_TendUtility_DoTend
    {
        public static void Postfix(Pawn doctor, Pawn patient)
        {
            if (doctor?.Faction != Faction.OfPlayer || patient == null || !SurrenderUtility.HasCapitulated(patient))
            {
                return;
            }
            MapComponent_SiegeMorale morale = patient.MapHeld.GetComponent<MapComponent_SiegeMorale>();
            if (morale != null)
            {
                morale.tendedByPlayer++;
            }
        }
    }

    /// <summary>
    /// Every shot, throw or blow of the player's side, aimed at those who capitulated or close to them, breaks the ceasefire.
    /// Combat Extended fires through the same method.
    /// </summary>
    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    public static class Patch_Verb_TryCastNextBurstShot
    {
        public static void Prefix(Verb __instance, LocalTargetInfo ___currentTarget)
        {
            Thing caster = __instance.caster;
            if (caster == null || !caster.Spawned || caster.Faction != Faction.OfPlayer || !SurrenderMedicUtility.IsAttackVerb(__instance))
            {
                return;
            }
            SurrenderMedicUtility.Notify_PlayerAttack(caster.Map, ___currentTarget);
        }
    }

    /// <summary>
    /// Whatever the player hurts them with (explosions, stray bullets, fire) breaks the ceasefire too.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Pawn_PostApplyDamage
    {
        public static void Postfix(Pawn __instance, DamageInfo dinfo)
        {
            // MapHeld: someone being carried by a medic counts too. A fire burns in the name of whoever lit it; its burns are not a new attack.
            if (dinfo.Instigator?.Faction == Faction.OfPlayer && __instance.MapHeld != null && !SurrenderFireUtility.DealingFireDamage
                && SurrenderUtility.HasCapitulated(__instance))
            {
                SurrenderMedicUtility.Notify_PlayerAttack(__instance.MapHeld, __instance);
            }
        }
    }

    /// <summary>
    /// Marks the burns a fire deals to what stands in it (vanilla credits them to whoever lit the fire).
    /// </summary>
    [HarmonyPatch(typeof(Fire), "DoFireDamage")]
    public static class Patch_Fire_DoFireDamage
    {
        public static void Prefix()
        {
            SurrenderFireUtility.fireDamageDepth++;
        }

        public static System.Exception Finalizer(System.Exception __exception)
        {
            SurrenderFireUtility.fireDamageDepth--;
            return __exception;
        }
    }

    /// <summary>
    /// Someone who surrendered and catches fire drops and rolls it out on the spot (vanilla: one chance in ten to think
    /// of it, otherwise running around or toward water, which the surrender forbids, so they just burned).
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_ExtinguishSelf), "TryGiveJob")]
    public static class Patch_JobGiver_ExtinguishSelf
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!SurrenderUtility.IsSurrendered(pawn) || pawn.Downed)
            {
                return true;
            }
            __result = pawn.GetAttachment(ThingDefOf.Fire) is Fire fire ? JobMaker.MakeJob(JobDefOf.ExtinguishSelf, fire) : null;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobGiver_JumpInWater), "TryGiveJob")]
    public static class Patch_JobGiver_JumpInWater
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!SurrenderUtility.IsSurrendered(pawn))
            {
                return true;
            }
            __result = null;
            return false;
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

        private static readonly TargetingParameters CapitulatedTargets = new TargetingParameters
        {
            canTargetPawns = true,
            canTargetBuildings = false,
            canTargetItems = false,
            mapObjectTargetsMustBeAutoAttackable = false,
            validator = t => t.Thing is Pawn p && SurrenderUtility.HasCapitulated(p)
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
            foreach (LocalTargetInfo target in GenUI.TargetsAt(clickPos, CapitulatedTargets, thingsOnly: true))
            {
                // Drafted doctors already get vanilla's "Tend" for anyone who is down.
                if (target.Thing is Pawn patient && patient != pawn && !(pawn.Drafted && patient.Downed))
                {
                    AddTendOptions(pawn, patient, opts);
                }
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

        /// <summary>
        /// Vanilla only lets drafted doctors tend enemies who are down. Those who capitulated can be tended by anyone,
        /// with the cheapest medicine at hand or without.
        /// </summary>
        private static void AddTendOptions(Pawn doctor, Pawn patient, List<FloatMenuOption> opts)
        {
            if (!patient.health.HasHediffsNeedingTend())
            {
                opts.Add(new FloatMenuOption("CannotTend".Translate(patient) + ": " + "TendingNotRequired".Translate(patient), null));
                return;
            }
            if (doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
            {
                opts.Add(new FloatMenuOption("CannotTend".Translate(patient) + ": " + "CannotPrioritizeWorkTypeDisabled".Translate(WorkTypeDefOf.Doctor.gerundLabel), null));
                return;
            }
            if (!doctor.CanReach(patient, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                opts.Add(new FloatMenuOption("CannotTend".Translate(patient) + ": " + "NoPath".Translate().CapitalizeFirst(), null));
                return;
            }
            Thing medicine = SurrenderMedicUtility.FindCheapestMedicine(doctor, patient);
            string with = medicine != null ? medicine.LabelNoCount : "WithoutMedicine".Translate().ToString();
            var option = new FloatMenuOption("Tend".Translate(patient) + " (" + with + ")", () => OrderTend(doctor, patient, medicine), MenuOptionPriority.Default, null, patient);
            opts.Add(FloatMenuUtility.DecoratePrioritizedTask(option, doctor, patient));
            if (medicine != null)
            {
                opts.Add(new FloatMenuOption("Tend".Translate(patient) + " (" + "WithoutMedicine".Translate() + ")", () => OrderTend(doctor, patient, null), MenuOptionPriority.Default, null, patient));
            }
        }

        public static void OrderTend(Pawn doctor, Pawn patient, Thing medicine)
        {
            Job job;
            if (medicine == null)
            {
                job = JobMaker.MakeJob(JobDefOf.TendPatient, patient);
            }
            else if (medicine.ParentHolder is Pawn_InventoryTracker inventory)
            {
                job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine, inventory.pawn);
            }
            else
            {
                job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
            }
            job.count = 1;
            doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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
