using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    public static class TownUtility
    {
        public static bool IsTownMap(Map map)
        {
            return map?.Parent is OccupiedSettlement;
        }

        /// <summary>
        /// A local living their life on their own town's map (needs frozen, no mental breaks, busy with town duties).
        /// </summary>
        public static bool IsTownsfolk(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && ProtectorateUtility.IsProtectorate(pawn.Faction) && pawn.Map.Parent is OccupiedSettlement;
        }

        /// <summary>
        /// Called after a visit map is generated: the stockpile is laid out in the warehouse and the locals walk out.
        /// </summary>
        public static void PopulateMap(OccupiedSettlement town, Map map)
        {
            CellRect rect = town.townRect.IsEmpty ? CellRect.CenteredOn(map.Center, 20) : town.townRect.ClipInsideMap(map);

            List<IntVec3> storage = StorageCells(map, rect);
            int next = 0;
            foreach (Thing thing in town.Stock.ToList())
            {
                town.contents.Remove(thing);
                IntVec3 cell = storage.Count > 0 ? storage[next++ % storage.Count] : rect.CenterCell;
                if (GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near, out Thing placed))
                {
                    placed.SetForbidden(false, warnOnFail: false);
                }
                else
                {
                    town.contents.TryAdd(thing);
                }
            }

            var locals = new List<Pawn>();
            foreach (Pawn pawn in town.Residents.ToList())
            {
                town.contents.Remove(pawn);
                if (!CellFinder.TryFindRandomCellInsideWith(rect, c => c.Standable(map) && c.GetFirstPawn(map) == null, out IntVec3 cell))
                {
                    cell = CellFinder.RandomClosewalkCellNear(rect.CenterCell, map, 10);
                }
                GenSpawn.Spawn(pawn, cell, map);
                if (pawn.RaceProps.Humanlike)
                {
                    locals.Add(pawn);
                }
            }
            if (locals.Count > 0)
            {
                LordMaker.MakeNewLord(town.Faction, new LordJob_TownLife(rect, town.state == OccupationState.Annexed), map, locals);
            }
            OAMod.DebugLog($"Populated {town.Label}: {locals.Count} locals, {storage.Count} storage cells.");
        }

        /// <summary>
        /// Shelves first; otherwise the largest roofed room without beds; otherwise open ground near the centre.
        /// </summary>
        public static List<IntVec3> StorageCells(Map map, CellRect rect)
        {
            var cells = new List<IntVec3>();
            foreach (IntVec3 cell in rect)
            {
                if (cell.InBounds(map) && cell.GetFirstThing<Building_Storage>(map) != null)
                {
                    cells.Add(cell);
                }
            }
            if (cells.Count >= 8)
            {
                return cells;
            }

            Room best = null;
            var seen = new HashSet<Room>();
            foreach (IntVec3 cell in rect)
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }
                Room room = cell.GetRoom(map);
                if (room == null || !seen.Add(room) || room.PsychologicallyOutdoors || room.CellCount > 400 || room.ContainedBeds.Any())
                {
                    continue;
                }
                if (best == null || room.CellCount > best.CellCount)
                {
                    best = room;
                }
            }
            if (best != null)
            {
                cells.AddRange(best.Cells.Where(c => c.Standable(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null));
            }
            if (cells.Count == 0)
            {
                cells.AddRange(GenRadial.RadialCellsAround(rect.CenterCell, 6f, useCenter: true).Where(c => c.InBounds(map) && c.Standable(map)));
            }
            return cells;
        }

        /// <summary>
        /// Buildings a local can work at: workbenches (with their recipes' effects), and things that need tending
        /// in a vanilla settlement: fermenting barrels, generators, fires.
        /// </summary>
        public static List<Building> WorkTargets(Map map, CellRect rect, Faction faction)
        {
            var result = new List<Building>();
            List<Building> buildings = map.listerBuildings.allBuildingsNonColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];
                if (building.Faction == faction && rect.Contains(building.Position) && IsWorkTarget(building))
                {
                    result.Add(building);
                }
            }
            return result;
        }

        public static bool IsWorkTarget(Building building)
        {
            if (building.def.hasInteractionCell && (building is Building_WorkTable || building is Building_ResearchBench || !building.def.AllRecipes.NullOrEmpty()))
            {
                return true;
            }
            return building is Building_FermentingBarrel
                || building.TryGetComp<CompPowerPlant>() != null
                || building.TryGetComp<CompRefuelable>() != null;
        }

        public static PathEndMode WorkPathEndMode(Building building)
        {
            return building.def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.Touch;
        }

        public static bool AnyCrops(Map map, CellRect rect)
        {
            foreach (IntVec3 cell in rect)
            {
                if (cell.InBounds(map) && cell.GetPlant(map) is Plant plant && IsCrop(plant))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsCrop(Plant plant)
        {
            return plant.def.plant.Sowable && !plant.def.plant.IsTree && plant.def.plant.harvestedThingDef != null;
        }
    }
}
