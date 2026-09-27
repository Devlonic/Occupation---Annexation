# Occupation & Annexation (RimWorld 1.5)

Enemy settlements no longer have to be wiped out. Break their defense and the survivors
surrender; occupy the town, win its loyalty, annex it, collect its taxes and visit it.

## Gameplay

1. **Capitulation.** While you assault a hostile faction settlement, the defenders' morale
   is tracked: lost fighters, destroyed turrets, a fallen commander, suppression
   (Combat Extended) and being outnumbered. When it breaks, every survivor drops weapons
   and ammo and lies face down. Surrendered pawns are no threat: colonists and turrets
   ignore them. Right-click one to **take them prisoner** on the spot. Settlement
   defenders no longer panic-flee off the map (configurable).
   Once no one has fired at them for 15–40 seconds (each medic waits a different time),
   surrendered pawns who can doctor get up and **tend their wounded** who weren't taken
   prisoner. They use only the medicine they carry, then lie down again. Fire at them or
   near them, or hurt one of them, and every medic abandons the wounded, lies face down
   and the wait starts over.
2. **Occupation.** Instead of becoming ruins, the settlement becomes a town of your
   **Protectorate**, a single permanently allied faction created on first use. Its
   buildings and turrets switch sides. When your people leave, the survivors stay as the
   town's population, and everything left lying around goes into its stockpile.
3. **Loyalty.** An occupied town starts resentful. Loyalty grows every day, faster with a
   **garrison** (colonists stationed there from a caravan), **low taxes** and **gifts**.
   Killing people who surrendered or locals, and taking prisoners, costs loyalty.
4. **Annexation.** At the configured loyalty and minimum days, press **Annex**.
   Annexed towns work at full strength and their loyalty settles at a content level.
5. **Economy.** Towns produce goods every day, depending on what they had when captured:
   fields → crops, kitchens → provisions, smithies → steel and components, and so on.
   Taxes always bring silver. Output depends on working adults, loyalty, tax level, and
   occupied vs. annexed. The stockpile has a limit per inhabitant. Profiles are XML
   (`Defs/Economy/OA_ProductionProfiles.xml`) and can be extended by other mods.
6. **Collecting.**
   - A caravan at the town: *Take from stockpile* / *Give gifts*.
   - *Request delivery*: townsfolk bring the chosen goods to your colony with pack
     animals, or by drop pod for industrial+ towns.
   - Visiting the town: the stockpile lies in its warehouse.
7. **Visits.** *Visit / Enter town* regenerates the map from a **snapshot** of the town as
   you left it: terrain, floors, roofs, every building with its damage, and crops. The
   locals follow a daily routine:
   - working at benches, tending barrels, generators and fires;
   - farming, hauling in the warehouse;
   - **repairing battle damage** (this is real and persists) and cleaning;
   - guards patrolling, socialising in the evening, sleeping in their beds at night.

   Needs are frozen while they live on schedule.
8. **Events.** Disloyal towns may **rise up**; a garrison can put the uprising down. Former
   owners may try to **retake** the town: you get two days of warning. With you on site, it
   is a real raid on the town map; otherwise it is resolved from garrison and loyalty. A
   lost town becomes a hostile settlement again.

## Compatibility

- **Harmony** is required.
- **Combat Extended** and **CAI 5000** are optional. They are detected at runtime, with no
  hard references:
  - CE suppression lowers defender morale;
  - surrendered pawns ignore suppression and don't re-arm;
  - CAI's combat reasoning skips surrendered pawns.
- The visit map generator does not inherit `MapCommonBase`, so mods that add scatter steps
  there (e.g. Real Ruins) do not touch towns.

## Development

```
Source/OccupationAnnexation/OccupationAnnexation.csproj   # net472, Krafs.Rimworld.Ref 1.5.4409, Lib.Harmony 2.3.3
dotnet build -c Release                                  # outputs to 1.5/Assemblies
```

The mod folder is linked into the game with a directory junction:
`RimWorld\Mods\OccupationAnnexation -> D:\Development\rimworld`.

The game locks the DLL while running; close it before rebuilding.

### Dev-mode tools

- **Debug actions**, category *Occupation & Annexation*: *Force capitulation*,
  *Log siege morale*, *Log hostile threats* (what keeps "Reform caravan" disabled),
  *Log surrendered medics* (the ceasefire and when each surrendered pawn may tend).
- **Town gizmos** with *Show dev gizmos* on: loyalty ±20, pass a day, produce 10 days,
  trigger uprising, trigger retake attempt.

### End-to-end self test

```
RimWorldWin64.exe -quicktest -oa_autotest -oa_autotest_quit -oa_report=C:\path\report.txt ^
    -savedatafolder=C:\path\testdata -logFile C:\path\rw.log
```

Use a separate `-savedatafolder` whose `Config/ModsConfig.xml` activates Harmony, the DLCs,
optionally CE/CAI, and this mod. Set `autosaveIntervalDays` > 0 in its `Prefs.xml`.

The test covers:
- the assault, capitulation, taking a prisoner and occupation;
- surrendered medics: the 15–40 s ceasefire, tending, real shots near them and far away,
  and hurting one of them;
- leaving the map, and a save/load round-trip;
- annexing, production, caravan and drop-pod deliveries;
- opening every window, tab and gizmo;
- a full-day visit with checks on snapshot restoration and town jobs;
- a second leave;
- retake and uprising.

It writes the PASS/FAIL report and counts every error logged during the run.
