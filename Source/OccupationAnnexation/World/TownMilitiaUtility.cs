using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// A town militia: townsfolk trained to take weapons from the town's stockpile and fight off attempts to retake
    /// the town. Training costs some production; a disloyal town disbands it.
    /// </summary>
    public static class TownMilitiaUtility
    {
        public const float MinLoyalty = 40f;
        public const float DisbandLoyalty = 25f;
        public const float ProductionFactor = 0.9f;
        private const float StrengthPerMember = 0.5f;

        public static bool IsMilitiaWeapon(ThingDef def)
        {
            return def.IsWeapon && def.equipmentType == EquipmentType.Primary;
        }

        public static int WeaponsInStock(OccupiedSettlement town)
        {
            return town.Stock.Where(t => IsMilitiaWeapon(t.def)).Sum(t => t.stackCount);
        }

        /// <summary>Adults who would take up arms: no more than there are weapons in the stockpile.</summary>
        public static int MilitiaSize(OccupiedSettlement town)
        {
            return town.militia ? Mathf.Min(town.WorkerCount, WeaponsInStock(town)) : 0;
        }

        /// <summary>What the militia adds to the town's defense when the fight is resolved abstractly.</summary>
        public static float Strength(OccupiedSettlement town)
        {
            return MilitiaSize(town) * StrengthPerMember * (town.loyalty / 100f);
        }

        public static void DailyCheck(OccupiedSettlement town)
        {
            if (town.militia && town.loyalty < DisbandLoyalty)
            {
                town.militia = false;
                Messages.Message("OA_MessageMilitiaDisbanded".Translate(town.Label), town, MessageTypeDefOf.NegativeEvent);
            }
        }

        /// <summary>
        /// On the town map: adults take weapons lying in the town and defend it. Their weapons go back to the
        /// stockpile when everyone leaves.
        /// </summary>
        public static List<Pawn> ArmMilitia(OccupiedSettlement town, Map map)
        {
            var armed = new List<Pawn>();
            if (!town.militia)
            {
                return armed;
            }
            CellRect rect = (town.townRect.IsEmpty ? CellRect.WholeMap(map) : town.townRect.ExpandedBy(3)).ClipInsideMap(map);
            List<Thing> weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)
                .Where(t => t.Spawned && IsMilitiaWeapon(t.def) && rect.Contains(t.Position) && t is ThingWithComps)
                .OrderByDescending(t => t.MarketValue)
                .ToList();
            List<Pawn> volunteers = map.mapPawns.SpawnedPawnsInFaction(town.Faction)
                .Where(p => p.RaceProps.Humanlike && !p.Downed && p.DevelopmentalStage.Adult() && p.equipment != null && p.equipment.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent))
                .ToList();
            int count = Mathf.Min(weapons.Count, volunteers.Count);
            for (int i = 0; i < count; i++)
            {
                var weapon = (ThingWithComps)weapons[i];
                if (weapon.stackCount > 1)
                {
                    weapon = (ThingWithComps)weapon.SplitOff(1);
                }
                if (weapon.Spawned)
                {
                    weapon.DeSpawn();
                }
                Pawn pawn = volunteers[i];
                pawn.equipment.AddEquipment(weapon);
                CECompat.TopUpMagazine(weapon);
                pawn.GetLord()?.RemovePawn(pawn);
                armed.Add(pawn);
            }
            if (armed.Count > 0)
            {
                LordMaker.MakeNewLord(town.Faction, new LordJob_DefendBase(town.Faction, rect.CenterCell), map, armed);
                Messages.Message("OA_MessageMilitiaArmed".Translate(town.Label, armed.Count), new LookTargets(armed), MessageTypeDefOf.NeutralEvent);
            }
            OAMod.DebugLog($"Militia of {town.Label}: {armed.Count} armed ({weapons.Count} weapons, {volunteers.Count} volunteers).");
            return armed;
        }

        /// <summary>
        /// Townsfolk hand their weapons back to the stockpile when the town map is collected.
        /// </summary>
        public static void ReturnWeapons(OccupiedSettlement town, Pawn pawn)
        {
            if (pawn.equipment == null)
            {
                return;
            }
            foreach (ThingWithComps equipment in pawn.equipment.AllEquipmentListForReading.ToList())
            {
                pawn.equipment.Remove(equipment);
                town.Store(equipment);
            }
        }
    }
}
