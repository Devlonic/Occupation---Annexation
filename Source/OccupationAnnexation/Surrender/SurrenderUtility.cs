using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    public static class SurrenderUtility
    {
        public static bool IsSurrendered(Pawn pawn)
        {
            return pawn != null && pawn.MentalStateDef == OA_DefOf.OA_Surrendered;
        }

        /// <summary>
        /// Puts a pawn into the surrendered mental state. Weapons are dropped in MentalState_Surrendered.PostStart.
        /// </summary>
        public static bool TrySurrender(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || !pawn.Spawned || IsSurrendered(pawn))
            {
                return false;
            }
            if (pawn.IsPrisoner || pawn.IsSlave || pawn.mindState?.mentalStateHandler == null)
            {
                return false;
            }
            return pawn.mindState.mentalStateHandler.TryStartMentalState(OA_DefOf.OA_Surrendered, forced: true, forceWake: true);
        }

        /// <summary>
        /// Drops everything a surrendered pawn could fight with: equipment, spare weapons and (CE) ammo.
        /// Items stay unforbidden so the player can collect them.
        /// </summary>
        public static void DropWeapons(Pawn pawn)
        {
            if (!pawn.Spawned)
            {
                return;
            }
            Map map = pawn.Map;
            IntVec3 pos = pawn.Position;
            if (pawn.carryTracker?.CarriedThing != null)
            {
                pawn.carryTracker.TryDropCarriedThing(pos, ThingPlaceMode.Near, out _);
            }
            pawn.equipment?.DropAllEquipment(pos, forbid: false);
            if (pawn.inventory == null)
            {
                return;
            }
            ThingOwner<Thing> inventory = pawn.inventory.innerContainer;
            for (int i = inventory.Count - 1; i >= 0; i--)
            {
                Thing thing = inventory[i];
                if (thing.def.IsWeapon || CECompat.IsAmmo(thing))
                {
                    inventory.TryDrop(thing, pos, map, ThingPlaceMode.Near, out Thing _);
                }
            }
        }

        public static bool IsDefenderOf(Pawn pawn, Faction faction)
        {
            return pawn.Faction == faction && pawn.RaceProps.Humanlike && !pawn.Dead && !pawn.IsPrisoner && !pawn.IsSlave;
        }

        /// <summary>
        /// Every surviving humanlike of the faction on the map lays down arms.
        /// </summary>
        public static void Capitulate(Map map, Faction faction)
        {
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale == null || morale.capitulated)
            {
                return;
            }
            morale.capitulated = true;
            morale.capitulationTick = Find.TickManager.TicksGame;

            List<Pawn> survivors = map.mapPawns.SpawnedPawnsInFaction(faction).Where(p => IsDefenderOf(p, faction)).ToList();
            var surrendered = new List<Pawn>();
            foreach (Pawn pawn in survivors)
            {
                if (TrySurrender(pawn) || IsSurrendered(pawn))
                {
                    surrendered.Add(pawn);
                }
            }

            foreach (Pawn pawn in surrendered)
            {
                pawn.GetLord()?.RemovePawn(pawn);
            }
            if (surrendered.Count > 0)
            {
                LordMaker.MakeNewLord(faction, new LordJob_Capitulated(), map, surrendered);
            }

            string settlementLabel = map.Parent?.LabelCap ?? faction.Name;
            Find.LetterStack.ReceiveLetter(
                "OA_LetterCapitulationLabel".Translate(settlementLabel),
                "OA_LetterCapitulation".Translate(settlementLabel, faction.Name, surrendered.Count),
                LetterDefOf.PositiveEvent,
                new LookTargets(surrendered),
                faction);
            OAMod.DebugLog($"{settlementLabel} capitulated, {surrendered.Count} pawns surrendered.");
        }

        /// <summary>
        /// Mirrors the private SettlementDefeatUtility.IsDefeated.
        /// </summary>
        public static bool IsDefeated(Map map, Faction faction)
        {
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(faction);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i].RaceProps.Humanlike && GenHostility.IsActiveThreatToPlayer(pawns[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool AnyLivingDefenders(Map map, Faction faction)
        {
            return map.mapPawns.SpawnedPawnsInFaction(faction).Any(p => IsDefenderOf(p, faction));
        }

        public static bool IsHostileSettlementMap(Map map, out Settlement settlement)
        {
            settlement = map?.Parent as Settlement;
            return settlement != null && settlement.Faction != null && !settlement.Faction.IsPlayer && settlement.Faction.HostileTo(Faction.OfPlayer);
        }
    }
}
