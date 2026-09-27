using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    public static class OccupationUtility
    {
        private const float LoyaltyLossPerPrisonerTaken = 2f;
        public const float LoyaltyPerTend = 1.5f;
        public const float MaxLoyaltyFromTending = 15f;

        /// <summary>
        /// Replaces the vanilla "settlement destroyed" outcome: the town survives as an occupied settlement
        /// owned by the Protectorate. Mirrors SettlementDefeatUtility.CheckDefeated side effects.
        /// </summary>
        public static OccupiedSettlement Occupy(Settlement settlement)
        {
            Map map = settlement.Map;
            Faction original = settlement.Faction;
            Faction protectorate = ProtectorateUtility.GetOrCreate();
            int now = Find.TickManager.TicksGame;

            IdeoUtility.Notify_PlayerRaidedSomeone(map.mapPawns.FreeColonistsSpawned);

            var town = (OccupiedSettlement)WorldObjectMaker.MakeWorldObject(OA_DefOf.OA_OccupiedSettlement);
            town.Tile = settlement.Tile;
            town.SetFaction(protectorate);
            town.Name = settlement.Name;
            town.originalFaction = original;
            town.originalFactionDef = original.def;
            town.occupiedTick = now;
            town.nextDayTick = now + GenDate.TicksPerDay;
            town.doorsAlwaysOpenForPlayerPawns = true;
            town.townRect = ComputeTownRect(map, original);
            // The same area the snapshot keeps, so the first leave does not count anything new.
            town.profile = DetermineProfile(map, town.townRect.ExpandedBy(MapSnapshot.Margin).ClipInsideMap(map));
            town.loyalty = Mathf.Clamp(OAMod.Settings.startingLoyalty, 0f, 100f);
            Find.WorldObjects.Add(town);

            var text = new StringBuilder();
            text.Append("OA_LetterOccupied".Translate(settlement.Label, original.Name, protectorate.Name));
            if (!HasAnyOtherBase(settlement))
            {
                original.defeated = true;
                text.AppendLine();
                text.AppendLine();
                text.Append("LetterFactionBaseDefeated_FactionDestroyed".Translate(original.Name));
            }
            foreach (Faction other in Find.FactionManager.AllFactions)
            {
                if (other.Hidden || other.IsPlayer || other == original || other == protectorate || !other.HostileTo(original))
                {
                    continue;
                }
                FactionRelationKind before = other.PlayerRelationKind;
                Faction.OfPlayer.TryAffectGoodwillWith(other, 20, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.DestroyedEnemyBase);
                text.AppendLine();
                text.AppendLine();
                text.Append("RelationsWith".Translate(other.Name) + ": " + 20.ToStringWithSign());
                other.TryAppendRelationKindChangedInfo(text, before, other.PlayerRelationKind);
            }

            map.info.parent = town;
            settlement.Destroy();

            // The town's buildings and animals now belong to the Protectorate: turrets and doors turn friendly.
            foreach (Building building in map.listerBuildings.allBuildingsNonColonist.ToList())
            {
                if (building.Faction == original)
                {
                    building.SetFaction(protectorate);
                }
            }
            foreach (Pawn pawn in map.mapPawns.SpawnedPawnsInFaction(original).ToList())
            {
                if (!pawn.RaceProps.Humanlike)
                {
                    pawn.GetLord()?.RemovePawn(pawn);
                    pawn.SetFaction(protectorate);
                }
            }

            List<Pawn> colonists = map.mapPawns.FreeColonists.ToList();
            if (colonists.Count > 0)
            {
                TaleRecorder.RecordTale(TaleDefOf.CaravanAssaultSuccessful, colonists.RandomElement());
            }
            Find.LetterStack.ReceiveLetter("OA_LetterOccupiedLabel".Translate(settlement.Label), text.ToString(), LetterDefOf.PositiveEvent, town, original);
            OAMod.DebugLog($"Occupied {town.Label}: rect {town.townRect}, profile {town.profile.Select(p => p.def.defName + "=" + p.share.ToString("F2")).ToCommaList()}");
            return town;
        }

        /// <summary>
        /// Called right before the town's map is removed: remembers the layout, moves the locals and the
        /// loose items into the world object, and applies the loyalty cost of what happened during the stay.
        /// </summary>
        public static void CollectFromMap(OccupiedSettlement town, Map map)
        {
            Faction protectorate = town.Faction;

            try
            {
                town.snapshot = MapSnapshot.Capture(map, town.townRect);
            }
            catch (Exception e)
            {
                Log.Error("[Occupation & Annexation] Failed to snapshot " + town.Label + ": " + e);
            }
            if (town.snapshot != null)
            {
                town.housing = PopulationUtility.CountHousing(town.snapshot);
                UpdateProfile(town, map, town.snapshot.rect);
            }

            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale != null)
            {
                float loss = morale.surrenderedKilled * OAMod.Settings.loyaltyLossPerKilledSurrendered + morale.prisonersTaken * LoyaltyLossPerPrisonerTaken;
                if (loss > 0f)
                {
                    town.loyalty = Mathf.Clamp(town.loyalty - loss, 0f, 100f);
                    Messages.Message("OA_MessageLoyaltyLostFromStay".Translate(town.Label, loss.ToString("F0")), town, MessageTypeDefOf.NegativeEvent);
                }
                float gain = Mathf.Min(MaxLoyaltyFromTending, morale.tendedByPlayer * LoyaltyPerTend);
                if (gain > 0f)
                {
                    town.loyalty = Mathf.Clamp(town.loyalty + gain, 0f, 100f);
                    Messages.Message("OA_MessageLoyaltyFromTending".Translate(town.Label, gain.ToString("0.#")), town, MessageTypeDefOf.PositiveEvent);
                }
                if (morale.capitulated && morale.surrenderedKilled == 0)
                {
                    SurrenderConsequencesUtility.GiveSparedThoughts(morale.participants);
                }
                morale.surrenderedKilled = 0;
                morale.prisonersTaken = 0;
                morale.tendedByPlayer = 0;
                morale.participants.Clear();
            }

            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (pawn.Dead || !pawn.Spawned)
                {
                    continue;
                }
                bool local = pawn.Faction == protectorate || (town.originalFaction != null && pawn.Faction == town.originalFaction);
                bool leftBehindPrisoner = pawn.IsPrisonerOfColony && (pawn.Faction == protectorate || pawn.Faction == town.originalFaction);
                if (!local && !leftBehindPrisoner)
                {
                    continue;
                }
                if (leftBehindPrisoner)
                {
                    pawn.guest.SetGuestStatus(null);
                }
                if (pawn.InMentalState)
                {
                    pawn.mindState.mentalStateHandler.Reset();
                }
                TownMilitiaUtility.ReturnWeapons(town, pawn);
                pawn.GetLord()?.RemovePawn(pawn);
                if (pawn.Faction != protectorate)
                {
                    pawn.SetFaction(protectorate);
                }
                pawn.jobs?.StopAll();
                pawn.DeSpawn();
                town.Store(pawn);
            }

            CellRect rect = town.townRect.IsEmpty ? CellRect.WholeMap(map) : town.townRect.ExpandedBy(3).ClipInsideMap(map);
            var items = new List<Thing>();
            foreach (IntVec3 cell in rect)
            {
                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing.def.category != ThingCategory.Item || thing is Corpse || !thing.def.EverHaulable)
                    {
                        continue;
                    }
                    if (thing.def.thingCategories != null && thing.def.IsWithinCategory(ThingCategoryDefOf.Chunks))
                    {
                        continue;
                    }
                    items.Add(thing);
                }
            }
            foreach (Thing thing in items)
            {
                thing.DeSpawn();
                town.Store(thing);
            }
            OAMod.DebugLog($"Collected {town.PopulationCount} locals and {items.Count} item stacks into {town.Label}.");
        }

        /// <summary>
        /// What the town produces follows what stands in it now: workshops and fields built or lost during a visit count.
        /// </summary>
        public static void UpdateProfile(OccupiedSettlement town, Map map, CellRect rect)
        {
            List<ProductionShare> updated = DetermineProfile(map, rect);
            if (updated.Count == 0)
            {
                return;
            }
            bool changed = updated.Count != town.profile.Count || updated.Any(u =>
            {
                ProductionShare old = town.profile.FirstOrDefault(p => p.def == u.def);
                return old == null || Mathf.Abs(old.share - u.share) >= 0.05f;
            });
            town.profile = updated;
            if (changed)
            {
                Messages.Message("OA_MessageProfileChanged".Translate(town.Label, ProfileText(town)), town, MessageTypeDefOf.NeutralEvent);
            }
        }

        public static string ProfileText(OccupiedSettlement town)
        {
            return town.profile.Select(p => p.def.label + " " + p.share.ToStringPercent()).ToCommaList();
        }

        public static CellRect ComputeTownRect(Map map, Faction faction)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (Building building in map.listerBuildings.allBuildingsNonColonist)
            {
                if (building.Faction != faction)
                {
                    continue;
                }
                CellRect occupied = building.OccupiedRect();
                minX = Mathf.Min(minX, occupied.minX);
                minZ = Mathf.Min(minZ, occupied.minZ);
                maxX = Mathf.Max(maxX, occupied.maxX);
                maxZ = Mathf.Max(maxZ, occupied.maxZ);
            }
            if (minX == int.MaxValue)
            {
                return CellRect.CenteredOn(map.Center, 20).ClipInsideMap(map);
            }
            return CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(2).ClipInsideMap(map);
        }

        /// <summary>
        /// Picks what the town produces from what it had when it was captured: workbenches, kitchens, fields.
        /// </summary>
        public static List<ProductionShare> DetermineProfile(Map map, CellRect rect)
        {
            var buildingCounts = new Dictionary<string, int>();
            int crops = 0;
            foreach (IntVec3 cell in rect)
            {
                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing is Building && thing.Position == cell)
                    {
                        buildingCounts.TryGetValue(thing.def.defName, out int count);
                        buildingCounts[thing.def.defName] = count + 1;
                    }
                    else if (thing is Plant plant && plant.def.plant.Sowable && !plant.def.plant.IsTree && plant.def.plant.harvestedThingDef != null)
                    {
                        crops++;
                    }
                }
            }

            var scores = new List<ProductionShare>();
            foreach (ProductionProfileDef def in DefDatabase<ProductionProfileDef>.AllDefsListForReading)
            {
                float score = def.baseWeight;
                foreach (string building in def.indicatorBuildings)
                {
                    if (buildingCounts.TryGetValue(building, out int count))
                    {
                        score += count * def.weightPerBuilding;
                    }
                }
                if (def.indicatorFields)
                {
                    score += crops * def.weightPerCrop;
                }
                if (score > 0f)
                {
                    scores.Add(new ProductionShare(def, score));
                }
            }
            scores = scores.OrderByDescending(s => s.share).Take(4).ToList();
            float total = scores.Sum(s => s.share);
            foreach (ProductionShare share in scores)
            {
                share.share /= total;
            }
            return scores;
        }

        private static bool HasAnyOtherBase(Settlement settlement)
        {
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].Faction == settlement.Faction && settlements[i] != settlement)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
