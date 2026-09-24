using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    public abstract class JobGiver_TownBase : ThinkNode_JobGiver
    {
        protected static bool TryGetTown(Pawn pawn, out OccupiedSettlement town, out CellRect rect)
        {
            town = pawn.Map?.Parent as OccupiedSettlement;
            rect = CellRect.Empty;
            if (town == null)
            {
                return false;
            }
            rect = town.townRect.IsEmpty ? CellRect.CenteredOn(pawn.Map.Center, 20) : town.townRect.ClipInsideMap(pawn.Map);
            return true;
        }
    }

    /// <summary>
    /// Works at a workbench, or tends a barrel, generator or fire of the town, for an hour or two (visual only).
    /// </summary>
    public class JobGiver_TownFakeWork : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return null;
            }
            Map map = pawn.Map;
            List<Building> targets = TownUtility.WorkTargets(map, rect, pawn.Faction)
                .Where(b => pawn.CanReserveAndReach(b, TownUtility.WorkPathEndMode(b), Danger.Some)
                    && (!b.def.hasInteractionCell || b.InteractionCell.Standable(map)))
                .ToList();
            if (!targets.TryRandomElement(out Building target))
            {
                return null;
            }
            return JobMaker.MakeJob(OA_TownDefOf.OA_FakeWork, target);
        }
    }

    /// <summary>Repairs buildings damaged in the fighting.</summary>
    public class JobGiver_TownRepair : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                || StatDefOf.ConstructionSpeed.Worker.IsDisabledFor(pawn))
            {
                return null;
            }
            List<Building> damaged = pawn.Map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == pawn.Faction && b.def.useHitPoints && b.HitPoints < b.MaxHitPoints && rect.Contains(b.Position))
                .OrderBy(b => b.Position.DistanceToSquared(pawn.Position))
                .Take(8)
                .ToList();
            foreach (Building building in damaged.InRandomOrder())
            {
                if (pawn.CanReserveAndReach(building, PathEndMode.Touch, Danger.Some))
                {
                    return JobMaker.MakeJob(OA_TownDefOf.OA_TownRepair, building);
                }
            }
            return null;
        }
    }

    /// <summary>Cleans up blood and dirt in the town.</summary>
    public class JobGiver_TownClean : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect) || StatDefOf.CleaningSpeed.Worker.IsDisabledFor(pawn))
            {
                return null;
            }
            List<Thing> filth = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)
                .Where(f => rect.Contains(f.Position))
                .OrderBy(f => f.Position.DistanceToSquared(pawn.Position))
                .Take(10)
                .ToList();
            foreach (Thing thing in filth.InRandomOrder())
            {
                if (pawn.CanReserveAndReach(thing, PathEndMode.Touch, Danger.Some))
                {
                    return JobMaker.MakeJob(OA_TownDefOf.OA_TownClean, thing);
                }
            }
            return null;
        }
    }

    /// <summary>Tends a crop of the town's fields (visual only).</summary>
    public class JobGiver_TownFakeFarm : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect))
            {
                return null;
            }
            Map map = pawn.Map;
            var crops = new List<Plant>();
            foreach (IntVec3 cell in rect)
            {
                if (cell.InBounds(map) && cell.GetPlant(map) is Plant plant && TownUtility.IsCrop(plant))
                {
                    crops.Add(plant);
                }
            }
            for (int attempt = 0; attempt < 6 && crops.Count > 0; attempt++)
            {
                Plant plant = crops.RandomElement();
                if (pawn.CanReserveAndReach(plant, PathEndMode.Touch, Danger.Some))
                {
                    return JobMaker.MakeJob(OA_TownDefOf.OA_FakeFarm, plant);
                }
            }
            return null;
        }
    }

    /// <summary>Moves goods of the stockpile around the warehouse.</summary>
    public class JobGiver_TownHaul : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return null;
            }
            Map map = pawn.Map;
            List<Thing> items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(t => t.Spawned && rect.Contains(t.Position) && !(t is Corpse)
                    && (t.def.thingCategories == null || !t.def.IsWithinCategory(ThingCategoryDefOf.Chunks))
                    && pawn.CanReserve(t))
                .ToList();
            if (!items.TryRandomElement(out Thing thing))
            {
                return null;
            }
            // The stockpile usually fills every shelf, so goods are moved around within the warehouse room (or nearby outdoors).
            Room room = thing.GetRoom();
            IEnumerable<IntVec3> candidates = room != null && !room.PsychologicallyOutdoors && room.CellCount <= 400
                ? room.Cells
                : GenRadial.RadialCellsAround(thing.Position, 6f, useCenter: false);
            IntVec3 target = candidates
                .Where(c => c != thing.Position && c.InBounds(map) && c.Standable(map) && c.GetFirstItem(map) == null
                    && (c.GetEdifice(map) == null || c.GetEdifice(map) is Building_Storage)
                    && pawn.CanReserveAndReach(c, PathEndMode.OnCell, Danger.Some))
                .RandomElementWithFallback(IntVec3.Invalid);
            if (!target.IsValid || !pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Some))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, target);
            job.count = thing.stackCount;
            job.haulMode = HaulMode.ToCellNonStorage;
            return job;
        }
    }

    /// <summary>Goes to bed at night: a free bed of the town, or the floor of a roofed room.</summary>
    public class JobGiver_TownSleep : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect))
            {
                return null;
            }
            Map map = pawn.Map;
            if (pawn.needs?.rest != null)
            {
                // Needs are frozen in towns; make them sleepy so they actually fall asleep.
                pawn.needs.rest.CurLevel = 0.3f;
            }
            Building_Bed bed = map.listerBuildings.allBuildingsNonColonist
                .OfType<Building_Bed>()
                .Where(b => b.Faction == pawn.Faction && rect.Contains(b.Position) && !b.Medical && !b.ForPrisoners && b.AnyUnoccupiedSleepingSlot
                    && pawn.CanReserveAndReach(b, PathEndMode.OnCell, Danger.Some, b.SleepingSlotsCount))
                .OrderBy(b => b.Position.DistanceToSquared(pawn.Position))
                .FirstOrDefault();
            Job job;
            if (bed != null)
            {
                job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            }
            else
            {
                if (!CellFinder.TryFindRandomCellInsideWith(rect, c => c.Roofed(map) && c.Standable(map) && c.GetEdifice(map) == null && pawn.CanReserveAndReach(c, PathEndMode.OnCell, Danger.Some), out IntVec3 spot))
                {
                    return null;
                }
                job = JobMaker.MakeJob(JobDefOf.LayDown, spot);
            }
            job.expiryInterval = GenDate.TicksPerHour * 3;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    /// <summary>Walks to a random point on the edge of the town and keeps watch for a while.</summary>
    public class JobGiver_TownPatrol : JobGiver_TownBase
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!TryGetTown(pawn, out _, out CellRect rect))
            {
                return null;
            }
            Map map = pawn.Map;
            CellRect edge = rect.ExpandedBy(2).ClipInsideMap(map);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                IntVec3 cell = edge.EdgeCells.RandomElement();
                if (cell.Standable(map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some))
                {
                    Job job = JobMaker.MakeJob(OA_TownDefOf.OA_Patrol, cell);
                    job.locomotionUrgency = LocomotionUrgency.Walk;
                    return job;
                }
            }
            return null;
        }
    }
}
