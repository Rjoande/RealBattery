using B9PartSwitch;
using KSP.Localization;
using KSP.UI.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RealBattery
{
    public partial class RealBattery : PartModule
    {
        // ============================================================================
        //  REALBATTERY – CONSTANTS & IDS
        //  Keep numeric thresholds, resource IDs, and tiny helpers here.
        // ============================================================================

        // --- Resource ratios / conversions ---
        public const double EC2SCratio = 3600;      // 3600 EC = 1 SC = 1 kWh

        // Cryo waste heat flux per liter of battery volume (W/L). Empirically matched to
        // CryoTanks' CoolingHeatCost. Moved to RealBatteryTuning.CryoWasteHeatPerL (T3.5) so the
        // T3b calibration protocol can test candidate values via an optional cfg override,
        // without recompiling; RealBatteryTuning.DEFAULT_CRYO_WASTE_HEAT_W_PER_L holds the same
        // 0.00002f value as the compiled-in default.

        // Volume-based warmup scaling: reference volume (L) at which nominal duration = 60 s.
        private const double WARMUP_VOL_REF = 200.0;
        // Hard cap on warmup/shutdown duration regardless of volume.
        private const double WARMUP_MAX_S   = 720.0;

        // --- End-of-life threshold ---
        const double EOL_THRESHOLD = 0.80;          // 80% of BatteryLife

        // --- Numeric epsilons ---
        private const double EPS = 1e-6;            // generic numeric epsilon (unify scattered locals)

        // --- KeepWarm upkeep multiplier ---
        // Thresholds are now per-instance fields: TempKeepWarmLo / TempKeepWarmHi
        private const double WARMUP_MULT = 6.0;       // warmup uses 6x upkeep cost

        // --- Thermal runaway tail model ---
        private const double RUNAWAY_TAU_SECONDS = 10.0; // exponential decay time constant

        // --- GUI_power display smoothing ---
        // Time constant (seconds) for the displayed charge/discharge power. Framerate- and
        // magnitude-independent by design: settles within ~4 tau (a couple of seconds) of the
        // real value regardless of session fps or battery DischargeRate.
        private const double GUI_POWER_SMOOTH_TAU_S = 0.4;

        // --- SystemHeat smoothFlux smoothing ---
        // Time constant (seconds) equivalent to the old per-tick ratio (0.01 at the reference
        // 50 Hz / 0.02 s physics tick: tau = fixedDeltaTime / ratio = 0.02 / 0.01 = 2.0 s).
        // Time-based like GUI_POWER_SMOOTH_TAU_S above: identical settle time at warp 1x,
        // correct (framerate/timewarp-independent) elsewhere.
        private const double SMOOTH_FLUX_TAU_S = 2.0;

        // --- Resource IDs (cached) ---
        // Lazy-resolved on first access (not a static-field initializer): a missing
        // CommunityResourcePack dependency would otherwise NRE inside the type initializer,
        // surfacing as a cryptic TypeInitializationException that takes down every RealBattery
        // module on load. Here it instead logs one clear diagnostic and disables charge/discharge
        // cleanly via ScResourceMissing (checked at the top of XferECtoRealBattery).
        private static int? _scIdCache;
        private static bool ScResourceMissing;
        private static int SC_ID
        {
            get
            {
                if (_scIdCache.HasValue) return _scIdCache.Value;

                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition("StoredCharge");
                if (def == null)
                {
                    RBLog.Error("[RealBattery] StoredCharge resource definition not found — is CommunityResourcePack installed? Charge/discharge disabled for all RealBattery parts.");
                    ScResourceMissing = true;
                    _scIdCache = 0;
                }
                else
                {
                    _scIdCache = def.id;
                }
                return _scIdCache.Value;
            }
        }


        // ============================================================================
        //  KSP FIELDS – CONFIGURATION & UI
        // ============================================================================

        // --- Configuration knobs (cfg-driven; non-persistent) -----------------------
        // Only charge/discharge gates and curves
        [KSPField(isPersistant = false)] public bool moduleActive = true;        // hide PAW for non-battery subtypes
        [KSPField(isPersistant = false)] public float HighEClevel = RBDefaults.HighEClevel;       // charge gate
        [KSPField(isPersistant = false)] public float LowEClevel = RBDefaults.LowEClevel;         // discharge gate
        [KSPField(isPersistant = false)] public float Crate = RBDefaults.Crate;                   // C-rate
        [KSPField(isPersistant = false)] public double SelfDischargeRate = RBDefaults.SelfDischargeRate; // % per day
        [KSPField(isPersistant = false)] public double CycleDurability = RBDefaults.CycleDurability;     // cycles until wear
        [KSPField(isPersistant = false)] public bool FixedOutput = RBDefaults.FixedOutput;        // thermal battery mode
        [KSPField(isPersistant = false)] public FloatCurve ChargeEfficiencyCurve = new FloatCurve();
        // Thermal model
        [KSPField(isPersistant = false)] public double ThermalLoss = RBDefaults.ThermalLoss;      // kW per EC/s
        [KSPField(isPersistant = false)] public float TempOptimal = RBDefaults.TempOptimal;       // K — target K signal for SystemHeat loop only
        [KSPField(isPersistant = false)] public float TempOverheat = RBDefaults.TempOverheat;     // K — RB internal threshold (wear/runaway/PCM/cap)
        [KSPField(isPersistant = false)] public float TempRunaway = RBDefaults.TempRunaway;       // K
        [KSPField(isPersistant = false)] public double RunawayHeatFactor = RBDefaults.RunawayHeatFactor; // <=0: use Crate
        [KSPField(isPersistant = false)] public bool KeepWarm = false;          // heat-aware warmup/upkeep (legacy v2 bool)
        [KSPField(isPersistant = false)] public bool SelfRunaway = RBDefaults.SelfRunaway;       // spontaneous runaway

        // --- v3 Chemistry DB fields -------------------------------------------------
        // ChemistryID: if non-empty, all parameters below are populated from the DB.
        // Falls back to inline cfg values when empty or ID not found (full v2 compat).
        [KSPField(isPersistant = false)] public string ChemistryID = "";
        // KeepWarmMode replaces the v2 bool KeepWarm; "false" | "warm" | "cryo"
        [KSPField(isPersistant = false)] public string KeepWarmMode   = RBDefaults.KeepWarmMode;
        [KSPField(isPersistant = false)] public float  TempKeepWarmLo = RBDefaults.TempKeepWarmLo;     // K: lower upkeep threshold
        [KSPField(isPersistant = false)] public float  TempKeepWarmHi = RBDefaults.TempKeepWarmHi;     // K: upper upkeep threshold
        [KSPField(isPersistant = false)] public bool   InfiniteCycles = RBDefaults.InfiniteCycles;     // no charge/discharge wear (SMES)
        [KSPField(isPersistant = false)] public bool   LifeDecay      = RBDefaults.LifeDecay;          // SDR decays BatteryLife, not SoC
        [KSPField(isPersistant = false)] public double RunawayBaseChance = RBDefaults.RunawayBaseChance; // per-chemistry RIP chance basis
        // CrateScale: "false" | "add" | "reduce"; read by RealBatteryLoadMaster to scale
        // Crate by the count of participating batteries. Not shown in PAW.
        [KSPField(isPersistant = false)] public string CrateScale     = RBDefaults.CrateScale;
        // Steepness/asymptote shared by both CrateScale curves — see RealBatteryLoadMaster.
        [KSPField(isPersistant = false)] public float  CrateScaleFactor = RBDefaults.CrateScaleFactor;

        // --- Capability / mode flags (persistent) ----------------------------------
        [KSPField(isPersistant = true)] public bool ActivationLatched = false;  // staged latch
        [KSPField(isPersistant = true)] public bool PreventOverheat = false;    // tech-gated

        // --- Curves & lookup tables (cfg-driven) ------------------------------------
        // Maps SOC (0..1) -> charge efficiency multiplier (0..1 or >1 if allowed)
        [KSPField(isPersistant = false)] public double SOC_ChargeEfficiency;
        // Maps life factors (e.g., cycles / temperature / wear proxy) -> life multiplier
        public static FloatCurve BatteryLifeCurve = new FloatCurve();
        static RealBattery()
        {
            BatteryLifeCurve.Add(0.0f, 1.0f);
            BatteryLifeCurve.Add(1.0f, 0.8f);
            BatteryLifeCurve.Add(1.2f, 0.4f);
            BatteryLifeCurve.Add(1.5f, 0.2f);
            BatteryLifeCurve.Add(2.0f, 0.0f);
        }
 
        // --- Runtime persistent state ----------------------------------------------
        [KSPField(isPersistant = true)] public double SC_SOC = 1;                  // state-of-charge (0..1)
        [KSPField(isPersistant = true)] public double BatteryLife = 1.0;           // 0..1 (computed)
        [KSPField(isPersistant = true)] public double WearCounter = 0.0;           // kWh transferred total
        [KSPField(isPersistant = true)] public bool   EOLToastSent = false;        // once-only message
        [KSPField(isPersistant = true)] public bool   BGSelfRunawaySent = false;   // suppresses EOL toast when BG RIP already fired
        [KSPField(isPersistant = true)] public double selfRunawayTimer = 0.0;      // accumulated "hazard time" for RIP self-runaway, in seconds.
        [KSPField(isPersistant = true)] public bool   forcedRunawayActive = false; // once triggered, the cell remains in forced-runaway mode
        [KSPField(isPersistant = true)] public bool   FixedOutputDefaultApplied = false;
        // Transient thermal capacity factor for InfiniteCycles batteries (0..1).
        // Reduced linearly between TempOverheat and TempRunaway; resets on cooling.
        [KSPField(isPersistant = true)] public double ThermalCapFactor = 1.0;
        // One-shot notification flag for InfiniteCycles thermal cap events.
        // Resets when ThermalCapFactor returns to 1.0 (battery cooled below TempOverheat).
        [KSPField(isPersistant = true)] public bool ThermalCapNotified = false;

        // --- Telemetry / Editor preview --------------------------------------------
        [KSPField(isPersistant = false)] public double lastECpower = 0; // +charge / -discharge
        // Recomputed every use (StoredCharge.maxAmount * Crate * ...) — no need to persist (I7).
        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "#LOC_RB_DischargeRate", guiUnits = "#LOC_RB_guiUnitsECs", guiFormat = "F2", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public double DischargeRate = 0.0;
        private double GUI_power = 0;
        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "#LOC_RB_ChargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string ChargeInfoEditor;
        private float smoothFlux = 0f;

        // --- PAW (flight + editor) --------------------------------------------------
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_BatteryToggle", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        [UI_Toggle(disabledText = "#LOC_RB_disableText", enabledText = "#LOC_RB_enableText")]
        public bool BatteryDisabled = false;

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_Tech", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryTypeDisplayName;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_StateOfCharge", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatterySOCStatus;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_Status", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryChargeStatus;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_TimeTo", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryTimeTo;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_BatteryHealth", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryHealthStatus;

        // --- Staging integration ----------------------------------------------------
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_StageArm", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        [UI_Toggle(disabledText = "#LOC_RB_StageArm_off", enabledText = "#LOC_RB_StageArm_on")]
        public bool BatteryStaged = RBDefaults.BatteryStaged;
        [KSPField(isPersistant = true)] private bool StageFired = false;
        [KSPField(isPersistant = true)] public bool BatteryStagedUserSet = false;
        public override bool IsStageable() => BatteryStaged && !StageFired;
        public override bool StagingToggleEnabledEditor() => BatteryStaged;


        // --- ACTIONS ----------------------------------------------------------------
        [KSPAction("#LOC_RB_ActionToggleBattery")] public void ToggleBatteryAction(KSPActionParam param) => BatteryDisabled = !BatteryDisabled;
        [KSPAction("#LOC_RB_ActionEnableBattery")] public void EnableBattery(KSPActionParam param) => BatteryDisabled = false;
        [KSPAction("#LOC_RB_ActionDisableBattery")] public void DisableBattery(KSPActionParam param) => BatteryDisabled = true;


        // --- Editor Simulation Mode -------------------------------------------------
        public enum SimMode { Idle, Discharge, Charge }
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_SimMode", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        [UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "#LOC_RB_SimMode_Idle", "#LOC_RB_SimMode_Discharge", "#LOC_RB_SimMode_Charge" })]
        public string SimulationMode;


        // ============================================================================
        //  RUNTIME STATE & DEPENDENCIES
        //  Keep only non-persistent state and cached external module refs here.
        // ============================================================================

        // --- External dependencies (cached) ----------------------------------------
        // Optional SystemHeat module; assigned on-demand when heat sim is in use.
        // private ModuleSystemHeat systemHeat = null;
        private PartModule systemHeat = null; // SystemHeat module (optional)

        // Physical part volume in liters (from RBbaseVolume cfg key set by MM patches); 0 if absent.
        private double _rbVolume = 0.0;

        // --- Cached BaseField references (P5) ---------------------------------------
        // Resolved once in OnStart; used by ModuleActiveHideUI/EnforceNonDisableableLatch and
        // other hot-path (OnUpdate/FixedUpdate) PAW field updates instead of repeated
        // Fields["..."] dictionary lookups every tick.
        private BaseField _fBatteryDisabled;
        private BaseField _fBatteryTypeDisplayName;
        private BaseField _fBatteryChargeStatus;
        private BaseField _fBatterySOCStatus;
        private BaseField _fBatteryTimeTo;
        private BaseField _fBatteryHealthStatus;
        private BaseField _fDischargeRate;
        private BaseField _fChargeInfoEditor;
        private BaseField _fBatteryStaged;
        private BaseField _fSimulationMode;
        private BaseField _fLastECpower;

        // Guards onFieldChanged/GameEvents subscriptions wired in OnStart (F3): if OnStart ever
        // re-runs on the same instance (e.g. a revert-to-editor / re-init path), this prevents
        // stacking duplicate subscriptions that would otherwise fire their handlers once per
        // accumulated registration instead of once per actual event.
        private bool _uiHandlersWired = false;

        // Effective warmup/shutdown duration for this part; cached at phase start by EffectiveWarmupSeconds().
        private double _keepWarmDuration = 60.0;

        // True for superconductor batteries (SMES): stored energy is the persistent supercurrent,
        // which is lost when the cryogenic state is interrupted.
        private bool IsSMES => KeepWarmMode == "cryo" && InfiniteCycles;

        // True when this cryo battery should use the CryoTanks-like waste heat model instead of EC upkeep.
        private bool UsesCryoWasteHeat() =>
            KeepWarmMode == "cryo" && RealBatterySettings.UseCryoWasteHeatMode;

        // True for non-rechargeable ("primary") batteries: unifies the two heuristics that used
        // to live separately in OnUpdate() and BackgroundSimulator.ApplySnapshot(). Three
        // independent signals, any of which marks a chemistry as primary:
        //   - CycleDurability <= 1.0: wear model treats it as consumed after one full cycle.
        //   - HighEClevel > 1: the charge gate is unreachable, so charging never engages
        //     (e.g. TBat, AgOx, NukeCell all set this explicitly).
        //   - ChargeEfficiencyCurve.Evaluate(0f) <= EPS: charge efficiency is zero at empty SOC,
        //     so it can't meaningfully recharge even if the two flags above weren't set (covers
        //     inline/user-defined chemistries that only zero out the curve).
        // Public: used by both this module (OnUpdate) and BackgroundSimulator (rb.IsPrimary).
        public bool IsPrimary =>
            CycleDurability <= 1.0 || HighEClevel > 1f || ChargeEfficiencyCurve.Evaluate(0f) <= (float)EPS;

        // --- KeepWarm / Controlled Shutdown state machine --------------------------
        // Tracks current thermal runaway state (true while active, cleared when extinguished)
        public bool    isRunaway = false;
        // Latency warmup/shutdown state
        private bool   keepWarmActive = false;      // true during warmup latency
        private double keepWarmTleft = 0.0;         // seconds left in warmup
        private double keepWarmGrace = 0.0;         // shutdown grace when upkeep EC missing
        private bool   controlledShutdownActive = false;
        private double shutdownTleft = 0.0;
        private int    pct = 0;
        private bool   upkeepShort = false;
        // UI safety lock during transitions
        private bool   uiToggleLockActive = false;    // lock PAW toggle to a forced state
        private bool   uiLockDisabledState = false;   // false => force ON, true => force OFF
        // Edge detector for ON/OFF transitions (synced in OnStart)
        private bool   lastDisabled = true;

        // --- Thermal warnings & runaway bookkeeping --------------------------------
        // Overheat (non-runaway) user notifications
        private bool   OverheatNotified = false;
        // Runaway: one-time toast + residual heat tail
        private bool   RunawayNotified = false;       // first-time runaway trigger in flight
        private double runawayTailKW = 0.0;           // snapshot of last heat power
        private bool   runawayExtinguished = false;   // chemical source depleted; apply short tail

        private string _lastAppliedChemistryID = null; // cache the last applied chemistry ID to avoid redundant DB lookups

        // Auxiliary resource flows from the active chemistry (RESOURCE_EXTRA); empty when none.
        private List<ResourceRequirement> _resourceExtras = new List<ResourceRequirement>();

        // True after we've sent an explicit zero-flux to SystemHeat because either
        // EnableHeatSimulation or UseSystemHeat is off. Prevents re-sending every FixedUpdate.
        // Reset when both settings are on again, so toggles work symmetrically.
        private bool _shSilenced = false;

        // Physics-tick de-dup guard for the InfiniteCycles thermal cap block in
        // ApplyThermalEffects: PartModule.FixedUpdate (this force-tick) and
        // VesselModule.FixedUpdate (RealBatteryLoadMaster -> XferECtoRealBattery) can both
        // land in the same physics step with no guaranteed order, which would otherwise apply
        // the cap severity/heat twice in one tick. Time.fixedTime is constant across all
        // FixedUpdate calls within a single physics step, so equality means "already done".
        private double _lastInfCycleCapFixedTime = -1.0;



        // ============================================================================
        //  LIFECYCLE
        // ============================================================================
        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // 0) Boot banner / early refs
            BatteryChargeStatus = Localizer.Format("#LOC_RB_Initializing");
            //if (RealBatterySettings.UseSystemHeat && systemHeat == null)
            //    systemHeat = part?.Modules?.GetModule<ModuleSystemHeat>();
            if (RealBatterySettings.UseSystemHeat && systemHeat == null)
                systemHeat = SystemHeatBridge.GetModule(part);

            // 0.5) Cache BaseField references used in hot paths (P5) — resolved once here
            // instead of repeated Fields["..."] dictionary lookups every tick.
            _fBatteryDisabled = Fields[nameof(BatteryDisabled)];
            _fBatteryTypeDisplayName = Fields[nameof(BatteryTypeDisplayName)];
            _fBatteryChargeStatus = Fields[nameof(BatteryChargeStatus)];
            _fBatterySOCStatus = Fields[nameof(BatterySOCStatus)];
            _fBatteryTimeTo = Fields[nameof(BatteryTimeTo)];
            _fBatteryHealthStatus = Fields[nameof(BatteryHealthStatus)];
            _fDischargeRate = Fields[nameof(DischargeRate)];
            _fChargeInfoEditor = Fields[nameof(ChargeInfoEditor)];
            _fBatteryStaged = Fields[nameof(BatteryStaged)];
            _fSimulationMode = Fields[nameof(SimulationMode)];
            _fLastECpower = Fields[nameof(lastECpower)];

            // 1) Load config & basic UI gating
            LoadConfig();
            ModuleActiveHideUI();

            // 2) Post-load custom hook moved to OnStartFinished()
            //    (B9PS applies DATA-block fields in its own OnStart, which runs after ours)

            // 3) Defaults & invariants
            // Default: thermal batteries spawn disabled in Editor/PreLaunch (once).
            if (FixedOutput && !FixedOutputDefaultApplied && !ActivationLatched)
            {
                bool editorScene = HighLogic.LoadedSceneIsEditor;
                bool newVesselSpawn = (HighLogic.LoadedSceneIsFlight && state.HasFlag(StartState.PreLaunch));
                if (editorScene || newVesselSpawn)
                {
                    BatteryDisabled = true;
                    FixedOutputDefaultApplied = true; // latch so we won't override later
                    RBLog.Verbose("[RealBattery] Applied default Disabled=true for FixedOutput battery.");
                }
            }
            // Keep invariant and UI in sync
            EnforceNonDisableableLatch();

            // 4) Staging wiring + PAW handlers
            ApplyStagingState();
            if (!_uiHandlersWired)
            {
                _uiHandlersWired = true;

                if (HighLogic.LoadedSceneIsEditor)
                    GameEvents.onEditorShipModified.Add(OnEditorShipModified);
                // Re-enforce latch when user flips BatteryDisabled in PAW (Editor & Flight).
                if (_fBatteryDisabled?.uiControlFlight != null)
                    _fBatteryDisabled.uiControlFlight.onFieldChanged += (f, o) => { EnforceNonDisableableLatch(); };
                if (_fBatteryDisabled?.uiControlEditor != null)
                    _fBatteryDisabled.uiControlEditor.onFieldChanged += (f, o) => { EnforceNonDisableableLatch(); };
                // Keep stageability in sync when BatteryStaged changes (Editor & Flight).
                WireStagedToggleHandlers();
            }

            // 5) Editor simulation mode (options + default fix)
            if (_fSimulationMode.uiControlEditor is UI_ChooseOption simModeUI)
            {
                string idle = Localizer.Format("#LOC_RB_SimMode_Idle");
                string disch = Localizer.Format("#LOC_RB_SimMode_Discharge");
                string charg = Localizer.Format("#LOC_RB_SimMode_Charge");

                simModeUI.options = new[] { idle, disch, charg };
                simModeUI.display = new[] { "#LOC_RB_SimMode_Idle", "#LOC_RB_SimMode_Discharge", "#LOC_RB_SimMode_Charge" };

                // Fix initial value if invalid
                if (SimulationMode != idle && SimulationMode != disch && SimulationMode != charg)
                {
                    SimulationMode = idle;
                    RBLog.Verbose($"[SimulationMode] Defaulting to Idle: {SimulationMode}");
                }
            }

            // 6) Launch-time resets & tech gates
            if (HighLogic.LoadedSceneIsFlight && state.HasFlag(StartState.PreLaunch))
            {
                WearCounter = 0.0;
                BatteryLife = 1.0;
                ThermalCapFactor = 1.0;
                ThermalCapNotified = false;
                smoothFlux = 0f;
                RBLog.Info("[RealBattery] Reset WearCounter, BatteryLife and ThermalCapFactor on launch (PreLaunch state)");
            }
            TechUnlockUI();

            _rbVolume = ReadPartVolumeL();

            // 7) Edge detectors & final UI state
            lastDisabled = BatteryDisabled; // initialize edge detector

            // 8) Initial SystemHeat handshake after scene re-entry.
            // Gate on SystemHeatAvailable (assembly present) rather than UseSystemHeat,
            // so we can still silence a residual flux when the user has SH installed but
            // has opted out via UseSystemHeat = false.
            if (HighLogic.LoadedSceneIsFlight
                && !state.HasFlag(StartState.PreLaunch)
                && RealBatterySettings.SystemHeatAvailable)
            {
                if (systemHeat == null)
                    systemHeat = SystemHeatBridge.GetModule(part);

                if (systemHeat != null)
                {
                    if (!RealBatterySettings.EnableHeatSimulation || !RealBatterySettings.UseSystemHeat)
                    {
                        // Heat sim or SH integration disabled at scene load: send an explicit zero
                        // so SystemHeat doesn't show a residual/default value before the first FixedUpdate runs.
                        smoothFlux = 0f;
                        SystemHeatBridge.AddFlux(systemHeat, "RealBattery", 0f, 0f, true);
                        _shSilenced = true;
                    }
                    else
                    {
                        SystemHeatBridge.AddFlux(systemHeat, "RealBattery", TempOptimal, (float)EPS, true);
                    }
                }
            }
        }

        // Called by KSP after ALL modules on the part have completed OnStart().
        // At this point B9PS has already applied DATA-block KSPFields (including EVAupgrade),
        // so EVA event visibility and labels can be wired correctly.
        public override void OnStartFinished(StartState state)
        {
            base.OnStartFinished(state);
            RB_AfterOnStart();

            // SMES that starts disabled has no active supercurrent:
            // zero ThermalCapFactor and StoredCharge immediately.
            if (HighLogic.LoadedSceneIsFlight && state.HasFlag(StartState.PreLaunch)
                && IsSMES && BatteryDisabled)
            {
                ThermalCapFactor = 0.0;
                PartResource sc = part.Resources.Get("StoredCharge");
                if (sc != null) sc.amount = 0.0;
                SC_SOC = 0.0;
                RBLog.Info("[RealBattery] SMES starts disabled: ThermalCapFactor and SC zeroed at PreLaunch.");
            }
        }

        public override void OnUpdate()
        {
            RBLog.Verbose($"[RealBattery] OnUpdate check on {part.partInfo.title} in {HighLogic.LoadedScene} | LastChemistryID: {_lastAppliedChemistryID}, ChemistryID: {ChemistryID}");
            if (ChemistryID != _lastAppliedChemistryID)
            {
                _lastAppliedChemistryID = ChemistryID;
                ApplyChemistryFromDB();
                // moduleActive only changes via a chemistry/subtype switch, not every tick —
                // no need to re-apply PAW visibility outside this branch (P5).
                ModuleActiveHideUI();
            }

            if (HighLogic.LoadedSceneIsEditor)
            {
                return;
            }
            
            if (!HighLogic.LoadedSceneIsFlight || !moduleActive) return;

            RBLog.Verbose("INF OnUpdate");

            // for slowing down the charge/discharge status. Time-based (not per-frame) so the
            // real-world settle time stays ~constant regardless of framerate or power
            // magnitude - a fixed per-frame ratio here used to take minutes to decay on
            // heavy craft (low fps) or high-DischargeRate batteries (large starting value).
            double  deltaTime = Math.Max(Time.deltaTime, 1e-4);
            double  guiPowerSmoothRatio = 1.0 - Math.Exp(-deltaTime / GUI_POWER_SMOOTH_TAU_S);
            double  ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            PartResource storedChargeRes = part.Resources.Get("StoredCharge");
            double  stored = storedChargeRes?.amount ?? 0.0;
            double  capacity = storedChargeRes?.maxAmount ?? 0.0;
            double  max = capacity * ActualLife;
            double  deltaSC = GUI_power > 0 ? max - stored : stored;
            double  timeInSeconds = (deltaSC * EC2SCratio) / Math.Abs(GUI_power);

            GUI_power += guiPowerSmoothRatio * (lastECpower - GUI_power);

            // Snap to exact zero once both the target and the smoothed value are negligible,
            // instead of leaving an ever-shrinking float below the display threshold.
            if (Math.Abs(lastECpower) < EPS && Math.Abs(GUI_power) < 0.001)
                GUI_power = 0.0;

            // GUI
            if (isRunaway)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_Status_Runaway");
            else if (ActualLife < 0.01)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_DeadBattery");
            else if (controlledShutdownActive)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_ShuttingDown", pct);
            else if (!BatteryDisabled && keepWarmActive)
                BatteryChargeStatus = (KeepWarmMode == "cryo")
                    ? Localizer.Format("#LOC_RB_CoolingDown", pct)
                    : Localizer.Format("#LOC_RB_WarmingUp", pct);
            else if (upkeepShort)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_KeepWarm_ShutdownPending", Math.Ceiling(keepWarmGrace));
            else if (GUI_power < -0.001)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Discharging", GUI_power.ToString("F1"));
            else if (GUI_power > 0.001)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Charging", GUI_power.ToString("F1"));
            else if (SC_SOC < 0.01)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_PrimaryDepleted");
            else
            {
                part.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double EC_amount, out double EC_maxAmount);
                double ecPct = EC_maxAmount > EPS ? (EC_amount / EC_maxAmount * 100.0) : 0.0;
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_idle", ecPct.ToString("F1"));
            }

            BatterySOCStatus = $"{(SC_SOC * 100):F0}%";

            if (InfiniteCycles)
            {
                // Show thermal efficiency (ThermalCapFactor), not BatteryLife/cycles
                BatteryHealthStatus = Localizer.Format(
                    "#LOC_RB_BatteryHealth_efficiency",
                    $"{(ThermalCapFactor * 100):F0}");
            }
            else if (!IsPrimary && RealBatterySettings.EnableBatteryWear && capacity > EPS)
            {
                double cyclesLeft = Math.Max(0.0, CycleDurability - (WearCounter / (capacity * 2)));
                string cyclesFmt = (cyclesLeft < 10.0) ? cyclesLeft.ToString("F2") : cyclesLeft.ToString("F0");

                BatteryHealthStatus = $"{(ActualLife * 100):F0}% ({cyclesFmt} {Localizer.Format("#LOC_RB_BatteryHealth_cycles")})";
            }
            else
            {
                // Primary (non-rechargeable) or invalid capacity: show only health percentage
                BatteryHealthStatus = $"{(ActualLife * 100):F0}%";
            }

            if (Math.Abs(GUI_power) > 0.001 && timeInSeconds > 0.05 && timeInSeconds < 1e7)
            {
                bool GUIdischarging = GUI_power < -0.001;

                _fBatteryTimeTo.guiName = GUIdischarging ? "#LOC_RB_TimeTo" : "#LOC_RB_TimeToCharge";
                BatteryTimeTo = FormatTimeSpan(TimeSpan.FromSeconds(timeInSeconds));
            }
            else
            {
                _fBatteryTimeTo.guiName = "#LOC_RB_TimeTo";
                BatteryTimeTo = "-";
            }

            RBLog.Verbose($"[RealBattery] GUI_power update: lastECpower={lastECpower:F3}, GUI_power={GUI_power:F3}, Δt={TimeWarp.fixedDeltaTime:F3}");

            // Register with SystemHeat even when disabled, so the loop uses our TempOptimal target
            if (RealBatterySettings.EnableHeatSimulation && RealBatterySettings.UseSystemHeat && ((BatteryDisabled && !isRunaway) || (!BatteryDisabled && keepWarmActive)))
            {
                // Ensure fresh SystemHeat reference if needed
                if (systemHeat == null)
                    systemHeat = SystemHeatBridge.GetModule(part);

                SystemHeatBridge.AddFlux(systemHeat, "RealBattery", TempOptimal, 0f, true);
            }
        }
        public void FixedUpdate()
        {
            if (!moduleActive) return;

            RBLog.Verbose($"[RealBattery] OnUpdate check on {part.partInfo.title} in {HighLogic.LoadedScene} | LastChemistryID: {_lastAppliedChemistryID}, ChemistryID: {ChemistryID}");
            if (ChemistryID != _lastAppliedChemistryID)
            {
                _lastAppliedChemistryID = ChemistryID;
                ApplyChemistryFromDB();
                ModuleActiveHideUI();
            }

            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;
            double dt = TimeWarp.fixedDeltaTime;
            if (dt <= 0) return;

            PartResource sc = part.Resources.Get("StoredCharge");

            if(HighLogic.LoadedSceneIsFlight)
            {
                EnforceNonDisableableLatch();
                TickCryoWasteHeat();

                // Enforce UI toggle lock: while a phase is in progress, force the
                // BatteryDisabled field to the locked value, defeating user clicks.
                if (KeepWarmMode != "false" && uiToggleLockActive)
                {
                    if (BatteryDisabled != uiLockDisabledState)
                        BatteryDisabled = uiLockDisabledState;
                }

                // -----------------------------------------------------------------
                // Detect transitions for KeepWarm state machine
                // -----------------------------------------------------------------
                bool DisabledNow = BatteryDisabled;
                if (uiToggleLockActive) DisabledNow = uiLockDisabledState;

                if (KeepWarmMode != "false")
                {
                    if (lastDisabled && !DisabledNow)
                    {
                        // OFF -> ON : start warmup latency (even if hot; EC cost may be 0 if >=600 K)
                        keepWarmActive = true;
                        _keepWarmDuration = EffectiveWarmupSeconds();
                        keepWarmTleft = _keepWarmDuration;
                        keepWarmGrace = 0.0;
                        controlledShutdownActive = false;
                        shutdownTleft = 0.0;
                        // Lock UI to ON during warmup
                        uiToggleLockActive = true;
                        uiLockDisabledState = false; // force ON
                        RBLog.Info($"[RealBattery] KeepWarm: warmup started on '{part.partInfo?.title}' for {keepWarmTleft:F0}s");
                    }

                    // ON -> OFF (manual): start controlled shutdown
                    if (!lastDisabled && DisabledNow)
                    {
                        // Lock UI to OFF during controlled shutdown
                        uiToggleLockActive = true;
                        uiLockDisabledState = true; // force OFF
                        controlledShutdownActive = true;
                        _keepWarmDuration = EffectiveWarmupSeconds();
                        shutdownTleft = _keepWarmDuration;
                        // cancel any warmup/grace
                        keepWarmActive = false;
                        keepWarmGrace = 0.0;
                        RBLog.Info($"[RealBattery] KeepWarm: controlled shutdown started on '{part.partInfo?.title}' for {shutdownTleft:F0}s");
                    }

                    lastDisabled = DisabledNow;

                    // -----------------------------------------------------------------
                    // Controlled shutdown (reverse warmup): force OFF, no EC, latency
                    // -----------------------------------------------------------------
                    if (controlledShutdownActive)
                    {
                        if (!BatteryDisabled) BatteryDisabled = true; // cannot flip ON
                        shutdownTleft = Math.Max(0.0, shutdownTleft - dt);
                        double total = Math.Max(1e-3, _keepWarmDuration);
                        pct = (int)Mathf.Round(100f * (float)((total - shutdownTleft) / total));
                        
                        // SMES: drive ThermalCapFactor -> 0 and clamp sc.amount each frame
                        if (IsSMES)
                        {
                            double progress = shutdownTleft / total;          // 1.0 -> 0.0
                            ThermalCapFactor = Math.Min(ThermalCapFactor, progress * progress);
                            if (sc != null)
                            {
                                double effCap = sc.maxAmount * ThermalCapFactor;
                                if (sc.amount > effCap) sc.amount = effCap;
                            }
                        }
                        if (shutdownTleft <= EPS)
                        {
                            controlledShutdownActive = false;
                            // Unlock UI at the end of controlled shutdown (remains OFF)
                            uiToggleLockActive = false;
                            RBLog.Info($"[RealBattery] KeepWarm: controlled shutdown completed on '{part.partInfo?.title}'");
                        }
                        return; // no upkeep, no Xfer while shutting down
                    }
                    
                    // -----------------------------------------------------------------
                    // Warmup latency: exclude from Xfer, heat-aware EC draw, status
                    // -----------------------------------------------------------------
                    if (!BatteryDisabled && keepWarmActive)
                    {
                        double got = TickKeepWarm(isWarmupPhase: true, dt);
                        
                        // progress UI
                        keepWarmTleft = Math.Max(0.0, keepWarmTleft - dt);
                        double total = Math.Max(1e-3, _keepWarmDuration);
                        pct = (int)Mathf.Round(100f * (float)((total - keepWarmTleft) / total));
                        //BatteryChargeStatus = Localizer.Format("#LOC_RB_WarmingUp", pct);
                        
                        // if heat-sim enabled and temp <600 K, EC may be required; detect shortfall
                        bool upkeepNeeded = KeepWarmTempMultiplier() > 0f;
                        double wantThisFrame = KeepWarmECperSec(true, (part.Resources.Get("StoredCharge")?.maxAmount ?? 0.0)) * dt;
                        upkeepShort = upkeepNeeded && (wantThisFrame > EPS) && (got < wantThisFrame * 0.999);
                        if (upkeepShort)
                        {
                            // Enter shutdown pending (grace = remaining warmup time)
                            keepWarmGrace = Math.Max(1.0, keepWarmTleft);
                            keepWarmActive = false;
                            //BatteryChargeStatus = Localizer.Format("#LOC_RB_KeepWarm_ShutdownPending", Math.Ceiling(keepWarmGrace));
                            return; // skip normal path while pending
                        }
                        
                        // SMES: ramp ThermalCapFactor 0->1 symmetric with shutdown drain. Math.Max so it only rises.
                        if (IsSMES)
                        {
                            double progress = 1.0 - (keepWarmTleft / total);  // 0.0 -> 1.0
                            ThermalCapFactor = Math.Max(ThermalCapFactor, progress * progress);
                        }

                        if (keepWarmTleft <= EPS)
                        {
                            keepWarmActive = false;
                            // Unlock UI after warmup completes (now fully ON)
                            uiToggleLockActive = false;
                            uiLockDisabledState = false;
                            RBLog.Info($"[RealBattery] KeepWarm: warmup completed on '{part.partInfo?.title}'");
                        }
                        return; // still warming (or just finished; next frame goes normal)
                    }
                    
                    // -----------------------------------------------------------------
                    // Normal operation with persistent upkeep (heat-aware)
                    // -----------------------------------------------------------------
                    if (!BatteryDisabled && !keepWarmActive)
                    {
                        //double dischargeKW = (sc?.maxAmount ?? 0.0) * Crate * ActualLife;
                        double wantEC = KeepWarmECperSec(isWarmupPhase: false, (sc?.maxAmount ?? 0.0)) * dt;
                        double gotEC = (wantEC > 0.0) ? part.RequestResource(PartResourceLibrary.ElectricityHashcode, wantEC) : 0.0;
                        
                        bool upkeepNeeded = wantEC > EPS;                   // temp < 600 K
                        upkeepShort = upkeepNeeded && (gotEC < wantEC * 0.999);
                        
                        if (RealBatterySettings.EnableHeatSimulation && upkeepShort)
                        {
                            // start/continue grace
                            if (keepWarmGrace <= 0.0) keepWarmGrace = _keepWarmDuration;
                            else keepWarmGrace = Math.Max(0.0, keepWarmGrace - dt);
                            
                            //BatteryChargeStatus = Localizer.Format("#LOC_RB_KeepWarm_ShutdownPending", Math.Ceiling(keepWarmGrace));
                            if (keepWarmGrace <= EPS)
                            {
                                BatteryDisabled = true; // short shutdown
                                keepWarmGrace = 0.0;
                                RBLog.Warn($"[RealBattery] KeepWarm: shutdown due to insufficient upkeep on '{part.partInfo?.title}'");
                            }
                        }
                        else
                        {
                            // upkeep satisfied or not needed -> cancel pending shutdown
                            keepWarmGrace = 0.0;
                        }
                        
                        // If we were pending shutdown during warmup and conditions recovered, resume warmup
                        if (keepWarmGrace <= EPS && KeepWarmMode != "false" && !BatteryDisabled && keepWarmTleft > EPS)
                        {
                            keepWarmActive = true; // resume latency
                            pct = (int)Mathf.Round(100f * (float)((_keepWarmDuration - keepWarmTleft) / Math.Max(1e-3, _keepWarmDuration)));
                        }
                    }
                }

                // -----------------------
                // Runaway check & trigger
                // -----------------------

                float tempK = GetCurrentTemperatureK();

                // Runaway can only be *triggered* if heat simulation and runaway features are enabled.
                // Once active, `isRunaway` stays latched until ApplyThermalEffects() explicitly
                // extinguishes it.
                // Note: neither KeepWarmMode nor FixedOutput are guarded here — chemistries that
                // should be immune to runaway already set TempRunaway = 9999 and RunawayHeatFactor = 0
                // in cfg.
                bool RunawayAllowed =
                    RealBatterySettings.EnableHeatSimulation &&
                    RealBatterySettings.EnableThermalRunaway &&
                    !InfiniteCycles;

                bool runawayCondition = (tempK > TempRunaway) || forcedRunawayActive;

                // Only handle OFF -> ON transitions here. Turning runaway OFF is handled inside
                // ApplyThermalEffects(), so that we can also run the decay tail and properly
                // clear forcedRunawayActive.
                if (!isRunaway && RunawayAllowed && runawayCondition && ActualLife >= 0.01)
                {
                    isRunaway = true;
                    forcedRunawayActive = true; // latch runaway state until explicitly cleared (e.g., by cooling or death)

                    if (RBLog.VerboseEnabled)
                    {
                        string loopT = "n/a";
                        if (RealBatterySettings.UseSystemHeat)
                        {
                            // Refresh on demand in case the module wasn't cached yet.
                            if (systemHeat == null)
                                systemHeat = SystemHeatBridge.GetModule(part);
                            
                            if (systemHeat != null && SystemHeatBridge.TryGetLoopTempK(systemHeat, out float loopTK))
                                loopT = loopTK.ToString("F1");
                        }
                        
                        RBLog.Verbose(
                        $"[Runaway][DEBUG] Setting isRunaway=true on '{part.partInfo?.title}' " +
                        $"canTriggerRunaway={RunawayAllowed}, tempK={tempK:F1}, TempRunaway={TempRunaway:F0}, " +
                        $"forcedRunawayActive={forcedRunawayActive}, SelfRunaway={SelfRunaway}, " +
                        $"UseSystemHeat={RealBatterySettings.UseSystemHeat}, " +
                        $"loopT={loopT}"
                        );
                    }
                }

                // If the feature is disabled in settings mid-flight, force-clear runaway state.
                if (!RunawayAllowed && isRunaway)
                {
                    isRunaway = false;
                    forcedRunawayActive = false;
                }

                // ----------------------------------
                // Self Runaway dice for Hf batteries
                // ----------------------------------

                if (SelfRunaway && RunawayBaseChance > 0.0
                    && !forcedRunawayActive && !isRunaway
                    && dt > 0 && SC_SOC >= 0.01)
                {
                    selfRunawayTimer += dt;

                    // Check once per in-game hour (3600 s)
                    const double interval = 3600.0;
                    while (selfRunawayTimer >= interval)
                    {
                        selfRunawayTimer -= interval;

                        // Derive half-life from SelfDischargeRate, then scale by RunawayBaseChance.
                        // p = RunawayBaseChance / halfLifeHours * SelfRunawayChanceMultiplier
                        double ripHoursPerDay   = RealBatterySettings.GetHoursPerDay();
                        double ripDecayPerSec   = SelfDischargeRate / (ripHoursPerDay * 3600.0);
                        double ripHalfLifeHours = ripDecayPerSec > 0.0
                            ? Math.Log(2) / ripDecayPerSec / 3600.0
                            : double.PositiveInfinity;
                        float p = (ripHalfLifeHours > 0.0 && !double.IsInfinity(ripHalfLifeHours))
                            ? (float)(RunawayBaseChance / ripHalfLifeHours * RealBatterySettings.SelfRunawayChanceMultiplier)
                            : 0f;
                        if (p > 0f && UnityEngine.Random.value < p)
                        {
                            // If battery wear is disabled, a RIP event instantly kills the cell.
                            // This prevents "saving" hafnium cells by simply cooling them down.
                            if (!RealBatterySettings.EnableBatteryWear)
                            {
                                // No wear: just dump the charge.
                                sc.amount = 0.0;
                                SC_SOC = 0.0;

                                RBLog.Info($"[RealBattery][RIP] Immediate RIP engaged (no wear). SelfRunaway = {SelfRunaway}");
                            }
                            else if (!RealBatterySettings.EnableThermalRunaway)
                            {
                                // Wear enabled but thermal runaway disabled:
                                // treat self-runaway as immediate, irreversible cell death without heat.
                                BatteryLife = 0.0;
                                sc.amount = 0.0;
                                SC_SOC = 0.0;

                                UpdateBatteryLife(); // keep capacity and UI in sync

                                RBLog.Info($"[RealBattery][RIP] Self-runaway with ThermalRunaway OFF. Cell killed on '{part.partInfo?.title}'.");
                            }
                            else
                            {
                                // Full thermal model enabled: trigger the usual runaway path
                                // and let FixedUpdate latch isRunaway using forcedRunawayActive.
                                forcedRunawayActive = true;

                                RBLog.Info($"[RealBattery][RIP] Self-runaway triggered (thermal model). SelfRunaway = {SelfRunaway}");
                            }

                            ScreenMessages.PostScreenMessage(
                                $"{Localizer.Format("#LOC_RB_RIP_SelfRunaway", part.partInfo.title)}",
                                12f, ScreenMessageStyle.UPPER_CENTER);

                            RBLog.Warn($"[RealBattery][RIP] Spontaneous self-runaway triggered on '{part.partInfo?.title}'");
                            break;
                        }
                    }
                }

                // Force-tick thermal effects for batteries idle/disabled while hot: keeps runaway
                // heat/wear and the InfiniteCycles thermal cap advancing even when
                // XferECtoRealBattery isn't called this tick (e.g. disabled battery, or idle
                // within the charge/discharge dead-band). Physics-rate only — this used to run
                // from OnUpdate() at render rate, making wear/heat accumulation framerate-
                // dependent. During runaway, XferECtoRealBattery exits before reaching
                // ApplyThermalEffects, so no overlap guard is needed for that branch; the
                // InfiniteCycles branch is guarded internally (see _lastInfCycleCapFixedTime).
                if (isRunaway || (InfiniteCycles && tempK > TempOverheat - 10f))
                    ApplyThermalEffects(0.0);

                // stop thermal flux if battery is spent after runaway
                if (RealBatterySettings.UseSystemHeat && systemHeat != null)
                {
                    if (ActualLife < 0.01 && !isRunaway && !forcedRunawayActive && Math.Abs(smoothFlux) > 1e-6f)
                    {
                        smoothFlux = 0f;
                        SystemHeatBridge.AddFlux(systemHeat, "RealBattery", 0f, 0f, true);
                        RBLog.Info($"[RealBattery] Cleared residual SystemHeat flux on '{part.partInfo?.title}' after battery death.");
                    }
                }

                // LifeDecay (radioactive cells): decay BatteryLife unconditionally,
                // regardless of whether the battery is enabled or disabled.
                if (LifeDecay && RealBatterySettings.EnableSelfDischarge)
                    ApplySelfDischarge();

                if (BatteryDisabled)
                {
                    // Standard self-discharge only when battery is OFF and not LifeDecay
                    // (LifeDecay cells do not bleed SoC — they decay life instead).
                    if (!LifeDecay && RealBatterySettings.EnableSelfDischarge)
                    {
                        RBLog.Verbose($"[FixedUpdate] Called ApplySelfDischarge");
                        ApplySelfDischarge();
                    }
                    return;
                }
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                double sign = 0;

                if (systemHeat != null)
                    SystemHeatBridge.MarkUsed(systemHeat);

                if (SimulationMode == Localizer.Format("#LOC_RB_SimMode_Discharge"))
                    sign = -1.0;
                else if (SimulationMode == Localizer.Format("#LOC_RB_SimMode_Charge"))
                    sign = +1.0;

                // Simulated EC power (kW), positive = charging, negative = discharging
                PartResource StoredCharge = part.Resources.Get("StoredCharge");
                DischargeRate = StoredCharge.maxAmount * Crate;
                lastECpower = sign * DischargeRate;

                _fLastECpower.SetValue(lastECpower, this);

                // Push live DischargeRate to PAW (Editor)
                _fDischargeRate.SetValue(DischargeRate, this);

                double chargeAtSOC0 = DischargeRate * ChargeEfficiencyCurve.Evaluate(0f);
                string chargeStr = chargeAtSOC0.ToString("F2");
                if (ChargeInfoEditor != chargeStr)
                {
                    ChargeInfoEditor = chargeStr;
                    _fChargeInfoEditor.SetValue(ChargeInfoEditor, this);
                }

                ApplyThermalEffects(lastECpower);
            }
        }

        private void OnEditorShipModified(ShipConstruct ship)
        {
            ApplyStagingState();
        }

        private void OnDestroy()
        {
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
        }

        public override void OnActive()
        {
            // Only react if the player armed staging and we haven't fired yet.
            if (!BatteryStaged || StageFired) return;

            // For thermal batteries we'll later enforce "non-disableable".
            // For now, staging simply enables the battery.
            BatteryDisabled = false;
            StageFired = true;
            part.stagingOn = false; // consume this stage
            ApplyStagingState(); // refresh stage icon/stack now that this stage fired

            RBLog.Info($"[RealBattery] Staged activation: Battery enabled on '{part.partInfo?.title}'");

            // Latch if this is a FixedOutput battery.
            EnforceNonDisableableLatch();
        }
        public override string GetInfo()
        {
            RBLog.Verbose("INF GetInfo");

            // Branch A: multi-chemistry part (has B9PS batterySwitch) — generic tooltip
            bool hasB9Switch = part?.Modules != null &&
                part.Modules.OfType<ModuleB9PartSwitch>().Any(m => m.moduleID == "batterySwitch");
            if (hasB9Switch)
            {
                double vol = ReadPartVolumeL();
                return vol > 0.0
                    ? Localizer.Format("#LOC_RB_VAB_Info", vol.ToString("F1"))
                    : Localizer.Format("#LOC_RB_VAB_Info_NoVolume");
            }

            // Branch B: single-chemistry part — resolve title/description
            ConfigNode partCfg = part?.partInfo?.partConfig;
            string title = null, descDetail = null, descSummary = null;

            if (partCfg != null)
            {
                ConfigNode rbModule = partCfg.GetNodes("MODULE")
                    .FirstOrDefault(n => n.GetValue("name") == "RealBattery");

                // Try ChemistryID → look up raw node in GameDatabase
                string chemId = rbModule?.GetValue("ChemistryID");
                if (!string.IsNullOrEmpty(chemId) && GameDatabase.Instance != null)
                {
                    ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REALBATTERY_CHEMISTRY");
                    if (nodes != null)
                    {
                        foreach (ConfigNode n in nodes)
                        {
                            if (n.GetValue("ChemistryID") == chemId)
                            {
                                title       = n.GetValue("title");
                                descDetail  = n.GetValue("descriptionDetail");
                                descSummary = n.GetValue("descriptionSummary");
                                break;
                            }
                        }
                    }
                }

                // Fallback: inline fields in MODULE { name = RealBattery }
                if (string.IsNullOrEmpty(title))
                    title = rbModule?.GetValue("title");
                if (string.IsNullOrEmpty(descDetail))
                    descDetail = rbModule?.GetValue("descriptionDetail");
                if (string.IsNullOrEmpty(descSummary))
                    descSummary = rbModule?.GetValue("descriptionSummary");
            }

            // No useful data → fall back to generic tooltip
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(descDetail) && string.IsNullOrEmpty(descSummary))
            {
                double vol = ReadPartVolumeL();
                return vol > 0.0
                    ? Localizer.Format("#LOC_RB_VAB_Info", vol.ToString("F1"))
                    : Localizer.Format("#LOC_RB_VAB_Info_NoVolume");
            }

            // Assemble Branch B tooltip
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(title))
                sb.Append("<b>").Append(Localizer.Format("#LOC_RB_Tech")).Append("</b>: ")
                  .Append(Localizer.Format(title));

            if (!string.IsNullOrEmpty(descDetail))
            {
                if (sb.Length > 0) sb.Append('\n').Append('\n');
                sb.Append(Localizer.Format(descDetail));
            }

            if (!string.IsNullOrEmpty(descSummary))
            {
                if (sb.Length > 0) sb.Append('\n').Append('\n');
                sb.Append(Localizer.Format(descSummary));
            }

            double volume = ReadPartVolumeL();
            if (volume > 0.0)
            {
                sb.Append('\n').Append('\n')
                  .Append(Localizer.Format("#LOC_RB_VAB_Info_Volume", volume.ToString("F1")));
            }

            return sb.ToString();
        }

        // Reads the RBbaseVolume cfg key set by MM patches; 0 if absent or unparseable.
        // Called from GetInfo() (before OnStart) and from OnStart() to populate _rbVolume.
        private double ReadPartVolumeL()
        {
            if (part?.partInfo?.partConfig != null &&
                part.partInfo.partConfig.HasValue("RBbaseVolume") &&
                double.TryParse(part.partInfo.partConfig.GetValue("RBbaseVolume"), out double v))
                return v;
            return 0.0;
        }
        private void LoadConfig(ConfigNode node = null)
        {
            ConfigNode config = node;

            // In Editor: usa il config originale della parte
            if (HighLogic.LoadedSceneIsEditor && part != null && part.partInfo != null)
            {
                config = part.partInfo.partConfig;
            }

            if (config != null)
            {
                if (config.HasValue("BatteryTypeDisplayName"))
                    BatteryTypeDisplayName = config.GetValue("BatteryTypeDisplayName");

                if (config.HasValue("HighEClevel"))
                    HighEClevel = float.Parse(config.GetValue("HighEClevel"));

                if (config.HasValue("LowEClevel"))
                    LowEClevel = float.Parse(config.GetValue("LowEClevel"));

                if (config.HasValue("Crate"))
                    Crate = float.Parse(config.GetValue("Crate"));

                if (config.HasValue("CycleDurability"))
                    CycleDurability = float.Parse(config.GetValue("CycleDurability"));

                if (config.HasValue("SelfDischargeRate"))
                    SelfDischargeRate = float.Parse(config.GetValue("SelfDischargeRate"));

                if (config.HasNode("ChargeEfficiencyCurve"))
                {
                    ChargeEfficiencyCurve = new FloatCurve();
                    ChargeEfficiencyCurve.Load(config.GetNode("ChargeEfficiencyCurve"));
                }

                if (config.HasValue("ThermalLoss"))
                    ThermalLoss = double.Parse(config.GetValue("ThermalLoss"));

                if (config.HasValue("TempOverheat"))
                    TempOverheat = float.Parse(config.GetValue("TempOverheat"));

                if (config.HasValue("TempRunaway"))
                    TempRunaway = float.Parse(config.GetValue("TempRunaway"));

                if (config.HasValue("TempOptimal"))
                    TempOptimal = float.Parse(config.GetValue("TempOptimal"));

                if (config.HasValue("moduleActive"))
                    bool.TryParse(config.GetValue("moduleActive"), out moduleActive); // default stays 'true' if missing

                if (config.HasValue("FixedOutput"))
                    bool.TryParse(config.GetValue("FixedOutput"), out FixedOutput);

                if (config.HasValue("KeepWarm"))
                    bool.TryParse(config.GetValue("KeepWarm"), out KeepWarm);

                if (config.HasValue("RunawayHeatFactor"))
                    RunawayHeatFactor = double.Parse(config.GetValue("RunawayHeatFactor"));
            }

            // v2 backward compat: KeepWarm (bool) -> KeepWarmMode (string).
            // Only migrate when the cfg did not supply KeepWarmMode explicitly.
            if (KeepWarm && KeepWarmMode == "false")
                KeepWarmMode = "warm";

            // v3: if ChemistryID is set, pull all parameters from the DB.
            // This overrides every inline cfg value read above.
            ApplyChemistryFromDB();

            // Inline RESOURCE_EXTRA support: when no ChemistryID is set the DB path never
            // runs, so parse any RESOURCE_EXTRA sub-nodes straight from this module's cfg.
            if (string.IsNullOrEmpty(ChemistryID))
                LoadInlineResourceExtras();

            PartResource ElectricCharge = part.Resources.Get("ElectricCharge");
            PartResource StoredCharge = part.Resources.Get("StoredCharge");

            if (ElectricCharge == null)
            {
                RealBatterySettings.Verbose("ElectricCharge not found, creating fallback...");
                ElectricCharge = part.Resources.Add("ElectricCharge", 0.1, 0.1, true, true, true, true, PartResource.FlowMode.Both);
            }

            if (StoredCharge == null)
            {
                RBLog.Verbose("StoredCharge not found, creating fallback...");
                StoredCharge = part.Resources.Add("StoredCharge", 0, 0, true, true, true, true, PartResource.FlowMode.Both);
            }

            DischargeRate = StoredCharge.maxAmount * Crate; //kW
            ChargeInfoEditor = String.Format("{0:F2}", DischargeRate * ChargeEfficiencyCurve.Evaluate(0f));

            // Keep SC_SOC and StoredCharge in sync without overwriting flight save data.
            // In flight we trust the saved resource amount and update SC_SOC from it.
            // In the editor we use SC_SOC to initialize the resource.
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (StoredCharge.maxAmount > 0)
                    SC_SOC = Math.Max(0.0, Math.Min(1.0, StoredCharge.amount / StoredCharge.maxAmount));
            }
            else
            {
                StoredCharge.amount = Math.Max(0.0, Math.Min(1.0, SC_SOC)) * StoredCharge.maxAmount;
            }

            UIPartActionWindow[] partWins = FindObjectsOfType<UIPartActionWindow>();
            foreach (UIPartActionWindow partWin in partWins)
            {
                partWin.displayDirty = true;
            }

            _fChargeInfoEditor.guiActiveEditor = (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f) > 0);

            if (ChargeEfficiencyCurve != null && ChargeEfficiencyCurve.Curve != null)
            {
                StringBuilder curveStr = new StringBuilder();
                foreach (var key in ChargeEfficiencyCurve.Curve.keys)
                {
                    curveStr.AppendFormat("({0:F2}, {1:F2}) ", key.time, key.value);
                }
                RBLog.Verbose($"  ChargeEfficiencyCurve = {curveStr.ToString().Trim()}");
            }
            else
            {
                RBLog.Verbose("  ChargeEfficiencyCurve = null or empty");
            }
        }


        private void ApplyChemistryFromDB()
        {
            if (string.IsNullOrEmpty(ChemistryID)) return;

            RealBatteryChemistry chem = RealBatteryChemistryDB.Get(ChemistryID);
            if (chem == null)
            {
                RBLog.Warn($"[RealBattery] ChemistryID '{ChemistryID}' not found in DB — using inline cfg values.");
                return;
            }

            // Electrical
            HighEClevel           = chem.HighEClevel;
            LowEClevel            = chem.LowEClevel;
            Crate                 = chem.Crate;
            ChargeEfficiencyCurve = chem.ChargeEfficiencyCurve;

            // Degradation
            CycleDurability       = chem.CycleDurability;
            SelfDischargeRate     = chem.SelfDischargeRate;
            EvaRefurbishEnabled   = chem.EvaRefurbishEnabled;
            SparePartsPerKWh      = chem.SparePartsPerKWh;
            EVAminLevel           = chem.EVAminLevel;

            // Thermal
            ThermalLoss           = chem.ThermalLoss;
            TempOverheat          = chem.TempOverheat;
            TempRunaway           = chem.TempRunaway;
            TempOptimal           = chem.TempOptimal;
            RunawayHeatFactor     = chem.RunawayHeatFactor;

            // Behavior flags
            FixedOutput           = chem.FixedOutput;
            if (!BatteryStagedUserSet)
            {
                // chem.BatteryStaged is assigned directly (not via the PAW toggle), so the
                // onFieldChanged handler in WireStagedToggleHandlers never fires for it — call
                // ApplyStagingState() here whenever it actually changes the value, otherwise the
                // staging icon/arming state goes stale until the part is reloaded.
                bool prevBatteryStaged = BatteryStaged;
                BatteryStaged = chem.BatteryStaged;
                if (BatteryStaged != prevBatteryStaged)
                    ApplyStagingState();
            }
            KeepWarmMode          = chem.KeepWarmMode;
            TempKeepWarmLo        = chem.TempKeepWarmLo;
            TempKeepWarmHi        = chem.TempKeepWarmHi;
            SelfRunaway           = chem.SelfRunaway;
            RunawayBaseChance     = chem.RunawayBaseChance;
            LifeDecay             = chem.LifeDecay;
            InfiniteCycles        = chem.InfiniteCycles;

            // v3.2.0 additions
            CrateScale            = chem.CrateScale;
            CrateScaleFactor      = chem.CrateScaleFactor;
            _resourceExtras       = chem.ResourceExtras ?? new List<ResourceRequirement>();

            BatteryTypeDisplayName = chem.displayName;

            RBLog.Verbose($"[RealBattery] Chemistry '{ChemistryID}' applied from DB on '{part?.partInfo?.title}'.");
        }

        // Parses RESOURCE_EXTRA sub-nodes for inline (non-ChemistryID) batteries directly
        // from this module's prefab cfg. Reads part.partInfo.partConfig (available in both
        // editor and flight) rather than the persisted node, which omits cfg-only sub-nodes.
        private void LoadInlineResourceExtras()
        {
            _resourceExtras = new List<ResourceRequirement>();

            ConfigNode partCfg = part?.partInfo?.partConfig;
            if (partCfg == null) return;

            ConfigNode rbModule = partCfg.GetNodes("MODULE")
                .FirstOrDefault(n => n.GetValue("name") == nameof(RealBattery));
            if (rbModule == null) return;

            foreach (ConfigNode reNode in rbModule.GetNodes("RESOURCE_EXTRA"))
                _resourceExtras.Add(ResourceRequirement.Load(reNode));

            if (_resourceExtras.Count > 0)
                RBLog.Verbose($"[LoadConfig] Loaded {_resourceExtras.Count} inline RESOURCE_EXTRA entr(ies) on '{part?.partInfo?.title}'.");
        }


        // ============================================================================
        //  CORE OPERATIONS
        // ============================================================================
        public double XferECtoRealBattery(double amount)
        {
            if (ScResourceMissing)
            {
                lastECpower = 0.0;
                return 0.0; // CommunityResourcePack missing — StoredCharge resource unavailable, see SC_ID
            }

            if (KeepWarmMode != "false" && (keepWarmActive || controlledShutdownActive))
            {
                lastECpower = 0.0;
                return 0.0; // fully blocked during warmup or controlled shutdown
            }

            if (RealBatterySettings.EnableThermalRunaway &&
                (isRunaway || forcedRunawayActive))
            {
                lastECpower = 0.0;
                return 0.0;
            }

            // normal battery part

            double EC_delta = 0;
            double SC_delta = 0;
            double EC_power = 0;

            PartResource StoredCharge = part.Resources.Get("StoredCharge");

            double EngBonus = EngineerBonus();
            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            // maximum discharge rate EC/s or kW
            DischargeRate = StoredCharge.maxAmount * Crate * ActualLife; //kW;

            if (!FixedOutput) DischargeRate *= EngBonus;

            double chargeLimit = InfiniteCycles ? ThermalCapFactor : ActualLife;

            // Operation for this tick (mirrors the charge/discharge branch guards below).
            bool charging    = amount > 0 && SC_SOC < chargeLimit && !BatteryDisabled && !FixedOutput;
            bool discharging = (amount < 0 || FixedOutput) && SC_SOC > 0 && !BatteryDisabled;

            // RESOURCE_EXTRA pre-flight gate — only for chemistries that actually define
            // extras. Skipped entirely (no curve eval, no helper call) for the common
            // no-extras battery, which is the vast majority.
            if (_resourceExtras != null && _resourceExtras.Count > 0)
            {
                // Planned EC magnitude for this tick (upper bound of what the transfer can move),
                // used to pre-flight RESOURCE_EXTRA inputs before any EC/SC is committed.
                double plannedECabs = charging
                    ? Math.Min(TimeWarp.fixedDeltaTime * DischargeRate * ChargeEfficiencyCurve.Evaluate((float)SC_SOC), amount)
                    : (discharging
                        ? (FixedOutput ? TimeWarp.fixedDeltaTime * DischargeRate
                                       : Math.Min(TimeWarp.fixedDeltaTime * DischargeRate, -amount))
                        : 0.0);

                // Atomic gate: if any active input RESOURCE_EXTRA can't cover the planned transfer,
                // bail before moving any EC/SC so the tick leaves vessel resource state untouched.
                if (!ResourceExtraInputsAvailable(plannedECabs, charging, discharging))
                {
                    lastECpower = 0.0;
                    return 0;
                }
            }

            if (charging) // Charge battery
            {
                double SOC_ChargeEfficiency = ChargeEfficiencyCurve.Evaluate((float)SC_SOC);
                EC_delta = TimeWarp.fixedDeltaTime * DischargeRate * SOC_ChargeEfficiency;  // maximum amount of EC the battery can convert to SC, limited by current charge capacity

                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, Math.Min(EC_delta, amount));

                SC_delta = -EC_delta / EC2SCratio;                  // SC_delta = -1EC / 10EC/SC * 0.9 = -0.09SC
                SC_delta = part.RequestResource(SC_ID, SC_delta);   // actual SC accepted (negative); may fall short of the request if the battery is near full

                // Overfill guard: if the battery couldn't accept the full planned SC (near-full
                // capacity), give back the unconverted EC to the network instead of destroying it.
                double excessEC = EC_delta + SC_delta * EC2SCratio;
                if (excessEC > EPS)
                {
                    part.RequestResource(PartResourceLibrary.ElectricityHashcode, -excessEC);
                    EC_delta -= excessEC;
                }

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;

                RBLog.Verbose("INF charged");
            }
            else if (discharging)  // Discharge battery (or fixed output)
            {
                SC_delta = TimeWarp.fixedDeltaTime * DischargeRate / EC2SCratio;      // maximum amount of SC the battery can convert to EC

                if (!FixedOutput)
                {
                    // Normal discharge: cap by demand (-amount)
                    SC_delta = part.RequestResource(SC_ID, Math.Min(SC_delta, -amount / EC2SCratio)); // requesting SC; positive return value
                }
                else
                {
                    // Fixed output: always consume up to the max allowed this tick
                    SC_delta = part.RequestResource(SC_ID, SC_delta); // requesting SC; positive return value
                }

                // Try to inject EC; if vessel EC is full, this may return 0
                EC_delta = -SC_delta * EC2SCratio;          // target EC to inject (negative = add to network)
                double EC_accepted = part.RequestResource(PartResourceLibrary.ElectricityHashcode, EC_delta);

                // Report thermal/power using *chemical* output when FixedOutput is true,
                // otherwise use the *accepted* electrical power.
                double chemEC = SC_delta * EC2SCratio;          // > 0 by construction
                EC_power = FixedOutput
                    ? -(chemEC / TimeWarp.fixedDeltaTime)       // negative = discharging (chemically)
                    : (EC_accepted / TimeWarp.fixedDeltaTime);  // could be 0 if EC is full

                RBLog.Verbose("INF discharged");
            }
            else
            {
                EC_power = 0;

                RBLog.Verbose("INF no charge or discharge");
            }

            // --- RESOURCE_EXTRA: chemistry-defined auxiliary resource flows ---
            // Consume inputs / produce outputs in proportion to the EC actually moved this
            // tick. Input availability was already gated up front by ResourceExtraInputsAvailable(),
            // so no blocking is needed here (the transfer is already committed).
            // Each RequestResource omits an explicit flow mode, so KSP applies the resource's
            // own default flow mode (e.g. STAGE_PRIORITY_FLOW respects decoupler crossfeed,
            // ALL_VESSEL stays vessel-wide). This keeps consumption consistent with the
            // crossfeed-aware gate, and lets excess output spill (be dumped) when tanks are
            // full or unreachable.
            if (_resourceExtras != null && _resourceExtras.Count > 0 && Math.Abs(EC_delta) > EPS)
            {
                foreach (ResourceRequirement req in _resourceExtras)
                {
                    if (req == null || string.IsNullOrEmpty(req.name) || req.ratio <= 0.0)
                        continue;

                    // Active only when the entry's mode matches the current operation.
                    bool active = (req.mode == "charge" && charging)
                               || (req.mode == "discharge" && discharging);
                    if (!active) continue;

                    double requested = req.ratio * Math.Abs(EC_delta) / EC2SCratio;
                    if (requested <= EPS) continue;

                    if (req.type == "output")
                    {
                        // Negative request = inject into the vessel; excess (full/unreachable
                        // tanks) is not stored and is effectively dumped.
                        double produced = part.RequestResource(req.name, -requested);
                        RBLog.Verbose($"[XferECtoRealBattery][ResourceExtra] output '{req.name}': requested={requested:F4}, received={(-produced):F4}");
                    }
                    else // "input"
                    {
                        double received = part.RequestResource(req.name, requested);
                        RBLog.Verbose($"[XferECtoRealBattery][ResourceExtra] input '{req.name}': requested={requested:F4}, received={received:F4}");
                    }
                }
            }

            // Count wear: for FixedOutput use SC actually consumed; otherwise EC accepted.
            // InfiniteCycles batteries skip wear accumulation entirely.
            double wearSC = FixedOutput
                ? SC_delta
                : ((Math.Abs(EC_delta) / EC2SCratio) / EngBonus);
            if (wearSC > EPS && !InfiniteCycles)
            {
                WearCounter += wearSC; // kWh equivalent in SC units
                UpdateBatteryLife();
            }

            //update SOC field for usage in other modules (load balancing)
            SC_SOC = StoredCharge.maxAmount > 0 ? StoredCharge.amount / StoredCharge.maxAmount : 0.0;

            lastECpower = EC_power;

            ApplyThermalEffects(EC_power);

            return EC_delta;
        }

        // Pre-flight check for RESOURCE_EXTRA inputs: returns false if any active input
        // entry can't cover the planned transfer this tick. Non-consuming (uses
        // GetConnectedResourceTotals) so the caller can bail before committing any EC/SC.
        // GetConnectedResourceTotals uses each resource's default flow mode, so crossfeed
        // barriers (e.g. a decoupler with resource transfer disabled) are respected: a
        // resource stranded on a non-communicating stage reads as unavailable and blocks.
        private bool ResourceExtraInputsAvailable(double plannedECabs, bool charging, bool discharging)
        {
            if (_resourceExtras == null || _resourceExtras.Count == 0 || plannedECabs <= EPS)
                return true;

            foreach (ResourceRequirement req in _resourceExtras)
            {
                if (req == null || req.type != "input" || string.IsNullOrEmpty(req.name) || req.ratio <= 0.0)
                    continue;

                bool active = (req.mode == "charge" && charging)
                           || (req.mode == "discharge" && discharging);
                if (!active) continue;

                double required = req.ratio * plannedECabs / EC2SCratio;
                if (required <= EPS) continue;

                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(req.name);
                if (def == null) continue; // unknown resource: don't block on it

                part.GetConnectedResourceTotals(def.id, out double avail, out _);
                if (avail < required * (1.0 - EPS))
                {
                    RBLog.Verbose($"[XferECtoRealBattery][ResourceExtra] input '{req.name}' short: need {required:F4}, have {avail:F4} — transfer cancelled.");
                    return false;
                }
            }
            return true;
        }
        private void ApplyThermalEffects(double EC_power)
        {
            bool heatSimOn = RealBatterySettings.EnableHeatSimulation;
            bool useSHOn = RealBatterySettings.UseSystemHeat;

            // If we shouldn't be feeding SystemHeat (heat sim off, or SH integration off),
            // send a one-shot zero to silence the loop. SH seems to treat registered-but-silent
            // modules as having a residual/default value.
            if (!heatSimOn || !useSHOn)
            {
                if (RealBatterySettings.SystemHeatAvailable && !_shSilenced)
                {
                    if (systemHeat == null)
                        systemHeat = SystemHeatBridge.GetModule(part);
                    if (systemHeat != null)
                    {
                        smoothFlux = 0f;
                        SystemHeatBridge.AddFlux(systemHeat, "RealBattery", 0f, 0f, true);
                        _shSilenced = true;
                        RBLog.Info($"[ApplyThermalEffects] Cleared SystemHeat flux on '{part.partInfo?.title}' (heat sim or SH integration disabled).");
                    }
                }

                // If heat sim is fully off, bail entirely.
                // Otherwise (heat sim on, SH off) fall through so the stock heat path still runs.
                if (!heatSimOn) return;
            }
            else
            {
                // Both heat sim and SH integration on: clear flag so off-branch can re-trigger later.
                _shSilenced = false;
            }

            // Resolve heat backend and current temperature once
            // Always refresh SystemHeat module if feature is enabled.

            // Ensure fresh SystemHeat reference if needed
            if (RealBatterySettings.UseSystemHeat && systemHeat == null)
                systemHeat = SystemHeatBridge.GetModule(part);

            bool useSH = RealBatterySettings.UseSystemHeat && systemHeat != null;

            float tempK = GetCurrentTemperatureK();
            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            // If thermal runaway is globally disabled, suppress runaway state
            if (!RealBatterySettings.EnableThermalRunaway)
                isRunaway = false;

            HandleRunawayNotifications(isRunaway, tempK);

            // After runaway suppression, re-check if we should still proceed
            if (BatteryDisabled && !isRunaway)
                return;

            PartResource sc = part.Resources.Get("StoredCharge");
            double safeLife = ActualLife > 0.001 ? ActualLife : 0.001;
            float EngBonus = (float)EngineerBonus();

            // Recompute DischargeRate safely (kW) for this context
            double dischargeKW = ActualLife * sc.maxAmount * RealBatterySettings.RunawayBaseMagnitude;// * 0.1;
            if (RunawayHeatFactor > 0.0)
                dischargeKW *= RunawayHeatFactor;
            else
                dischargeKW *= Crate;

            double heatPowerKW = isRunaway ? dischargeKW : Math.Abs(EC_power);

            float flux = 0f;
            if (isRunaway && !InfiniteCycles && RealBatterySettings.EnableThermalRunaway && heatPowerKW > EPS)
            {
                WearCounter += (heatPowerKW * CycleDurability * TimeWarp.fixedDeltaTime / 3600.0);
                UpdateBatteryLife(); // will reduce BatteryLife via BatteryLifeCurve
            }

            // If the battery is essentially "spent", stop chemical runaway and switch to a short decay tail.
            if (isRunaway && ActualLife < 0.01)
            {
                // Capture last heat level as tail start and mark extinguished
                runawayTailKW = Math.Max(runawayTailKW, heatPowerKW);
                runawayExtinguished = true;
                isRunaway = false;          // stop the chemical runaway path
                heatPowerKW = 0.0;          // no more chemical heat injection

                // Clear forced-runaway after the cell is essentially "burned out"
                forcedRunawayActive = false;
            }

            // Apply a small exponential tail after extinction to model residual heat/fire dying out.
            if (runawayExtinguished && !isRunaway)
            {
                // Choose a short tau for gameplay feel; tweakable later via settings if needed.
                const double tauSeconds = 10.0; // seconds
                double dt = Math.Max(TimeWarp.fixedDeltaTime, 0.0);
                double decay = Math.Exp(-dt / Math.Max(tauSeconds, 0.001));

                runawayTailKW *= decay;

                if (runawayTailKW > 1e-4)
                {
                    // Feed the thermal pipeline with the residual tail (treated as discharge-like loss)
                    double tailFluxKW = runawayTailKW;
                    // Reuse existing heat path below via "flux" calculation:
                    // set heatPowerKW so the usual flux code generates W from kW consistently.
                    heatPowerKW = Math.Max(heatPowerKW, tailFluxKW);
                }
                else
                {
                    // Tail ended; reset state
                    runawayTailKW = 0.0;
                    runawayExtinguished = false;
                    heatPowerKW = 0.0;
                }
            }

            // In waste heat mode, thermal modeling is handled exclusively by TickCryoWasteHeat()
            if (!UsesCryoWasteHeat())
            {
                // If we have power movement or a runaway condition, compute heat
                if (heatPowerKW > 0.0001)
                {
                    // Use "discharge-like" path for runaway to avoid division by charge efficiency
                    bool treatAsDischarge = (EC_power < 0) || isRunaway;
                    double ineff = Math.Max(SOC_ChargeEfficiency, 0.001); // avoid 0 div

                    if (treatAsDischarge)
                        flux = (float)(heatPowerKW * ThermalLoss / safeLife);
                    else
                        flux = (float)(heatPowerKW * ThermalLoss / ineff / safeLife);

                    if (!isRunaway) flux /= EngBonus;

                    SendThermalFlux("RealBattery", flux, smooth: true, tempK, isRunaway ? "ACTIVE (Runaway)" : "ACTIVE");
                }
                else if (useSH && systemHeat != null)
                {
                    // Battery idle: no heat injected, but keep the operating target declared
                    // so the loop doesn't collapse toward 0 K between active cycles (SystemHeat
                    // only — stock has no equivalent "declare idle" concept).
                    smoothFlux = 0f;
                    SendThermalFlux("RealBattery", 0f, smooth: false, tempK, "IDLE");
                }
            }

            // --- Thermal wear (flight only) ---

            if (!RealBatterySettings.EnableBatteryWear) return;
            if (!HighLogic.LoadedSceneIsFlight) return;

            // --- InfiniteCycles thermal cap (replaces classic wear/runaway path for these batteries) ---
            // Skip during warmup/shutdown: ThermalCapFactor is owned by the state machine ramps in those
            // phases.
            if (InfiniteCycles && sc != null && RealBatterySettings.EnableBatteryWear
                && !keepWarmActive && !controlledShutdownActive)
            {
                // De-dup guard: the FixedUpdate force-tick and a same-tick XferECtoRealBattery
                // call (via RealBatteryLoadMaster, a separate VesselModule with no guaranteed
                // FixedUpdate ordering relative to this PartModule) can both reach this block in
                // the same physics step. Only apply the cap once per physics tick.
                if (_lastInfCycleCapFixedTime == Time.fixedTime)
                    return;
                _lastInfCycleCapFixedTime = Time.fixedTime;

                if (tempK > TempOverheat)
                {
                    float range = Mathf.Max(Mathf.Min(TempOverheat - TempOptimal, TempRunaway - TempOverheat), 1f);
                    float severity = Mathf.Clamp01((tempK - TempOverheat) / range);

                    // Linear cap: 1.0 at TempOverheat -> 0.0 at TempRunaway
                    ThermalCapFactor = 1.0 - severity;

                    // Clamp sc.amount to effective cap (mirrors UpdateBatteryLife pattern)
                    double effectiveCap = sc.maxAmount * ThermalCapFactor;
                    if (sc.amount > effectiveCap)
                        sc.amount = effectiveCap;
                    SC_SOC = sc.maxAmount > 0 ? sc.amount / sc.maxAmount : 0.0;

                    // Additional heat proportional to severity (linear, unlike the exponential
                    // used in the classic path — appropriate for InfiniteCycles physics).
                    // Pure power term, not scaled by fixedDeltaTime: previously
                    // severity * fixedDeltaTime * sc.maxAmount was an energy-per-tick term used
                    // directly as a power, so it scaled up with timewarp. The 0.02 coefficient
                    // preserves the exact previous value at the reference 50 Hz / 0.02 s physics
                    // tick (warp 1x), but the term no longer depends on the actual timestep.
                    float heatBoost = severity * (float)sc.maxAmount * 0.02f;
                    SendThermalFlux("RealBattery", heatBoost * (float)ThermalLoss, smooth: false, tempK, "InfCycleCap");

                    RBLog.Verbose($"[ApplyThermalEffects] InfiniteCycles thermal cap: " +
                                  $"severity={severity:F2}, cap={ThermalCapFactor:F3}, " +
                                  $"effectiveCap={effectiveCap:F3}");

                    if (ThermalCapFactor < 1.0 && !ThermalCapNotified)
                    {
                        ThermalCapNotified = true;

                        var msg = new MessageSystem.Message(
                            Localizer.Format("#LOC_RB_ThermalCap_title"),
                            Localizer.Format("#LOC_RB_ThermalCap_body", part.partInfo.title),
                            MessageSystemButton.MessageButtonColor.ORANGE,
                            MessageSystemButton.ButtonIcons.ALERT
                        );
                        MessageSystem.Instance?.AddMessage(msg);

                        RBLog.Warn($"[ApplyThermalEffects] InfiniteCycles thermal cap triggered on " +
                                   $"'{part.partInfo?.title}' ({tempK:F1} K > {TempOverheat:F0} K)");
                    }
                }
                else if (tempK < TempOverheat - 10f)
                {
                    // Reset cap on cooling (hysteresis matches OverheatNotified reset)
                    ThermalCapFactor = 1.0;
                    ThermalCapNotified = false; // reset: allow re-notification on next overheat event
                }

                // InfiniteCycles batteries do not use the classic overheat/runaway path: return early.
                return;
            }
            
            if (!InfiniteCycles && sc != null && tempK > TempOverheat)
            {
                float range = Mathf.Max(Mathf.Min(TempOverheat - TempOptimal, TempRunaway - TempOverheat), 1f);
                float severity = Mathf.Clamp01((tempK - TempOverheat) / range);
                float thermalFactor = Mathf.Exp(severity * 4.0f) - 1.0f;
                float deltaWear = thermalFactor * TimeWarp.fixedDeltaTime * (float)(sc.maxAmount);

                if (!FixedOutput && !isRunaway) deltaWear /= EngBonus;

                WearCounter += deltaWear;
                RBLog.Verbose($"[ApplyThermalEffects] Thermal wear: Δ={deltaWear:F5}, T={tempK:F1} K");
            }

            // --- Automatic shutdown on overheat (if PCM unlocked) or toast alert ---
            HandleOverheatUX(tempK);
        }

        // Sends thermal output to the active backend (SystemHeat if enabled and available,
        // otherwise stock part.AddThermalFlux), unifying what used to be duplicated per call site
        // with two different conventions. Preserves each backend's exact prior numeric behavior:
        //   - SystemHeat: fluxKW is scaled x0.01 (empirical tuning factor, unchanged) before being
        //     sent. When smooth=true, the scaled value is ramped through the smoothFlux state via
        //     a time-based exponential (SMOOTH_FLUX_TAU_S) instead of being sent directly — this
        //     is the "active charge/discharge" path. When smooth=false, the scaled value is sent
        //     as-is without touching smoothFlux — this is the InfiniteCycles thermal-cap path,
        //     which never interacted with the main flux's smoothing state. Callers that need a
        //     hard reset (silencing, idle) should set smoothFlux = 0f themselves before calling
        //     with fluxKW = 0f, smooth = false.
        //   - Stock: fluxKW is sent as-is via part.AddThermalFlux — no scaling, never smoothed.
        // tempK/logTag are only used for the verbose log line.
        private void SendThermalFlux(string sourceId, float fluxKW, bool smooth, float tempK, string logTag)
        {
            bool useSH = RealBatterySettings.UseSystemHeat && systemHeat != null;
            if (useSH)
            {
                float scaled = fluxKW * 0.01f;
                float toSend;
                if (smooth)
                {
                    double ratio = 1.0 - Math.Exp(-TimeWarp.fixedDeltaTime / SMOOTH_FLUX_TAU_S);
                    smoothFlux += (float)ratio * (scaled - smoothFlux);
                    toSend = smoothFlux;
                }
                else
                {
                    toSend = scaled;
                }
                SystemHeatBridge.AddFlux(systemHeat, sourceId, TempOptimal, toSend, true);
                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux {logTag} (SystemHeat): {toSend:F2} W @ target={TempOptimal:F0} K (loop={tempK:F1} K)");
            }
            else
            {
                part.AddThermalFlux(fluxKW);
                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux {logTag} (stock): {fluxKW:F2} W (part={tempK:F1} K)");
            }
        }

        public void UpdateBatteryLife()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            RBLog.Verbose($"[UpdateBatteryLife] Scene is FLIGHT");

            if (!RealBatterySettings.EnableBatteryWear) return;
            RBLog.Verbose($"[UpdateBatteryLife] UseBatteryWear is ON");

            if (InfiniteCycles) return;

            PartResource sc = part.Resources["StoredCharge"];
            
            if (sc.maxAmount < 0 || CycleDurability <= 0)
            {
                BatteryLife = 1.0;
                return;
            }

            double maxWear = sc.maxAmount * CycleDurability * 2;
            double ratio = WearCounter / maxWear;

            BatteryLife = Math.Min(BatteryLife, BatteryLifeCurve.Evaluate((float)ratio));

            if (sc.amount > (sc.maxAmount * BatteryLife))
                sc.amount = sc.maxAmount * BatteryLife;

            if (sc.maxAmount > 0)
                SC_SOC = sc.amount / sc.maxAmount;

            RBLog.Verbose($"[BatteryLife] capacity={sc.maxAmount:F3}, CycleDurability={CycleDurability}, maxWear={maxWear:F3}, WearCounter={WearCounter:F3}, ratio={ratio:F3}, BatteryLife={BatteryLife:F3}");

            if (!EOLToastSent && !BGSelfRunawaySent && BatteryLife < EOL_THRESHOLD)
            {
                EOLToastSent = true;

                string msgTitle = Localizer.Format("#LOC_RB_Toast_BatteryEOL_Title");
                string msgText = Localizer.Format(
                    "#LOC_RB_Toast_BatteryEOL",
                    part.partInfo?.title ?? part.partName
                );

                // Add a stock system message (like contract completion)
                MessageSystem.Message m = new MessageSystem.Message(
                    msgTitle,
                    msgText,
                    MessageSystemButton.MessageButtonColor.ORANGE,
                    MessageSystemButton.ButtonIcons.ALERT
                );
                MessageSystem.Instance.AddMessage(m);

                RBLog.Warn($"[BatteryLife] EOL reached on '{part.partInfo?.title}': BatteryLife={BatteryLife:P0}");
            }
        }
        public void ApplySelfDischarge()
        {
            if (!RealBatterySettings.EnableSelfDischarge) return;

            double hoursPerDay = RealBatterySettings.GetHoursPerDay();

            if (LifeDecay)
            {
                // Radioactive cells: SelfDischargeRate decays BatteryLife per tick
                // regardless of whether the battery is enabled or not.
                if (!RealBatterySettings.EnableBatteryWear) return;

                double lifeDecayPerSecond = SelfDischargeRate / (hoursPerDay * 3600.0);
                BatteryLife = Math.Max(0.0, BatteryLife - lifeDecayPerSecond);
                UpdateBatteryLife(); // propagate capacity changes

                RBLog.Verbose($"[ApplySelfDischarge] LifeDecay: BatteryLife={BatteryLife:F6} on '{part.partInfo?.title}'");
                return;
            }

            // Standard self-discharge: only when battery is OFF.
            RBLog.Verbose($"[ApplySelfDischarge] UseSelfDischarge is ON");
            if (!BatteryDisabled) return;
            RBLog.Verbose($"[ApplySelfDischarge] Battery is OFF");
            if (SC_SOC <= 0) return;

            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;
            double socLossPerDay    = SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0);
            double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);

            double SC_loss   = socLossPerSecond * part.Resources["StoredCharge"].maxAmount;
            double actualLoss = part.RequestResource("StoredCharge", SC_loss);

            if (part.Resources["StoredCharge"].maxAmount > 0.0)
                SC_SOC = part.Resources["StoredCharge"].amount / part.Resources["StoredCharge"].maxAmount;

            RBLog.Verbose($"[ApplySelfDischarge] Self-discharge of {actualLoss:F9} kWh applied to '{part.partInfo?.title}'");
        }


        // ============================================================================
        //  UI & TECH GATES
        // ============================================================================
        private void TechUnlockUI()
        {
            bool sandbox = HighLogic.CurrentGame?.Mode == Game.Modes.SANDBOX;

            bool SOCGaugeUnlocked = false;
            bool PCMUnlocked = false;
            bool BMSUnlocked = false;
            bool advBMSUnlocked = false;

            if (sandbox)
            {
                SOCGaugeUnlocked = true;
                PCMUnlocked = true;
                BMSUnlocked = true;
                advBMSUnlocked = true;
            }
            else
            {
                var SOCGauge = PartLoader.getPartInfoByName("RB.SOCGauge");
                var PCM = PartLoader.getPartInfoByName("RB.PCM");
                var BMS = PartLoader.getPartInfoByName("RB.BMS");
                var advBMS = PartLoader.getPartInfoByName("RB.advBMS");

                if (SOCGauge != null && ResearchAndDevelopment.Instance != null)
                    SOCGaugeUnlocked = ResearchAndDevelopment.PartModelPurchased(SOCGauge);

                if (PCM != null && ResearchAndDevelopment.Instance != null)
                    PCMUnlocked = ResearchAndDevelopment.PartModelPurchased(PCM);

                if (BMS != null && ResearchAndDevelopment.Instance != null)
                    BMSUnlocked = ResearchAndDevelopment.PartModelPurchased(BMS);

                if (advBMS != null && ResearchAndDevelopment.Instance != null)
                    advBMSUnlocked = ResearchAndDevelopment.PartModelPurchased(advBMS);
            }

            // --- Fetch PAW fields (guarded) ---
            BaseField fSOCStatus = _fBatterySOCStatus;
            BaseField fTimeTo = _fBatteryTimeTo;
            BaseField fLife = _fBatteryHealthStatus;

            // --- Apply visibility (Editor + Flight). Keep it symmetric unless you want editor-only behavior. ---
            if (fSOCStatus != null)
                fSOCStatus.guiActive = SOCGaugeUnlocked; // SOC gauge only with RB.SOCGauge unlocked

            if (fTimeTo != null)
                fTimeTo.guiActive = BMSUnlocked; // Time-to requires RB.BMS

            if (fLife != null)
                fLife.guiActive = advBMSUnlocked; // SOH/Life requires RB.advBMS

            // Automatic overheat protection: unlocked together with PCM
            PreventOverheat = PCMUnlocked;
        }
        private void ModuleActiveHideUI()
        {
            _fBatteryDisabled.guiActive = moduleActive;
            _fBatteryDisabled.guiActiveEditor = moduleActive;

            _fBatteryTypeDisplayName.guiActive = moduleActive;
            _fBatteryTypeDisplayName.guiActiveEditor = moduleActive;

            _fBatteryChargeStatus.guiActive = moduleActive;

            // Tech-gated fields must not be forced visible here.
            // Only force them OFF when the module is inactive.
            if (!moduleActive)
            {
                _fBatterySOCStatus.guiActive = moduleActive;
                _fBatteryTimeTo.guiActive = moduleActive;
                _fBatteryHealthStatus.guiActive = moduleActive;
            }

            _fDischargeRate.guiActiveEditor = moduleActive;
            _fChargeInfoEditor.guiActiveEditor = moduleActive;
            _fBatteryStaged.guiActiveEditor = moduleActive;
            _fSimulationMode.guiActiveEditor = moduleActive;
        }
        private void EnforceNonDisableableLatch()
        {
            // Latch only applies to FixedOutput designs.
            if (!FixedOutput)
                return;

            // If we are in flight and currently enabled, mark the latch.
            if (HighLogic.LoadedSceneIsFlight && !BatteryDisabled && !ActivationLatched)
            {
                ActivationLatched = true; // first activation observed
                RBLog.Info("[RealBattery] Activation latched (FixedOutput): this battery can no longer be disabled.");
            }

            // If latch is active, force Disabled=false no matter what tried to flip it.
            if (ActivationLatched && BatteryDisabled)
            {
                BatteryDisabled = false; // revert any attempt to re-disable
                RBLog.Verbose("[RealBattery] Latch invariant enforced: reverting BatteryDisabled -> false");
            }

            // UI/PAW: make the toggle read-only after latch in flight (still visible for status).
            if (_fBatteryDisabled != null)
            {
                // Editor remains configurable pre-flight; flight is locked post-latch.
                if (HighLogic.LoadedSceneIsFlight && ActivationLatched)
                {
                    if (_fBatteryDisabled.uiControlFlight != null)
                        _fBatteryDisabled.uiControlFlight.controlEnabled = false; // greyed out
                }
            }
        }


        // ============================================================================
        //  HELPERS
        // ============================================================================
        // Pushed by RealBatteryLoadMaster (vessel-level) on structural/crew-change events,
        // instead of each battery independently scanning all vessel parts every tick.
        public int CachedEngineerLevel = 0;

        // internal (not private): also called by RealBatteryPowerLedger.ReportConsumedEc so the
        // wear credited through the public ledger can never drift out of sync with the live
        // sim's own formula.
        internal double EngineerBonus() => 0.95 + 0.06 * CachedEngineerLevel;
        private float GetCurrentTemperatureK()
        {
            // --- SystemHeat branch ---
            if (RealBatterySettings.UseSystemHeat)
            {
                if (systemHeat == null)
                    systemHeat = SystemHeatBridge.GetModule(part);

                // guard clause: avoid null access if module missing or feature disabled mid-flight
                if (systemHeat != null && SystemHeatBridge.TryGetLoopTempK(systemHeat, out float tK))
                    return tK;
                if (systemHeat != null)
                    RBLog.Warn($"[RealBattery] SystemHeat module present but temperature read failed on '{part.partInfo?.title}', falling back to stock temperature.");
                else
                    RBLog.Warn($"[RealBattery] SystemHeat expected but missing on '{part.partInfo?.title}', falling back to stock temperature.");
            }

            // --- Stock branch ---
            return (float)part.temperature;
        }

        private static string FormatTimeSpan(TimeSpan t)
        {
            if (t.TotalDays >= 1)
                return Localizer.Format("#LOC_RB_days", (int)t.TotalDays) + " " +
                       Localizer.Format("#LOC_RB_hours", t.Hours);
            if (t.TotalHours >= 1)
                return Localizer.Format("#LOC_RB_hours", (int)t.TotalHours) + " " +
                       Localizer.Format("#LOC_RB_minutes", t.Minutes);
            if (t.TotalMinutes >= 1)
                return Localizer.Format("#LOC_RB_minutes", (int)t.TotalMinutes) + " " +
                       Localizer.Format("#LOC_RB_seconds", t.Seconds);
                return Localizer.Format("#LOC_RB_seconds", t.Seconds);
        }
        private void ApplyStagingState()
        {
            if (BatteryStaged)
            {
                // KSP 1.12 stagingIcon only accepts the stock DefaultIcons enum (no custom
                // texture via cfg). FUEL_TANK is a generic-storage silhouette, kept for standard
                // batteries to avoid changing the icon players already see; FixedOutput (thermal)
                // batteries get RCS_TANK instead purely to be visually distinguishable in the
                // stage list at a glance — neither name implies the resource actually consumed.
                if (string.IsNullOrEmpty(part.stagingIcon))
                    part.stagingIcon = FixedOutput ? "RCS_TANK" : "FUEL_TANK";
                part.stagingOn = true;
            }
            else
            {
                part.stagingOn = false;
            }
            part.UpdateStageability(true, true);

            // Force stage-stack icon rebuild. StageManager is the same singleton used for both
            // the editor and flight staging lists (SortIcons has no scene-restricted overload),
            // so this refresh applies in both — event-driven (arm/disarm/fire/chemistry change),
            // never per-frame.
            StageManager.Instance?.SortIcons(true);
        }
        private void WireStagedToggleHandlers()
        {
            if (_fBatteryStaged?.uiControlEditor != null)
                _fBatteryStaged.uiControlEditor.onFieldChanged += (f, o) =>
                {
                    BatteryStagedUserSet = true;
                    ApplyStagingState();
                };

            if (_fBatteryStaged?.uiControlFlight != null)
                _fBatteryStaged.uiControlFlight.onFieldChanged += (f, o) =>
                {
                    BatteryStagedUserSet = true;
                    ApplyStagingState();
                };
        }
        private float KeepWarmTempMultiplier()
        {
            if (!RealBatterySettings.EnableHeatSimulation) return 1f;
            if (KeepWarmMode == "false") return 0f;

            float tempK = GetCurrentTemperatureK();
            float span  = Mathf.Max(TempKeepWarmHi - TempKeepWarmLo, 1f);
            float t     = Mathf.Clamp01((tempK - TempKeepWarmLo) / span); // 0 at Lo, 1 at Hi

            // warm: full upkeep when cold, zero when hot  (LO->HI = 1->0)
            // cryo: zero upkeep when cold, full when hot  (LO->HI = 0->1)
            return KeepWarmMode == "cryo" ? t : 1f - t;
        }
        private double TickKeepWarm(bool isWarmupPhase, double dt)
        {
            //double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;
            PartResource sc = part.Resources.Get("StoredCharge");
            //double dischargeKW = (sc?.maxAmount ?? 0.0) * Crate * ActualLife; // mirrors Xfer estimation
            double ecPerSec = KeepWarmECperSec(isWarmupPhase, sc?.maxAmount ?? 0.0);
                        if (ecPerSec <= EPS) return 0.0; // hot enough or negligible cost
            double wantEC = ecPerSec * dt;
            double gotEC = (wantEC > 0.0) ? part.RequestResource(PartResourceLibrary.ElectricityHashcode, wantEC) : 0.0;
                return Math.Max(0.0, gotEC);
        }
        private double KeepWarmECperSec(bool isWarmupPhase, double capacity)
        {
            // Cryo waste heat mode: cryocooler driven by SystemHeat, not EC; upkeep is always zero.
            if (UsesCryoWasteHeat()) return 0.0;
            double baseMul = isWarmupPhase ? WARMUP_MULT : 1.0;
            float tempMul = KeepWarmTempMultiplier();
            // Scale on volume (L) when available; KeepWarmFrac is then EC/s per litre.
            // Falls back to sc.maxAmount for parts without RBbaseVolume.
            double scalingBase = _rbVolume > 0.0 ? _rbVolume : capacity;
            return scalingBase * RealBatterySettings.KeepWarmFrac * baseMul * tempMul;
        }

        // Warmup/shutdown duration scaled on part volume via sqrt curve (WARMUP_VOL_REF = 200 L → 60 s).
        // Parts without volume data fall back to 60 s. Hard cap: WARMUP_MAX_S.
        private double EffectiveWarmupSeconds()
        {
            if (_rbVolume <= 0.0) return 60.0;
            return Math.Max(1.0, Math.Min(WARMUP_MAX_S, 60.0 * Math.Sqrt(_rbVolume / WARMUP_VOL_REF)));
        }
        // Injects a fixed waste heat flux into SystemHeat for cryo batteries in waste heat mode.
        // Flux = _rbVolume × RealBatteryTuning.CryoWasteHeatPerL × phase (0..1), ramping with
        // warmup/shutdown.
        private bool _lastCryoWHMActive = false;
        private void TickCryoWasteHeat()
        {
            bool nowActive = UsesCryoWasteHeat() && _rbVolume > 0.0;

            // On mode toggle-off: flush zero to SH so the source doesn't persist.
            if (_lastCryoWHMActive && !nowActive)
            {
                if (systemHeat == null) systemHeat = SystemHeatBridge.GetModule(part);
                if (systemHeat != null)
                    SystemHeatBridge.AddFlux(systemHeat, "RealBattery_Cryo", TempOptimal, 0f, true);
            }
            _lastCryoWHMActive = nowActive;
            if (!nowActive) return;

            if (!UsesCryoWasteHeat() || _rbVolume <= 0.0) return;

            if (systemHeat == null) systemHeat = SystemHeatBridge.GetModule(part);
            if (systemHeat == null) return;

            float phase;
            if (controlledShutdownActive)
                phase = (float)(shutdownTleft / Math.Max(1e-3, _keepWarmDuration));
            else if (keepWarmActive)
                phase = 1f - (float)(keepWarmTleft / Math.Max(1e-3, _keepWarmDuration));
            else if (BatteryDisabled)
                phase = 0f;
            else
                phase = 1f;

            float wasteW = Mathf.Clamp01(phase) * (float)_rbVolume * RealBatteryTuning.CryoWasteHeatPerL;
            SystemHeatBridge.AddFlux(systemHeat, "RealBattery_Cryo", TempOptimal, wasteW, true);
            RBLog.Verbose($"[TickCryoWasteHeat] wasteW={wasteW:F3} W, phase={phase:F2}, vol={_rbVolume:F1} L");
        }

        private void HandleRunawayNotifications(bool isRunaway, float tempK)
        {
            if (isRunaway && forcedRunawayActive)
                return;

            if (isRunaway && !RunawayNotified && HighLogic.LoadedSceneIsFlight)
            {
                RunawayNotified = true;
                ScreenMessages.PostScreenMessage(
                    $"{part.partInfo.title}: {Localizer.Format("#LOC_RB_Runaway_detected")}",
                    12f, ScreenMessageStyle.UPPER_CENTER
                );
                RBLog.Warn($"[Runaway] {part.partInfo.title} exceeded TempRunaway ({tempK:F1} K > {TempRunaway:F0} K)");
            }
            else if (!isRunaway && RunawayNotified)
            {
                // Cooldown completed -> allow future notifications
                RunawayNotified = false;
            }
        }
        private void HandleOverheatUX(float tempK)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;

            if (!RealBatterySettings.EnableHeatSimulation) return;

            if (tempK >= TempRunaway || RunawayNotified) return;

            if (tempK > TempOverheat)
            {
                // PCM is bypassed for SMES
                if (!BatteryDisabled && PreventOverheat && !RealBatterySettings.DisablePCM && !IsSMES && !FixedOutput)
                {
                    BatteryDisabled = true;
                    RBLog.Warn($"[ApplyThermalEffects] Battery '{part.partInfo.title}' auto-disabled (overheat {tempK:F1} K > {TempOverheat:F0} K)");

                    if (!OverheatNotified)
                    {
                        OverheatNotified = true;

                        // Kerbal-flavored variants (localized)
                        string[] bodies = new string[]
                        {
                            Localizer.Format("#LOC_RB_OverheatMsg1", part.partInfo.title),
                            Localizer.Format("#LOC_RB_OverheatMsg2", part.partInfo.title),
                            Localizer.Format("#LOC_RB_OverheatMsg3", part.partInfo.title)
                        };
                        string body = bodies[UnityEngine.Random.Range(0, bodies.Length)];

                        var msg = new MessageSystem.Message(
                            Localizer.Format("#LOC_RB_Overheat_title"),
                            body,
                            MessageSystemButton.MessageButtonColor.ORANGE,
                            MessageSystemButton.ButtonIcons.MESSAGE
                        );
                        MessageSystem.Instance?.AddMessage(msg);
                    }
                }
                else
                {
                    // Visual toast (one-shot throttle kept by OverheatNotified flag)
                    if (!OverheatNotified)
                    {
                        OverheatNotified = true;
                        ScreenMessages.PostScreenMessage(
                            $"{part.partInfo.title}: {Localizer.Format("#LOC_RB_OverheatToast")}",
                            12f, ScreenMessageStyle.UPPER_CENTER
                        );
                        RBLog.Warn($"[ApplyThermalEffects] Overheat detected on {part.partInfo.title} ({tempK:F1} K > {TempOverheat:F0} K)");
                    }
                }
            }
            else if (tempK < TempOverheat - 10f)
            {
                // Reset flags after cooling to allow future alerts
                OverheatNotified = false;
            }
        }
    }
}