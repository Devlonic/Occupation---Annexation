using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Once the shooting has stopped after a capitulation, surrendered defenders who can doctor get up and tend
    /// their wounded that were not taken prisoner, with the medicine they carry, then lie back down.
    /// A shot or blow from the player toward them starts the ceasefire over: the medics drop their patients and lie face down.
    /// </summary>
    public static class SurrenderMedicUtility
    {
        /// <summary>Each medic waits its own 15 to 40 seconds (at normal speed) after the last attack.</summary>
        public const int MinCeasefireTicks = 900;
        public const int MaxCeasefireTicks = 2400;

        /// <summary>Shots at a spot this close to someone who capitulated count as shots at them.</summary>
        private const float NearRadius = 5.9f;

        public static bool CanDoctor(Pawn pawn)
        {
            return pawn.RaceProps.Humanlike && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
        }

        /// <summary>
        /// A surrendered pawn on its feet whose ceasefire delay has passed, able to doctor.
        /// </summary>
        public static bool MayTendNow(Pawn pawn)
        {
            if (!SurrenderUtility.IsSurrendered(pawn) || pawn.Downed || !pawn.Spawned)
            {
                return false;
            }
            MapComponent_SiegeMorale morale = pawn.Map.GetComponent<MapComponent_SiegeMorale>();
            return morale != null && morale.capitulated && morale.capitulatedPawns.Contains(pawn) && morale.StillCapitulated(pawn)
                && Find.TickManager.TicksGame >= morale.TendAllowedTick(pawn) && CanDoctor(pawn);
        }

        /// <summary>
        /// A comrade of the medic who is down or surrendered, free (not a prisoner) and, if asked, still needs tending.
        /// </summary>
        public static bool IsPatientFor(Pawn medic, Pawn patient, bool needsTend)
        {
            if (patient == null || patient.Dead || !patient.Spawned || patient.Map != medic.Map)
            {
                return false;
            }
            if (patient != medic)
            {
                if (patient.Faction != medic.Faction || !patient.RaceProps.Humanlike || patient.IsPrisoner || patient.IsSlave)
                {
                    return false;
                }
                if (!patient.Downed && !SurrenderUtility.IsSurrendered(patient))
                {
                    return false;
                }
            }
            return !needsTend || patient.health.HasHediffsNeedingTend();
        }

        /// <summary>
        /// The downed come first, then the worst bleeding, then the nearest. The medic's own wounds come last.
        /// </summary>
        public static Pawn FindPatient(Pawn medic)
        {
            Pawn best = null;
            bool bestDowned = false;
            float bestBleeding = 0f;
            int bestDistance = int.MaxValue;
            foreach (Pawn pawn in medic.Map.mapPawns.SpawnedPawnsInFaction(medic.Faction))
            {
                if (pawn == medic || !IsPatientFor(medic, pawn, needsTend: true))
                {
                    continue;
                }
                bool downed = pawn.Downed;
                float bleeding = pawn.health.hediffSet.BleedRateTotal;
                int distance = medic.Position.DistanceToSquared(pawn.Position);
                if (best != null)
                {
                    if (downed != bestDowned)
                    {
                        if (!downed)
                        {
                            continue;
                        }
                    }
                    else if (bleeding != bestBleeding)
                    {
                        if (bleeding < bestBleeding)
                        {
                            continue;
                        }
                    }
                    else if (distance >= bestDistance)
                    {
                        continue;
                    }
                }
                if (!medic.CanReserveAndReach(pawn, PathEndMode.ClosestTouch, Danger.Deadly))
                {
                    continue;
                }
                best = pawn;
                bestDowned = downed;
                bestBleeding = bleeding;
                bestDistance = distance;
            }
            if (best == null && IsPatientFor(medic, medic, needsTend: true))
            {
                best = medic;
            }
            return best;
        }

        public static Job TryGiveTendJob(Pawn medic)
        {
            if (!MayTendNow(medic))
            {
                return null;
            }
            Pawn patient = FindPatient(medic);
            if (patient == null)
            {
                return null;
            }
            // Only what they carry: the town's own medicine is left for the occupiers.
            Thing medicine = HealthAIUtility.FindBestMedicine(medic, patient, onlyUseInventory: true);
            Job job = medicine != null
                ? JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine, medic)
                : JobMaker.MakeJob(JobDefOf.TendPatient, patient);
            job.endAfterTendedOnce = patient == medic;
            return job;
        }

        /// <summary>
        /// A player's shot or blow on a map where people capitulated: aimed at them or close to them, it breaks the ceasefire.
        /// </summary>
        public static void Notify_PlayerAttack(Map map, LocalTargetInfo target)
        {
            if (map == null || !target.IsValid)
            {
                return;
            }
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale == null || !morale.capitulated || morale.capitulatedPawns.Count == 0)
            {
                return;
            }
            if (target.Thing is Pawn pawn && morale.capitulatedPawns.Contains(pawn))
            {
                morale.Notify_CeasefireBroken();
                return;
            }
            IntVec3 cell = target.Cell;
            foreach (Pawn capitulated in morale.capitulatedPawns)
            {
                if (capitulated.Spawned && capitulated.Map == map && capitulated.Position.InHorDistOf(cell, NearRadius))
                {
                    morale.Notify_CeasefireBroken();
                    return;
                }
            }
        }

        /// <summary>
        /// Weapons only: shooting, throwing and melee. Beating out fires, jumping or casting a buff near them is fine.
        /// </summary>
        public static bool IsAttackVerb(Verb verb)
        {
            VerbProperties props = verb.verbProps;
            return props != null && (props.IsMeleeAttack || props.defaultProjectile != null || verb is Verb_LaunchProjectile || CECompat.IsProjectileVerb(verb));
        }

        /// <summary>
        /// A medic sent back down puts the medicine in its hands back into its pockets instead of dropping it.
        /// </summary>
        public static void StowCarriedMedicine(Pawn pawn)
        {
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried != null && carried.def.IsMedicine && pawn.inventory != null)
            {
                pawn.carryTracker.innerContainer.TryTransferToContainer(carried, pawn.inventory.innerContainer, carried.stackCount);
            }
        }
    }
}
