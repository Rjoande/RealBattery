using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KSP.Localization;

using UnityEngine;
using static FinePrint.ContractDefs;

namespace RealBattery
{
    public class RealBattery : PartModule
    {
        [KSPField(isPersistant = false)]
        public bool moduleActive = true;

        // defines the battery characteristics, e.g. Lead_Acid
        //[KSPField(isPersistant = true)]
        //public string BatteryTypeDisplayName;

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

        
        //------GUI

        // Battery charge Status string
        [KSPField(isPersistant = false, guiActive = true, guiName = "#LOC_RB_Status", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryChargeStatus;

        // Battery tech string for Editor
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#LOC_RB_Tech", groupName = "RealBatteryInfo", groupDisplayName = "#LOC_RB_PAWgroup")]
        public string BatteryTypeDisplayName;
        //public string BatteryTech;

        // discharge string for Editor
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "#LOC_RB_DischargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo")]
        public string DischargeInfoEditor;

        // charge string for Editor
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "#LOC_RB_ChargeRate", guiUnits = "#LOC_RB_guiUnitsECs", groupName = "RealBatteryInfo")]
        public string ChargeInfoEditor;

        // Amount of Ec per storedCharge; 3600 EC = 1SC = 3600kWs = 1kWh
        public const double EC2SCratio = 3600;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);  // opzionale ma buona pratica

            if (!moduleActive)
            {
                foreach (BaseField f in Fields)
                {
                    f.guiActive = false;
                    f.guiActiveEditor = false;
                }

                foreach (BaseEvent e in Events)
                {
                    e.guiActive = false;
                    e.guiActiveEditor = false;
                }
            }
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

                if (config.HasNode("ChargeEfficiencyCurve"))
                {
                    ChargeEfficiencyCurve = new FloatCurve();
                    ChargeEfficiencyCurve.Load(config.GetNode("ChargeEfficiencyCurve"));
                }
            }

            //BatteryTech = BatteryTypeDisplayName;

            PartResource ElectricCharge = part.Resources.Get("ElectricCharge");
            PartResource StoredCharge = part.Resources.Get("StoredCharge");

            if (ElectricCharge == null)
            {
                RBlog("ElectricCharge not found, creating fallback...");
                ElectricCharge = part.Resources.Add("ElectricCharge", 0.1, 0.1, true, true, true, true, PartResource.FlowMode.Both);
            }

            if (StoredCharge == null)
            {
                RBlog("StoredCharge not found, creating fallback...");
                StoredCharge = part.Resources.Add("StoredCharge", 0, 0, true, true, true, true, PartResource.FlowMode.Both);
            }

            double DischargeRate = StoredCharge.maxAmount * Crate; //kW

            DischargeInfoEditor = String.Format("{0:F2}", DischargeRate);
            ChargeInfoEditor = String.Format("{0:F2}", DischargeRate * ChargeEfficiencyCurve.Evaluate(0f));

            StoredCharge.amount = SC_SOC * StoredCharge.maxAmount;

            UIPartActionWindow[] partWins = FindObjectsOfType<UIPartActionWindow>();
            foreach (UIPartActionWindow partWin in partWins)
            {
                partWin.displayDirty = true;
            }

            Fields["ChargeInfoEditor"].guiActiveEditor = (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f) > 0);
        }

        public override void OnStart(StartState state)
        {
            Debug.Log("RealBattery: INF OnStart");

            BatteryChargeStatus = "#LOC_RB_Initializing";

            LoadConfig();

            base.OnStart(state);

            StartCoroutine(DelayedInitialize());
        }

        private bool initialized = false;
        private IEnumerator DelayedInitialize()
        {
            yield return null;

            if (!initialized)
            {
                LoadConfig();
                initialized = true;
            }
        }

        public override string GetInfo()
        {
            RBlog("RealBattery: INF GetInfo");

            LoadConfig();

            PartResource StoredCharge = part.Resources.Get("StoredCharge");
            double DischargeRate = StoredCharge.maxAmount * Crate;            

            return Localizer.Format("#LOC_RB_VAB_Info", BatteryTypeDisplayName, DischargeRate.ToString("F2"), (DischargeRate * ChargeEfficiencyCurve.Evaluate(0f)).ToString("F2"));
        }
                
        // update context menu
        private double GUI_power = 0;
        public override void OnUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            RBlog("RealBattery: INF OnUpdate");

            // for slowing down the charge/discharge status
            double statusLowPassTauRatio = 0.01;

            GUI_power = GUI_power + statusLowPassTauRatio * (lastECpower - GUI_power);

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
        }

        private bool doLogDebugStuff = false;
        private void RBlog(string message)
        {
            if (doLogDebugStuff) // just for debugging
            Debug.Log(message);
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
            double DischargeRate = StoredCharge.maxAmount * Crate;
            if (amount > 0 && SC_SOC < 1) // Charge battery
            {
                double SOC_ChargeEfficiency = ChargeEfficiencyCurve.Evaluate((float)SC_SOC);
                int SC_id = PartResourceLibrary.Instance.GetDefinition("StoredCharge").id;

                EC_delta = TimeWarp.fixedDeltaTime * DischargeRate * SOC_ChargeEfficiency;  // maximum amount of EC the battery can convert to SC, limited by current charge capacity

                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, Math.Min(EC_delta, amount));

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;

                SC_delta = -EC_delta / EC2SCratio;          // SC_delta = -1EC / 10EC/SC * 0.9 = -0.09SC
                SC_delta = part.RequestResource(SC_id, SC_delta);   //issue: we might "overfill" the battery and should give back some EC


                RBlog("RealBattery: INF charged");
            }
            else if (amount < 0 && SC_SOC > 0)  // Discharge battery
            {
                int SC_id = PartResourceLibrary.Instance.GetDefinition("StoredCharge").id;

                SC_delta = TimeWarp.fixedDeltaTime * DischargeRate / EC2SCratio;      // maximum amount of SC the battery can convert to EC

                SC_delta = part.RequestResource(SC_id, Math.Min(SC_delta, -amount / EC2SCratio)); //requesting SC from storage, so SC_delta will be positive

                EC_delta = -SC_delta * EC2SCratio;         // EC_delta = -0.1SC * 10EC/SC = 1EC
                EC_delta = part.RequestResource(PartResourceLibrary.ElectricityHashcode, EC_delta);

                EC_power = EC_delta / TimeWarp.fixedDeltaTime;


                RBlog("RealBattery: INF discharged");
            }
            else
            {
                EC_power = 0;

                RBlog("RealBattery: INF no charge or discharge");
            }

            //update SOC field for usage in other modules (load balancing)
            SC_SOC = part.Resources["StoredCharge"].amount / part.Resources["StoredCharge"].maxAmount;

            lastECpower = EC_power;

            return EC_delta;
        }
    }
}
