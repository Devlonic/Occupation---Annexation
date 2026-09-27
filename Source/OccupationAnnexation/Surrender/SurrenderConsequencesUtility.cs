using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// How the colony and the world react to what happens to those who surrendered: colonists' moods follow their
    /// ideoligion's view of executions (or their traits without Ideology), and other factions hear of killings.
    /// </summary>
    public static class SurrenderConsequencesUtility
    {
        public enum View
        {
            Abhorrent,
            Horrible,
            Uneasy,
            Indifferent,
            Approving
        }

        /// <summary>
        /// A psychopath never cares. Otherwise the ideoligion's execution precept decides, and without one
        /// the traits: bloodlust approves, kind people are horrified, anyone else is uneasy.
        /// </summary>
        public static View ViewOf(Pawn pawn)
        {
            if (pawn.story?.traits != null && pawn.story.traits.HasTrait(TraitDefOf.Psychopath))
            {
                return View.Indifferent;
            }
            if (ModsConfig.IdeologyActive && pawn.Ideo != null)
            {
                foreach (Precept precept in pawn.Ideo.PreceptsListForReading)
                {
                    switch (precept.def.defName)
                    {
                        case "Execution_Abhorrent":
                            return View.Abhorrent;
                        case "Execution_Horrible":
                            return View.Horrible;
                        case "Execution_HorribleIfInnocent":
                            return View.Uneasy;
                        case "Execution_DontCare":
                        case "Execution_RespectedIfGuilty":
                            return View.Indifferent;
                        case "Execution_Required":
                            return View.Approving;
                    }
                }
            }
            if (pawn.story?.traits != null)
            {
                if (pawn.story.traits.HasTrait(TraitDefOf.Bloodlust))
                {
                    return View.Approving;
                }
                if (pawn.story.traits.HasTrait(TraitDefOf.Kind))
                {
                    return View.Horrible;
                }
            }
            return View.Uneasy;
        }

        /// <summary>Stage of <see cref="OA_DefOf.OA_KilledSurrendered"/>, or -1 for no thought.</summary>
        public static int KilledStage(View view)
        {
            switch (view)
            {
                case View.Abhorrent:
                    return 0;
                case View.Horrible:
                    return 1;
                case View.Uneasy:
                    return 2;
                case View.Approving:
                    return 3;
                default:
                    return -1;
            }
        }

        /// <summary>Stage of <see cref="OA_DefOf.OA_SparedSurrendered"/>, or -1 for no thought.</summary>
        public static int SparedStage(View view)
        {
            switch (view)
            {
                case View.Abhorrent:
                case View.Horrible:
                    return 0;
                case View.Uneasy:
                case View.Indifferent:
                    return 1;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// Someone who had surrendered was killed by the player's side: the colonists who saw it react,
        /// and word reaches the other factions.
        /// </summary>
        public static void Notify_SurrenderedKilled(Pawn victim)
        {
            Map map = victim.MapHeld;
            if (map != null)
            {
                foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned)
                {
                    GiveThought(colonist, OA_DefOf.OA_KilledSurrendered, KilledStage(ViewOf(colonist)));
                }
            }
            LoseGoodwill(victim);
        }

        /// <summary>
        /// Everyone who took part in a conquest where nobody who surrendered was killed.
        /// </summary>
        public static void GiveSparedThoughts(IEnumerable<Pawn> colonists)
        {
            foreach (Pawn colonist in colonists)
            {
                if (colonist != null && !colonist.Dead && colonist.IsFreeColonist)
                {
                    GiveThought(colonist, OA_DefOf.OA_SparedSurrendered, SparedStage(ViewOf(colonist)));
                }
            }
        }

        private static void GiveThought(Pawn pawn, ThoughtDef def, int stage)
        {
            if (stage < 0 || pawn.needs?.mood?.thoughts?.memories == null)
            {
                return;
            }
            pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(def, stage));
        }

        /// <summary>
        /// Factions at peace with the colony think less of it. Enemies already do; the Protectorate does not judge.
        /// </summary>
        private static void LoseGoodwill(Pawn victim)
        {
            int loss = OAMod.Settings.goodwillLossPerKilledSurrendered;
            if (loss <= 0)
            {
                return;
            }
            var names = new List<string>();
            foreach (Faction faction in Find.FactionManager.AllFactionsListForReading)
            {
                if (faction.IsPlayer || faction.Hidden || faction.defeated || faction == victim.Faction || ProtectorateUtility.IsProtectorate(faction)
                    || !faction.def.humanlikeFaction || faction.HostileTo(Faction.OfPlayer))
                {
                    continue;
                }
                if (Faction.OfPlayer.TryAffectGoodwillWith(faction, -loss, canSendMessage: false, canSendHostilityLetter: true, OA_DefOf.OA_SurrenderedKilled))
                {
                    names.Add(faction.Name);
                }
            }
            if (names.Count > 0)
            {
                Messages.Message("OA_MessageGoodwillKilledSurrendered".Translate((-loss).ToString(), names.ToCommaList()), victim, MessageTypeDefOf.NegativeEvent);
            }
        }
    }
}
