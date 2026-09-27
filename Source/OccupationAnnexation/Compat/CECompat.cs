using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Soft integration with Combat Extended. Nothing here references CE at compile time.
    /// </summary>
    public static class CECompat
    {
        public static readonly Type CompSuppressableType = AccessTools.TypeByName("CombatExtended.CompSuppressable");
        public static readonly Type AmmoDefType = AccessTools.TypeByName("CombatExtended.AmmoDef");
        public static readonly Type VerbLaunchProjectileType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
        public static readonly Type CompAmmoUserType = AccessTools.TypeByName("CombatExtended.CompAmmoUser");

        private static readonly FieldInfo isSuppressedField;
        private static readonly MethodInfo isHunkeringGetter;
        private static readonly MethodInfo resetAmmoCount = CompAmmoUserType == null ? null : AccessTools.Method(CompAmmoUserType, "ResetAmmoCount");

        public static bool Active => CompSuppressableType != null;

        static CECompat()
        {
            if (CompSuppressableType == null)
            {
                return;
            }
            isSuppressedField = AccessTools.Field(CompSuppressableType, "isSuppressed");
            isHunkeringGetter = AccessTools.PropertyGetter(CompSuppressableType, "IsHunkering");
        }

        public static ThingComp GetSuppressable(Pawn pawn)
        {
            if (!Active)
            {
                return null;
            }
            List<ThingComp> comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (CompSuppressableType.IsInstanceOfType(comps[i]))
                {
                    return comps[i];
                }
            }
            return null;
        }

        public static bool IsSuppressedOrHunkering(Pawn pawn)
        {
            ThingComp comp = GetSuppressable(pawn);
            if (comp == null)
            {
                return false;
            }
            try
            {
                if (isSuppressedField != null && (bool)isSuppressedField.GetValue(comp))
                {
                    return true;
                }
                if (isHunkeringGetter != null && (bool)isHunkeringGetter.Invoke(comp, null))
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Occupation & Annexation] Failed to read CE suppression: " + e, 0x4F41_0001);
            }
            return false;
        }

        public static bool IsAmmo(Thing thing)
        {
            return AmmoDefType != null && AmmoDefType.IsInstanceOfType(thing.def);
        }

        public static bool IsProjectileVerb(Verb verb)
        {
            return VerbLaunchProjectileType != null && VerbLaunchProjectileType.IsInstanceOfType(verb);
        }

        /// <summary>
        /// A gun taken from a stockpile has an empty magazine under CE; the militia gets one full magazine.
        /// </summary>
        public static void TopUpMagazine(ThingWithComps weapon)
        {
            if (CompAmmoUserType == null || weapon == null)
            {
                return;
            }
            foreach (ThingComp comp in weapon.AllComps)
            {
                if (!CompAmmoUserType.IsInstanceOfType(comp))
                {
                    continue;
                }
                try
                {
                    resetAmmoCount?.Invoke(comp, new object[] { null });
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[Occupation & Annexation] Failed to load a CE magazine: " + e, 0x4F41_0002);
                }
                return;
            }
        }
    }
}
