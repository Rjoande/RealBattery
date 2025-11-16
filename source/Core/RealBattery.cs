using Contracts.Agents.Mentalities;
using KSP.Localization;
using KSP.UI.Screens;
using RealBattery;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SystemHeat;
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

        // --- End-of-life threshold ---
        const double EOL_THRESHOLD = 0.80;          // 80% of BatteryLife

        // --- Numeric epsilons ---
        private const double EPS = 1e-6;            // generic numeric epsilon (unify scattered locals)

        // --- KeepWarm thresholds (temp-based gating) ---
        private const float TEMP_NO_UPKEEP_LO = 500f; // <500 K -> full upkeep
        private const float TEMP_NO_UPKEEP_HI = 600f; // >600 K -> no upkeep
        private const double WARMUP_MULT = 6.0;       // warmup uses 6x upkeep cost

        // --- Thermal runaway tail model ---
        private const double RUNAWAY_TAU_SECONDS = 10.0; // exponential decay time constant

        // --- Resource IDs (cached) ---
        private static readonly int SC_ID = PartResourceLibrary.Instance.GetDefinition("StoredCharge").id;


        // ============================================================================
        //  KSP FIELDS – CONFIGURATION & UI
        // ============================================================================

        // --- Configuration knobs (cfg-driven; non-persistent) -----------------------
        // Only charge/discharge gates and curves
        [KSPField(isPersistant = false)] public bool moduleActive = true;        // hide PAW for non-battery subtypes
        [KSPField(isPersistant = false)] public float HighEClevel = 0.95f;       // charge gate
        [KSPField(isPersistant = false)] public float LowEClevel = 0.90f;        // discharge gate
        [KSPField(isPersistant = false)] public float Crate = 1.0f;              // C-rate
        [KSPField(isPersistant = false)] public double SelfDischargeRate = 0.01; // % per day
        [KSPField(isPersistant = false)] public double CycleDurability = 1;      // cycles until wear
        [KSPField(isPersistant = false)] public bool FixedOutput = false;        // thermal battery mode
        [KSPField(isPersistant = false)] public FloatCurve ChargeEfficiencyCurve = new FloatCurve();
        // Thermal model
        [KSPField(isPersistant = false)] public double ThermalLoss = 0.01;      // kW per EC/s
        [KSPField(isPersistant = false)] public float TempOverheat = 350f;      // K
        [KSPField(isPersistant = false)] public float TempRunaway = 400f;       // K
        [KSPField(isPersistant = false)] public double RunawayHeatFactor = 0.0; // <=0: use Crate
        [KSPField(isPersistant = false)] public bool KeepWarm = false;          // heat-aware warmup/upkeep
        [KSPField(isPersistant = false)] public bool SelfRunaway = false;       // spontaneous runaway
        
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
        [KSPField(isPersistant = true)] public double selfRunawayTimer = 0.0;      // accumulated "hazard time" for RIP self-runaway, in seconds.
        [KSPField(isPersistant = true)] public bool   forcedRunawayActive = false; // once triggered, the cell remains in forced-runaway mode
        [KSPField(isPersistant = true)] public bool   FixedOutputDefaultApplied = false;

        // --- Telemetry / Editor preview --------------------------------------------
        [KSPField(isPersistant = false)] public double lastECpower = 0; // +charge / -discharge
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_DischargeRate", guiUnits = "#LOC_RB_guiUnitsECs", guiFormat = "F2", groupName = "RealBatteryInfo")]
        public double DischargeRate = 0.0;
        private double GUI_power = 0;
        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "#LOC_RB_ChargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo")]
        public string ChargeInfoEditor;
        private float smoothFlux = 0f;

        // --- PAW (flight + editor) --------------------------------------------------
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_BatteryToggle", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        [UI_Toggle(disabledText = "#LOC_RB_disableText", enabledText = "#LOC_RB_enableText")]
        public bool BatteryDisabled = false;

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_Tech", groupName = "RealBatteryInfo")]
        public string BatteryTypeDisplayName;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_StateOfCharge", groupName = "RealBatteryInfo")]
        public string BatterySOCStatus;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_Status", groupName = "RealBatteryInfo")]
        public string BatteryChargeStatus;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_TimeTo", groupName = "RealBatteryInfo")]
        public string BatteryTimeTo;
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_BatteryHealth", groupName = "RealBatteryInfo")]
        public string BatteryHealthStatus;

        // --- Staging integration ----------------------------------------------------
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_StageArm", groupName = "RealBatteryInfo")]
        [UI_Toggle(disabledText = "#LOC_RB_StageArm_off", enabledText = "#LOC_RB_StageArm_on")]
        public bool BatteryStaged = false;
        [KSPField(isPersistant = true)] private bool StageFired = false;
        public override bool IsStageable() => BatteryStaged && !StageFired;
        public override bool StagingToggleEnabledEditor() => true;


        // --- ACTIONS ----------------------------------------------------------------
        [KSPAction("#LOC_RB_ActionToggleBattery")] public void ToggleBatteryAction(KSPActionParam param) => BatteryDisabled = !BatteryDisabled;
        [KSPAction("#LOC_RB_ActionEnableBattery")] public void EnableBattery(KSPActionParam param) => BatteryDisabled = false;
        [KSPAction("#LOC_RB_ActionDisableBattery")] public void DisableBattery(KSPActionParam param) => BatteryDisabled = true;


        // --- Editor Simulation Mode -------------------------------------------------
        public enum SimMode { Idle, Discharge, Charge }
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_SimMode", groupName = "RealBatteryInfo")]
        [UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "#LOC_RB_SimMode_Idle", "#LOC_RB_SimMode_Discharge", "#LOC_RB_SimMode_Charge" })]
        public string SimulationMode;


        // ============================================================================
        //  RUNTIME STATE & DEPENDENCIES
        //  Keep only non-persistent state and cached external module refs here.
        // ============================================================================

        // --- External dependencies (cached) ----------------------------------------
        // Optional SystemHeat module; assigned on-demand when heat sim is in use.
        private ModuleSystemHeat systemHeat = null;  // resolved lazily (UseSystemHeat)

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
        private bool OverheatNotified = false;
        // Runaway: one-time toast + residual heat tail
        private bool   RunawayNotified = false;       // first-time runaway trigger in flight
        private double runawayTailKW = 0.0;           // snapshot of last heat power
        private bool   runawayExtinguished = false;   // chemical source depleted; apply short tail



        // ============================================================================
        //  LIFECYCLE
        // ============================================================================
        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // 0) Boot banner / early refs
            BatteryChargeStatus = Localizer.Format("#LOC_RB_Initializing");
            if (RealBatterySettings.UseSystemHeat && systemHeat == null)
                systemHeat = part?.Modules?.GetModule<ModuleSystemHeat>();

            // 1) Load config & basic UI gating
            LoadConfig();
            ModuleActiveHideUI();

            // 2) Post-load custom hook (existing)
            RB_AfterOnStart();

            // 3) Defaults & invariants
            // Default: thermal batteries spawn disabled in Editor/PreLaunch (once).
            if (FixedOutput && !FixedOutputDefaultApplied && !ActivationLatched)
            {
                bool editorScene = HighLogic.LoadedSceneIsEditor;
                bool newVesselSpawn = (HighLogic.LoadedSceneIsFlight && state == StartState.PreLaunch);
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
            // Show/hide staging icon and enablement depending on BatteryStaged.
            InitStagingIconAndEnablement();
            // Re-enforce latch when user flips BatteryDisabled in PAW (Editor & Flight).
            var disabledField = Fields[nameof(BatteryDisabled)];
            if (disabledField?.uiControlFlight != null)
                disabledField.uiControlFlight.onFieldChanged += (f, o) => { EnforceNonDisableableLatch(); };
            if (disabledField?.uiControlEditor != null)
                disabledField.uiControlEditor.onFieldChanged += (f, o) => { EnforceNonDisableableLatch(); };
            // Keep stageability in sync when BatteryStaged changes (Editor & Flight).
            WireStagedToggleHandlers();

            // 5) Editor simulation mode (options + default fix)
            if (Fields["SimulationMode"].uiControlEditor is UI_ChooseOption simModeUI)
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
            if (HighLogic.LoadedSceneIsFlight && state == StartState.PreLaunch)
            {
                WearCounter = 0.0;
                BatteryLife = 1.0;
                smoothFlux = 0f;
                Debug.Log($"Reset WearCounter and BatteryLife on launch (PreLaunch state)");
            }
            TechUnlockUI();

            // 7) Edge detectors & final UI state
            lastDisabled = BatteryDisabled; // initialize edge detector
        }
        public override void OnUpdate()
        {
            ModuleActiveHideUI();

            if (HighLogic.LoadedSceneIsEditor)
            {
                InitStagingIconAndEnablement();
                return;
            }
            
            if (!HighLogic.LoadedSceneIsFlight || !moduleActive) return;

            RBLog.Verbose("INF OnUpdate");

            // for slowing down the charge/discharge status
            double  statusLowPassTauRatio = 0.01;
            double  ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            double  stored = part.Resources["StoredCharge"].amount;
            double  capacity = part.Resources["StoredCharge"]?.maxAmount ?? 0.0;
            double  max = part.Resources["StoredCharge"].maxAmount * ActualLife;
            double  deltaSC = GUI_power > 0 ? max - stored : stored;
            double  timeInSeconds = (deltaSC * EC2SCratio) / Math.Abs(GUI_power);
            float   tempK = GetCurrentTemperatureK();
            bool    isPrimary = (CycleDurability <= 1.0) || (HighEClevel > 1);

            GUI_power = GUI_power + statusLowPassTauRatio * (lastECpower - GUI_power);

            // GUI
            if (isRunaway)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_Status_Runaway");
            else if (isPrimary && SC_SOC < 0.01)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_PrimaryDepleted");
            else if (ActualLife < 0.01)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_DeadBattery");
            else if (controlledShutdownActive)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_ShuttingDown", pct);
            else if (!BatteryDisabled && keepWarmActive)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_WarmingUp", pct);
            else if (upkeepShort)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_KeepWarm_ShutdownPending", Math.Ceiling(keepWarmGrace));
            else if (GUI_power < -0.001)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Discharging", GUI_power.ToString("F1"));
            else if (GUI_power > 0.001)
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Charging", GUI_power.ToString("F1"));
            else
            {
                part.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double EC_amount, out double EC_maxAmount);
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_idle", (EC_amount / EC_maxAmount * 100).ToString("F1"));
            }

            BatterySOCStatus = $"{(SC_SOC * 100):F0}%";

            if (!isPrimary && RealBatterySettings.EnableBatteryWear && capacity > EPS)
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

                Fields["BatteryTimeTo"].guiName = GUIdischarging ? "#LOC_RB_TimeTo" : "#LOC_RB_TimeToCharge";
                BatteryTimeTo = FormatTimeSpan(TimeSpan.FromSeconds(timeInSeconds));
            }
            else
            {
                Fields["BatteryTimeTo"].guiName = "#LOC_RB_TimeTo";
                BatteryTimeTo = "-";
            }

            RBLog.Verbose($"[RealBattery] GUI_power update: lastECpower={lastECpower:F3}, GUI_power={GUI_power:F3}, Δt={TimeWarp.fixedDeltaTime:F3}");

            // Runaway trigger while OFF or idle: if temperature exceeds TempRunaway, force a thermal pass up to battery death.
            if (isRunaway) 
                ApplyThermalEffects(0.0);            
        }
        public void FixedUpdate()
        {
            if (!moduleActive) return;
            
            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;
            double dt = TimeWarp.fixedDeltaTime;
            if (dt <= 0) return;

            PartResource sc = part.Resources.Get("StoredCharge");

            if(HighLogic.LoadedSceneIsFlight)
            {
                EnforceNonDisableableLatch();

                // Enforce UI toggle lock: while a phase is in progress, force the
                // BatteryDisabled field to the locked value, defeating user clicks.
                if (KeepWarm && uiToggleLockActive)
                {
                    if (BatteryDisabled != uiLockDisabledState)
                        BatteryDisabled = uiLockDisabledState;
                }

                // -----------------------------------------------------------------
                // Detect transitions for KeepWarm state machine
                // -----------------------------------------------------------------
                bool DisabledNow = BatteryDisabled;
                if (uiToggleLockActive) DisabledNow = uiLockDisabledState;

                if (KeepWarm)
                {
                    if (lastDisabled && !DisabledNow)
                    {
                        // OFF -> ON : start warmup latency (even if hot; EC cost may be 0 if >=600 K)
                        keepWarmActive = true;
                        keepWarmTleft = RealBatterySettings.WarmupSeconds;
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
                        shutdownTleft = RealBatterySettings.WarmupSeconds;
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
                        double total = Math.Max(1e-3, RealBatterySettings.WarmupSeconds);
                        pct = (int)Mathf.Round(100f * (float)((total - shutdownTleft) / total));
                        //BatteryChargeStatus = Localizer.Format("#LOC_RB_ShuttingDown", pct);
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
                        double total = Math.Max(1e-3, RealBatterySettings.WarmupSeconds);
                        pct = (int)Mathf.Round(100f * (float)((total - keepWarmTleft) / total));
                        //BatteryChargeStatus = Localizer.Format("#LOC_RB_WarmingUp", pct);
                        
                        // if heat-sim enabled and temp <600 K, EC may be required; detect shortfall
                        bool upkeepNeeded = KeepWarmTempMultiplier() > 0f;
                        double wantThisFrame = KeepWarmECperSec(true, (part.Resources.Get("StoredCharge")?.maxAmount ?? 0.0) * Crate * ActualLife) * dt;
                        upkeepShort = upkeepNeeded && (wantThisFrame > EPS) && (got < wantThisFrame * 0.999);
                        if (upkeepShort)
                        {
                            // Enter shutdown pending (grace = remaining warmup time)
                            keepWarmGrace = Math.Max(1.0, keepWarmTleft);
                            keepWarmActive = false;
                            //BatteryChargeStatus = Localizer.Format("#LOC_RB_KeepWarm_ShutdownPending", Math.Ceiling(keepWarmGrace));
                            return; // skip normal path while pending
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
                        double dischargeKW = (sc?.maxAmount ?? 0.0) * Crate * ActualLife;
                        double wantEC = KeepWarmECperSec(isWarmupPhase: false, dischargeKW) * dt;
                        double gotEC = (wantEC > 0.0) ? part.RequestResource(PartResourceLibrary.ElectricityHashcode, wantEC) : 0.0;
                        
                        bool upkeepNeeded = wantEC > EPS;                   // temp < 600 K
                        upkeepShort = upkeepNeeded && (gotEC < wantEC * 0.999);
                        
                        if (RealBatterySettings.EnableHeatSimulation && upkeepShort)
                        {
                            // start/continue grace
                            if (keepWarmGrace <= 0.0) keepWarmGrace = RealBatterySettings.WarmupSeconds;
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
                            // upkeep satisfied or not needed → cancel pending shutdown
                            keepWarmGrace = 0.0;
                        }
                        
                        // If we were pending shutdown during warmup and conditions recovered, resume warmup
                        if (keepWarmGrace <= EPS && KeepWarm && !BatteryDisabled && keepWarmTleft > EPS)
                        {
                            keepWarmActive = true; // resume latency
                            pct = (int)Mathf.Round(100f * (float)((RealBatterySettings.WarmupSeconds - keepWarmTleft) / Math.Max(1e-3, RealBatterySettings.WarmupSeconds)));
                            //BatteryChargeStatus = Localizer.Format("#LOC_RB_WarmingUp",
                            //(int)Mathf.Round(100f * (float)((RealBatterySettings.WarmupSeconds - keepWarmTleft) / Math.Max(1e-3, RealBatterySettings.WarmupSeconds))));
                        }
                    }
                }

                // -----------------------
                // Runaway check & trigger
                // -----------------------

                float tempK = GetCurrentTemperatureK();

                // Runaway can only be *triggered* if heat simulation and runaway features are enabled
                // and this is not a KeepWarm / FixedOutput design. Once active, `isRunaway` stays
                // latched until ApplyThermalEffects() explicitly extinguishes it.
                bool RunawayAllowed =
                    RealBatterySettings.EnableHeatSimulation &&
                    RealBatterySettings.EnableThermalRunaway &&
                    !KeepWarm &&
                    !FixedOutput;

                bool runawayCondition = (tempK > TempRunaway) || forcedRunawayActive;

                // Only handle OFF -> ON transitions here. Turning runaway OFF is handled inside
                // ApplyThermalEffects(), so that we can also run the decay tail and properly
                // clear forcedRunawayActive.
                if (!isRunaway && RunawayAllowed && runawayCondition && ActualLife >= 0.01)
                {
                    isRunaway = true;

                    RBLog.Verbose(
                        $"[Runaway][DEBUG] Setting isRunaway=true on '{part.partInfo?.title}' " +
                        $"canTriggerRunaway={RunawayAllowed}, tempK={tempK:F1}, TempRunaway={TempRunaway:F0}, " +
                        $"forcedRunawayActive={forcedRunawayActive}, SelfRunaway={SelfRunaway}, " +
                        $"UseSystemHeat={RealBatterySettings.UseSystemHeat}, " +
                        $"loopT={(systemHeat != null ? systemHeat.currentLoopTemperature.ToString("F1") : "n/a")}"
                    );
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

                if (SelfRunaway && RealBatterySettings.SelfRunawayChancePerHour > 0f
                    && !forcedRunawayActive && !isRunaway 
                    && dt > 0 && SC_SOC >= 0.01)
                {
                    selfRunawayTimer += dt;

                    // Check once per in-game hour (3600s)
                    const double interval = 3600.0;
                    while (selfRunawayTimer >= interval)
                    {
                        selfRunawayTimer -= interval;

                        float p = RealBatterySettings.SelfRunawayChancePerHour;
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

                // stop thermal flux if battery is spent after runaway
                if (RealBatterySettings.UseSystemHeat && systemHeat != null)
                {
                    if (ActualLife < 0.01 && !isRunaway && !forcedRunawayActive && Math.Abs(smoothFlux) > 1e-6f)
                    {
                        smoothFlux = 0f;
                        systemHeat.AddFlux("RealBattery", 0f, 0f, true);
                        RBLog.Info($"[RealBattery] Cleared residual SystemHeat flux on '{part.partInfo?.title}' after battery death.");
                    }
                }

                if (BatteryDisabled && RealBatterySettings.EnableSelfDischarge)
                {
                    RBLog.Verbose($"[FixedUpdate] Called ApplySelfDischarge");
                    ApplySelfDischarge();
                    return;
                }
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                double sign = 0;

                if (systemHeat != null)
                    systemHeat.moduleUsed = true;

                if (SimulationMode == Localizer.Format("#LOC_RB_SimMode_Discharge"))
                    sign = -1.0;
                else if (SimulationMode == Localizer.Format("#LOC_RB_SimMode_Charge"))
                    sign = +1.0;

                // Simulated EC power (kW), positive = charging, negative = discharging
                PartResource StoredCharge = part.Resources.Get("StoredCharge");
                DischargeRate = StoredCharge.maxAmount * Crate;
                lastECpower = sign * DischargeRate;

                Fields["lastECpower"].SetValue(lastECpower, this);

                // Push live DischargeRate to PAW (Editor)
                Fields[nameof(DischargeRate)].SetValue(DischargeRate, this);

                double chargeAtSOC0 = DischargeRate * ChargeEfficiencyCurve.Evaluate(0f);
                string chargeStr = chargeAtSOC0.ToString("F2");
                if (ChargeInfoEditor != chargeStr)
                {
                    ChargeInfoEditor = chargeStr;
                    Fields[nameof(ChargeInfoEditor)].SetValue(ChargeInfoEditor, this);
                }

                InitStagingIconAndEnablement();

                ApplyThermalEffects(lastECpower);
            }

            ModuleActiveHideUI();
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

            RBLog.Info($"[RealBattery] Staged activation: Battery enabled on '{part.partInfo?.title}'");

            // Latch if this is a FixedOutput battery.
            EnforceNonDisableableLatch();
        }
        public override string GetInfo()
        {
            RBLog.Verbose("INF GetInfo");

            LoadConfig();

            PartResource StoredCharge = part.Resources.Get("StoredCharge");
            DischargeRate = StoredCharge.maxAmount * Crate;

            return Localizer.Format("#LOC_RB_VAB_Info", BatteryTypeDisplayName, DischargeRate.ToString("F2"), (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f)).ToString("F2"));
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

                if (config.HasValue("moduleActive"))
                    bool.TryParse(config.GetValue("moduleActive"), out moduleActive); // default stays 'true' if missing

                if (config.HasValue("FixedOutput"))
                    bool.TryParse(config.GetValue("FixedOutput"), out FixedOutput);

                if (config.HasValue("KeepWarm"))
                    bool.TryParse(config.GetValue("KeepWarm"), out KeepWarm);

                if (config.HasValue("RunawayHeatFactor"))
                    RunawayHeatFactor = double.Parse(config.GetValue("RunawayHeatFactor"));
            }

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

            Fields["ChargeInfoEditor"].guiActiveEditor = (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f) > 0);

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


        // ============================================================================
        //  CORE OPERATIONS
        // ============================================================================
        public double XferECtoRealBattery(double amount)
        {
            if (KeepWarm && (keepWarmActive || controlledShutdownActive))
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

            if (amount > 0 && SC_SOC < ActualLife && !BatteryDisabled && !FixedOutput) // Charge battery
            {
                double SOC_ChargeEfficiency = ChargeEfficiencyCurve.Evaluate((float)SC_SOC);
                EC_delta = TimeWarp.fixedDeltaTime * DischargeRate * SOC_ChargeEfficiency;  // maximum amount of EC the battery can convert to SC, limited by current charge capacity

                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, Math.Min(EC_delta, amount));

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;

                SC_delta = -EC_delta / EC2SCratio;                  // SC_delta = -1EC / 10EC/SC * 0.9 = -0.09SC
                SC_delta = part.RequestResource(SC_ID, SC_delta);   //issue: we might "overfill" the battery and should give back some EC

                RBLog.Verbose("INF charged");
            }
            else if ((amount < 0 || FixedOutput) && SC_SOC > 0 && !BatteryDisabled)  // Discharge battery (or fixed output)
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

            // Count wear: for FixedOutput use SC actually consumed; otherwise EC accepted.
            double wearSC = FixedOutput
                ? SC_delta
                : ((Math.Abs(EC_delta) / EC2SCratio) / EngBonus);
            if (wearSC > EPS)
            {
                WearCounter += wearSC; // kWh equivalent in SC units
                UpdateBatteryLife();
            }

            //update SOC field for usage in other modules (load balancing)
            SC_SOC = part.Resources["StoredCharge"].amount / part.Resources["StoredCharge"].maxAmount;

            lastECpower = EC_power;

            ApplyThermalEffects(EC_power);

            return EC_delta;
        }
        private void ApplyThermalEffects(double EC_power)
        {
            if (!RealBatterySettings.EnableHeatSimulation) return;

            // Resolve heat backend and current temperature once
            // Always refresh SystemHeat module if feature is enabled.
            bool useSH = RealBatterySettings.UseSystemHeat && systemHeat != null;

            // Ensure fresh SystemHeat reference if needed
            if (RealBatterySettings.UseSystemHeat && systemHeat == null)
                systemHeat = part?.Modules?.GetModule<ModuleSystemHeat>();

            float tempK = GetCurrentTemperatureK();
            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            // Runaway gate (honor ThermalRunaway setting)
            if (!RealBatterySettings.EnableThermalRunaway)
            {
                // If runaway is globally disabled, respect OFF state and bypass runaway logic
                if (BatteryDisabled) return;
                isRunaway = false; // do not trigger any runaway-specific path
            }

            // First-run runaway toast + flag reset when cooled down
            HandleRunawayNotifications(isRunaway, tempK);

            // Respect OFF only if not in runaway
            if (BatteryDisabled && !isRunaway)
                return;

            // Derive a chemical-equivalent power to turn into heat when in runaway even if EC_power = 0.
            // If EC_power is 0 and runaway is active, we use a chemistry-specific proxy based on DischargeRate.
            PartResource sc = part.Resources.Get("StoredCharge");
            double safeLife = ActualLife > 0.001 ? ActualLife : 0.001;
            float EngBonus = (float)EngineerBonus();

            // Recompute DischargeRate safely (kW) for this context
            double dischargeKW = ActualLife * sc.maxAmount * RealBatterySettings.RunawayBaseMagnitude;// * 0.1;
            if (RunawayHeatFactor > 0.0)
                dischargeKW *= RunawayHeatFactor;
            else
                dischargeKW *= Crate;
            //if (forcedRunawayActive) dischargeKW *= 100;

            double heatPowerKW = isRunaway ? dischargeKW : Math.Abs(EC_power);

            float flux = 0f;

            if (isRunaway && RealBatterySettings.EnableThermalRunaway && heatPowerKW > EPS)
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

            // If we have power movement or a runaway condition, compute heat.
            // Charging inefficiency -> extra heat, discharging -> direct loss -> heat.
            // During runaway we model it as "discharge-like" heat based on chemistry.
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

                if (useSH && systemHeat != null)
                {
                    // Keep the existing smoothing/scaling behavior
                    float tempTarget = (float)TempOverheat - 10;
                    if (tempTarget > 1000) tempTarget = 1000; // clamp to prevent runaway target
                    flux *= 0.01f;
                    float tau = 0.01f;
                    smoothFlux += tau * (flux - smoothFlux);
                    systemHeat.moduleUsed = true;
                    systemHeat.AddFlux("RealBattery", tempTarget, smoothFlux, true);
                    RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux ACTIVE {(isRunaway ? "(Runaway) " : "")}(SystemHeat): {smoothFlux:F2} W @ {TempOverheat:F0} K (loop={tempK:F1} K)");
                }
                else
                {
                    part.AddThermalFlux(flux);
                    RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux ACTIVE {(isRunaway ? "(Runaway) " : "")}(stock): {flux:F2} W @ {TempOverheat:F0} K (part={tempK:F1} K)");
                }
            }
            else if (useSH && systemHeat != null)
            {
                // Smooth decay to 0 over a long timescale (idle cooling)
                // tauSeconds is the e-folding time (seconds) for the residual flux.
                float tauSeconds = 120f; // 2 minutes to drop by 63%
                float dt = TimeWarp.fixedDeltaTime;

                // Clamp to avoid overshoot if dt is very large (high timewarp, etc.)
                float k = dt / tauSeconds;
                if (k > 1f) k = 1f;

                smoothFlux += (0f - smoothFlux) * k;

                systemHeat.moduleUsed = true;
                systemHeat.AddFlux("RealBattery", 0f, smoothFlux, true);
                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux IDLE: {smoothFlux:F2} W -> no EC transfer, loop={tempK:F1} K");
            }

            // --- Thermal wear (flight only) ---

            if (!RealBatterySettings.EnableBatteryWear) return;
            if (!HighLogic.LoadedSceneIsFlight) return;

            //var sc = part.Resources.Get("StoredCharge");
            if (sc != null && tempK > TempOverheat)
            {
                float severity = Mathf.Clamp01((tempK - TempOverheat) / Mathf.Max(TempRunaway - TempOverheat, 1e-3f));
                float thermalFactor = Mathf.Exp(severity * 4.0f) - 1.0f;                
                float deltaWear = thermalFactor * TimeWarp.fixedDeltaTime * (float)(sc.maxAmount);

                if (!FixedOutput && !isRunaway) deltaWear /= EngBonus;

                WearCounter += deltaWear;
                RBLog.Verbose($"[ApplyThermalEffects] Thermal wear: Δ={deltaWear:F5}, T={tempK:F1} K");
            }

            // --- Automatic shutdown on overheat (if PCM unlocked) or toast alert ---
            HandleOverheatUX(tempK);
        }
        public void UpdateBatteryLife()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            RBLog.Verbose($"[UpdateBatteryLife] Scene is FLIGHT");

            if (!RealBatterySettings.EnableBatteryWear) return;
            RBLog.Verbose($"[UpdateBatteryLife] UseBatteryWear is ON");

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

            if (!EOLToastSent && BatteryLife < EOL_THRESHOLD)
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
            RBLog.Verbose($"[ApplySelfDischarge] UseSelfDischarge is ON");

            if (!BatteryDisabled) return;
            RBLog.Verbose($"[ApplySelfDischarge] Battery is OFF");

            if (SC_SOC <= 0) return;

            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;

            double socLossPerDay = SelfDischargeRate / (ActualLife > 0 ? ActualLife : 1.0);
            double hoursPerDay = RealBatterySettings.GetHoursPerDay();
            double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);

            double SC_loss = socLossPerSecond * part.Resources["StoredCharge"].maxAmount;
            double actualLoss = part.RequestResource("StoredCharge", SC_loss);

            if (part.Resources["StoredCharge"].maxAmount > 0.0)
                SC_SOC = part.Resources["StoredCharge"].amount / part.Resources["StoredCharge"].maxAmount;

            RBLog.Verbose($"[ApplySelfDischarge] Self-discharge of {actualLoss:F9} kWh applied to {part.partInfo.title}");
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
                Debug.Log($"[Tech] Game mode is set to SANDBOX. All fields unlocked.");
            }
            else
            {
                var SOCGauge = PartLoader.getPartInfoByName("RB.SOCGauge");
                var PCM = PartLoader.getPartInfoByName("RB.PCM");
                var BMS = PartLoader.getPartInfoByName("RB.BMS");
                var advBMS = PartLoader.getPartInfoByName("RB.advBMS");

                if (SOCGauge != null && ResearchAndDevelopment.Instance != null)
                {
                    SOCGaugeUnlocked = ResearchAndDevelopment.PartModelPurchased(SOCGauge);
                    Debug.Log($"[Tech] Found AvailablePart '{SOCGauge.name}', Unlocked = {SOCGaugeUnlocked}");
                }

                if (PCM != null && ResearchAndDevelopment.Instance != null)
                {
                    PCMUnlocked = ResearchAndDevelopment.PartModelPurchased(PCM);
                    Debug.Log($"[Tech] Found AvailablePart '{PCM.name}', Unlocked = {PCMUnlocked}");
                }

                if (BMS != null && ResearchAndDevelopment.Instance != null)
                {
                    BMSUnlocked = ResearchAndDevelopment.PartModelPurchased(BMS);
                    Debug.Log($"[Tech] Found AvailablePart '{BMS.name}', Unlocked = {BMSUnlocked}");
                }

                if (advBMS != null && ResearchAndDevelopment.Instance != null)
                {
                    advBMSUnlocked = ResearchAndDevelopment.PartModelPurchased(advBMS);
                    Debug.Log($"[Tech] Found AvailablePart '{advBMS.name}', Unlocked = {advBMSUnlocked}");
                }

                if (ResearchAndDevelopment.Instance == null)
                    Debug.Log($"[Tech] ResearchAndDevelopment.Instance returned NULL");
            }

            // --- Fetch PAW fields (guarded) ---
            BaseField fSOCStatus = Fields["BatterySOCStatus"];
            BaseField fTimeTo = Fields["BatteryTimeTo"];
            BaseField fLife = Fields["BatteryHealthStatus"];

            // --- Apply visibility (Editor + Flight). Keep it symmetric unless you want editor-only behavior. ---
            if (fSOCStatus != null)
            {
                // SOC gauge only with RB.SOCGauge unlocked
                fSOCStatus.guiActive = SOCGaugeUnlocked;
                Debug.Log($"[Tech] Field '{fSOCStatus.name}' is {(fSOCStatus.guiActive ? "active" : "NOT active")}.");
            }

            if (fTimeTo != null)
            {
                // Time-to requires RB.BMS
                fTimeTo.guiActive = BMSUnlocked;
                Debug.Log($"[Tech] Field '{fTimeTo.name}' is {(fTimeTo.guiActive ? "active" : "NOT active")}.");
            }

            if (fLife != null)
            {
                // SOH/Life requires RB.advBMS
                fLife.guiActive = advBMSUnlocked;
                Debug.Log($"[Tech] Field '{fLife.name}' is {(fLife.guiActive ? "active" : "NOT active")}.");
            }

            // Automatic overheat protection: unlocked together with PCM
            PreventOverheat = PCMUnlocked;
            Debug.Log($"[Tech] PreventOverheat set to {PreventOverheat} (PCMUnlocked={PCMUnlocked})");
        }
        private void ModuleActiveHideUI()
        {
            Fields["BatteryDisabled"].guiActive = moduleActive;
            Fields["BatteryDisabled"].guiActiveEditor = moduleActive;

            Fields["BatteryTypeDisplayName"].guiActive = moduleActive;
            Fields["BatteryTypeDisplayName"].guiActiveEditor = moduleActive;

            Fields["BatterySOCStatus"].guiActive = moduleActive;
            Fields["BatteryChargeStatus"].guiActive = moduleActive;
            Fields["BatteryTimeTo"].guiActive = moduleActive;
            Fields["BatteryHealthStatus"].guiActive = moduleActive;

            Fields["DischargeRate"].guiActiveEditor = moduleActive;
            Fields["ChargeInfoEditor"].guiActiveEditor = moduleActive;
            Fields["BatteryStaged"].guiActiveEditor = moduleActive;
            Fields["SimulationMode"].guiActiveEditor = moduleActive;
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
            var disabledField = Fields[nameof(BatteryDisabled)];
            if (disabledField != null)
            {
                // Editor remains configurable pre-flight; flight is locked post-latch.
                if (HighLogic.LoadedSceneIsFlight && ActivationLatched)
                {
                    if (disabledField.uiControlFlight != null)
                    disabledField.uiControlFlight.controlEnabled = false; // greyed out
                }
            }
        }


        // ============================================================================
        //  HELPERS
        // ============================================================================
        private double EngineerBonus()
        {
            int EngLvl = 0;
            var vessel = this.vessel;
            if (vessel != null)
                EngLvl = Mathf.Clamp(ModuleEnergyEstimator.GetMaxSpecialistLevel(this.vessel, "Engineer"), 0, 5);
            double EngBonus = 0.95 + 0.06 * EngLvl;
            return EngBonus;
        }
        private float GetCurrentTemperatureK()
        {
            // --- SystemHeat branch ---
            if (RealBatterySettings.UseSystemHeat)
            {
                if (systemHeat == null)
                    systemHeat = part?.Modules?.GetModule<ModuleSystemHeat>();

                // guard clause: avoid null access if module missing or feature disabled mid-flight
                if (systemHeat != null)
                    return (float)systemHeat.currentLoopTemperature;
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
        private void InitStagingIconAndEnablement()
        {
            if (BatteryStaged)
            {
                if (string.IsNullOrEmpty(part.stagingIcon))
                    part.stagingIcon = "FUEL_TANK";
                part.stagingOn = true;
            }
            else
            {
                part.stagingOn = false;
            }
        }
        private void WireStagedToggleHandlers()
        {
            var stagedField = Fields[nameof(BatteryStaged)];
            if (stagedField?.uiControlEditor != null)
                stagedField.uiControlEditor.onFieldChanged = (f, o) =>
                {
                    part.stagingOn = BatteryStaged;
                    part.UpdateStageability(true, true);
                    if (BatteryStaged && string.IsNullOrEmpty(part.stagingIcon))
                        part.stagingIcon = "FUEL_TANK";
                };

            if (stagedField?.uiControlFlight != null)
                stagedField.uiControlFlight.onFieldChanged = (f, o) =>
                {
                    part.stagingOn = BatteryStaged;
                    part.UpdateStageability(true, true);
                    if (BatteryStaged && string.IsNullOrEmpty(part.stagingIcon))
                        part.stagingIcon = "FUEL_TANK";
                };
        }
        private float KeepWarmTempMultiplier()
        {
            if (!RealBatterySettings.EnableHeatSimulation) return 1f;

            float tempK = GetCurrentTemperatureK();

            if (tempK >= TEMP_NO_UPKEEP_HI) return 0f;
            if (tempK <= TEMP_NO_UPKEEP_LO) return 1f;
            return 1f - Mathf.Clamp01((tempK - TEMP_NO_UPKEEP_LO) / (TEMP_NO_UPKEEP_HI - TEMP_NO_UPKEEP_LO)); // linear LO->HI K => 1->0
        }
        private double TickKeepWarm(bool isWarmupPhase, double dt)
        {
            double ActualLife = RealBatterySettings.EnableBatteryWear ? BatteryLife : 1.0;
            PartResource sc = part.Resources.Get("StoredCharge");
            double dischargeKW = (sc?.maxAmount ?? 0.0) * Crate * ActualLife; // mirrors Xfer estimation
            double ecPerSec = KeepWarmECperSec(isWarmupPhase, dischargeKW);
                        if (ecPerSec <= EPS) return 0.0; // hot enough or negligible cost
            double wantEC = ecPerSec * dt;
            double gotEC = (wantEC > 0.0) ? part.RequestResource(PartResourceLibrary.ElectricityHashcode, wantEC) : 0.0;
                return Math.Max(0.0, gotEC);
        }
        private double KeepWarmECperSec(bool isWarmupPhase, double dischargeRateKW)
        {
            double baseMul = isWarmupPhase ? WARMUP_MULT : 1.0; // warmup is 6× upkeep
            float tempMul = KeepWarmTempMultiplier();  // heat-aware 0..1
            return dischargeRateKW* RealBatterySettings.KeepWarmFrac * baseMul* tempMul; // EC/s (≈kW)
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

            if (tempK >= TempRunaway || RunawayNotified) return;

            if (tempK > TempOverheat)
            {
                if (!BatteryDisabled && PreventOverheat && !RealBatterySettings.DisablePCM)
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
