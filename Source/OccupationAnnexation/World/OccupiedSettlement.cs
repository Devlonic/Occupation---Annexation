using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    public enum OccupationState
    {
        Occupied,
        Annexed
    }

    public enum TaxLevel
    {
        Low,
        Normal,
        High
    }

    /// <summary>
    /// A conquered town on the world map. While nobody is there it has no map: its population, garrison and
    /// stockpile are held in <see cref="contents"/> and the economy runs abstractly once a day.
    /// Visiting regenerates the map from <see cref="snapshot"/>.
    /// </summary>
    [StaticConstructorOnStartup]
    public class OccupiedSettlement : MapParent, ISuspendableThingHolder, IRenameable
    {
        private static readonly Texture2D AnnexIcon = ContentFinder<Texture2D>.Get("UI/Commands/Settle");
        private static readonly Texture2D TaxIcon = ContentFinder<Texture2D>.Get("UI/Commands/SellableItems");
        private static readonly Texture2D DeliveryIcon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter");
        private static readonly Texture2D TakeIcon = ContentFinder<Texture2D>.Get("UI/Commands/AddToCaravan");
        private static readonly Texture2D GiftIcon = ContentFinder<Texture2D>.Get("UI/Commands/OfferGifts");
        private static readonly Texture2D GarrisonAddIcon = ContentFinder<Texture2D>.Get("UI/Commands/Draft");
        private static readonly Texture2D GarrisonRemoveIcon = ContentFinder<Texture2D>.Get("UI/Commands/RemoveFromCaravan");
        private static readonly Texture2D AbandonIcon = ContentFinder<Texture2D>.Get("UI/Commands/AbandonHome");
        private static readonly Texture2D EnterIcon = ContentFinder<Texture2D>.Get("UI/Commands/ShowMap");
        private static readonly Texture2D EnemyIcon = ContentFinder<Texture2D>.Get("UI/Commands/Attack");

        public OccupationState state = OccupationState.Occupied;
        private string nameInt;
        public Faction originalFaction;
        public FactionDef originalFactionDef;
        public int occupiedTick;
        public int annexedTick = -1;
        public float loyalty;
        public TaxLevel tax = TaxLevel.Normal;
        public ThingOwner<Thing> contents;
        public List<ProductionShare> profile = new List<ProductionShare>();
        public Dictionary<ThingDef, float> productionProgress = new Dictionary<ThingDef, float>();
        public CellRect townRect = CellRect.Empty;
        public MapSnapshot snapshot;
        public int nextDayTick = -1;
        public int visits;
        public bool annexReadyNotified;
        public bool stockpileFullNotified;
        public int lowLoyaltyDays;
        public int retakeAttackTick = -1;

        private Material cachedMat;
        private List<ThingDef> tmpProgressKeys;
        private List<float> tmpProgressValues;

        public OccupiedSettlement()
        {
            contents = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        // ------------------------------------------------------------------ contents

        /// <summary>Everyone living in the town: people and their animals.</summary>
        public IEnumerable<Pawn> Residents
        {
            get
            {
                for (int i = 0; i < contents.Count; i++)
                {
                    if (contents[i] is Pawn pawn && pawn.Faction != Faction.OfPlayer)
                    {
                        yield return pawn;
                    }
                }
            }
        }

        /// <summary>The townsfolk (humanlike residents).</summary>
        public IEnumerable<Pawn> Population => Residents.Where(p => p.RaceProps.Humanlike);

        public IEnumerable<Pawn> Garrison
        {
            get
            {
                for (int i = 0; i < contents.Count; i++)
                {
                    if (contents[i] is Pawn pawn && pawn.Faction == Faction.OfPlayer)
                    {
                        yield return pawn;
                    }
                }
            }
        }

        public IEnumerable<Thing> Stock
        {
            get
            {
                for (int i = 0; i < contents.Count; i++)
                {
                    if (!(contents[i] is Pawn))
                    {
                        yield return contents[i];
                    }
                }
            }
        }

        public int PopulationCount => Population.Count();

        public int GarrisonCount => Garrison.Count();

        public int WorkerCount => Population.Count(p => !p.Downed && p.ageTracker.AgeBiologicalYears >= 13);

        public float StockValue => Stock.Sum(t => t.MarketValue * t.stackCount);

        public float StockCap => OAMod.Settings.stockpileCapPerPawn * Mathf.Max(1, PopulationCount);

        public TechLevel TechLevel => originalFactionDef?.techLevel ?? TechLevel.Industrial;

        public float DaysOccupied => (Find.TickManager.TicksGame - occupiedTick) / (float)GenDate.TicksPerDay;

        public bool IsContentsSuspended => true;

        public new ThingOwner GetDirectlyHeldThings() => contents;

        public override void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, contents);
            if (HasMap)
            {
                outChildren.Add(Map);
            }
        }

        /// <summary>
        /// Stores a pawn or item. Pawns are taken out of world pawns so they are saved exactly once (here).
        /// </summary>
        public void Store(Thing thing)
        {
            if (thing is Pawn pawn)
            {
                if (Find.WorldPawns.Contains(pawn))
                {
                    Find.WorldPawns.RemovePawn(pawn);
                }
                // A despawned pawn loses its native verbs; a leftover attack cooldown would point at a verb that is never saved.
                pawn.stances?.CancelBusyStanceHard();
            }
            thing.holdingOwner?.Remove(thing);
            if (!contents.TryAdd(thing, canMergeWithExistingStacks: true))
            {
                Log.Warning("[Occupation & Annexation] Could not store " + thing + " in " + Label);
            }
        }

        public void AddStock(ThingDef def, int count)
        {
            while (count > 0)
            {
                int stack = Mathf.Min(count, def.stackLimit);
                Thing thing = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                thing.stackCount = stack;
                contents.TryAdd(thing, canMergeWithExistingStacks: true);
                count -= stack;
            }
        }

        // ------------------------------------------------------------------ naming and drawing

        public string RenamableLabel
        {
            get => nameInt ?? BaseLabel;
            set => nameInt = value;
        }

        public string BaseLabel => def.label;

        public string InspectLabel => Label;

        public string Name
        {
            get => nameInt;
            set => nameInt = value;
        }

        public override string Label => nameInt ?? base.Label;

        public override bool HasName => !nameInt.NullOrEmpty();

        public override Material Material
        {
            get
            {
                if (cachedMat == null)
                {
                    string path = originalFactionDef?.settlementTexturePath;
                    if (path.NullOrEmpty())
                    {
                        path = "World/WorldObjects/DefaultSettlement";
                    }
                    Color color = Faction?.Color ?? Color.white;
                    cachedMat = MaterialPool.MatFrom(path, ShaderDatabase.WorldOverlayTransparentLit, color, WorldMaterials.WorldObjectRenderQueue);
                }
                return cachedMat;
            }
        }

        public override Texture2D ExpandingIcon => originalFactionDef?.FactionIcon ?? base.ExpandingIcon;

        public override Color ExpandingIconColor => Faction?.Color ?? base.ExpandingIconColor;

        // ------------------------------------------------------------------ lifecycle

        public override MapGeneratorDef MapGeneratorDef => OA_DefOf.OA_OccupiedTown;

        protected override bool UseGenericEnterMapFloatMenuOption => false;

        public override void Tick()
        {
            base.Tick();
            if (Destroyed)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (nextDayTick < 0)
            {
                nextDayTick = now + GenDate.TicksPerDay;
            }
            if (now >= nextDayTick)
            {
                nextDayTick = now + GenDate.TicksPerDay;
                EconomyUtility.DailyTick(this);
            }
            if (retakeAttackTick >= 0 && now >= retakeAttackTick && !Destroyed)
            {
                TownEventsUtility.ResolveRetake(this);
            }
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            return !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            visits++;
            TownUtility.PopulateMap(this, Map);
        }

        public override void Notify_MyMapAboutToBeRemoved()
        {
            base.Notify_MyMapAboutToBeRemoved();
            OccupationUtility.CollectFromMap(this, Map);
        }

        public override void Notify_MyMapRemoved(Map map)
        {
            base.Notify_MyMapRemoved(map);
            if (PopulationCount == 0 && GarrisonCount == 0)
            {
                // Nobody to run the town; its animals and goods are lost with it.
                Find.LetterStack.ReceiveLetter("OA_LetterTownDesertedLabel".Translate(Label), "OA_LetterTownDeserted".Translate(Label), LetterDefOf.NeutralEvent, new GlobalTargetInfo(Tile));
                Destroy();
            }
        }

        public override void PostRemove()
        {
            base.PostRemove();
            if (Garrison.Any())
            {
                WithdrawGarrison();
            }
            // Anything still held here goes down with the town (uprisings move the population out first).
            for (int i = contents.Count - 1; i >= 0; i--)
            {
                Thing thing = contents[i];
                if (thing is Pawn pawn)
                {
                    contents.Remove(pawn);
                    if (!pawn.Dead && !pawn.Destroyed)
                    {
                        Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Decide);
                    }
                }
            }
            contents.ClearAndDestroyContents();
        }

        public bool CanAnnexNow(out string reason)
        {
            reason = null;
            if (state != OccupationState.Occupied)
            {
                reason = "OA_AlreadyAnnexed".Translate();
                return false;
            }
            if (HasMap)
            {
                reason = "OA_CannotAnnexWhileVisiting".Translate();
                return false;
            }
            OASettings settings = OAMod.Settings;
            if (loyalty < settings.annexLoyaltyThreshold)
            {
                reason = "OA_AnnexNeedsLoyalty".Translate(settings.annexLoyaltyThreshold.ToString("F0"), loyalty.ToString("F0"));
                return false;
            }
            if (DaysOccupied < settings.minOccupationDays)
            {
                reason = "OA_AnnexNeedsDays".Translate(settings.minOccupationDays, DaysOccupied.ToString("F1"));
                return false;
            }
            return true;
        }

        public void Annex()
        {
            state = OccupationState.Annexed;
            annexedTick = Find.TickManager.TicksGame;
            loyalty = Mathf.Min(100f, loyalty + 10f);
            Find.LetterStack.ReceiveLetter("OA_LetterAnnexedLabel".Translate(Label), "OA_LetterAnnexed".Translate(Label, Faction.Name), LetterDefOf.PositiveEvent, this);
        }

        // ------------------------------------------------------------------ UI

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            void Line(string text)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append(text);
            }

            Line(state == OccupationState.Occupied
                ? "OA_InspectStateOccupied".Translate(DaysOccupied.ToString("F1"))
                : "OA_InspectStateAnnexed".Translate());
            Line("OA_InspectLoyalty".Translate(loyalty.ToString("F0"), EconomyUtility.LoyaltyLabel(loyalty)));
            if (state == OccupationState.Occupied)
            {
                Line(CanAnnexNow(out string reason) ? "OA_InspectCanAnnex".Translate().ToString() : reason);
            }
            if (!HasMap)
            {
                Line("OA_InspectPopulation".Translate(PopulationCount, WorkerCount, GarrisonCount));
                Line("OA_InspectStockpile".Translate(StockValue.ToStringMoney(), StockCap.ToStringMoney()));
            }
            Line("OA_InspectTax".Translate(EconomyUtility.TaxLabel(tax)));
            if (retakeAttackTick >= 0 && originalFaction != null)
            {
                Line("OA_InspectRetakeIncoming".Translate(originalFaction.Name, (retakeAttackTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()).Colorize(ColorLibrary.RedReadable));
            }
            if (profile.Count > 0)
            {
                Line("OA_InspectProfile".Translate(profile.Select(p => p.def.label + " " + p.share.ToStringPercent()).ToCommaList()));
            }
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (state == OccupationState.Occupied)
            {
                var annex = new Command_Action
                {
                    defaultLabel = "OA_CommandAnnex".Translate(),
                    defaultDesc = "OA_CommandAnnexDesc".Translate(),
                    icon = AnnexIcon,
                    action = Annex
                };
                if (!CanAnnexNow(out string reason))
                {
                    annex.Disable(reason);
                }
                yield return annex;
            }

            yield return new Command_Action
            {
                defaultLabel = "OA_CommandTax".Translate(EconomyUtility.TaxLabel(tax)),
                defaultDesc = "OA_CommandTaxDesc".Translate(),
                icon = TaxIcon,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (TaxLevel level in new[] { TaxLevel.Low, TaxLevel.Normal, TaxLevel.High })
                    {
                        TaxLevel captured = level;
                        options.Add(new FloatMenuOption(EconomyUtility.TaxLabel(level) + ": " + EconomyUtility.TaxDescription(level), () => tax = captured));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            var delivery = new Command_Action
            {
                defaultLabel = "OA_CommandRequestDelivery".Translate(),
                defaultDesc = "OA_CommandRequestDeliveryDesc".Translate(),
                icon = DeliveryIcon,
                action = () => Find.WindowStack.Add(new Dialog_StockpileTransfer(this, DeliveryUtility.BestHomeMap(Tile)))
            };
            if (HasMap)
            {
                delivery.Disable("OA_CannotWhileVisiting".Translate());
            }
            else if (!Stock.Any())
            {
                delivery.Disable("OA_StockpileEmpty".Translate());
            }
            else if (DeliveryUtility.BestHomeMap(Tile) == null)
            {
                delivery.Disable("OA_NoHomeMap".Translate());
            }
            yield return delivery;

            if (GarrisonCount > 0 && !HasMap)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OA_CommandWithdrawGarrison".Translate(),
                    defaultDesc = "OA_CommandWithdrawGarrisonDesc".Translate(),
                    icon = GarrisonRemoveIcon,
                    action = WithdrawGarrison
                };
            }

            // Leaving is blocked while any enemy remains (vanilla only hides its button); show who it is.
            if (HasMap)
            {
                List<Thing> enemies = RemainingEnemies(Map);
                if (enemies.Count > 0)
                {
                    int next = 0;
                    yield return new Command_Action
                    {
                        defaultLabel = "OA_CommandShowEnemy".Translate(enemies.Count),
                        defaultDesc = "OA_CommandShowEnemyDesc".Translate(enemies.Take(5).Select(t => t.LabelCap.ToString()).ToCommaList()),
                        icon = EnemyIcon,
                        action = () =>
                        {
                            List<Thing> current = RemainingEnemies(Map);
                            if (current.Count > 0)
                            {
                                CameraJumper.TryJumpAndSelect(current[next++ % current.Count]);
                            }
                        }
                    };
                }
            }

            if (HasMap && state == OccupationState.Occupied && visits == 0)
            {
                yield return SettleInExistingMapUtility.SettleCommand(Map, requiresNoEnemies: false);
            }

            if (!HasMap)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OA_CommandAbandon".Translate(),
                    defaultDesc = "OA_CommandAbandonDesc".Translate(),
                    icon = AbandonIcon,
                    action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("OA_ConfirmAbandon".Translate(Label), AbandonTown, destructive: true))
                };
            }

            if (DebugSettings.ShowDevGizmos)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Loyalty +20",
                    action = () => loyalty = Mathf.Min(100f, loyalty + 20f)
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Loyalty -20",
                    action = () => loyalty = Mathf.Max(0f, loyalty - 20f)
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Pass 1 day",
                    action = () =>
                    {
                        occupiedTick -= GenDate.TicksPerDay;
                        EconomyUtility.DailyTick(this);
                    }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Produce 10 days",
                    action = () => EconomyUtility.Produce(this, 10f)
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Trigger uprising",
                    action = () => TownEventsUtility.Uprising(this)
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Trigger retake attempt",
                    action = () => TownEventsUtility.RetakeAttempt(this)
                };
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(caravan))
            {
                yield return option;
            }
            foreach (FloatMenuOption option in CaravanArrivalAction_VisitTown.GetFloatMenuOptions(caravan, this))
            {
                yield return option;
            }
        }

        public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
        {
            foreach (Gizmo gizmo in base.GetCaravanGizmos(caravan))
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "OA_CommandEnterTown".Translate(),
                defaultDesc = "OA_CommandEnterTownDesc".Translate(Label),
                icon = EnterIcon,
                action = () => CaravanArrivalAction_VisitTown.Enter(caravan, this)
            };
            if (HasMap)
            {
                yield break;
            }

            var take = new Command_Action
            {
                defaultLabel = "OA_CommandTakeStock".Translate(),
                defaultDesc = "OA_CommandTakeStockDesc".Translate(),
                icon = TakeIcon,
                action = () => Find.WindowStack.Add(new Dialog_StockpileTransfer(this, caravan, gift: false))
            };
            if (!Stock.Any())
            {
                take.Disable("OA_StockpileEmpty".Translate());
            }
            yield return take;

            yield return new Command_Action
            {
                defaultLabel = "OA_CommandGift".Translate(),
                defaultDesc = "OA_CommandGiftDesc".Translate(),
                icon = GiftIcon,
                action = () => Find.WindowStack.Add(new Dialog_StockpileTransfer(this, caravan, gift: true))
            };

            var garrison = new Command_Action
            {
                defaultLabel = "OA_CommandStationGarrison".Translate(),
                defaultDesc = "OA_CommandStationGarrisonDesc".Translate(),
                icon = GarrisonAddIcon,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (Pawn pawn in caravan.PawnsListForReading.Where(p => p.IsFreeColonist && !p.Downed))
                    {
                        Pawn captured = pawn;
                        options.Add(new FloatMenuOption(captured.LabelCap, () => StationGarrison(captured, caravan)));
                    }
                    if (options.Count > 0)
                    {
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                }
            };
            if (caravan.PawnsListForReading.Count(p => p.IsFreeColonist && !p.Downed) < 2)
            {
                garrison.Disable("OA_GarrisonNeedsOneLeft".Translate());
            }
            yield return garrison;
        }

        /// <summary>Everything on the town map that keeps a caravan from reforming.</summary>
        public static List<Thing> RemainingEnemies(Map map)
        {
            var result = new List<Thing>();
            foreach (Verse.AI.IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer))
            {
                if (GenHostility.IsActiveThreatToPlayer(target))
                {
                    result.Add(target.Thing);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ garrison and abandoning

        public void StationGarrison(Pawn pawn, Caravan caravan)
        {
            if (caravan.PawnsListForReading.Count(p => p.IsFreeColonist && !p.Downed) < 2)
            {
                return;
            }
            foreach (Thing item in CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => CaravanInventoryUtility.GetOwnerOf(caravan, t) == pawn).ToList())
            {
                CaravanInventoryUtility.MoveInventoryToSomeoneElse(pawn, item, caravan.PawnsListForReading, new List<Pawn> { pawn }, item.stackCount);
            }
            caravan.RemovePawn(pawn);
            Store(pawn);
            Messages.Message("OA_MessageGarrisonStationed".Translate(pawn.LabelShort, Label, pawn.Named("PAWN")), this, MessageTypeDefOf.NeutralEvent);
        }

        public void WithdrawGarrison()
        {
            List<Pawn> pawns = Garrison.ToList();
            if (pawns.Count == 0)
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                contents.Remove(pawn);
            }
            Caravan caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, Tile, addToWorldPawnsIfNotAlready: true);
            Find.WorldSelector.Select(caravan);
        }

        public void AbandonTown()
        {
            WithdrawGarrison();
            Find.LetterStack.ReceiveLetter("OA_LetterAbandonedLabel".Translate(Label), "OA_LetterAbandoned".Translate(Label), LetterDefOf.NeutralEvent, new GlobalTargetInfo(Tile));
            Destroy();
        }

        // ------------------------------------------------------------------ saving

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref state, "state", OccupationState.Occupied);
            Scribe_Values.Look(ref nameInt, "name");
            Scribe_References.Look(ref originalFaction, "originalFaction");
            Scribe_Defs.Look(ref originalFactionDef, "originalFactionDef");
            Scribe_Values.Look(ref occupiedTick, "occupiedTick", 0);
            Scribe_Values.Look(ref annexedTick, "annexedTick", -1);
            Scribe_Values.Look(ref loyalty, "loyalty", 0f);
            Scribe_Values.Look(ref tax, "tax", TaxLevel.Normal);
            Scribe_Deep.Look(ref contents, "contents", this);
            Scribe_Collections.Look(ref profile, "profile", LookMode.Deep);
            Scribe_Collections.Look(ref productionProgress, "productionProgress", LookMode.Def, LookMode.Value, ref tmpProgressKeys, ref tmpProgressValues);
            Scribe_Values.Look(ref townRect, "townRect", CellRect.Empty);
            Scribe_Deep.Look(ref snapshot, "snapshot");
            Scribe_Values.Look(ref nextDayTick, "nextDayTick", -1);
            Scribe_Values.Look(ref visits, "visits", 0);
            Scribe_Values.Look(ref annexReadyNotified, "annexReadyNotified", false);
            Scribe_Values.Look(ref stockpileFullNotified, "stockpileFullNotified", false);
            Scribe_Values.Look(ref lowLoyaltyDays, "lowLoyaltyDays", 0);
            Scribe_Values.Look(ref retakeAttackTick, "retakeAttackTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                contents ??= new ThingOwner<Thing>(this, oneStackOnly: false);
                profile ??= new List<ProductionShare>();
                profile.RemoveAll(p => p.def == null);
                productionProgress ??= new Dictionary<ThingDef, float>();
                productionProgress.RemoveAll(kv => kv.Key == null);
            }
        }
    }
}
