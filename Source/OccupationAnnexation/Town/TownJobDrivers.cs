using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Works at a bench with the effects and sounds of one of its recipes. Nothing is produced:
    /// the town's output is abstract.
    /// </summary>
    public class JobDriver_FakeWork : JobDriver
    {
        private int duration = 2500;
        private RecipeDef recipe;

        private Thing Bench => job.GetTarget(TargetIndex.A).Thing;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref duration, "duration", 2500);
            Scribe_Defs.Look(ref recipe, "recipe");
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            duration = Rand.Range(1500, 4500);
            List<RecipeDef> recipes = Bench?.def.AllRecipes;
            recipe = recipes?.Where(r => r.effectWorking != null || r.soundWorking != null).RandomElementWithFallback();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, Bench is Building building ? TownUtility.WorkPathEndMode(building) : PathEndMode.Touch);

            Toil work = ToilMaker.MakeToil("OA_FakeWork");
            work.tickAction = () => pawn.rotationTracker.FaceTarget(Bench);
            work.defaultCompleteMode = ToilCompleteMode.Delay;
            work.defaultDuration = duration;
            work.WithEffect(() => recipe?.effectWorking, TargetIndex.A);
            work.PlaySustainerOrSound(() => recipe?.soundWorking);
            work.WithProgressBarToilDelay(TargetIndex.A);
            work.activeSkill = () => recipe?.workSkill;
            yield return work;
        }

        public override string GetReport()
        {
            if (recipe != null)
            {
                return "OA_ReportFakeWorkRecipe".Translate(recipe.label);
            }
            return base.GetReport();
        }
    }

    /// <summary>Tends a crop: kneels next to it with the harvesting effect and sound.</summary>
    public class JobDriver_FakeFarm : JobDriver
    {
        private Plant Plant => (Plant)job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil tend = ToilMaker.MakeToil("OA_FakeFarm");
            tend.tickAction = () => pawn.rotationTracker.FaceTarget(Plant);
            tend.defaultCompleteMode = ToilCompleteMode.Delay;
            tend.defaultDuration = Rand.Range(400, 900);
            tend.WithEffect(EffecterDefOf.Harvest_Plant, TargetIndex.A);
            tend.PlaySustainerOrSound(() => Plant?.def.plant.soundHarvesting);
            tend.WithProgressBarToilDelay(TargetIndex.A);
            tend.activeSkill = () => SkillDefOf.Plants;
            yield return tend;
        }
    }

    /// <summary>Walks to a point and keeps watch there for a while.</summary>
    public class JobDriver_Patrol : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil watch = Toils_General.Wait(Rand.Range(600, 1500));
            watch.tickAction = () =>
            {
                if (pawn.IsHashIntervalTick(300))
                {
                    pawn.Rotation = Rot4.Random;
                }
            };
            yield return watch;
        }
    }

    /// <summary>
    /// Mends a damaged building of the town. This is real: hit points come back and are kept in the snapshot.
    /// </summary>
    public class JobDriver_TownRepair : JobDriver
    {
        private const float TicksPerHitPoint = 20f;
        private float progress;

        private Thing Target => job.GetTarget(TargetIndex.A).Thing;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref progress, "progress", 0f);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil repair = ToilMaker.MakeToil("OA_TownRepair");
            repair.tickAction = () =>
            {
                Thing target = Target;
                pawn.rotationTracker.FaceTarget(target);
                progress += pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
                while (progress >= TicksPerHitPoint)
                {
                    progress -= TicksPerHitPoint;
                    target.HitPoints = Mathf.Min(target.HitPoints + 1, target.MaxHitPoints);
                }
                if (target.HitPoints >= target.MaxHitPoints)
                {
                    ReadyForNextToil();
                }
            };
            repair.defaultCompleteMode = ToilCompleteMode.Delay;
            repair.defaultDuration = GenDate.TicksPerHour;
            repair.WithEffect(() => Target?.def.repairEffect, TargetIndex.A);
            repair.WithProgressBar(TargetIndex.A, () => Target == null ? 1f : (float)Target.HitPoints / Target.MaxHitPoints);
            repair.activeSkill = () => SkillDefOf.Construction;
            yield return repair;
        }
    }

    /// <summary>
    /// Cleans filth in the town. Vanilla cleaning only works inside the player's home area, which towns do not have.
    /// </summary>
    public class JobDriver_TownClean : JobDriver
    {
        private float progress;

        private Filth Filth => job.GetTarget(TargetIndex.A).Thing as Filth;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref progress, "progress", 0f);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil clean = ToilMaker.MakeToil("OA_TownClean");
            clean.tickAction = () =>
            {
                Filth filth = Filth;
                if (filth == null || filth.Destroyed)
                {
                    ReadyForNextToil();
                    return;
                }
                progress += pawn.GetStatValue(StatDefOf.CleaningSpeed);
                if (progress > filth.def.filth.cleaningWorkToReduceThickness)
                {
                    progress = 0f;
                    filth.ThinFilth();
                    if (filth.Destroyed)
                    {
                        ReadyForNextToil();
                    }
                }
            };
            clean.defaultCompleteMode = ToilCompleteMode.Delay;
            clean.defaultDuration = 1500;
            clean.WithEffect(EffecterDefOf.Clean, TargetIndex.A);
            clean.PlaySustainerOrSound(() => SoundDefOf.Interact_CleanFilth);
            yield return clean;
        }
    }
}
