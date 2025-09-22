using KSP.Localization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using SystemHeat;

namespace RealBattery
{
    public class RealBattery : PartModule
    {
        [KSPField(isPersistant = false)]
        public bool moduleActive = true;

        // Only charge if total EC is higher than this, eg 0.95
        [KSPField(isPersistant = false)]
        public float HighEClevel = 0.95f;

        // Only discharge if total EC is lower than this, eg 0.9
        [KSPField(isPersistant = false)]
        public float LowEClevel = 0.90f;

        // discharge rate based on StoredCharge amount
        [KSPField(isPersistant = false)]
        public float Crate = 1.0f;

        // chargin efficiency based on SOC, eg. to slow down charging on a full battery
        [KSPField(isPersistant = false)]
        public FloatCurve ChargeEfficiencyCurve = new FloatCurve();

        // for load balancing
        [KSPField(isPersistant = true)]
        public double SC_SOC = 1;
        
        // shows current charge (= positive value, means this part is CONSUMING xx EC/s) and discharges (= negative value, means this part is GENERATING xx EC/s) power
        [KSPField(isPersistant = false)]
        public double lastECpower = 0;

        // maximum of 100% charge/discharge cycles allowed before losing efficiency
        [KSPField(isPersistant = true)]
        public double CycleDurability = 1;

        // % of SOC lost in 1 day
        [KSPField(isPersistant = true)]
        public double SelfDischargeRate = 1;


        // === Thermal simulation ===

        [KSPField(isPersistant = false)]
        public double ThermalLoss = 0.01; // kW per EC/s, chemistry dependent

        [KSPField(isPersistant = false)]
        public float TempOverheat = 350f; // K — threshold beyond which accelerated wear starts

        [KSPField(isPersistant = false)]
        public float TempRunaway = 400f;  // K — threshold beyond which battery wear increase exponentially

        // SystemHeat support
        private ModuleSystemHeat systemHeat;



        //------GUI

        // Battery toggle
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_BatteryToggle", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        [UI_Toggle(disabledText = "#LOC_RB_disableText", enabledText = "#LOC_RB_enableText")]
        public bool BatteryDisabled = false;

        // Battery tech string
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_Tech", groupName = "RealBatteryInfo")]
        public string BatteryTypeDisplayName;

        // Battery charge Status string
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_Status", groupName = "RealBatteryInfo")]
        public string BatteryChargeStatus;

        // battery SOC string
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_StateOfCharge", groupName = "RealBatteryInfo")]
        public string BatterySOCStatus;

        // Battery charge/discharge time
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_TimeTo", groupName = "RealBatteryInfo")]
        public string BatteryTimeTo;

        [KSPField(isPersistant = false)]
        public double BatteryLife = 1.0; // 0–1, computed from WearCounter

