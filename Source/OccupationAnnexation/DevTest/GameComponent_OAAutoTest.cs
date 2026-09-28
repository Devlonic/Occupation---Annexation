using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using HarmonyLib;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;

namespace OccupationAnnexation
{
    /// <summary>
    /// End-to-end self test. Only runs when the game is started with "-quicktest -oa_autotest"
    /// (optionally "-oa_report=C:\path\report.txt"); otherwise it does nothing.
    /// Assault → capitulation → prisoner → occupation → leave → save/load → annex → deliveries → visit → leave → events.
    /// </summary>
    public class GameComponent_OAAutoTest : GameComponent
    {
        private static readonly bool Enabled = GenCommandLine.CommandLineArgPassed("oa_autotest");
        private const string SaveName = "OA_AutoTest";

        private int step = -1;
        private int stepStartTick = -1;
        private int lastActionTick;
        private int failures;
        private int checks;
        private Settlement target;
        private OccupiedSettlement town;
        private Caravan caravan;
        private Pawn prisonerTarget;
        private Pawn captor;
        private int populationBeforeSave;
        private float stockBeforeSave;
        private int homeItemsBefore;
        private int townTile = -1;
        private List<string> log = new List<string>();
        private Dictionary<string, int> jobHistogram = new Dictionary<string, int>();
        private bool visitChecked;
        private List<Pawn> visitLocals = new List<Pawn>();
        private int damagedAtStart;
        private int filthAtStart;
        private int uiFrames;
        private int uiStage;
        private int uiErrorsAtStart;
        private Window uiWindow;
        private static int errorsSeen;
        private static readonly List<string> errorSamples = new List<string>();
        private static bool logHooked;
        private DateTime heartbeatTime = DateTime.MinValue;
        private int heartbeatTick;
        private int medicPhase;
        private int medicSettleTick = -1;
        private Pawn fireVictim;
        private Pawn selfBurner;
        private Pawn fireHelper;
        private bool selfRolled;
        private int fireMark;
        private int fireAttackTick;
        private int medicMark;
        private int ceasefireStart;
        private int tendedAtStart;
        private int medicineGiven;
        private int mapMedicine;
        private bool earlyTendReported;
        private bool shotWarmupSeen;
        private Pawn medicPatient;
        private Pawn shooter;
        private List<Pawn> medics = new List<Pawn>();
        private HashSet<Pawn> inBedAtStart = new HashSet<Pawn>();
        private int consequencePhase;
        private int tendedBefore;
        private Pawn doctor;
        private Pawn playerPatient;
        private float expectedLoyaltyAfterLeave = -1f;
        private bool killedDuringStay;
        private List<Pawn> leavers = new List<Pawn>();
        private ProductionProfileDef boostedProfile;
        private float boostedShareBefore;
        private int housingBefore;
        private int militiaArmed;

        public GameComponent_OAAutoTest(Game game)
        {
            if (Enabled && !logHooked)
            {
                logHooked = true;
                Application.logMessageReceivedThreaded += (condition, stackTrace, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception)
                    {
                        errorsSeen++;
                        if (errorSamples.Count < 15)
                        {
                            errorSamples.Add(condition.Length > 300 ? condition.Substring(0, 300) : condition);
                        }
                    }
                };
            }
        }

        public override void GameComponentOnGUI()
        {
            if (Enabled)
            {
                uiFrames++;
            }
        }

        public override void ExposeData()
        {
            if (!Enabled)
            {
                return;
            }
            Scribe_Values.Look(ref step, "oaTestStep", -1);
            Scribe_Values.Look(ref failures, "oaTestFailures", 0);
            Scribe_Values.Look(ref checks, "oaTestChecks", 0);
            Scribe_References.Look(ref town, "oaTestTown");
            Scribe_References.Look(ref caravan, "oaTestCaravan");
            Scribe_Values.Look(ref populationBeforeSave, "oaTestPop", 0);
            Scribe_Values.Look(ref stockBeforeSave, "oaTestStock", 0f);
            Scribe_Values.Look(ref townTile, "oaTestTownTile", -1);
            Scribe_Collections.Look(ref log, "oaTestLog", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                log ??= new List<string>();
                stepStartTick = -1;
            }
        }

        public override void GameComponentUpdate()
        {
            // Entering a hostile map pauses the game; the test keeps time running (runs every frame, even when paused).
            if (!Enabled || step >= 100 || LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }
            if (shotQueue.Count > 0)
            {
                UpdateShots();
                return;
            }
            // Screenshots show the game as players see it, without the dev toolbar and dev gizmos.
            Prefs.DevMode = ShotDir == null || step != 20;
            if (step == 20)
            {
                try
                {
                    UiSmokeTest();
                }
                catch (Exception e)
                {
                    Fail("Exception in the UI smoke test: " + e);
                    Next(7);
                }
                return;
            }
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            if (Find.WindowStack.WindowsForcePause)
            {
                foreach (Window window in Find.WindowStack.Windows.ToList())
                {
                    if (window.forcePause)
                    {
                        Note("Closing dialog that pauses the game: " + window.GetType().Name);
                        window.Close(doCloseSound: false);
                    }
                }
            }
        }

