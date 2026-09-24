using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    public static class DeliveryUtility
    {
        private const int PackingTicks = GenDate.TicksPerDay / 4;
        private const int PodFlightTicks = GenDate.TicksPerHour * 3;
        private const int MaxPackAnimals = 12;

        public static Map BestHomeMap(int fromTile)
        {
            return Find.Maps.Where(m => m.IsPlayerHome)
                .OrderBy(m => Find.WorldGrid.ApproxDistanceInTiles(m.Tile, fromTile))
                .FirstOrDefault();
        }

        public static bool CanUsePods(OccupiedSettlement town)
        {
            return OAMod.Settings.allowDropPodDelivery && town.TechLevel >= TechLevel.Industrial;
        }

        public static int TravelTicks(OccupiedSettlement town, Map destination, bool byPods)
        {
            if (byPods)
            {
                return PackingTicks + PodFlightTicks;
            }
            int ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(town.Tile, destination.Tile, null);
            if (ticks <= 0)
            {
                ticks = Find.WorldGrid.TraversalDistanceBetween(town.Tile, destination.Tile) * 3300;
            }
            return PackingTicks + ticks;
        }

        /// <summary>
        /// Takes the given things out of the town's stockpile and sends them on their way.
        /// </summary>
        public static void RequestDelivery(OccupiedSettlement town, Map destination, List<Thing> things, bool byPods)
        {
            var order = new DeliveryOrder
            {
                source = town,
                sourceTile = town.Tile,
                sourceLabel = town.Label,
                destination = destination,
                byPods = byPods,
                arrivalTick = Find.TickManager.TicksGame + TravelTicks(town, destination, byPods)
            };
            foreach (Thing thing in things)
            {
                thing.holdingOwner?.Remove(thing);
                order.items.TryAdd(thing, canMergeWithExistingStacks: true);
            }
            GameComponent_Occupation.Instance.deliveries.Add(order);
            Messages.Message("OA_MessageDeliveryDispatched".Translate(town.Label, destination.Parent.Label, (order.arrivalTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()), town, MessageTypeDefOf.NeutralEvent);
        }

        public static void Arrive(DeliveryOrder order)
        {
            Map map = order.destination;
            if (map == null || !Find.Maps.Contains(map) || !map.IsPlayerHome)
            {
                map = BestHomeMap(order.sourceTile >= 0 ? order.sourceTile : 0);
            }
            List<Thing> things = order.items.ToList();
            order.items.Clear();
            if (things.Count == 0)
            {
                return;
            }
            if (map == null)
            {
                // Nowhere to deliver: the goods go back to the stockpile, or are lost with the town.
                if (order.source != null && !order.source.Destroyed)
                {
                    foreach (Thing thing in things)
                    {
                        order.source.Store(thing);
                    }
                }
                return;
            }

            var lookAt = new List<Thing>();
            if (order.byPods)
            {
                IntVec3 center = DropCellFinder.TradeDropSpot(map);
                DropPodUtility.DropThingsNear(center, map, things, canRoofPunch: false, forbid: false, allowFogged: false);
                lookAt.AddRange(things);
            }
            else if (!TrySendCaravan(order, map, things, lookAt))
            {
                IntVec3 center = DropCellFinder.TradeDropSpot(map);
                DropPodUtility.DropThingsNear(center, map, things, canRoofPunch: false, forbid: false, allowFogged: false);
                lookAt.AddRange(things);
            }

            string summary = things.Select(t => t.LabelCap).ToLineList("  - ");
            Find.LetterStack.ReceiveLetter(
                "OA_LetterDeliveryLabel".Translate(order.sourceLabel),
                "OA_LetterDelivery".Translate(order.sourceLabel, summary),
                LetterDefOf.PositiveEvent,
                new LookTargets(lookAt));
        }

        /// <summary>
        /// A townsman with pack animals walks in, drops the goods near the colony and leaves.
        /// </summary>
        private static bool TrySendCaravan(DeliveryOrder order, Map map, List<Thing> things, List<Thing> lookAt)
        {
            Faction protectorate = ProtectorateUtility.GetOrCreate();
            Rot4 dir = order.sourceTile >= 0 ? Find.WorldGrid.GetRotFromTo(map.Tile, order.sourceTile) : Rot4.Random;
            if (!CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map) && !c.Fogged(map) && map.reachability.CanReachColony(c), map, dir, CellFinder.EdgeRoadChance_Always, out IntVec3 entry)
                && !RCellFinder.TryFindRandomPawnEntryCell(out entry, map, CellFinder.EdgeRoadChance_Always))
            {
                return false;
            }

            if (!map.Biome.AllWildAnimals.Where(k => k.RaceProps.packAnimal).TryRandomElement(out PawnKindDef packKind))
            {
                packKind = PawnKindDefOf.Muffalo;
            }

            var pawns = new List<Pawn>();
            Pawn driver = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, protectorate);
            pawns.Add(driver);

            var animals = new List<Pawn>();
            Pawn current = null;
            float currentMass = 0f;
            foreach (Thing thing in things.OrderByDescending(t => t.GetStatValue(StatDefOf.Mass) * t.stackCount))
            {
                float mass = thing.GetStatValue(StatDefOf.Mass) * thing.stackCount;
                if (current == null || (currentMass + mass > MassUtility.Capacity(current) && animals.Count < MaxPackAnimals))
                {
                    current = PawnGenerator.GeneratePawn(packKind, protectorate);
                    animals.Add(current);
                    currentMass = 0f;
                }
                current.inventory.innerContainer.TryAdd(thing, canMergeWithExistingStacks: true);
                currentMass += mass;
            }
            pawns.AddRange(animals);

            foreach (Pawn pawn in pawns)
            {
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(entry, map, 4);
                GenSpawn.Spawn(pawn, cell, map);
            }
            IntVec3 dropSpot = DropCellFinder.TradeDropSpot(map);
            LordMaker.MakeNewLord(protectorate, new LordJob_DeliverGoods(dropSpot), map, pawns);
            lookAt.Add(driver);
            return true;
        }
    }
}