        // discharge string for Editor
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "#LOC_RB_DischargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo")]
        public string DischargeInfoEditor;

        // charge string for Editor
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "#LOC_RB_ChargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo")]
        public string ChargeInfoEditor;


        //------ACTIONS

        [KSPAction("#LOC_RB_ActionToggleBattery")]
        public void ToggleBatteryAction(KSPActionParam param)
        {
            BatteryDisabled = !BatteryDisabled;
        }

        [KSPAction("#LOC_RB_ActionEnableBattery")]
        public void EnableBattery(KSPActionParam param)
        {
            BatteryDisabled = false;
        }

        [KSPAction("#LOC_RB_ActionDisableBattery")]
        public void DisableBattery(KSPActionParam param)
        {
            BatteryDisabled = true;
        }

        // === Editor output preview ===
        public enum SimMode
        {
            Idle,
            Discharge,
            Charge
        }

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "#LOC_RB_SimMode", groupName = "RealBatteryInfo")]
        [UI_ChooseOption(scene = UI_Scene.Editor, options = new[] { "#LOC_RB_SimMode_Idle", "#LOC_RB_SimMode_Discharge", "#LOC_RB_SimMode_Charge" })]
        public string SimulationMode; //= "#LOC_RB_SimMode_Idle";


        // === Obsolescence fields ===
        [KSPField(isPersistant = true)]
        public double WearCounter = 0.0; // kWh transferred total

        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_BatteryHealth", groupName = "RealBatteryInfo")]
        public string BatteryHealthStatus;

        // Loadable curve for degradation
        public static FloatCurve BatteryLifeCurve = new FloatCurve();

        static RealBattery()
        {
            BatteryLifeCurve.Add(0.0f, 1.0f);
            BatteryLifeCurve.Add(1.0f, 0.8f);
            BatteryLifeCurve.Add(1.2f, 0.4f);
            BatteryLifeCurve.Add(1.5f, 0.2f);
            BatteryLifeCurve.Add(2.0f, 0.0f);
        }


        // Amount of Ec per storedCharge; 3600 EC = 1SC = 3600kWs = 1kWh
        public const double EC2SCratio = 3600;

        [KSPField(isPersistant = true)]
        public double DischargeRate = 0.0;

        [KSPField(isPersistant = false)]
        public double SOC_ChargeEfficiency;

               
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
            }

            PartResource ElectricCharge = part.Resources.Get("ElectricCharge");
            PartResource StoredCharge = part.Resources.Get("StoredCharge");

            if (ElectricCharge == null)
            {
                RBLog.Verbose("ElectricCharge not found, creating fallback...");
                ElectricCharge = part.Resources.Add("ElectricCharge", 0.1, 0.1, true, true, true, true, PartResource.FlowMode.Both);
            }

            if (StoredCharge == null)
            {
                RBLog.Verbose("StoredCharge not found, creating fallback...");
                StoredCharge = part.Resources.Add("StoredCharge", 0, 0, true, true, true, true, PartResource.FlowMode.Both);
            }

            DischargeRate = StoredCharge.maxAmount * Crate; //kW

            DischargeInfoEditor = String.Format("{0:F2}", DischargeRate);
            ChargeInfoEditor = String.Format("{0:F2}", DischargeRate * ChargeEfficiencyCurve.Evaluate(0f));

            StoredCharge.amount = SC_SOC * StoredCharge.maxAmount;

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

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            BatteryChargeStatus = Localizer.Format("#LOC_RB_Initializing");

            systemHeat = part.Modules.GetModule<ModuleSystemHeat>();

            LoadConfig();

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

            if (HighLogic.LoadedSceneIsFlight && state == StartState.PreLaunch)
            {
                WearCounter = 0.0;
                BatteryLife = 1.0;
                smoothFlux = 0f;
                Debug.Log($"Reset WearCounter and BatteryLife on launch (PreLaunch state)");
            }

        }

        // update context menu
        private double GUI_power = 0;
        public override void OnUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            
            RBLog.Verbose("INF OnUpdate");

            // for slowing down the charge/discharge status
            double statusLowPassTauRatio = 0.01;

            GUI_power = GUI_power + statusLowPassTauRatio * (lastECpower - GUI_power);

            double stored = part.Resources["StoredCharge"].amount;
            double max = part.Resources["StoredCharge"].maxAmount * BatteryLife;
            double deltaSC = GUI_power > 0 ? max - stored : stored;
            double timeInSeconds = (deltaSC * EC2SCratio) / Math.Abs(GUI_power);
            
            // GUI
            if (GUI_power < -0.001)
            {
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Discharging", GUI_power.ToString("F1"));
            }
            else if (GUI_power > 0.001)
            {
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_Charging", GUI_power.ToString("F1"));
            }
            else
            {
                part.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double EC_amount, out double EC_maxAmount);
                BatteryChargeStatus = Localizer.Format("#LOC_RB_INF_idle", (EC_amount / EC_maxAmount * 100).ToString("F1"));
            }

            BatterySOCStatus = $"{(SC_SOC * 100):F0}%";

            BatteryHealthStatus = $"{(BatteryLife * 100):F0}%";

            if (Math.Abs(GUI_power) > 0.001 && timeInSeconds > 0.05 && timeInSeconds < 1e7)
            {
                BatteryTimeTo = FormatTimeSpan(TimeSpan.FromSeconds(timeInSeconds));
            }
            else
            {
                BatteryTimeTo = "-"; // no localizzazione necessaria per un simbolo
            }

            RBLog.Verbose($"[RealBattery] GUI_power update: lastECpower={lastECpower:F3}, GUI_power={GUI_power:F3}, Δt={TimeWarp.fixedDeltaTime:F3}");
        }  
        
        // positive means sending EC to the battery, ie charging the battery
        // same for return value: positive value means EC was sent to the battery to charge it
        public double XferECtoRealBattery(double amount) 
        {
            // normal battery part

            double EC_delta = 0;
            double SC_delta = 0;
            double EC_power = 0;

            PartResource StoredCharge = part.Resources.Get("StoredCharge");

            // maximum discharge rate EC/s or kW
            DischargeRate = StoredCharge.maxAmount * Crate * BatteryLife; //kW;

            if (amount > 0 && SC_SOC < BatteryLife && !BatteryDisabled) // Charge battery
            {
                double SOC_ChargeEfficiency = ChargeEfficiencyCurve.Evaluate((float)SC_SOC);
                int SC_id = PartResourceLibrary.Instance.GetDefinition("StoredCharge").id;

                EC_delta = TimeWarp.fixedDeltaTime * DischargeRate * SOC_ChargeEfficiency;  // maximum amount of EC the battery can convert to SC, limited by current charge capacity

                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, Math.Min(EC_delta, amount));

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;

                SC_delta = -EC_delta / EC2SCratio;          // SC_delta = -1EC / 10EC/SC * 0.9 = -0.09SC
                SC_delta = part.RequestResource(SC_id, SC_delta);   //issue: we might "overfill" the battery and should give back some EC

                RBLog.Verbose("INF charged");
            }
            else if (amount < 0 && SC_SOC > 0 && !BatteryDisabled)  // Discharge battery
            {
                int SC_id = PartResourceLibrary.Instance.GetDefinition("StoredCharge").id;

                SC_delta = TimeWarp.fixedDeltaTime * DischargeRate / EC2SCratio;      // maximum amount of SC the battery can convert to EC

                SC_delta = part.RequestResource(SC_id, Math.Min(SC_delta, -amount / EC2SCratio)); //requesting SC from storage, so SC_delta will be positive

                EC_delta = -SC_delta * EC2SCratio;         // EC_delta = -0.1SC * 10EC/SC = 1EC
                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, EC_delta);

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;


                RBLog.Verbose("INF discharged");
            }
            else
            {
                EC_power = 0;

                RBLog.Verbose("INF no charge or discharge");
            }
                        
            if (Math.Abs(EC_delta) > 0.0001)
            {
                WearCounter += Math.Abs(EC_delta) / EC2SCratio; // kWh

                UpdateBatteryLife();
            }

            //update SOC field for usage in other modules (load balancing)
            SC_SOC = part.Resources["StoredCharge"].amount / part.Resources["StoredCharge"].maxAmount;

            lastECpower = EC_power;

            ApplyThermalEffects(EC_power);

            return EC_delta;
        }

        public void FixedUpdate()
        {
            if (HighLogic.LoadedSceneIsFlight && BatteryDisabled && RealBatterySettings.UseSelfDischarge)
            {
                RBLog.Verbose($"[FixedUpdate] Called ApplySelfDischarge");
                ApplySelfDischarge();
                return;
            }
            
            if (HighLogic.LoadedSceneIsEditor)
            {
                double sign = 0;

                systemHeat = part.Modules.GetModule<ModuleSystemHeat>();
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

                ApplyThermalEffects(lastECpower);
            }
        }

        public void ApplySelfDischarge()
        {
            if (!RealBatterySettings.UseSelfDischarge) return;
            RBLog.Verbose($"[ApplySelfDischarge] UseSelfDischarge is ON");

            if (!BatteryDisabled) return;
            RBLog.Verbose($"[ApplySelfDischarge] Battery is OFF");

            if (SC_SOC <= 0) return;

            double socLossPerDay = SelfDischargeRate / (BatteryLife > 0 ? BatteryLife : 1.0);
            double hoursPerDay = RealBatterySettings.Instance?.GetHoursPerDay() ?? 6.0;
            double socLossPerSecond = socLossPerDay / (hoursPerDay * 3600.0);

            double SC_loss = socLossPerSecond * part.Resources["StoredCharge"].maxAmount;
            double actualLoss = part.RequestResource("StoredCharge", SC_loss);

            RBLog.Verbose($"[ApplySelfDischarge] Self-discharge of {actualLoss:F9} kWh applied to {part.partInfo.title}");
        }

        private float smoothFlux = 0f;

        // === LEGACY | Will be removed in v2.3 ===

        private void ApplyThermalEffects(double EC_power)
        {
            if (!RealBatterySettings.UseHeatSimulation) return;
            RBLog.Verbose($"[ApplyThermalEffects] UseHeatSimulation is ON");

            if (BatteryDisabled) return;
            RBLog.Verbose($"[ApplyThermalEffects] Battery is ON");

            if (systemHeat == null || !systemHeat.moduleUsed || BatteryDisabled)
                return;

            float loopTemp = systemHeat.currentLoopTemperature;
            float flux = 0f;

            // Protection to avoid division by zero
            double safeBatteryLife = BatteryLife > 0.001 ? BatteryLife : 0.001;

            if (Math.Abs(EC_power) > 0.0001)
            {
                if (EC_power < 0) // Discharge
                {
                    flux = (float)(Math.Abs(EC_power) * ThermalLoss / safeBatteryLife);
                }
                else // Charge
                {
                    double ineff = Math.Max(SOC_ChargeEfficiency, 0.001); // avoid division by 0
                    flux = (float)(EC_power * ThermalLoss / ineff / safeBatteryLife);
                }

                flux *= 0.01f; // Reduces heat flux by 100× for balancing

                // Apply low-pass filter

                float tau = 0.01f;  // same as statusLowPassTauRatio
                smoothFlux += tau * (flux - smoothFlux);

                systemHeat.AddFlux("RealBattery", (float)TempOverheat, smoothFlux, true);

                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux ACTIVE: {smoothFlux:F2} W @ {TempOverheat:F0} K (loop={loopTemp:F1} K)");
            }
            else
            {
                // decay to 0 smoothly
                float tau = 0.01f;
                smoothFlux += tau * (0f - smoothFlux);
                systemHeat.AddFlux("RealBattery", 0f, smoothFlux, true);

                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux IDLE: {smoothFlux:F2} W → no EC transfer, loop={loopTemp:F1} K");
            }

            if (!RealBatterySettings.UseBatteryWear) return;

            if (!HighLogic.LoadedSceneIsFlight) return;

            if (loopTemp > TempOverheat)
            {
                PartResource StoredCharge = part.Resources.Get("StoredCharge");

                float t = loopTemp;
                float severity = (t - TempOverheat) / (TempRunaway - TempOverheat);
                severity = Mathf.Clamp01(severity);
                float thermalFactor = Mathf.Exp(severity * 4.0f) - 1.0f;

                float deltaWear = thermalFactor * TimeWarp.fixedDeltaTime * (float)(StoredCharge.maxAmount);

                WearCounter += deltaWear;

                RBLog.Verbose($"[ApplyThermalEffects] Thermal wear: Δ={deltaWear:F5}, T={loopTemp:F1} K");
            }
        }

        // === READY FOR v2.3 ===
        /*private void ApplyThermalEffects(double EC_power)
        {
            if (!RealBatterySettings.UseHeatSimulation) return;
            //RBLog.Verbose($"[ApplyThermalEffects] UseHeatSimulation is ON");

            if (BatteryDisabled) return;
            //RBLog.Verbose($"[ApplyThermalEffects] Battery is ON");

            // Protection to avoid division by zero
            double safeBatteryLife = BatteryLife > 0.001 ? BatteryLife : 0.001;
            double ineff = Math.Max(SOC_ChargeEfficiency, 0.001); // avoid division by 0
            float flux = 0f;
            float temp = 0f;

            bool useSH = RealBatterySettings.UseSystemHeat && systemHeat != null && systemHeat.moduleUsed;

            temp = useSH ? systemHeat.currentLoopTemperature : (float)part.temperature;

            if (Math.Abs(EC_power) > 0.0001)
            {
                if (EC_power < 0) // Discharge
                {
                    flux = (float)(Math.Abs(EC_power) * ThermalLoss / safeBatteryLife);
                }
                else // Charge
                {

                    flux = (float)(EC_power * ThermalLoss / ineff / safeBatteryLife);
                }

                flux *= 0.01f; // Reduces heat flux by 100× for balancing

                if (useSH)
                {
                    float tau = 0.01f;  // Low-pass filter for smoother SystemHeat flux
                    smoothFlux += tau * (flux - smoothFlux);
                    systemHeat.AddFlux("RealBattery", (float)TempOverheat, smoothFlux, true);
                    RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux ACTIVE (SystemHeat): {smoothFlux:F2} W @ {TempOverheat:F0} K (loop={temp:F1} K)");
                }
                else
                {
                    part.AddThermalFlux(flux);
                    RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux ACTIVE (stock): {flux:F2} W @ {TempOverheat:F0} K (part={temp:F1} K)");
                }
            }
            else if (RealBatterySettings.UseSystemHeat)
            {
                // decay to 0 smoothly
                float tau = 0.01f;
                smoothFlux += tau * (0f - smoothFlux);
                systemHeat.AddFlux("RealBattery", 0f, smoothFlux, true);

                RBLog.Verbose($"[ApplyThermalEffects] ThermalFlux IDLE: {smoothFlux:F2} W → no EC transfer, loop={temp:F1} K");
            }

            // --- Thermal wear (flight only) ---

            if (!RealBatterySettings.UseBatteryWear) return;
            if (!HighLogic.LoadedSceneIsFlight) return;

            var sc = part.Resources.Get("StoredCharge");
            if (sc != null && temp > TempOverheat)
            {
                float severity = Mathf.Clamp01((temp - TempOverheat) / Mathf.Max(TempRunaway - TempOverheat, 1e-3f));
                float thermalFactor = Mathf.Exp(severity * 4.0f) - 1.0f;                
                float deltaWear = thermalFactor * TimeWarp.fixedDeltaTime * (float)(sc.maxAmount);

                WearCounter += deltaWear;
                RBLog.Verbose($"[ApplyThermalEffects] Thermal wear: Δ={deltaWear:F5}, T={temp:F1} K");
            }            
        }*/

        public void UpdateBatteryLife()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            RBLog.Verbose($"[UpdateBatteryLife] Scene is FLIGHT");

            if (!RealBatterySettings.UseBatteryWear) return;
            RBLog.Verbose($"[UpdateBatteryLife] UseBatteryWear is ON");

            double capacity = part.Resources["StoredCharge"]?.maxAmount ?? 0.0;
            if (capacity <= 0 || CycleDurability <= 0)
            {
                BatteryLife = 1.0;
                return;
            }

            double maxWear = capacity * CycleDurability;
            double ratio = WearCounter / maxWear;

            BatteryLife = Math.Min(BatteryLife, BatteryLifeCurve.Evaluate((float)ratio));

            RBLog.Verbose($"[BatteryLife] capacity={capacity:F3}, CycleDurability={CycleDurability}, maxWear={maxWear:F3}, WearCounter={WearCounter:F3}, ratio={ratio:F3}, BatteryLife={BatteryLife:F3}");
        }

        public override string GetInfo()
        {
            RBLog.Verbose("INF GetInfo");

            LoadConfig();

            PartResource StoredCharge = part.Resources.Get("StoredCharge");
            DischargeRate = StoredCharge.maxAmount * Crate;

            return Localizer.Format("#LOC_RB_VAB_Info", BatteryTypeDisplayName, DischargeRate.ToString("F2"), (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f)).ToString("F2"));
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
    }
}
