using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Fire comes before any tending, whatever the ceasefire: burns get worse every second and spread to the patient's
    /// bed and to whoever lies next to them, while a bandage can wait a few seconds. Someone who surrendered and catches
    /// fire rolls it out on the spot instead of running around, and the nearest one on their feet puts out a comrade who
    /// burns on the ground. A fire lit by the player is not an attack by itself: only the shot or throw that lit it was.
    /// </summary>
    public static class SurrenderFireUtility
    {
        /// <summary>Burning comrades farther than this are left to someone closer.</summary>
        public const float MaxHelpDistance = 40f;

        /// <summary>Above zero while a fire burns what stands in it; that damage is not the player's attack.</summary>
        public static int fireDamageDepth;

        public static bool DealingFireDamage => fireDamageDepth > 0;

        public static bool CanFightFires(Pawn pawn)
        {
            return pawn.RaceProps.Humanlike && !pawn.WorkTagIsDisabled(WorkTags.Firefighting)
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        public static bool IsFireJob(JobDef def)
        {
            return def == JobDefOf.BeatFire || def == JobDefOf.ExtinguishSelf;
        }

        /// <summary>
        /// Someone who capitulated here, on their feet and able to fight fires. A colonist coming for them keeps them down.
        /// </summary>
        public static bool MayFightFiresNow(Pawn pawn)
        {
            return SurrenderUtility.IsSurrendered(pawn) && !pawn.Downed && pawn.Spawned && SurrenderUtility.HasCapitulated(pawn)
                && CanFightFires(pawn) && !SurrenderMedicUtility.ClaimedByPlayer(pawn);
        }

        /// <summary>
        /// The fire on a downed comrade, or in the cell where they lie. Those on their feet put out their own.
        /// </summary>
        public static Fire FireOnDowned(Pawn pawn)
        {
            if (!pawn.Spawned || !pawn.Downed)
            {
                return null;
            }
            return pawn.GetAttachment(ThingDefOf.Fire) as Fire ?? pawn.Position.GetFirstThing<Fire>(pawn.Map);
        }

        /// <summary>Someone other than <paramref name="except"/> is already beating out this fire.</summary>
        public static bool IsBeingPutOut(Fire fire, Pawn except)
        {
            foreach (Pawn pawn in fire.Map.mapPawns.AllPawnsSpawned)
            {
                if (pawn != except && pawn.CurJobDef == JobDefOf.BeatFire && pawn.CurJob.targetA.Thing == fire)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The nearest downed comrade on fire that nobody is putting out yet, and that fire.
        /// </summary>
        public static Fire FindFireOnComrade(Pawn pawn)
        {
            Fire best = null;
            int bestDistance = int.MaxValue;
            foreach (Pawn comrade in pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction))
            {
                if (comrade == pawn || !SurrenderMedicUtility.IsPatientFor(pawn, comrade, needsTend: false))
                {
                    continue;
                }
                Fire fire = FireOnDowned(comrade);
                if (fire == null)
                {
                    continue;
                }
                int distance = pawn.Position.DistanceToSquared(comrade.Position);
                if (distance >= bestDistance || distance > MaxHelpDistance * MaxHelpDistance || IsBeingPutOut(fire, pawn)
                    || !pawn.CanReach(fire, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                best = fire;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// Their own fire first, then a comrade burning on the ground.
        /// </summary>
        public static Job TryGiveFireJob(Pawn pawn)
        {
            if (!SurrenderUtility.IsSurrendered(pawn) || pawn.Downed || !pawn.Spawned)
            {
                return null;
            }
            if (pawn.GetAttachment(ThingDefOf.Fire) is Fire own)
            {
                return JobMaker.MakeJob(JobDefOf.ExtinguishSelf, own);
            }
            if (!MayFightFiresNow(pawn))
            {
                return null;
            }
            Fire fire = FindFireOnComrade(pawn);
            return fire != null ? JobMaker.MakeJob(JobDefOf.BeatFire, fire) : null;
        }
    }
}