        public override void GameComponentTick()
        {
            if (!Enabled || step >= 100)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now % 250 == 0)
            {
                DateTime real = DateTime.Now;
                if (heartbeatTime != DateTime.MinValue)
                {
                    double seconds = (real - heartbeatTime).TotalSeconds;
                    Note($"heartbeat: {(now - heartbeatTick) / Math.Max(0.001, seconds):F0} TPS, maps {Find.Maps.Count}, current {Find.CurrentMap}");
                }
                heartbeatTime = real;
                heartbeatTick = now;
            }
            if (stepStartTick < 0)
            {
                stepStartTick = now;
            }
            try
            {
                RunStep(now);
            }
            catch (Exception e)
            {
                Fail($"Exception in step {step}: {e}");
                Finish();
            }
        }

        private int StepTicks => Find.TickManager.TicksGame - stepStartTick;

        private void Next(int nextStep)
        {
            Note($"--- step {step} -> {nextStep} after {StepTicks} ticks");
            step = nextStep;
            stepStartTick = Find.TickManager.TicksGame;
        }

        private void RunStep(int now)
        {
            switch (step)
            {
                case -1:
                    if (StepTicks > 1300)
                    {
                        Next(0);
                    }
                    break;
                case 0:
                    Setup();
                    break;
                case 1:
                    if (target != null && target.HasMap && target.Map.mapPawns.FreeColonistsSpawnedCount > 0)
                    {
                        MapComponent_SiegeMorale morale = target.Map.GetComponent<MapComponent_SiegeMorale>();
                        if (morale.initialized)
                        {
                            TrySpawnPlayerVehicle(target.Map);
                            Note($"Settlement map ready: {morale.baselineDefenders} defenders, power {morale.baselinePower:F0}, turrets {morale.baselineTurrets}");
                            Check(morale.baselineDefenders > 0, "settlement has defenders");
                            Next(2);
                        }
                    }
                    else if (StepTicks > 5000)
                    {
                        Fail("Settlement map was not generated");
                        Finish();
                    }
                    break;
                case 2:
                    BreakDefense(now);
                    break;
                case 3:
                    VerifyCapitulation();
                    break;
                case 4:
                    TakePrisoner();
                    break;
                case 5:
                    RecordLeaveExpectations();
                    LeaveMap(6);
                    break;
                case 6:
                    VerifyCollected();
                    break;
                case 7:
                    SaveAndReload();
                    break;
                case 8:
                    VerifyAfterLoadAndAnnex();
                    break;
                case 9:
                    StartDeliveries();
                    break;
                case 10:
                    VerifyDeliveries();
                    break;
                case 11:
                    StartVisit();
                    break;
                case 12:
                    ObserveVisit();
                    break;
                case 13:
                    LeaveMap(14);
                    break;
                case 14:
                    VerifySecondCollection();
                    break;
                case 15:
                    TestEvents();
                    break;
                case 16:
                    Finish();
                    break;
                case 17:
                    TestGarrisonAndGifts();
                    break;
                case 18:
                    TestGettingUp();
                    break;
                case 19:
                    TestMedics(now);
                    break;
                case 21:
                    TestTreatmentOfSurrendered(now);
                    break;
                case 22:
                    TestPopulation();
                    break;
                case 20:
                    // Driven from GameComponentUpdate.
                    break;
            }
        }

        // ------------------------------------------------------------------ steps

        private void Setup()
        {
            OAMod.Settings.minCombatTicks = 0;
            OAMod.Settings.debugLogging = true;
            Map home = Find.AnyPlayerHomeMap;
            target = Find.WorldObjects.Settlements
                .Where(s => s.Faction != null && !s.Faction.IsPlayer && s.Faction.HostileTo(Faction.OfPlayer) && s.Faction.def.humanlikeFaction)
                .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(s.Tile, home.Tile))
                .FirstOrDefault();
            if (target == null)
            {
                Fail("No hostile settlement in the world");
                Finish();
                return;
            }
            townTile = target.Tile;
            Note($"Target: {target.Label} of {target.Faction.Name} ({target.Faction.def.defName}, {target.Faction.def.techLevel}) at tile {target.Tile}");
            if (ShotDir != null)
            {
                // Pictures are taken in daylight: the assault starts in the morning, local time.
                int hours = (8 - GenLocalDate.HourOfDay(target.Tile) + 24) % 24;
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + hours * GenDate.TicksPerHour);
            }

            var squad = new List<Pawn>();
            for (int i = 0; i < 6; i++)
            {
                Pawn pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                if (ShotDir != null)
                {
                    EquipForPictures(pawn);
                }
                squad.Add(pawn);
            }
            caravan = CaravanMaker.MakeCaravan(squad, Faction.OfPlayer, target.Tile, addToWorldPawnsIfNotAlready: true);
            if (ShotDir != null)
            {
                // A squad that brought food: no starving caravan in the world map pictures.
                Thing food = ThingMaker.MakeThing(ThingDefOf.Pemmican);
                food.stackCount = 300;
                CaravanInventoryUtility.GiveThing(caravan, food);
            }
            SettlementUtility.Attack(caravan, target);
            Next(1);
        }

        private void BreakDefense(int now)
        {
            Map map = target.HasMap ? target.Map : null;
            if (map == null || target.Destroyed)
            {
                // The settlement object is replaced on occupation.
                Next(3);
                return;
            }
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale.capitulated)
            {
                Check(true, "capitulation triggered by morale");
                Next(3);
                return;
            }
            int fleeing = map.mapPawns.SpawnedPawnsInFaction(target.Faction).Count(p => p.MentalStateDef == MentalStateDefOf.PanicFlee);
            if (fleeing > 0)
            {
                Fail($"{fleeing} defenders are panic-fleeing although fleeing is disabled");
            }
            if (now - lastActionTick < 60)
            {
                return;
            }
            lastActionTick = now;
            List<Pawn> active = morale.ActiveDefenders(target.Faction);
            if (active.Count == 0)
            {
                Note("No active defenders left before capitulation");
                Next(3);
                return;
            }
            Pawn victim = active.RandomElement();
            if (Rand.Bool)
            {
                victim.Kill(null);
            }
            else
            {
                HealthUtility.DamageUntilDowned(victim, allowBleedingWounds: false);
            }
            morale.ShouldCapitulate(target.Faction, out string report);
            Note("Morale: " + report);
            if (StepTicks > 30000)
            {
                Fail("Defense never broke");
                Finish();
            }
        }

        private void VerifyCapitulation()
        {
            town = Find.WorldObjects.AllWorldObjects.OfType<OccupiedSettlement>().FirstOrDefault(t => t.Tile == townTile);
            if (town == null)
            {
                if (StepTicks > 2000)
                {
                    Fail("Settlement was not occupied after capitulation");
                    Finish();
                }
                return;
            }
            Map map = town.Map;
            Check(map != null && map.Parent == town, "map reparented to the occupied town");
            Check(target.Destroyed, "original settlement object removed");
            Faction protectorate = ProtectorateUtility.Protectorate;
            Check(protectorate != null, "protectorate faction created");
            Check(protectorate != null && protectorate.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally, "protectorate is allied");
            Faction original = town.originalFaction;
            List<Pawn> locals = map.mapPawns.SpawnedPawnsInFaction(original).Where(p => p.RaceProps.Humanlike).ToList();
            Note($"Locals after capitulation: {locals.Count} (surrendered {locals.Count(SurrenderUtility.IsSurrendered)}, downed {locals.Count(p => p.Downed)})");
            Check(locals.Count > 0, "survivors exist");
            Check(locals.All(p => SurrenderUtility.IsSurrendered(p) || p.Downed), "all survivors surrendered or downed");
            Check(locals.All(p => p.ThreatDisabled(null)), "survivors are not threats");
            Check(locals.Where(SurrenderUtility.IsSurrendered).All(p => p.equipment?.Primary == null), "surrendered pawns are unarmed");
            Check(locals.Where(p => SurrenderUtility.IsSurrendered(p) && !p.Downed).All(p => p.CurJobDef == OA_DefOf.OA_Surrender || p.CurJobDef == null), "surrendered pawns lie down");
            int foreignTurrets = map.listerBuildings.allBuildingsNonColonist.Count(b => b is Building_Turret && b.Faction == original);
            Check(foreignTurrets == 0, "no turrets left for the defeated faction");
            Note($"Profile: {town.profile.Select(p => p.def.defName + " " + p.share.ToString("F2")).ToCommaList()}; rect {town.townRect}");
            CheckReformGizmos(map, "after occupation");
            TestShowEnemyGizmo(map);

            if (ShotDir != null)
            {
                // The player has walked through the town by now; pictures should not show black rooms.
                map.fogGrid.ClearAllFog();
            }
            // The picture shows as many of those who surrendered as possible: the camera goes to the biggest group.
            Pawn focus = locals.Where(p => SurrenderUtility.IsSurrendered(p) && p.Spawned && !p.Position.Fogged(map))
                .OrderByDescending(p => locals.Count(o => o.Spawned && o.Position.InHorDistOf(p.Position, 9f)))
                .ThenBy(p => p.Downed ? 1 : 0)
                .FirstOrDefault();
            if (focus != null)
            {
                List<Pawn> group = locals.Where(o => o.Spawned && o.Position.InHorDistOf(focus.Position, 9f)).ToList();
                IntVec3 groupCenter = new IntVec3((int)group.Average(p => p.Position.x), 0, (int)group.Average(p => p.Position.z));
                Shoot("01_capitulation", () =>
                {
                    FrameOn(map, groupCenter, 13f);
                    SelectOnMap(focus);
                }, CloseWindows);
                string letterLabel = "OA_LetterCapitulationLabel".Translate(target.LabelCap);
                Letter letter = Find.LetterStack.LettersListForReading.LastOrDefault(l => l.Label.ToString() == letterLabel);
                Shoot("02_capitulation_letter", () =>
                {
                    FrameOn(map, focus.Position, 17f);
                    letter?.OpenLetter();
                }, CloseWindows);
            }
            Next(4);
        }

        private void TakePrisoner()
        {
            Map map = town.Map;
            if (prisonerTarget == null)
            {
                prisonerTarget = map.mapPawns.SpawnedPawnsInFaction(town.originalFaction)
                    .Where(SurrenderUtility.IsSurrendered)
                    .OrderBy(p => p.Downed ? 1 : 0)
                    .FirstOrDefault();
                captor = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Downed && !p.InMentalState)
                    .OrderByDescending(p => p.health.summaryHealth.SummaryHealthPercent)
                    .FirstOrDefault();
                if (prisonerTarget == null || captor == null)
                {
                    Note("Nobody to take prisoner (or no capable colonist); skipping");
                    Next(18);
                    return;
                }
                Note($"Captor {captor.LabelShort} (health {captor.health.summaryHealth.SummaryHealthPercent:P0}, drafted {captor.Drafted}, job {captor.CurJobDef?.defName}) -> {prisonerTarget.LabelShort} at distance {captor.Position.DistanceTo(prisonerTarget.Position):F0}, reachable {captor.CanReach(prisonerTarget, PathEndMode.Touch, Danger.Deadly)}, reservable {captor.CanReserve(prisonerTarget)}");
                if (prisonerTarget.Position.Fogged(map))
                {
                    // Fogged cells get no float menu at all: the player would look into that room first.
                    FloodFillerFog.FloodUnfog(prisonerTarget.Position, map);
                }
                string expectedLabel = "OA_TakePrisoner".Translate(prisonerTarget.LabelShort, prisonerTarget);
                // Float menus are only built for pawns on the map being looked at.
                Current.Game.CurrentMap = map;
                List<FloatMenuOption> options = FloatMenuMakerMap.ChoicesAtFor(prisonerTarget.DrawPos, captor);
                if (!options.Any(o => o.Label.StartsWith(expectedLabel)))
                {
                    Note("Float menu options: " + options.Select(o => o.Label).ToCommaList());
                }
                Check(options.Any(o => o.Label.StartsWith(expectedLabel)), "float menu offers taking the surrendered pawn prisoner");
                Pawn prisonerShot = prisonerTarget;
                Pawn captorShot = captor;
                Shoot("03_take_prisoner", () =>
                {
                    FrameOn(map, prisonerShot.Position, 12f);
                    SelectOnMap(captorShot);
                    OpenFloatMenuAtCenter(FloatMenuMakerMap.ChoicesAtFor(prisonerShot.DrawPos, captorShot));
                }, CloseWindows);
                Job job = JobMaker.MakeJob(OA_DefOf.OA_TakeSurrenderedPrisoner, prisonerTarget);
                job.count = 1;
                Check(captor.jobs.TryTakeOrderedJob(job, JobTag.Misc), "prisoner job accepted");
                return;
            }
            if (StepTicks % 250 == 0)
            {
                Note($"  captor job {captor.CurJobDef?.defName} toil {captor.jobs.curDriver?.CurToilString}, pos {captor.Position}, downed {captor.Downed}, mental {captor.MentalStateDef?.defName}; victim surrendered {SurrenderUtility.IsSurrendered(prisonerTarget)}, downed {prisonerTarget.Downed}, prisoner {prisonerTarget.IsPrisoner}, job {prisonerTarget.CurJobDef?.defName}, spawned {prisonerTarget.Spawned}");
            }
            if (prisonerTarget.IsPrisonerOfColony)
            {
                CheckReformGizmos(map, "after taking a prisoner");
                Check(true, "surrendered pawn became a prisoner");
                Check(!SurrenderUtility.IsSurrendered(prisonerTarget), "prisoner no longer in the surrendered state");
                Next(18);
            }
            else if (StepTicks > 12000)
            {
                Fail("Taking a prisoner did not complete");
                Next(18);
            }
        }

        private void LeaveMap(int nextStep)
        {
            Map map = town.Map;
            if (map == null)
            {
                Next(nextStep);
                return;
            }
            List<Pawn> leaving = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony).ToList();
            caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(leaving, Faction.OfPlayer, town.Tile, town.Tile, -1, sendMessage: false);
            Note($"Left the town with {leaving.Count} pawns (caravan {caravan?.Label})");
            Next(nextStep);
        }

        private void VerifyCollected()
        {
            if (town.HasMap)
            {
                if (StepTicks > 2000)
                {
                    Fail("Town map was not removed after leaving");
                    Finish();
                }
                return;
            }
            Note($"Town after leaving: population {town.PopulationCount}, workers {town.WorkerCount}, stock {town.Stock.Count()} stacks worth {town.StockValue:F0}, loyalty {town.loyalty:F0}");
            Check(!town.Destroyed, "town still exists");
            Check(town.PopulationCount > 0, "population stored");
            Check(town.snapshot != null && town.snapshot.buildings.Count > 0, $"snapshot taken ({town.snapshot?.buildings.Count ?? 0} buildings, {town.snapshot?.plants.Count ?? 0} plants)");
            Check(town.Population.All(p => !Find.WorldPawns.Contains(p)), "population is not duplicated in world pawns");
            Check(town.Population.All(p => ProtectorateUtility.IsProtectorate(p.Faction)), "population belongs to the protectorate");
            Check(town.Population.All(p => !p.InMentalState), "population no longer surrendered");
            Check(town.Population.All(p => p.equipment?.Primary == null), "townsfolk hold no weapons");
            if (expectedLoyaltyAfterLeave >= 0f)
            {
                Check(Mathf.Abs(town.loyalty - expectedLoyaltyAfterLeave) < 0.01f, $"loyalty after the stay counts kills, prisoners and your doctors' care ({town.loyalty:F1}, expected {expectedLoyaltyAfterLeave:F1})");
                int spared = leavers.Count(p => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(OA_DefOf.OA_SparedSurrendered) != null);
                Check(killedDuringStay ? spared == 0 : spared > 0, $"only a conquest without killings earns the 'spared the defeated' memory ({spared} have it, killings {killedDuringStay})");
            }
            Note($"Housing {town.housing}, profile {OccupationUtility.ProfileText(town)}");
            Check(town.housing >= 0, "the town's beds were counted");
            if (town.snapshot != null)
            {
                var defs = town.snapshot.buildings.GroupBy(b => b.def).OrderByDescending(g => g.Count()).Take(40).Select(g => g.Key + "=" + g.Count());
                Note("Snapshot buildings: " + defs.ToCommaList());
            }
            Next(20);
        }

        private void SaveAndReload()
        {
            populationBeforeSave = town.PopulationCount;
            stockBeforeSave = town.StockValue;
            step = 8;
            stepStartTick = -1;
            Note("Saving and reloading");
            GameDataSaveLoader.SaveGame(SaveName);
            LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(SaveName));
            step = 99;
        }

        private void VerifyAfterLoadAndAnnex()
        {
            if (StepTicks < 60)
            {
                return;
            }
            Check(town != null && !town.Destroyed, "town survived save/load");
            Check(town.PopulationCount == populationBeforeSave, $"population survived save/load ({town.PopulationCount}/{populationBeforeSave})");
            Check(Math.Abs(town.StockValue - stockBeforeSave) < 1f, $"stock survived save/load ({town.StockValue:F0}/{stockBeforeSave:F0})");

            town.loyalty = 90f;
            town.occupiedTick -= GenDate.TicksPerDay * 10;
            EconomyUtility.DailyTick(town);
            Check(town.CanAnnexNow(out string reason), "annexation available: " + reason);
            town.Annex();
            Check(town.state == OccupationState.Annexed, "town annexed");
            float before = town.StockValue;
            float cap = OAMod.Settings.stockpileCapPerPawn;
            OAMod.Settings.stockpileCapPerPawn = 100000f;
            EconomyUtility.Produce(town, 5f);
            OAMod.Settings.stockpileCapPerPawn = cap;
            Note($"Produced 5 days: stock {before:F0} -> {town.StockValue:F0} ({town.WorkerCount} workers, efficiency {EconomyUtility.ProductionEfficiency(town):F2})");
            Check(town.StockValue > before, "production adds to the stock");
            Next(9);
        }

        private void StartDeliveries()
        {
            Map home = Find.AnyPlayerHomeMap;
            List<Thing> stock = town.Stock.ToList();
            if (stock.Count < 2)
            {
                EconomyUtility.Produce(town, 10f);
                stock = town.Stock.ToList();
            }
            homeItemsBefore = home.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Item);
            List<Thing> byCaravan = stock.Take(Math.Max(1, stock.Count / 2)).ToList();
            List<Thing> byPods = stock.Skip(byCaravan.Count).Take(3).ToList();
            DeliveryUtility.RequestDelivery(town, home, byCaravan, byPods: false);
            if (byPods.Count > 0)
            {
                DeliveryUtility.RequestDelivery(town, home, byPods, byPods: true);
            }
            foreach (DeliveryOrder order in GameComponent_Occupation.Instance.deliveries)
            {
                order.arrivalTick = Find.TickManager.TicksGame + 300;
            }
            Note($"Requested deliveries: {byCaravan.Count} stacks by caravan, {byPods.Count} by pods");
            Next(10);
        }

        private void VerifyDeliveries()
        {
            Map home = Find.AnyPlayerHomeMap;
            if (GameComponent_Occupation.Instance.deliveries.Count > 0)
            {
                return;
            }
            bool carriersPresent = home.mapPawns.AllPawnsSpawned.Any(p => ProtectorateUtility.IsProtectorate(p.Faction));
            if (StepTicks < 12000 && carriersPresent)
            {
                return;
            }
            int itemsNow = home.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Item);
            Note($"Home items: {homeItemsBefore} -> {itemsNow}; carriers still on map: {carriersPresent}");
            Check(itemsNow > homeItemsBefore || home.listerThings.AllThings.Any(t => t is ActiveDropPod), "delivered goods reached the colony");
            Next(11);
        }

        private void StartVisit()
        {
            if (caravan == null || caravan.Destroyed)
            {
                caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && c.Tile == town.Tile);
            }
            if (caravan == null)
            {
                Fail("No caravan at the town to visit it");
                Next(15);
                return;
            }
            populationBeforeSave = town.PopulationCount;
            CaravanArrivalAction_VisitTown.Enter(caravan, town);
            Next(12);
        }

        private void ObserveVisit()
        {
            if (!town.HasMap)
            {
                if (StepTicks > 3000)
                {
                    Fail("Visit map was not generated");
                    Next(15);
                }
                return;
            }
            Map map = town.Map;
            List<Pawn> locals = map.mapPawns.SpawnedPawnsInFaction(town.Faction).Where(p => p.RaceProps.Humanlike).ToList();
            if (!visitChecked)
            {
                visitChecked = true;
                visitLocals = locals.ToList();
                if (ShotDir != null)
                {
                    // A regenerated town starts fogged; pictures should show the locals indoors too.
                    map.fogGrid.ClearAllFog();
                }
                int buildings = map.listerBuildings.allBuildingsNonColonist.Count(b => b.Faction == town.Faction);
                int expected = 0, restored = 0;
                var missing = new Dictionary<string, int>();
                foreach (BuildingRecord record in town.snapshot.buildings)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.def);
                    if (def == null)
                    {
                        continue;
                    }
                    expected++;
                    if (record.pos.InBounds(map) && record.pos.GetThingList(map).Any(t => t.def == def))
                    {
                        restored++;
                    }
                    else
                    {
                        missing.TryGetValue(record.def, out int n);
                        missing[record.def] = n + 1;
                    }
                }
                Note($"Visit map: {buildings} town buildings, snapshot records restored {restored}/{expected}, {locals.Count} locals, stock left in object {town.Stock.Count()}");
                if (missing.Count > 0)
                {
                    Note("Not restored: " + missing.OrderByDescending(kv => kv.Value).Take(20).Select(kv => kv.Key + "=" + kv.Value).ToCommaList());
                }
                Check(restored >= expected * 0.97f, $"snapshot restored ({restored}/{expected})");
                damagedAtStart = MissingHitPointsInTown(map);
                filthAtStart = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
                Note("Filth on the fresh visit map: " + map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)
                    .GroupBy(f => f.def.defName).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + "=" + g.Count()).ToCommaList()
                    + $"; in town area: {map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count(f => town.townRect.Contains(f.Position))}");
                Check(locals.Count == populationBeforeSave, $"all locals spawned ({locals.Count}/{populationBeforeSave})");
                Check(locals.Any(p => p.GetLord()?.LordJob is LordJob_TownLife), "locals follow the town routine");
                Check(!town.Stock.Any(), "stockpile laid out on the map");
                BuildDuringVisit(map);
            }
            if (ShotDir != null && StepTicks > 2500 && StepTicks % 60 == 0 && GenLocalDate.HourOfDay(map) >= 9 && GenLocalDate.HourOfDay(map) <= 16)
            {
                // A local at work in daylight, with others around.
                Pawn worker = locals.Where(p => p.Spawned && !p.Position.Fogged(map)
                        && (p.CurJobDef?.defName == "OA_FakeWork" || p.CurJobDef?.defName == "OA_FakeFarm" || p.CurJobDef?.defName == "OA_TownRepair"))
                    .OrderByDescending(p => locals.Count(o => o.Position.InHorDistOf(p.Position, 10f)))
                    .FirstOrDefault();
                if (worker != null)
                {
                    // No selection: the inspect pane shows nothing about a local's work anyway.
                    Shoot("11_town_life", () => FrameOn(map, worker.Position, 14f), CloseWindows);
                }
            }
            if (StepTicks % 500 == 0)
            {
                foreach (Pawn pawn in locals)
                {
                    string key = pawn.CurJobDef?.defName ?? "none";
                    jobHistogram.TryGetValue(key, out int count);
                    jobHistogram[key] = count + 1;
                }
                jobHistogram["_samples"] = jobHistogram.TryGetValue("_samples", out int samples) ? samples + 1 : 1;
            }
            if (StepTicks > GenDate.TicksPerDay)
            {
                int damagedNow = MissingHitPointsInTown(map);
                int filthNow = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
                Note($"Missing hit points {damagedAtStart} -> {damagedNow}; filth {filthAtStart} -> {filthNow}");
                Note("Town jobs seen: " + jobHistogram.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value).ToCommaList());
                Check(damagedAtStart == 0 || damagedNow < damagedAtStart, "locals repair battle damage");
                Check(jobHistogram.ContainsKey("LayDown"), "locals sleep at night");
                Check(jobHistogram.Keys.Any(k => k == "OA_FakeWork" || k == "OA_FakeFarm" || k == "HaulToCell" || k == "OA_Patrol" || k == "LayDown"), "locals do town jobs");
                Note("Locals at the end of the visit: " + visitLocals.Select(p => $"{p.LabelShort} {(p.Spawned ? (p.Downed ? "downed" : "up") : "gone")} {p.GetLord()?.LordJob?.GetType().Name ?? "no lord"}").ToCommaList());
                Check(visitLocals.Where(p => !p.Dead).All(p => p.Spawned && p.Map == map && p.GetLord()?.LordJob is LordJob_TownLife), "every living local is still in town and on the town routine");
                TestMilitiaOnMap(map);
                Next(13);
            }
        }

        /// <summary>
        /// As if the player built during the visit: workshops of the town's weakest trade and a few beds.
        /// Leaving must update what the town produces and how many people it can house.
        /// </summary>
        private void BuildDuringVisit(Map map)
        {
            housingBefore = town.housing;
            ProductionProfileDef pick = null;
            ThingDef workshop = null;
            float pickShare = float.MaxValue;
            foreach (ProductionProfileDef profileDef in DefDatabase<ProductionProfileDef>.AllDefsListForReading)
            {
                ThingDef building = profileDef.indicatorBuildings.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
                    .FirstOrDefault(d => d != null && d.category == ThingCategory.Building && d.size.x <= 3 && d.size.z <= 3);
                float share = town.profile.FirstOrDefault(p => p.def == profileDef)?.share ?? 0f;
                if (building != null && profileDef.outputs.Any(o => o.AllowedFor(town.TechLevel)) && share < pickShare)
                {
                    pick = profileDef;
                    workshop = building;
                    pickShare = share;
                }
            }
            boostedProfile = pick;
            if (pick != null)
            {
                boostedShareBefore = pickShare;
                int built = 0;
                while (built < 12 && SpawnInTown(map, workshop, Faction.OfPlayer) != null)
                {
                    built++;
                }
                Note($"Built {built} x {workshop.defName} for {pick.defName} (share {pickShare:P0})");
            }
            int beds = 0;
            while (beds < 4 && SpawnInTown(map, ThingDefOf.Bed, Faction.OfPlayer) != null)
            {
                beds++;
            }
            Note($"Built {beds} beds (housing {housingBefore})");
        }

        private Thing SpawnInTown(Map map, ThingDef def, Faction faction)
        {
            foreach (IntVec3 cell in town.townRect.ClipInsideMap(map).Cells.InRandomOrder().Take(600))
            {
                CellRect occupied = GenAdj.OccupiedRect(cell, Rot4.North, def.size);
                if (!occupied.ExpandedBy(1).InBounds(map) || occupied.ExpandedBy(1).Cells.Any(c => !c.Standable(map) || c.GetEdifice(map) != null || c.GetFirstItem(map) != null || c.GetFirstPawn(map) != null))
                {
                    continue;
                }
                Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                if (def.CanHaveFaction)
                {
                    thing.SetFaction(faction);
                }
                GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
                return thing;
            }
            return null;
        }

        /// <summary>
        /// A retake raid while the player is there arms the militia from the weapons lying in the town.
        /// </summary>
        private void TestMilitiaOnMap(Map map)
        {
            town.militia = true;
            CellRect rect = town.townRect.ExpandedBy(3).ClipInsideMap(map);
            int weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).Count(t => TownMilitiaUtility.IsMilitiaWeapon(t.def) && rect.Contains(t.Position));
            if (weapons == 0)
            {
                for (int i = 0; i < 2; i++)
                {
                    GenPlace.TryPlaceThing(ThingMaker.MakeThing(ThingDef.Named("Gun_Revolver")), rect.CenterCell, map, ThingPlaceMode.Near);
                }
                weapons = 2;
            }
            List<Pawn> armed = TownMilitiaUtility.ArmMilitia(town, map);
            militiaArmed = armed.Count;
            Pawn militiaman = armed.FirstOrDefault(p => !p.Position.Fogged(map));
            if (militiaman != null)
            {
                Shoot("12_militia", () =>
                {
                    FrameOn(map, militiaman.Position, 12f);
                    SelectOnMap(militiaman);
                }, CloseWindows);
            }
            Check(armed.Count > 0 && armed.All(p => p.equipment.Primary != null && p.GetLord()?.LordJob is LordJob_DefendBase),
                $"the militia takes up arms from the stockpile ({armed.Count} armed, {weapons} weapons in town)");
            town.militia = false;
        }

        private int MissingHitPointsInTown(Map map)
        {
            return map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == town.Faction && b.def.useHitPoints && town.townRect.Contains(b.Position))
                .Sum(b => b.MaxHitPoints - b.HitPoints);
        }

        private void VerifySecondCollection()
        {
            if (town.HasMap)
            {
                if (StepTicks > 2000)
                {
                    Fail("Town map was not removed after the visit");
                    Next(15);
                }
                return;
            }
            List<Pawn> missing = visitLocals.Where(p => !town.Population.Contains(p)).ToList();
            foreach (Pawn pawn in missing)
            {
                Note($"  missing local {pawn.LabelShort}: dead {pawn.Dead}, destroyed {pawn.Destroyed}, spawned {pawn.Spawned}, world pawn {pawn.IsWorldPawn()}, holder {pawn.ParentHolder?.GetType().Name}, faction {pawn.Faction?.Name}, carried by caravan {pawn.GetCaravan()?.Label}");
            }
            int died = missing.Count(p => p.Dead);
            Check(town.PopulationCount + died == populationBeforeSave, $"population back in the town after the visit ({town.PopulationCount}/{populationBeforeSave}, died {died})");
            Check(town.visits == 1, "visit counted");
            if (boostedProfile != null)
            {
                float after = town.profile.FirstOrDefault(p => p.def == boostedProfile)?.share ?? 0f;
                Check(after > boostedShareBefore, $"workshops built during the visit change what the town produces ({boostedProfile.label} {boostedShareBefore:P0} -> {after:P0}; {OccupationUtility.ProfileText(town)})");
            }
            Check(town.housing > housingBefore, $"beds built during the visit add housing ({housingBefore} -> {town.housing})");
            Check(town.Population.All(p => p.equipment?.Primary == null) && TownMilitiaUtility.WeaponsInStock(town) >= militiaArmed,
                $"militia weapons go back to the stockpile ({TownMilitiaUtility.WeaponsInStock(town)} in stock, {militiaArmed} were armed)");
            Next(17);
        }

        /// <summary>
        /// A leftover enemy (like Real Ruins' hostile ruin animals) is listed by the town's "enemies left" gizmo.
        /// </summary>
        private void TestShowEnemyGizmo(Map map)
        {
            string label = "OA_CommandShowEnemy".Translate(1);
            Check(!town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "no 'enemies left' gizmo without enemies");
            Pawn fox = PawnGenerator.GeneratePawn(PawnKindDef.Named("Fox_Red"), Faction.OfAncientsHostile);
            // Vanilla ignores enemies shut in fogged rooms; this one must be out in the open.
            IntVec3 near = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(near, map, 12, c => !c.Fogged(map));
            GenSpawn.Spawn(fox, cell, map);
            List<Thing> enemies = OccupiedSettlement.RemainingEnemies(map);
            Check(enemies.Contains(fox), "a hostile ruin animal counts as a remaining enemy");
            Check(town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "'enemies left' gizmo appears");
            fox.Destroy();
            Check(OccupiedSettlement.RemainingEnemies(map).Count == 0 && !town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "'enemies left' gizmo disappears");
        }

        /// <summary>
        /// Vanilla clears the mind of a pawn that stops being downed. A capitulated pawn must stay surrendered.
        /// </summary>
        private void TestGettingUp()
        {
            Map map = town.Map;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            Pawn pawn = morale.capitulatedPawns.FirstOrDefault(p => morale.StillCapitulated(p) && p.Spawned && p.Downed)
                ?? morale.capitulatedPawns.FirstOrDefault(p => morale.StillCapitulated(p) && p.Spawned);
            if (pawn == null)
            {
                Note("No capitulated pawn left to test getting up; skipping");
                Next(19);
                return;
            }
            if (!pawn.Downed)
            {
                HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
            }
            Check(pawn.Downed, $"test pawn {pawn.LabelShort} is downed");
            pawn.health.RemoveAllHediffs();
            Check(!pawn.Downed, "downed capitulated pawn got back up");
            Check(SurrenderUtility.IsSurrendered(pawn), "the pawn that got up surrendered again");
            Check(pawn.ThreatDisabled(null), "the pawn that got up is not a threat");
            Check(pawn.GetLord()?.LordJob is LordJob_Capitulated, "the pawn that got up is back in the capitulated lord");

            // Other ways the surrendered state can be lost are caught by the periodic check.
            pawn.mindState.mentalStateHandler.Reset();
            morale.MaintainSurrender();
            Check(SurrenderUtility.IsSurrendered(pawn), "a lost surrender is restored by the periodic check");

            // The real chain behind "there are still enemies here": a capitulated pawn goes down later (bleeding out),
            // vanilla drops it from its lord, then it gets back up and vanilla clears its mind.
            // Armor, drugs or traits (Combat Extended) can keep someone on their feet: take whoever actually goes down.
            Pawn other = null;
            foreach (Pawn candidate in morale.capitulatedPawns.Where(p => p != pawn && p.Spawned && morale.StillCapitulated(p)).ToList())
            {
                if (candidate.Downed)
                {
                    candidate.health.RemoveAllHediffs();
                }
                HealthUtility.DamageUntilDowned(candidate, allowBleedingWounds: false);
                if (!candidate.Downed && !candidate.Dead)
                {
                    HealthUtility.DamageUntilDowned(candidate, allowBleedingWounds: true);
                }
                if (candidate.Downed && !candidate.Dead)
                {
                    other = candidate;
                    break;
                }
                Note($"{candidate.LabelShort} did not go down (dead {candidate.Dead}); trying someone else");
            }
            if (other != null)
            {
                Check(other.Downed && other.GetLord()?.LordJob is LordJob_Capitulated, "a pawn that goes down after capitulating stays in the capitulated lord");

                // Control: the old behaviour (dropped from the lord, no MakeUndowned patch) makes it an enemy again.
                MethodInfo makeUndowned = AccessTools.Method(typeof(Pawn_HealthTracker), "MakeUndowned");
                MethodInfo postfix = AccessTools.Method(typeof(Patch_Pawn_HealthTracker_MakeUndowned), "Postfix");
                var harmony = new Harmony(OAMod.HarmonyId);
                harmony.Unpatch(makeUndowned, postfix);
                try
                {
                    other.GetLord()?.RemovePawn(other);
                    other.health.RemoveAllHediffs();
                    // Vanilla ignores enemies shut in fogged rooms (medics may have carried this one indoors).
                    FloodFillerFog.FloodUnfog(other.Position, map);
                    bool threat = GenHostility.IsActiveThreatToPlayer(other);
                    Note($"Control without the patch: {other.LabelShort} downed {other.Downed}, surrendered {SurrenderUtility.IsSurrendered(other)}, active threat {threat}");
                    Check(!SurrenderUtility.IsSurrendered(other) && threat, "control: vanilla makes a recovered pawn an enemy again");
                    morale.MaintainSurrender();
                    Check(SurrenderUtility.IsSurrendered(other) && !GenHostility.IsActiveThreatToPlayer(other) && other.GetLord()?.LordJob is LordJob_Capitulated,
                        "control: the periodic check restores the surrender and the capitulated lord");
                }
                finally
                {
                    harmony.Patch(makeUndowned, postfix: new HarmonyMethod(postfix));
                }
            }

            // Saves made before the capitulation list existed: the list is rebuilt from the defeated faction's people.
            int listed = morale.capitulatedPawns.Count;
            List<Pawn> listedPawns = morale.capitulatedPawns.Where(morale.StillCapitulated).ToList();
            morale.capitulatedPawns.Clear();
            Traverse.Create(morale).Field("capitulatedListBuilt").SetValue(false);
            pawn.mindState.mentalStateHandler.Reset();
            morale.MaintainSurrender();
            Check(morale.capitulatedPawns.Count > 0 && morale.capitulatedPawns.Contains(pawn), $"old saves: capitulation list rebuilt ({morale.capitulatedPawns.Count}, was {listed})");
            Check(SurrenderUtility.IsSurrendered(pawn), "old saves: pawns that lost the surrender lie down again");
            foreach (Pawn before in listedPawns)
            {
                if (!morale.capitulatedPawns.Contains(before))
                {
                    Note($"  old saves: {before.LabelShort} is not in the rebuilt list (dead {before.Dead}, spawned {before.Spawned}, held by {before.ParentHolder?.GetType().Name}, faction {before.Faction?.Name})");
                    morale.capitulatedPawns.Add(before);
                }
            }

            CheckReformGizmos(map, "after a downed pawn got up");
            Next(19);
        }

        /// <summary>
        /// Once the shooting has stopped for 15 to 40 seconds, those who surrendered and can doctor carry the badly wounded
        /// into beds and tend their wounded. A real shot next to them sends them back down and restarts the wait;
        /// a shot elsewhere on the map does not.
        /// </summary>
        private void TestMedics(int now)
        {
            Map map = town.Map;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            Pawn working = medics.FirstOrDefault(p => SurrenderMedicUtility.IsMedicJob(p.CurJobDef));
            Pawn tending = medics.FirstOrDefault(p => p.CurJobDef == JobDefOf.TendPatient);
            Pawn carrying = medics.FirstOrDefault(p => p.Spawned && p.carryTracker?.CarriedThing is Pawn && !p.Position.Fogged(map));
            if (medicPhase >= 4 && medicPhase <= 6 && carrying != null)
            {
                Shoot("05_medic_carries", () =>
                {
                    FrameOn(map, carrying.Position, 11f);
                    SelectOnMap(carrying);
                }, CloseWindows, keepMessages: true);
            }
            if (medicPhase >= 5 && medicPhase <= 6 && tending != null && tending.jobs.curDriver?.CurToilString == "Wait" && !tending.Position.Fogged(map))
            {
                Shoot("04_medic_tends", () =>
                {
                    FrameOn(map, tending.Position, 11f);
                    SelectOnMap(tending);
                }, CloseWindows, keepMessages: true);
            }
            if (medicPhase == 3 || medicPhase == 4 || medicPhase == 10)
            {
                if (working != null && now - ceasefireStart < SurrenderMedicUtility.MinCeasefireTicks && !earlyTendReported)
                {
                    earlyTendReported = true;
                    Fail($"{working.LabelShort} got up to help the wounded only {now - ceasefireStart} ticks into the ceasefire");
                }
            }
            switch (medicPhase)
            {
                case 0:
                    // The ceasefire may have run for a while and medics may be carrying someone: send them down first.
                    if (medicSettleTick < 0)
                    {
                        medicSettleTick = now;
                        morale.Notify_CeasefireBroken();
                        break;
                    }
                    if (now - medicSettleTick < 60 && morale.capitulatedPawns.Any(p => !p.Dead && p.ParentHolder is Pawn_CarryTracker))
                    {
                        break;
                    }
                    SetUpMedicTest(map, morale, now);
                    break;
                case 1:
                    // The shot next to a medic.
                    if (morale.lastAttackTick >= medicMark)
                    {
                        Check(true, $"a shot next to those who surrendered breaks the ceasefire ({morale.lastAttackTick - medicMark} ticks after the order)");
                        ceasefireStart = morale.lastAttackTick;
                        medicPhase = 2;
                    }
                    else if (now - medicMark > 900)
                    {
                        Fail($"the shot next to those who surrendered was not noticed (shooter job {shooter.CurJobDef?.defName}, stance {shooter.stances.curStance?.GetType().Name})");
                        ceasefireStart = morale.CeasefireStartTick;
                        medicPhase = 2;
                    }
                    break;
                case 2:
                    if (now - ceasefireStart == 1)
                    {
                        Check(working == null, "nobody helps the wounded right after the shot");
                    }
                    if (now - ceasefireStart < 60)
                    {
                        // Combat Extended bullets fly: one that hits someone lands a little later and counts too.
                        break;
                    }
                    ceasefireStart = morale.lastAttackTick;
                    if (!FireFarFromCapitulated(map, morale))
                    {
                        Fail("could not fire far from those who surrendered");
                        medicPhase = 4;
                        break;
                    }
                    medicMark = now;
                    medicPhase = 3;
                    break;
                case 3:
                    // The shot far away: wait until it has actually been fired.
                    if (shooter.stances.curStance is Stance_Warmup)
                    {
                        shotWarmupSeen = true;
                    }
                    if (shotWarmupSeen && shooter.stances.curStance is Stance_Cooldown)
                    {
                        Check(morale.lastAttackTick == ceasefireStart, "a shot far from those who surrendered does not break the ceasefire");
                        medicPhase = 4;
                    }
                    else if (now - medicMark > 600)
                    {
                        Fail($"the shot far away was never fired (shooter job {shooter.CurJobDef?.defName}, stance {shooter.stances.curStance?.GetType().Name})");
                        medicPhase = 4;
                    }
                    break;
                case 4:
                    if (working != null)
                    {
                        int delay = now - ceasefireStart;
                        Note($"{working.LabelShort} gets up {delay} ticks ({delay / 60f:F1} s) into the ceasefire: {working.CurJobDef.defName} {working.CurJob.targetA.Thing?.LabelShort}");
                        Check(delay >= SurrenderMedicUtility.MinCeasefireTicks && delay <= SurrenderMedicUtility.MaxCeasefireTicks + 31, $"the first medic gets up 15-40 s after the shooting stops ({delay / 60f:F1} s)");
                        Check(SurrenderUtility.IsSurrendered(working) && working.ThreatDisabled(null) && !GenHostility.IsActiveThreatToPlayer(working), "a medic stays surrendered and is no threat");
                        Check(working.equipment?.Primary == null, "a medic is unarmed");
                        bool anyBleeding = morale.capitulatedPawns.Any(p => !p.Dead && p.Spawned && p.health.hediffSet.BleedRateTotal > 0f && p.health.HasHediffsNeedingTend());
                        Check(!anyBleeding || medics.Any(p => p.CurJobDef == JobDefOf.TendPatient), $"bleeding is stopped before anyone is carried (jobs: {medics.Where(p => p.CurJob != null && SurrenderMedicUtility.IsMedicJob(p.CurJobDef)).Select(p => p.CurJobDef.defName).ToCommaList()})");
                        CheckReformGizmos(map, "while those who surrendered tend their wounded");
                        NoteWoundedAndBeds(map, morale, working);
                        tendedAtStart = TendedWounds(morale);
                        medicMark = now;
                        medicPhase = 5;
                    }
                    else if (now - ceasefireStart > SurrenderMedicUtility.MaxCeasefireTicks + 600)
                    {
                        Fail("no surrendered medic got up to help the wounded");
                        foreach (Pawn medic in medics)
                        {
                            Note($"  {medic.LabelShort}: may tend {SurrenderMedicUtility.MayTendNow(medic)}, to carry {SurrenderMedicUtility.FindWoundedToCarry(medic, out _)?.LabelShort}, patient {SurrenderMedicUtility.FindPatient(medic)?.LabelShort}, job {medic.CurJobDef?.defName}, downed {medic.Downed}, surrendered {SurrenderUtility.IsSurrendered(medic)}");
                        }
                        FinishMedicTest(map);
                    }
                    break;
                case 5:
                    if (tending != null)
                    {
                        Check(tending.MentalState?.InspectLine == "OA_SurrenderedTendingInspect".Translate(), "a medic's inspect line says it tends the wounded");
                        Thing medicine = tending.CurJob.targetB.Thing;
                        Check(medicine != null && medicine.def.IsMedicine && (tending.inventory.Contains(medicine) || tending.carryTracker.CarriedThing == medicine), "a medic uses the medicine it carries");
                        medicPhase = 6;
                    }
                    else if (now - medicMark > 6000)
                    {
                        Fail($"no surrendered medic started tending (working: {working?.CurJobDef?.defName})");
                        FinishMedicTest(map);
                    }
                    break;
                case 6:
                    bool tended = TendedWounds(morale) > tendedAtStart;
                    List<Pawn> bedded = NewlyInBed(morale);
                    if (tended && bedded.Count > 0)
                    {
                        Check(true, $"the wounded got tended ({now - medicMark} ticks, {TendedWounds(morale) - tendedAtStart} wounds; test patient still needs tending {medicPatient.health.HasHediffsNeedingTend()})");
                        Check(true, $"medics carried the badly wounded into beds ({bedded.Select(p => p.LabelShort).ToCommaList()})");
                        Note($"Medicine the medics still carry: {MedicineWithMedics()}/{medicineGiven}");
                        Check(SpawnedMedicine(map) >= mapMedicine, $"the town's own medicine is left alone ({SpawnedMedicine(map)}/{mapMedicine})");
                        if (SpawnedMedicine(map) > mapMedicine)
                        {
                            foreach (Thing dropped in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine))
                            {
                                Pawn near = GenClosest.ClosestThing_Global(dropped.Position, map.mapPawns.AllPawnsSpawned, 5f) as Pawn;
                                Note($"  medicine on the ground: {dropped.LabelCap} at {dropped.Position}, nearest pawn {near?.LabelShort} (downed {near?.Downed}, job {near?.CurJobDef?.defName})");
                            }
                        }
                        if (working == null && !medicPatient.Dead)
                        {
                            // Someone must be at work for the next shot: a fresh wound (not from the player).
                            medicPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 3f, armorPenetration: 999f));
                        }
                        medicMark = now;
                        medicPhase = 7;
                    }
                    else if (now - medicMark > 12000)
                    {
                        Fail($"the wounded were not tended ({tended}) or carried into beds ({bedded.Count}) (medic job {working?.CurJobDef?.defName} {working?.jobs.curDriver?.CurToilString}, free bed {medics.Any(m => SurrenderMedicUtility.FindBedFor(m, medicPatient) != null)})");
                        FinishMedicTest(map);
                    }
                    break;
                case 7:
                    if (working != null)
                    {
                        if (!FireNear(map, working.Position))
                        {
                            Fail("could not fire next to a medic at work");
                            FinishMedicTest(map);
                            break;
                        }
                        medicPatient = working.CurJob.targetA.Pawn ?? medicPatient;
                        mapMedicine = SpawnedMedicine(map);
                        Note($"Second shot while {working.LabelShort} is at {working.CurJobDef.defName} '{working.jobs.curDriver?.CurToilString}', carrying {working.carryTracker.CarriedThing?.LabelShort ?? "nothing"}");
                        medicMark = now;
                        medicPhase = 8;
                    }
                    else if (now - medicMark > 600)
                    {
                        Fail("no medic went back to work for the second shot");
                        FinishMedicTest(map);
                    }
                    break;
                case 8:
                    if (morale.lastAttackTick >= medicMark)
                    {
                        ceasefireStart = morale.lastAttackTick;
                        earlyTendReported = false;
                        medicPhase = 9;
                    }
                    else if (now - medicMark > 900)
                    {
                        Fail("the shot next to a medic at work was not noticed");
                        FinishMedicTest(map);
                    }
                    break;
                case 9:
                    Check(working == null, "medics abandon the wounded when shots resume");
                    Check(medics.Where(p => !p.Dead && !p.Downed).All(p => p.CurJobDef == OA_DefOf.OA_Surrender), "medics lie face down again");
                    Check(medics.All(p => p.carryTracker?.CarriedThing == null), "medics drop whoever they carried and hold no medicine");
                    Check(SpawnedMedicine(map) == mapMedicine, $"medics pocket their medicine instead of dropping it ({SpawnedMedicine(map)}/{mapMedicine} on the ground)");
                    medicPhase = 10;
                    break;
                case 10:
                    if (working != null && now - ceasefireStart >= SurrenderMedicUtility.MinCeasefireTicks)
                    {
                        Check(now - ceasefireStart <= SurrenderMedicUtility.MaxCeasefireTicks + 31, $"medics get up again 15-40 s into the new ceasefire ({(now - ceasefireStart) / 60f:F1} s)");
                        medicPhase = 20;
                    }
                    else if (now - ceasefireStart > SurrenderMedicUtility.MaxCeasefireTicks + 600)
                    {
                        Fail("medics never got up again after the new ceasefire");
                        FinishMedicTest(map);
                    }
                    break;
                case 11:
                    // Hurting one of them in any way counts like a shot.
                    Pawn victim = new[] { medicPatient }.Concat(medics).FirstOrDefault(p => !p.Dead && p.Spawned) ?? medicPatient;
                    victim.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 1f, armorPenetration: 999f, instigator: shooter));
                    Check(morale.lastAttackTick == now, $"hurting one of those who surrendered breaks the ceasefire ({victim.LabelShort}: listed {morale.capitulatedPawns.Contains(victim)}, "
                        + $"capitulated {SurrenderUtility.HasCapitulated(victim)}, spawned {victim.Spawned}, last attack {morale.lastAttackTick})");
                    medicPhase = 12;
                    break;
                case 12:
                    Check(working == null, "medics lie down after one of them is hurt");
                    FinishMedicTest(map);
                    break;
                case 20:
                    SetUpFireTest(map, morale, working, now);
                    break;
                case 21:
                    WatchFireTest(map, morale, now);
                    break;
            }
        }

        /// <summary>
        /// Fire before tending: the patient of a medic at work catches fire from a fire the player lit, and so does someone
        /// on their feet. The burns must not break the ceasefire; the one on their feet rolls the fire out and someone puts
        /// out the one on the ground.
        /// </summary>
        private void SetUpFireTest(Map map, MapComponent_SiegeMorale morale, Pawn working, int now)
        {
            // Not everyone can catch fire: someone lying in a doorway, or whose armor does not burn (Combat Extended).
            Pawn patient = working?.CurJob.targetA.Pawn;
            bool medicsPatient = patient != null && patient != working && patient.Spawned && patient.Downed && !patient.Dead && patient.CanEverAttachFire();
            fireVictim = medicsPatient ? patient : morale.capitulatedPawns.FirstOrDefault(p => p.Spawned && !p.Dead && p.Downed && p.CanEverAttachFire());
            selfBurner = morale.capitulatedPawns.Where(p => p.Spawned && !p.Dead && !p.Downed && p != working && SurrenderUtility.IsSurrendered(p) && p.CanEverAttachFire())
                .OrderBy(p => p.CurJobDef == OA_DefOf.OA_Surrender ? 0 : 1).FirstOrDefault();
            if (fireVictim == null)
            {
                Note("Nobody lies downed who can catch fire; skipping the fire test");
                medicPhase = 11;
                return;
            }
            fireAttackTick = morale.lastAttackTick;
            fireVictim.TryAttachFire(0.5f, shooter);
            selfBurner?.TryAttachFire(0.5f, shooter);
            Note($"{shooter.LabelShort}'s fire: {fireVictim.LabelShort} burns on the ground ({(medicsPatient ? $"patient of {working.LabelShort} at {working.CurJobDef.defName}" : "not a patient")}), "
                + $"{selfBurner?.LabelShort ?? "nobody"} on their feet");
            if (!fireVictim.HasAttachment(ThingDefOf.Fire))
            {
                Fail($"could not set {fireVictim.LabelShort} on fire");
                medicPhase = 11;
                return;
            }
            // Someone who could tend and can fight fires (not every background allows it), close enough to help.
            Fire fire = (Fire)fireVictim.GetAttachment(ThingDefOf.Fire);
            Pawn medic = new[] { working }.Concat(medics).FirstOrDefault(m => m != null && m != selfBurner && !m.HasAttachment(ThingDefOf.Fire)
                && SurrenderMedicUtility.MayTendNow(m) && SurrenderFireUtility.MayFightFiresNow(m)
                && m.Position.InHorDistOf(fireVictim.Position, SurrenderFireUtility.MaxHelpDistance) && m.CanReach(fire, PathEndMode.Touch, Danger.Deadly));
            if (medic != null)
            {
                Job next = new JobGiver_Surrendered().TryIssueJobPackage(medic, default).Job;
                Check(next?.def == JobDefOf.BeatFire, $"a medic puts out a burning comrade before tending anyone ({medic.LabelShort}: next job {next?.def.defName})");
            }
            else
            {
                Note($"No medic who can fight fires is close enough to check the priority ({working.LabelShort} can fight fires {SurrenderFireUtility.CanFightFires(working)})");
            }
            Check(SurrenderMedicUtility.FindPatient(working) != fireVictim && SurrenderMedicUtility.FindWoundedToCarry(working, out _) != fireVictim,
                "nobody bandages or carries a comrade who is burning");
            fireHelper = null;
            selfRolled = false;
            fireMark = now;
            medicPhase = 21;
        }

        private void WatchFireTest(Map map, MapComponent_SiegeMorale morale, int now)
        {
            if (selfBurner != null && selfBurner.CurJobDef == JobDefOf.ExtinguishSelf)
            {
                selfRolled = true;
            }
            if (fireHelper == null)
            {
                fireHelper = morale.capitulatedPawns.FirstOrDefault(p => p.Spawned && p.CurJobDef == JobDefOf.BeatFire);
                if (fireHelper != null)
                {
                    Note($"{fireHelper.LabelShort} puts out the fire {now - fireMark} ticks after it started");
                }
            }
            bool victimBurns = SurrenderFireUtility.FireOnDowned(fireVictim) != null;
            bool selfBurns = selfBurner != null && selfBurner.Spawned && selfBurner.HasAttachment(ThingDefOf.Fire);
            if (!victimBurns && !selfBurns)
            {
                Check(morale.lastAttackTick == fireAttackTick, $"burns from a fire the player lit do not break the ceasefire (last attack {morale.lastAttackTick}, was {fireAttackTick})");
                Check(fireHelper != null, $"someone who surrendered puts out a comrade burning on the ground ({fireHelper?.LabelShort ?? "nobody"}, {now - fireMark} ticks; {fireVictim.LabelShort} {(fireVictim.Dead ? "died" : "survived")})");
                if (selfBurner != null)
                {
                    Check(selfRolled, $"someone who surrendered and catches fire rolls it out on the spot ({selfBurner.LabelShort} {(selfBurner.Dead ? "died" : "survived")})");
                }
                Check(morale.capitulatedPawns.Where(p => p.Spawned && !p.Dead && !p.Downed).All(p => SurrenderUtility.IsSurrendered(p) && p.ThreatDisabled(null)),
                    "those who fought the fire stay surrendered and are no threat");
                ClearFires(map);
                medicPhase = 11;
            }
            else if (now - fireMark > 2500)
            {
                Fail($"the fires were not put out (on the ground {victimBurns}, on their feet {selfBurns}; jobs: "
                    + morale.capitulatedPawns.Where(p => p.Spawned && !p.Downed).Select(p => $"{p.LabelShort} {p.CurJobDef?.defName}").ToCommaList() + ")");
                ClearFires(map);
                medicPhase = 11;
            }
        }

        /// <summary>Whatever the test fire spread to must not burn down the town for the rest of the run.</summary>
        private static void ClearFires(Map map)
        {
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
            {
                fire.Destroy();
            }
        }

        private void NoteWoundedAndBeds(Map map, MapComponent_SiegeMorale morale, Pawn medic)
        {
            foreach (Pawn pawn in morale.capitulatedPawns.Where(p => p.Spawned && p.Downed))
            {
                Note($"  downed {pawn.LabelShort}: faction {pawn.Faction?.Name}, in bed {pawn.InBed()}, patient {SurrenderMedicUtility.IsPatientFor(medic, pawn, needsTend: false)}, "
                    + $"reach {medic.CanReach(pawn, PathEndMode.ClosestTouch, Danger.Deadly)}, reserve {medic.CanReserve(pawn)}, bed {SurrenderMedicUtility.FindBedFor(medic, pawn)?.Position.ToString() ?? "none"}");
            }
            Note("  beds: " + map.listerThings.ThingsInGroup(ThingRequestGroup.Bed).OfType<Building_Bed>().Select(b =>
                $"{b.def.defName}@{b.Position} {b.Faction?.Name} med {b.Medical} prison {b.ForPrisoners} owners {b.OwnersForReading.Count} free {b.AnyUnoccupiedSleepingSlot} "
                + $"use {RestUtility.CanUseBedNow(b, medicPatient, checkSocialProperness: false, allowMedBedEvenIfSetToNoCare: true)} reserve {medic.CanReserveAndReach(b, PathEndMode.Touch, Danger.Deadly, b.SleepingSlotsCount, 0)}").ToCommaList());
            Pawn toCarry = SurrenderMedicUtility.FindWoundedToCarry(medic, out Building_Bed bed);
            Note($"  {medic.LabelShort} would carry {toCarry?.LabelShort ?? "nobody"} to {bed?.Position.ToString() ?? "no bed"}");
        }

        /// <summary>
        /// Defenders downed in their sleep lie in beds already; the medics must have someone to carry.
        /// </summary>
        private static void PutOnFloor(Pawn pawn, Map map)
        {
            if (!pawn.InBed())
            {
                return;
            }
            if (CellFinder.TryFindRandomCellNear(pawn.Position, map, 6, c => c.Standable(map) && c.GetFirstPawn(map) == null && c.GetFirstBuilding(map) == null, out IntVec3 floor))
            {
                pawn.jobs.StopAll();
                pawn.Position = floor;
                pawn.Notify_Teleported();
            }
        }

        private List<Pawn> NewlyInBed(MapComponent_SiegeMorale morale)
        {
            return morale.capitulatedPawns.Where(p => !p.Dead && p.Spawned && p.Downed && p.InBed() && !inBedAtStart.Contains(p)).ToList();
        }

        /// <summary>
        /// A wooden bed of the town where there is room, reachable for the medics.
        /// </summary>
        private Building_Bed SpawnTownBed(Map map)
        {
            ThingDef def = ThingDefOf.Bed;
            foreach (IntVec3 cell in town.townRect.ClipInsideMap(map).Cells.InRandomOrder())
            {
                CellRect occupied = GenAdj.OccupiedRect(cell, Rot4.North, def.size);
                if (!occupied.InBounds(map) || occupied.Cells.Any(c => !c.Standable(map) || c.Fogged(map) || c.GetEdifice(map) != null || c.GetFirstItem(map) != null || c.GetFirstPawn(map) != null))
                {
                    continue;
                }
                if (!medics[0].CanReach(cell, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                var bed = (Building_Bed)ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
                bed.SetFaction(town.Faction);
                GenSpawn.Spawn(bed, cell, map, Rot4.North);
                return bed;
            }
            return null;
        }

        private void SetUpMedicTest(Map map, MapComponent_SiegeMorale morale, int now)
        {
            List<Pawn> alive = morale.capitulatedPawns.Where(morale.StillCapitulated).ToList();
            medics = alive.Where(p => !p.Downed && SurrenderUtility.IsSurrendered(p) && SurrenderMedicUtility.CanDoctor(p)).ToList();
            if (medics.Count == 0)
            {
                Pawn candidate = alive.FirstOrDefault(p => p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                candidate?.health.RemoveAllHediffs();
                medics = alive.Where(p => !p.Downed && SurrenderUtility.IsSurrendered(p) && SurrenderMedicUtility.CanDoctor(p)).ToList();
            }
            medicPatient = alive.Where(p => !medics.Contains(p)).OrderBy(p => p.InBed() ? 1 : 0).ThenBy(p => p.Downed ? 0 : 1).FirstOrDefault();
            if (medicPatient == null && medics.Count > 1)
            {
                medicPatient = medics.Last();
                medics.Remove(medicPatient);
            }
            shooter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Violent));
            if (medics.Count == 0 || medicPatient == null || shooter == null)
            {
                Note($"Cannot test the surrendered medics (medics {medics.Count}, patient {medicPatient?.LabelShort}, shooter {shooter?.LabelShort}); skipping");
                Next(21);
                return;
            }
            if (!medicPatient.Downed)
            {
                HealthUtility.DamageUntilDowned(medicPatient, allowBleedingWounds: true);
            }
            if (!medicPatient.Dead)
            {
                // A fresh cut bleeds: the medics have to stop that before carrying anyone.
                medicPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 4f, armorPenetration: 999f));
            }
            PutOnFloor(medicPatient, map);
            Check(!medicPatient.Dead && medicPatient.Spawned && medicPatient.Downed && !medicPatient.InBed() && medicPatient.health.HasHediffsNeedingTend(),
                $"wounded comrade {medicPatient.LabelShort} lies on the ground and needs tending (dead {medicPatient.Dead}, spawned {medicPatient.Spawned}, "
                + $"held by {medicPatient.ParentHolder?.GetType().Name}, downed {medicPatient.Downed}, in bed {medicPatient.InBed()}, needs tending {medicPatient.health.HasHediffsNeedingTend()})");
            // Someone down without bleeding: the medics carry them to a bed while the bleeding one is tended.
            Pawn carryPatient = alive.FirstOrDefault(p => p != medicPatient && !medics.Contains(p) && !p.Dead);
            if (carryPatient != null)
            {
                if (!carryPatient.Downed)
                {
                    HealthUtility.DamageUntilDowned(carryPatient, allowBleedingWounds: false);
                }
                PutOnFloor(carryPatient, map);
                Note($"Also down on the ground: {carryPatient.LabelShort} (downed {carryPatient.Downed}, bleeding {carryPatient.health.hediffSet.BleedRateTotal:F2})");
            }
            // Settlement defenders rarely carry medicine; each medic gets some, the town's stock must stay untouched.
            foreach (Pawn medic in medics)
            {
                Thing herbal = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                herbal.stackCount = 3;
                medic.inventory.innerContainer.TryAdd(herbal);
            }
            medicineGiven = MedicineWithMedics();
            mapMedicine = SpawnedMedicine(map);
            inBedAtStart = new HashSet<Pawn>(alive.Where(p => p.InBed()));
            int freeBeds = map.listerThings.ThingsInGroup(ThingRequestGroup.Bed).OfType<Building_Bed>()
                .Count(b => b.Faction != Faction.OfPlayer && !b.ForPrisoners && RestUtility.CanUseBedNow(b, medicPatient, checkSocialProperness: false, allowMedBedEvenIfSetToNoCare: true));
            for (int i = freeBeds; i < 2; i++)
            {
                Building_Bed bed = SpawnTownBed(map);
                Note(bed != null ? $"Not enough free beds for the wounded: spawned one at {bed.Position}" : "Not enough free beds for the wounded, and none could be spawned");
            }
            Note($"Medics: {medics.Select(p => p.LabelShort).ToCommaList()}; ceasefire so far {now - morale.CeasefireStartTick} ticks, tending already {medics.Count(p => p.CurJobDef == JobDefOf.TendPatient)}");

            ThingDef gunDef = ThingDef.Named("Gun_Revolver");
            var gun = (ThingWithComps)ThingMaker.MakeThing(gunDef, GenStuff.DefaultStuffFor(gunDef));
            shooter.equipment.DestroyAllEquipment();
            shooter.equipment.AddEquipment(gun);
            Type ammoUser = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            ThingComp ammo = ammoUser == null ? null : gun.AllComps.FirstOrDefault(c => ammoUser.IsInstanceOfType(c));
            if (ammo != null)
            {
                AccessTools.Method(ammoUser, "ResetAmmoCount").Invoke(ammo, new object[] { null });
            }
            shooter.drafter.Drafted = true;

            medicMark = now;
            if (!medics.Any(m => FireNear(map, m.Position)))
            {
                Fail("could not fire next to those who surrendered");
                FinishMedicTest(map);
                return;
            }
            medicPhase = 1;
        }

        private static int TendedWounds(MapComponent_SiegeMorale morale)
        {
            return morale.capitulatedPawns.Where(p => !p.Dead).Sum(p => p.health.hediffSet.hediffs.Count(h => h.IsTended()));
        }

        private int MedicineWithMedics()
        {
            return medics.Sum(p => p.inventory.innerContainer.Where(t => t.def.IsMedicine).Sum(t => t.stackCount)
                + (p.carryTracker.CarriedThing?.def.IsMedicine == true ? p.carryTracker.CarriedThing.stackCount : 0));
        }

        private static int SpawnedMedicine(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).Sum(t => t.stackCount);
        }

        private bool FireNear(Map map, IntVec3 around)
        {
            if (around.Fogged(map))
            {
                // The shooter has looked into the room the medic is working in.
                FloodFillerFog.FloodUnfog(around, map);
            }
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(around, 3f, useCenter: false))
            {
                if (cell.InBounds(map) && cell.Standable(map) && !cell.Fogged(map) && cell.GetFirstPawn(map) == null && FireAt(map, cell))
                {
                    return true;
                }
            }
            return false;
        }

        private bool FireFarFromCapitulated(Map map, MapComponent_SiegeMorale morale)
        {
            List<Pawn> capitulated = morale.capitulatedPawns.Where(p => p.Spawned).ToList();
            IEnumerable<IntVec3> cells = map.AllCells
                .Where(c => c.Standable(map) && !c.Fogged(map) && c.GetFirstPawn(map) == null && capitulated.All(p => !p.Position.InHorDistOf(c, 25f)))
                .InRandomOrder()
                .Take(50);
            foreach (IntVec3 cell in cells)
            {
                if (FireAt(map, cell))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Puts the armed test colonist within range and orders a single burst at the cell, as the player does with "Attack".
        /// </summary>
        private bool FireAt(Map map, IntVec3 target)
        {
            Verb verb = shooter.equipment?.PrimaryEq?.PrimaryVerb;
            if (verb == null)
            {
                return false;
            }
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(target, 12f, useCenter: false))
            {
                if (cell.DistanceTo(target) < 5f || !cell.InBounds(map) || !cell.Standable(map) || cell.Fogged(map) || (cell.GetFirstPawn(map) != null && cell != shooter.Position))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(cell, target))
                {
                    continue;
                }
                shooter.jobs.StopAll();
                shooter.stances.CancelBusyStanceHard();
                shooter.Position = cell;
                shooter.Notify_Teleported();
                shotWarmupSeen = false;
                Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                job.verbToUse = verb;
                job.maxNumStaticAttacks = 1;
                job.playerForced = true;
                bool ordered = shooter.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                Note($"{shooter.LabelShort} fires at {target} from {cell}: ordered {ordered}");
                return ordered;
            }
            return false;
        }

        private void FinishMedicTest(Map map)
        {
            if (shooter != null)
            {
                shooter.jobs.StopAll();
                shooter.drafter.Drafted = false;
                shooter.equipment.DestroyAllEquipment();
            }
            CheckReformGizmos(map, "after the medic test");
            Next(21);
        }

        /// <summary>
        /// The player's doctors tend someone who surrendered (the town remembers it), then one of them is killed:
        /// colonists react according to their views, and factions at peace lose goodwill.
        /// </summary>
        private void TestTreatmentOfSurrendered(int now)
        {
            Map map = town.Map;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            List<Pawn> alive = morale.capitulatedPawns.Where(morale.StillCapitulated).ToList();
            switch (consequencePhase)
            {
                case 0:
                    doctor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.InMentalState
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
                    if (doctor?.drafter != null)
                    {
                        // An undrafted doctor: vanilla has no way at all for them to tend enemies.
                        doctor.drafter.Drafted = false;
                    }
                    // Someone lying face down but not downed (vanilla offers no way to tend them), preferably not a medic who might get up and walk off.
                    playerPatient = alive.Where(p => p.Spawned && !p.Downed && SurrenderUtility.IsSurrendered(p))
                        .OrderBy(p => p.Position.Fogged(map) ? 1 : 0).ThenBy(p => SurrenderMedicUtility.CanDoctor(p) ? 1 : 0).FirstOrDefault()
                        ?? alive.FirstOrDefault(p => p.Spawned);
                    if (playerPatient != null && playerPatient.Position.Fogged(map))
                    {
                        // Fogged cells get no float menu at all, as for the player: someone has to look into that room first.
                        FloodFillerFog.FloodUnfog(playerPatient.Position, map);
                    }
                    if (doctor == null || playerPatient == null)
                    {
                        Note($"Cannot test tending by your doctors (doctor {doctor?.LabelShort}, patient {playerPatient?.LabelShort}); skipping");
                        consequencePhase = 2;
                        break;
                    }
                    if (!playerPatient.health.HasHediffsNeedingTend())
                    {
                        playerPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 3f, armorPenetration: 999f));
                    }
                    // The surrendered medics wait out a fresh ceasefire, so the colony's doctor is the one who tends.
                    morale.Notify_CeasefireBroken();
                    Thing herbal = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                    herbal.stackCount = 2;
                    doctor.inventory.innerContainer.TryAdd(herbal);
                    IntVec3 near = CellFinder.StandableCellNear(playerPatient.Position, map, 3f);
                    if (near.IsValid)
                    {
                        doctor.jobs.StopAll();
                        doctor.Position = near;
                        doctor.Notify_Teleported();
                    }
                    Current.Game.CurrentMap = map;
                    string prefix = "Tend".Translate(playerPatient);
                    string without = "WithoutMedicine".Translate();
                    List<FloatMenuOption> options = FloatMenuMakerMap.ChoicesAtFor(playerPatient.DrawPos, doctor);
                    FloatMenuOption tend = options.FirstOrDefault(o => o.Label.StartsWith(prefix) && o.action != null && !o.Label.Contains(without));
                    if (tend == null)
                    {
                        Note("Float menu options: " + options.Select(o => o.Label).ToCommaList());
                    }
                    Check(tend != null, $"the float menu offers tending {playerPatient.LabelShort}, who surrendered (downed {playerPatient.Downed})");
                    Check(options.Any(o => o.Label.StartsWith(prefix) && o.Label.Contains(without)) || tend?.Label.Contains(without) == true, "tending someone who surrendered can be done without medicine");
                    tendedBefore = morale.tendedByPlayer;
                    Pawn doctorShot = doctor;
                    Pawn patientShot = playerPatient;
                    Shoot("06_tend_surrendered", () =>
                    {
                        FrameOn(map, patientShot.Position, 12f);
                        SelectOnMap(doctorShot);
                        OpenFloatMenuAtCenter(FloatMenuMakerMap.ChoicesAtFor(patientShot.DrawPos, doctorShot));
                    }, CloseWindows);
                    doctor.jobs.debugLog = true;
                    tend?.action();
                    medicMark = now;
                    consequencePhase = 1;
                    break;
                case 1:
                    if ((now - medicMark) % 600 == 0)
                    {
                        // Keep the surrendered medics down so the colony's doctor is the one who tends.
                        morale.Notify_CeasefireBroken();
                    }
                    if (now - medicMark == 1 || (now - medicMark) % 500 == 0)
                    {
                        Note($"  doctor {doctor.LabelShort}: job {doctor.CurJobDef?.defName} {doctor.CurJob?.targetA.Thing?.LabelShort} toil '{doctor.jobs.curDriver?.CurToilString}', at {doctor.Position}; "
                            + $"patient at {playerPatient.Position}, job {playerPatient.CurJobDef?.defName}, needs tending {playerPatient.health.HasHediffsNeedingTend()}, capitulated {SurrenderUtility.HasCapitulated(playerPatient)}");
                    }
                    if (now - medicMark == 30)
                    {
                        doctor.jobs.debugLog = false;
                    }
                    if (morale.tendedByPlayer > tendedBefore)
                    {
                        Check(true, $"your doctor tended someone who surrendered ({now - medicMark} ticks)");
                        doctor.jobs.debugLog = false;
                        consequencePhase = 2;
                    }
                    else if (now - medicMark > 4000)
                    {
                        Fail($"your doctor did not tend someone who surrendered (job {doctor.CurJobDef?.defName} {doctor.jobs.curDriver?.CurToilString}, patient needs tending {playerPatient.health.HasHediffsNeedingTend()})");
                        doctor.jobs.debugLog = false;
                        consequencePhase = 2;
                    }
                    break;
                case 2:
                    TestKillingSurrendered(map, morale, alive);
                    Next(5);
                    break;
            }
        }

        private void TestKillingSurrendered(Map map, MapComponent_SiegeMorale morale, List<Pawn> alive)
        {
            Pawn killer = doctor ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            Pawn victim = alive.FirstOrDefault(p => p != playerPatient && SurrenderUtility.IsSurrendered(p));
            if (killer == null || victim == null)
            {
                Note("Nobody to test killing someone who surrendered; skipping");
                return;
            }
            var goodwillBefore = new Dictionary<Faction, int>();
            foreach (Faction faction in Find.FactionManager.AllFactionsListForReading)
            {
                if (!faction.IsPlayer && !faction.Hidden && !faction.defeated && faction != victim.Faction && !ProtectorateUtility.IsProtectorate(faction)
                    && faction.def.humanlikeFaction && !faction.HostileTo(Faction.OfPlayer))
                {
                    goodwillBefore[faction] = faction.PlayerGoodwill;
                }
            }
            int killedBefore = morale.surrenderedKilled;
            victim.Kill(new DamageInfo(DamageDefOf.Cut, 99f, instigator: killer));
            Check(victim.Dead && morale.surrenderedKilled == killedBefore + 1, "killing someone who surrendered is counted");

            var views = new List<string>();
            bool thoughtsRight = true;
            foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned)
            {
                SurrenderConsequencesUtility.View view = SurrenderConsequencesUtility.ViewOf(colonist);
                int stage = SurrenderConsequencesUtility.KilledStage(view);
                bool has = colonist.needs.mood.thoughts.memories.Memories.Any(m => m.def == OA_DefOf.OA_KilledSurrendered && m.CurStageIndex == stage);
                bool any = colonist.needs.mood.thoughts.memories.Memories.Any(m => m.def == OA_DefOf.OA_KilledSurrendered);
                thoughtsRight &= stage >= 0 ? has : !any;
                views.Add($"{colonist.LabelShort}: {view}{(stage >= 0 ? " " + (has ? "has thought" : "NO thought") : "")}");
            }
            Note("Views on killing the surrendered: " + views.ToCommaList());
            Check(thoughtsRight, "colonists react to the killing according to their ideoligion and traits");
            Pawn upset = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => c != killer && c.needs.mood.thoughts.memories.GetFirstMemoryOfDef(OA_DefOf.OA_KilledSurrendered) != null)
                ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => c.needs.mood.thoughts.memories.GetFirstMemoryOfDef(OA_DefOf.OA_KilledSurrendered) != null);

            if (goodwillBefore.Count == 0)
            {
                Note("No faction at peace with the colony; goodwill not checked");
            }
            else
            {
                Note("Goodwill: " + goodwillBefore.Select(kv => $"{kv.Key.Name} {kv.Value} -> {kv.Key.PlayerGoodwill}").ToCommaList());
                Check(goodwillBefore.All(kv => kv.Key.PlayerGoodwill < kv.Value), "factions at peace lose goodwill over the killing");
            }

            // The other way round: sparing everyone. Checked directly, since this stay did have a killing.
            Pawn spared = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => SurrenderConsequencesUtility.SparedStage(SurrenderConsequencesUtility.ViewOf(p)) >= 0);
            if (spared != null)
            {
                SurrenderConsequencesUtility.GiveSparedThoughts(new[] { spared });
                Check(spared.needs.mood.thoughts.memories.GetFirstMemoryOfDef(OA_DefOf.OA_SparedSurrendered) != null, "sparing the defeated gives a good memory");
            }
            // The picture shows whichever thought someone has; the test memory is taken back afterwards.
            Pawn thinker = upset ?? spared;
            if (thinker != null)
            {
                Shoot("07_thoughts", () =>
                {
                    FrameOn(map, thinker.Position, 12f);
                    SelectOnMap(thinker);
                    ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = typeof(ITab_Pawn_Needs);
                }, () =>
                {
                    ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = null;
                    CloseWindows();
                    spared?.needs.mood.thoughts.memories.RemoveMemoriesOfDef(OA_DefOf.OA_SparedSurrendered);
                });
            }
            if (ShotDir == null)
            {
                spared?.needs.mood.thoughts.memories.RemoveMemoriesOfDef(OA_DefOf.OA_SparedSurrendered);
            }
        }

        /// <summary>
        /// What leaving the conquered town should do to its loyalty: kills and prisoners cost, tending by your doctors helps.
        /// </summary>
        private void RecordLeaveExpectations()
        {
            MapComponent_SiegeMorale morale = town.Map?.GetComponent<MapComponent_SiegeMorale>();
            if (morale == null)
            {
                return;
            }
            float loss = morale.surrenderedKilled * OAMod.Settings.loyaltyLossPerKilledSurrendered + morale.prisonersTaken * 2f;
            float gain = Mathf.Min(OccupationUtility.MaxLoyaltyFromTending, morale.tendedByPlayer * OccupationUtility.LoyaltyPerTend);
            expectedLoyaltyAfterLeave = Mathf.Clamp(Mathf.Clamp(town.loyalty - loss, 0f, 100f) + gain, 0f, 100f);
            killedDuringStay = morale.surrenderedKilled > 0;
            leavers = town.Map.mapPawns.FreeColonistsSpawned.ToList();
            Note($"Leaving: loyalty {town.loyalty:F1}, killed {morale.surrenderedKilled}, prisoners {morale.prisonersTaken}, tended by your doctors {morale.tendedByPlayer} -> expected {expectedLoyaltyAfterLeave:F1}");
        }

        /// <summary>
        /// Newcomers until the beds are full, volunteers for the colony, and the militia's weight in a fight.
        /// </summary>
        private void TestPopulation()
        {
            town.loyalty = 100f;
            int housing = PopulationUtility.Housing(town);
            int population = town.PopulationCount;
            Note($"Population {population}, housing {housing}, growth chance {PopulationUtility.GrowthChancePerDay(town):P0} a day");
            if (population < housing)
            {
                Check(PopulationUtility.GrowthChancePerDay(town) > 0f, "a loyal town with free beds can grow");
                Pawn newcomer = PopulationUtility.AddNewcomer(town);
                Check(town.PopulationCount == population + 1 && town.Population.Contains(newcomer), "a newcomer settles in the town");
                Check(newcomer.Faction == town.Faction && newcomer.equipment?.Primary == null && !newcomer.inventory.innerContainer.Any() && !newcomer.IsWorldPawn(),
                    "the newcomer is an unarmed townsperson held by the town");
            }
            int savedHousing = town.housing;
            town.housing = town.PopulationCount;
            Check(PopulationUtility.GrowthChancePerDay(town) == 0f, "no growth once every bed is taken");
            town.housing = savedHousing;
            town.loyalty = 40f;
            Check(PopulationUtility.GrowthChancePerDay(town) == 0f, "no growth in a town that is not loyal enough");
            town.loyalty = 100f;

            if (caravan == null || caravan.Destroyed)
            {
                caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && c.Tile == town.Tile);
            }
            if (caravan == null)
            {
                Fail("No caravan at the town to test recruiting");
            }
            else
            {
                string label = "OA_CommandRecruit".Translate();
                Command recruitCommand = town.GetCaravanGizmos(caravan).OfType<Command>().FirstOrDefault(c => c.defaultLabel == label);
                Check(recruitCommand != null && !recruitCommand.Disabled, $"an annexed, loyal town offers a volunteer ({recruitCommand?.disabledReason})");
                Pawn volunteer = PopulationUtility.RecruitCandidates(town).FirstOrDefault();
                if (volunteer != null)
                {
                    int before = town.PopulationCount;
                    float loyaltyBefore = town.loyalty;
                    PopulationUtility.Recruit(town, volunteer, caravan);
                    Check(volunteer.Faction == Faction.OfPlayer && volunteer.IsColonist && caravan.PawnsListForReading.Contains(volunteer) && volunteer.IsWorldPawn(),
                        $"{volunteer.LabelShort} joins the colony and the caravan");
                    Check(town.PopulationCount == before - 1 && Mathf.Abs(town.loyalty - (loyaltyBefore - PopulationUtility.RecruitLoyaltyCost)) < 0.01f, "recruiting takes a townsperson and some loyalty");
                    recruitCommand = town.GetCaravanGizmos(caravan).OfType<Command>().FirstOrDefault(c => c.defaultLabel == label);
                    Check(recruitCommand != null && recruitCommand.Disabled, $"the next volunteer has to wait ({recruitCommand?.disabledReason})");
                }
            }

            string militiaLabel = "OA_CommandMilitia".Translate();
            Check(town.GetGizmos().OfType<Command_Toggle>().Any(c => c.defaultLabel == militiaLabel), "the town has a militia toggle");
            if (TownMilitiaUtility.WeaponsInStock(town) == 0)
            {
                town.Store(ThingMaker.MakeThing(ThingDef.Named("Gun_Revolver"), null));
            }
            town.militia = false;
            float withoutMilitia = TownEventsUtility.DefenseStrength(town);
            float outputWithout = EconomyUtility.ProductionEfficiency(town);
            town.militia = true;
            float withMilitia = TownEventsUtility.DefenseStrength(town);
            Note($"Militia of {TownMilitiaUtility.MilitiaSize(town)} ({TownMilitiaUtility.WeaponsInStock(town)} weapons in stock): defense {withoutMilitia:F2} -> {withMilitia:F2}");
            Check(withMilitia > withoutMilitia, "the militia strengthens the town's defense");
            Check(EconomyUtility.ProductionEfficiency(town) < outputWithout, "training the militia costs production");
            town.loyalty = TownMilitiaUtility.DisbandLoyalty - 5f;
            TownMilitiaUtility.DailyCheck(town);
            Check(!town.militia, "a disloyal town disbands its militia");
            town.loyalty = 90f;
            Shoot("13_world_annexed", () => ShowOnWorld(town, typeof(WITab_Town)), HideWorld);
            if (caravan != null && !caravan.Destroyed)
            {
                Caravan caravanShot = caravan;
                Shoot("14_world_caravan", () => ShowOnWorld(caravanShot, null), HideWorld);
            }
            Next(15);
        }

        private void TestGarrisonAndGifts()
        {
            if (caravan == null || caravan.Destroyed)
            {
                caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && c.Tile == town.Tile);
            }
            Pawn guard = caravan?.PawnsListForReading.FirstOrDefault(p => p.IsFreeColonist && !p.Downed);
            if (guard == null || caravan.PawnsListForReading.Count(p => p.IsFreeColonist && !p.Downed) < 2)
            {
                Note("Not enough colonists in the caravan to test the garrison; skipping");
                Next(22);
                return;
            }
            float loyaltyBefore = town.loyalty;
            town.StationGarrison(guard, caravan);
            Check(town.GarrisonCount == 1 && !caravan.PawnsListForReading.Contains(guard), "colonist stationed as garrison");
            Check(!Find.WorldPawns.Contains(guard), "garrison is held by the town, not by world pawns");
            EconomyUtility.UpdateLoyalty(town);
            Check(town.loyalty > loyaltyBefore || town.loyalty >= 100f, $"garrison raises loyalty ({loyaltyBefore:F1} -> {town.loyalty:F1})");

            Thing gift = CaravanInventoryUtility.AllInventoryItems(caravan).FirstOrDefault();
            if (gift != null)
            {
                int stacksBefore = town.Stock.Count();
                gift.holdingOwner?.Remove(gift);
                town.Store(gift);
                Check(town.Stock.Contains(gift) || town.Stock.Count() >= stacksBefore, "gift stored in the stockpile");
            }

            town.WithdrawGarrison();
            Check(town.GarrisonCount == 0, "garrison withdrawn");
            Check(Find.WorldObjects.Caravans.Any(c => c.PawnsListForReading.Contains(guard)), "withdrawn garrison forms a caravan");
            Check(Find.WorldPawns.Contains(guard), "withdrawn colonist is a world pawn again");
            Next(22);
        }

        private void TestEvents()
        {
            int tile = town.Tile;
            TownEventsUtility.RetakeAttempt(town);
            Check(town.retakeAttackTick > 0, "retake attempt scheduled");
            TownEventsUtility.ResolveRetake(town);
            if (town.Destroyed)
            {
                Check(Find.WorldObjects.Settlements.Any(s => s.Tile == tile), "lost town became a settlement again");
            }
            else
            {
                town.loyalty = 0f;
                TownEventsUtility.Uprising(town);
                Check(town.Destroyed && Find.WorldObjects.Settlements.Any(s => s.Tile == tile), "uprising turned the town into a hostile settlement");
            }
            Next(16);
        }

        /// <summary>
        /// Opens every window and tab of the mod for a few frames; errors while drawing are caught by the log hook.
        /// </summary>
        private void UiSmokeTest()
        {
            const int FramesPerStage = 30;
            if (uiStage > 0)
            {
                if (ShotDir != null && uiFrames == 20)
                {
                    string[] names = { "10_settings", null, null, null, null, "09_delivery_dialog" };
                    if (uiStage - 1 < names.Length && names[uiStage - 1] != null && shotsTaken.Add(names[uiStage - 1]))
                    {
                        Capture(names[uiStage - 1]);
                    }
                }
                if (uiFrames < FramesPerStage)
                {
                    return;
                }
                uiWindow?.Close(doCloseSound: false);
                uiWindow = null;
            }
            else
            {
                uiErrorsAtStart = errorsSeen;
            }
            uiFrames = 0;
            if (ShotDir != null)
            {
                Messages.Clear();
            }
            switch (uiStage++)
            {
                case 0:
                    Note("UI: mod settings");
                    if (ShotDir != null)
                    {
                        // The picture shows the settings a player gets, not the test's.
                        OAMod.Settings.minCombatTicks = 1250;
                        OAMod.Settings.debugLogging = false;
                    }
                    uiWindow = new Dialog_ModSettings(LoadedModManager.GetMod<OAMod>());
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 1:
                    OAMod.Settings.minCombatTicks = 0;
                    OAMod.Settings.debugLogging = true;
                    Note("UI: town on the world map (inspect string, gizmos, town tab)");
                    CameraJumper.TryShowWorld();
                    Find.WorldSelector.ClearSelection();
                    Find.WorldSelector.Select(town, playSound: false);
                    Find.World.UI.inspectPane.OpenTabType = typeof(WITab_Town);
                    break;
                case 2:
                    Note("UI: caravan at the town (caravan gizmos)");
                    Find.WorldSelector.ClearSelection();
                    Find.WorldSelector.Select(caravan, playSound: false);
                    Check(town.GetFloatMenuOptions(caravan).Any(), "town offers caravan float menu options");
                    Check(town.GetCaravanGizmos(caravan).Any(), "town offers caravan gizmos");
                    Check(town.GetGizmos().Any(), "town offers gizmos");
                    break;
                case 3:
                    Note("UI: take from stockpile dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, caravan, gift: false);
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 4:
                    Note("UI: gift dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, caravan, gift: true);
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 5:
                    Note("UI: delivery dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, Find.AnyPlayerHomeMap);
                    Find.WindowStack.Add(uiWindow);
                    break;
                default:
                    Find.WorldSelector.ClearSelection();
                    CameraJumper.TryHideWorld();
                    Check(errorsSeen == uiErrorsAtStart, $"UI drew without errors ({errorsSeen - uiErrorsAtStart} errors)");
                    uiStage = 0;
                    Next(7);
                    return;
            }
        }

        // ------------------------------------------------------------------ screenshots (-oa_screenshots=<dir>)

        private class Shot
        {
            public string name;
            public Action setup;
            public Action cleanup;
            public bool keepMessages;
        }

        private static readonly string ShotDir = GenCommandLine.TryGetCommandLineArg("oa_screenshots", out string shotDir) ? shotDir : null;
        private readonly List<Shot> shotQueue = new List<Shot>();
        private readonly HashSet<string> shotsTaken = new HashSet<string>();
        private int shotFrames;

        /// <summary>
        /// Queues a picture for the Workshop page. The game pauses, <paramref name="setup"/> frames the scene,
        /// and the frame is captured once the camera and the UI have settled.
        /// </summary>
        private void Shoot(string name, Action setup, Action cleanup = null, bool keepMessages = false)
        {
            if (ShotDir != null && shotsTaken.Add(name))
            {
                shotQueue.Add(new Shot { name = name, setup = setup, cleanup = cleanup, keepMessages = keepMessages });
                // Stops the remaining ticks of this frame, so the scene is still there when the picture is taken.
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }
        }

        private void UpdateShots()
        {
            Shot shot = shotQueue[0];
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Prefs.DevMode = false;
            if (shotFrames == 0)
            {
                if (!shot.keepMessages)
                {
                    // The test's own bookkeeping leaves messages no player would see together.
                    Messages.Clear();
                }
                try
                {
                    shot.setup();
                }
                catch (Exception e)
                {
                    Note($"Screenshot {shot.name} could not be set up: {e.Message}");
                    shotFrames = 1000;
                }
            }
            Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), doCloseSound: false);
            shotFrames++;
            if (shotFrames == 40)
            {
                Capture(shot.name);
            }
            else if (shotFrames >= 60)
            {
                try
                {
                    shot.cleanup?.Invoke();
                }
                catch (Exception e)
                {
                    Note($"Screenshot {shot.name} could not be cleaned up: {e.Message}");
                }
                shotQueue.RemoveAt(0);
                shotFrames = 0;
                Prefs.DevMode = true;
            }
        }

        private void Capture(string name)
        {
            Directory.CreateDirectory(ShotDir);
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, name + ".png"));
            Note("Screenshot " + name);
        }

        private static void FrameOn(Map map, IntVec3 cell, float size)
        {
            CameraJumper.TryHideWorld();
            Current.Game.CurrentMap = map;
            Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted(), size);
        }

        private static void SelectOnMap(Thing thing)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
        }

        /// <summary>A right-click menu next to whatever the camera is centred on.</summary>
        private static void OpenFloatMenuAtCenter(List<FloatMenuOption> options)
        {
            var menu = new FloatMenu(options) { vanishIfMouseDistant = false };
            Find.WindowStack.Add(menu);
            menu.windowRect.x = UI.screenWidth / 2f + 24f;
            menu.windowRect.y = UI.screenHeight / 2f - 16f;
        }

        private static void CloseWindows()
        {
            foreach (Window window in Find.WindowStack.Windows.Where(w => w is FloatMenu || w is Dialog_NodeTree).ToList())
            {
                window.Close(doCloseSound: false);
            }
            Find.Selector.ClearSelection();
        }

        private static void ShowOnWorld(WorldObject worldObject, Type tab)
        {
            CameraJumper.TryJumpAndSelect(worldObject);
            Find.WorldSelector.ClearSelection();
            Find.WorldSelector.Select(worldObject, playSound: false);
            Find.World.UI.inspectPane.OpenTabType = tab;
        }

        private static void HideWorld()
        {
            Find.WorldSelector.ClearSelection();
            CameraJumper.TryHideWorld();
        }

        /// <summary>The assault squad of the pictures carries rifles and armor.</summary>
        private static void EquipForPictures(Pawn pawn)
        {
            ThingDef gun = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_AssaultRifle");
            if (gun != null)
            {
                pawn.equipment.DestroyAllEquipment();
                pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
            }
            foreach (string name in new[] { "Apparel_FlakVest", "Apparel_AdvancedHelmet" })
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def != null)
                {
                    pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)), dropReplacedApparel: false);
                }
            }
        }

        // ------------------------------------------------------------------ Vehicle Framework

        private static readonly Type VehiclePawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");

        private void TrySpawnPlayerVehicle(Map map)
        {
            if (VehiclePawnType == null)
            {
                return;
            }
            try
            {
                Type defType = AccessTools.TypeByName("Vehicles.VehicleDef");
                Def def = GenDefDatabase.GetDefSilentFail(defType, "VVE_BangBus") ?? GenDefDatabase.GetAllDefsInDatabaseForDef(defType).FirstOrDefault();
                MethodInfo generate = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleSpawner"), "GenerateVehicle", new[] { defType, typeof(Faction) });
                var vehicle = (Pawn)generate.Invoke(null, new object[] { def, Faction.OfPlayer });
                Pawn colonist = map.mapPawns.FreeColonistsSpawned.First();
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(colonist.Position, map, 8, c => c.Standable(map));
                GenSpawn.Spawn(vehicle, cell, map, Rot4.North);
                Note($"Spawned player vehicle {vehicle.LabelShort} ({def.defName}) at {cell}");
            }
            catch (Exception e)
            {
                Fail("Could not spawn a player vehicle: " + e);
            }
        }

        /// <summary>
        /// Vanilla "Reform caravan" and Vehicle Framework's "Reform vehicle caravan" must both be usable once the town is taken.
        /// </summary>
        private void CheckReformGizmos(Map map, string when)
        {
            bool vanillaThreat = GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: true);
            bool defaultThreat = GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer, out IAttackTarget threat);
            Note($"Threats {when}: vanilla check {vanillaThreat}, default check {defaultThreat}, first threat {threat?.Thing?.ToString() ?? "none"} ({threat?.Thing?.Faction?.Name})");
            int listed = 0;
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer))
            {
                if (listed++ >= 25)
                {
                    break;
                }
                Thing thing = target.Thing;
                Note($"  hostile target {thing} [{thing.GetType().Name}] faction {thing.Faction?.Name}, active threat {GenHostility.IsActiveThreatTo(target, Faction.OfPlayer)}, threat disabled {target.ThreatDisabled(null)}, surrendered {thing is Pawn p && SurrenderUtility.IsSurrendered(p)}, downed {(thing as Pawn)?.Downed}");
            }
            FormCaravanComp comp = map.Parent.GetComponent<FormCaravanComp>();
            if (comp == null)
            {
                Fail("the town has no FormCaravanComp");
                return;
            }
            foreach (Gizmo gizmo in comp.GetGizmos())
            {
                if (gizmo is Command command)
                {
                    Note($"  caravan gizmo '{command.defaultLabel}' disabled {command.Disabled} {command.disabledReason}");
                    if (VehiclePawnType != null && command.defaultLabel == "VF_CommandReformVehicleCaravan".Translate())
                    {
                        Check(!command.Disabled, $"Reform vehicle caravan is available {when}");
                    }
                }
            }
            Check(!vanillaThreat && !defaultThreat, $"no active threats {when}");
        }

        // ------------------------------------------------------------------ reporting

        private void Check(bool condition, string what)
        {
            checks++;
            if (condition)
            {
                Note("PASS " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private void Fail(string what)
        {
            failures++;
            Note("FAIL " + what);
        }

        private void Note(string text)
        {
            string line = $"[{Find.TickManager?.TicksGame}] {text}";
            log.Add(line);
            Log.Message("[OA AutoTest] " + line);
        }

        private void Finish()
        {
            Note($"Errors logged during the whole run: {errorsSeen}");
            foreach (string sample in errorSamples)
            {
                Note("  error: " + sample);
            }
            Note($"DONE: {checks} checks, {failures} failures");
            step = 100;
            try
            {
                if (GenCommandLine.TryGetCommandLineArg("oa_report", out string path))
                {
                    File.WriteAllLines(path, log);
                }
            }
            catch (Exception e)
            {
                Log.Error("[OA AutoTest] Could not write report: " + e);
            }
            if (GenCommandLine.CommandLineArgPassed("oa_autotest_quit"))
            {
                Root.Shutdown();
            }
        }
    }
}
