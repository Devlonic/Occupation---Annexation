using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// The layout of a town as the player left it: terrain, roofs, buildings and crops inside the town area.
    /// The surrounding land is regenerated from the tile seed, which gives the same natural terrain.
    /// </summary>
    public class MapSnapshot : IExposable
    {
        public const int Margin = 4;

        public IntVec3 mapSize;
        public CellRect rect;
        public List<string> terrainPalette = new List<string>();
        public List<string> roofPalette = new List<string>();
        public byte[] topTerrain;
        public byte[] underTerrain;
        public byte[] roofs;
        public List<BuildingRecord> buildings = new List<BuildingRecord>();
        public List<PlantRecord> plants = new List<PlantRecord>();

        public static MapSnapshot Capture(Map map, CellRect townRect)
        {
            if (townRect.IsEmpty)
            {
                return null;
            }
            var snapshot = new MapSnapshot
            {
                mapSize = map.Size,
                rect = townRect.ExpandedBy(Margin).ClipInsideMap(map)
            };
            snapshot.terrainPalette.Add(string.Empty);
            snapshot.roofPalette.Add(string.Empty);
            var terrainIndex = new Dictionary<TerrainDef, ushort>();
            var roofIndex = new Dictionary<RoofDef, ushort>();

            int count = snapshot.rect.Area;
            var top = new ushort[count];
            var under = new ushort[count];
            var roof = new ushort[count];
            var seen = new HashSet<Thing>();
            int i = 0;
            foreach (IntVec3 cell in snapshot.rect)
            {
                top[i] = PaletteIndex(map.terrainGrid.TerrainAt(cell), snapshot.terrainPalette, terrainIndex);
                under[i] = PaletteIndex(map.terrainGrid.UnderTerrainAt(cell), snapshot.terrainPalette, terrainIndex);
                roof[i] = PaletteIndex(map.roofGrid.RoofAt(cell), snapshot.roofPalette, roofIndex);
                i++;

                List<Thing> things = cell.GetThingList(map);
                for (int k = 0; k < things.Count; k++)
                {
                    Thing thing = things[k];
                    if (!seen.Add(thing))
                    {
                        continue;
                    }
                    if (thing.def.category == ThingCategory.Building && !(thing is Frame) && !thing.def.IsBlueprint)
                    {
                        snapshot.buildings.Add(BuildingRecord.From(thing));
                    }
                    else if (thing is Plant plant && snapshot.rect.Contains(plant.Position))
                    {
                        snapshot.plants.Add(new PlantRecord { def = plant.def.defName, pos = plant.Position, growth = plant.Growth });
                    }
                }
            }
            snapshot.topTerrain = ToBytes(top);
            snapshot.underTerrain = ToBytes(under);
            snapshot.roofs = ToBytes(roof);
            OAMod.DebugLog($"Snapshot of {map}: rect {snapshot.rect}, {snapshot.buildings.Count} buildings, {snapshot.plants.Count} plants.");
            return snapshot;
        }

        /// <summary>
        /// Rebuilds the town area on a freshly generated map. Buildings belong to <paramref name="faction"/>.
        /// </summary>
        public void Restore(Map map, Faction faction)
        {
            if (mapSize != map.Size)
            {
                Log.Warning($"[Occupation & Annexation] Map size changed since the snapshot ({mapSize} -> {map.Size}); restoring what fits.");
            }
            List<TerrainDef> terrains = ResolvePalette<TerrainDef>(terrainPalette);
            List<RoofDef> roofDefs = ResolvePalette<RoofDef>(roofPalette);
            ushort[] top = FromBytes(topTerrain);
            ushort[] under = FromBytes(underTerrain);
            ushort[] roof = FromBytes(roofs);

            int i = 0;
            foreach (IntVec3 cell in rect)
            {
                int index = i++;
                if (!cell.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = cell.GetThingList(map);
                for (int k = things.Count - 1; k >= 0; k--)
                {
                    if (k < things.Count && !(things[k] is Pawn) && !things[k].Destroyed)
                    {
                        things[k].Destroy(DestroyMode.Vanish);
                    }
                }
                TerrainDef topDef = SafeGet(terrains, top, index);
                if (topDef != null)
                {
                    map.terrainGrid.SetTerrain(cell, topDef);
                    map.terrainGrid.SetUnderTerrain(cell, SafeGet(terrains, under, index));
                }
                map.roofGrid.SetRoof(cell, SafeGet(roofDefs, roof, index));
            }

            int failed = 0;
            foreach (BuildingRecord record in buildings)
            {
                if (!record.TrySpawn(map, faction))
                {
                    failed++;
                }
            }
            foreach (PlantRecord record in plants)
            {
                record.TrySpawn(map);
            }
            if (failed > 0)
            {
                OAMod.DebugLog($"{failed} buildings from the snapshot could not be restored (missing defs or blocked cells).");
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapSize, "mapSize");
            Scribe_Values.Look(ref rect, "rect");
            Scribe_Collections.Look(ref terrainPalette, "terrainPalette", LookMode.Value);
            Scribe_Collections.Look(ref roofPalette, "roofPalette", LookMode.Value);
            DataExposeUtility.LookByteArray(ref topTerrain, "topTerrain");
            DataExposeUtility.LookByteArray(ref underTerrain, "underTerrain");
            DataExposeUtility.LookByteArray(ref roofs, "roofs");
            Scribe_Collections.Look(ref buildings, "buildings", LookMode.Deep);
            Scribe_Collections.Look(ref plants, "plants", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                terrainPalette ??= new List<string>();
                roofPalette ??= new List<string>();
                buildings ??= new List<BuildingRecord>();
                plants ??= new List<PlantRecord>();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static ushort PaletteIndex<T>(T def, List<string> palette, Dictionary<T, ushort> index) where T : Def
        {
            if (def == null)
            {
                return 0;
            }
            if (!index.TryGetValue(def, out ushort value))
            {
                value = (ushort)palette.Count;
                palette.Add(def.defName);
                index[def] = value;
            }
            return value;
        }

        private static List<T> ResolvePalette<T>(List<string> palette) where T : Def
        {
            var result = new List<T>(palette.Count);
            foreach (string name in palette)
            {
                result.Add(name.NullOrEmpty() ? null : DefDatabase<T>.GetNamedSilentFail(name));
            }
            return result;
        }

        private static T SafeGet<T>(List<T> palette, ushort[] indices, int index) where T : class
        {
            if (indices == null || index >= indices.Length)
            {
                return null;
            }
            int paletteIndex = indices[index];
            return paletteIndex < palette.Count ? palette[paletteIndex] : null;
        }

        private static byte[] ToBytes(ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static ushort[] FromBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            var values = new ushort[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 2);
            return values;
        }
    }

    public class BuildingRecord : IExposable
    {
        public string def;
        public string stuff;
        public IntVec3 pos;
        public Rot4 rot;
        public int hitPoints = -1;
        public int quality = -1;
        /// <summary>Ruins, wrecks and natural rock had no owner and keep having none.</summary>
        public bool unowned;

        public static BuildingRecord From(Thing thing)
        {
            var record = new BuildingRecord
            {
                def = thing.def.defName,
                stuff = thing.Stuff?.defName,
                pos = thing.Position,
                rot = thing.Rotation,
                hitPoints = thing.def.useHitPoints ? thing.HitPoints : -1,
                unowned = thing.Faction == null
            };
            if (thing.TryGetQuality(out QualityCategory qc))
            {
                record.quality = (int)qc;
            }
            return record;
        }

        public bool TrySpawn(Map map, Faction faction)
        {
            ThingDef thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (thingDef == null || !pos.InBounds(map))
            {
                return false;
            }
            ThingDef stuffDef = stuff.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuff);
            if (thingDef.MadeFromStuff && stuffDef == null)
            {
                stuffDef = GenStuff.DefaultStuffFor(thingDef);
            }
            if (!thingDef.MadeFromStuff)
            {
                stuffDef = null;
            }
            try
            {
                if (!GenAdj.OccupiedRect(pos, rot, thingDef.size).InBounds(map))
                {
                    return false;
                }
                Thing thing = ThingMaker.MakeThing(thingDef, stuffDef);
                if (thingDef.CanHaveFaction && faction != null && !unowned)
                {
                    thing.SetFaction(faction);
                }
                if (hitPoints > 0 && thingDef.useHitPoints)
                {
                    thing.HitPoints = Mathf.Clamp(hitPoints, 1, thing.MaxHitPoints);
                }
                if (quality >= 0)
                {
                    thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)quality, ArtGenerationContext.Outsider);
                }
                GenSpawn.Spawn(thing, pos, map, rot, WipeMode.Vanish);
                return true;
            }
            catch (Exception e)
            {
                OAMod.DebugLog($"Could not restore {def} at {pos}: {e.Message}");
                return false;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref def, "def");
            Scribe_Values.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref pos, "pos");
            Scribe_Values.Look(ref rot, "rot");
            Scribe_Values.Look(ref hitPoints, "hp", -1);
            Scribe_Values.Look(ref quality, "q", -1);
            Scribe_Values.Look(ref unowned, "unowned", false);
        }
    }

    public class PlantRecord : IExposable
    {
        public string def;
        public IntVec3 pos;
        public float growth;

        public void TrySpawn(Map map)
        {
            ThingDef plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (plantDef?.plant == null || !pos.InBounds(map) || pos.GetPlant(map) != null || pos.GetEdifice(map) != null)
            {
                return;
            }
            var plant = (Plant)ThingMaker.MakeThing(plantDef);
            plant.Growth = Mathf.Clamp01(growth);
            GenSpawn.Spawn(plant, pos, map);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref def, "def");
            Scribe_Values.Look(ref pos, "pos");
            Scribe_Values.Look(ref growth, "growth", 0f);
        }
    }
}
