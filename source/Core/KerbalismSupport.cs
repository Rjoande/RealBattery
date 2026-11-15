using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RealBattery
{
    // Kerbalism integration on the PartModule side:
    //  - StoredCharge handling that stays outside Kerbalism's resource cache (flow disabled,
    //    direct amount writes) so charging never trips the "incoherent producer" check;
    //  - the static BackgroundUpdate hook Kerbalism calls on unloaded vessels, replacing
    //    RealBattery's own BackgroundSimulator while Kerbalism owns the background simulation.
    public partial class RealBattery
    {
        // With Kerbalism the SC resource must be excluded from its sync set: Kerbalism honours
        // only the per-part flowState, and a flow-disabled resource is never read nor written by
        // it. Stock Part.RequestResource returns 0 for a flow-disabled resource, so SC is then
        // moved by RequestSC() directly. Without Kerbalism the flag is forced back on, so a save
        // that drops Kerbalism keeps working (SC is not tweakable: players never touch it).
        private void EnforceScFlowState()
        {
            if (!HighLogic.LoadedSceneIsFlight || ScResourceMissing) return;
            PartResource sc = part.Resources.Get(SC_ID);
            if (sc == null) return;
            bool wanted = !KerbalismBridge.Active;
            if (sc.flowState != wanted) sc.flowState = wanted;
        }

        // Stock-signed StoredCharge request (positive = take, negative = add) that bypasses the
        // flow system when Kerbalism is active. Returns the amount actually moved, stock-signed.
        private double RequestSC(double amount)
        {
            if (!KerbalismBridge.Active) return part.RequestResource(SC_ID, amount);

            PartResource sc = part.Resources.Get(SC_ID);
            if (sc == null || amount == 0.0) return 0.0;

            if (amount > 0.0)
            {
                double got = Math.Min(amount, sc.amount);
                sc.amount -= got;
                return got;
            }
            double put = Math.Min(-amount, Math.Max(0.0, sc.maxAmount - sc.amount));
            sc.amount += put;
            return -put;
        }

        // ------------------------------------------------------------------------------------
        //  Kerbalism background hook (unloaded vessels)
        // ------------------------------------------------------------------------------------

        // Vessel-wide figures shared by every RealBattery of one Kerbalism background step. All
        // modules of a vessel are processed within the same call, so the UT is the cache key.
        private sealed class KbgVesselContext
        {
            public Guid vesselId;
            public double ut;
            public double totalVirtualCap;   // kWh, enabled batteries only
            public double ecAmount;
            public double ecCapacity;
            public double netOthers;         // EC/s of all non-RB brokers on the last Kerbalism step
            public bool netOthersKnown;      // false right after a game load (no broker record yet)
            // Net EC/s of everything but RB, measured from the EC change since the previous
            // step (catches consumers Kerbalism doesn't list as brokers, e.g. other mods writing
            // proto resources). Valid only when the two steps are contiguous and EC isn't full.
            public double measuredOthers;
            public bool measuredValid;
            // EC (units) that RB offered on the previous step but Kerbalism clamped away at full
            // capacity, and the total RB production rate of that step, to hand the SC back.
            public double surplusPrevEc;
            public double producedPrevRate;
            // Sunlight fraction of this step relative to the previous one (<= 1): the expected
            // surplus from the last step is scaled by it, so charging backs off entering an eclipse.
            public double sunNow;
            public double sunScale;
            public double engBonus;
            public double hoursPerDay;
        }
        private static KbgVesselContext _kbgCtx;
        // Proto-only bookkeeping values (not KSPFields): last known others' balance, and the
        // vessel EC / UT / own rate at the start of the previous step for the measured balance.
        private const string KbgNetKey  = "kbgNetOthers";
        private const string KbgEcKey   = "kbgEcPrev";
        private const string KbgUtKey   = "kbgUtPrev";
        private const string KbgRateKey = "kbgRatePrev";
        private const string KbgDtKey   = "kbgDtPrev";
        private const string KbgSunKey  = "kbgSunPrev";

        // Background steps can last thousands of seconds while the consumers' rates keep moving
        // (climatization follows day/night), so producing exactly the last known deficit lets the
        // small EC buffer run dry within one step. RB therefore offers this much more than the
        // expected deficit; Kerbalism discards what does not fit and the next step refunds the SC.
        private const double KbgDischargeHeadroom = 2.0;

        // Chemistry parameters needed in background, resolved from the DB (by persisted or
        // prefab ChemistryID, or the B9PS subtype name) with the prefab's inline values as fallback.
        private struct KbgChemistry
        {
            public float crate, highLevel, lowLevel;
            public double cycleDurability, selfDischargeRate;
            public bool fixedOutput, infiniteCycles, lifeDecay;
            public FloatCurve chargeEfficiency;
            public bool isPrimary;
        }

        // Exact signature required by Kerbalism (Background.cs BackgroundDelegate). Kerbalism
        // calls it per RealBattery module of an unloaded vessel, with elapsed_s possibly hours.
        public static string BackgroundUpdate(Vessel v,
            ProtoPartSnapshot part_snapshot, ProtoPartModuleSnapshot module_snapshot,
            PartModule proto_part_module, Part proto_part,
            Dictionary<string, double> availableResources, List<KeyValuePair<string, double>> resourceChangeRequest,
            double elapsed_s)
        {
            try
            {
                if (v == null || part_snapshot == null || module_snapshot == null || elapsed_s <= 0.0)
                    return KerbalismBridge.BrokerId;
                if (!KerbalismBridge.Active) return KerbalismBridge.BrokerId;

                ProtoPartResourceSnapshot sc = FindProtoResource(part_snapshot, "StoredCharge");
                if (sc == null || sc.maxAmount <= 0.0) return KerbalismBridge.BrokerId;

                RealBattery prefab = proto_part_module as RealBattery;
                KbgChemistry chem = ResolveBackgroundChemistry(part_snapshot, module_snapshot, prefab);
                KbgVesselContext ctx = GetBackgroundContext(v, availableResources, elapsed_s);

                bool wearOn      = RealBatterySettings.EnableBatteryWear;
                bool disabled    = PGetBool(module_snapshot, "BatteryDisabled", false)
                                || PGetBool(module_snapshot, "forcedRunawayActive", false);
                double life      = PGet(module_snapshot, "BatteryLife", 1.0);
                double wear      = PGet(module_snapshot, "WearCounter", 0.0);
                double thermalCap = PGet(module_snapshot, "ThermalCapFactor", 1.0);
                double actualLife = wearOn ? life : 1.0;
                double virtualCap = sc.maxAmount * actualLife;
                double chargeCap  = sc.maxAmount * (chem.infiniteCycles ? thermalCap : actualLife);
                bool enabled = !disabled && virtualCap > 1e-9;

                // Refund the part of the previous step's production that Kerbalism clamped away.
                double ownPrev = PGet(module_snapshot, KbgRateKey, 0.0);
                double refundSC = 0.0;
                if (ownPrev > 0.0 && ctx.producedPrevRate > 1e-12 && ctx.surplusPrevEc > 0.0)
                {
                    refundSC = ctx.surplusPrevEc * (ownPrev / ctx.producedPrevRate) / EC2SCratio;
                    refundSC = Math.Min(refundSC, Math.Max(0.0, sc.maxAmount - sc.amount));
                    sc.amount += refundSC;
                    if (!chem.infiniteCycles)
                        wear = Math.Max(0.0, wear - (chem.fixedOutput ? refundSC : refundSC / ctx.engBonus));
                }

                // Kerbalism rebuilds its broker list at every Sync and never saves it, so the
                // first step after a game load knows nothing about the consumers: a long step
                // would then drain the EC buffer before RB reacts. Keep the last known balance in
                // the proto module and fall back to it (0 if none was ever recorded).
                double netOthers = ctx.netOthersKnown ? ctx.netOthers : PGet(module_snapshot, KbgNetKey, 0.0);
                // Trust the measured balance when it shows more consumption than the brokers do
                // (never less: an EC-full clamp would otherwise read as phantom consumption).
                if (ctx.measuredValid && ctx.measuredOthers < netOthers) netOthers = ctx.measuredOthers;
                if (ctx.netOthersKnown || ctx.measuredValid) PSet(module_snapshot, KbgNetKey, netOthers);
                // A surplus seen on the last step (solar) only holds for the lit part of this one.
                if (netOthers > 0.0) netOthers *= ctx.sunScale;

                double dischargeRate = sc.maxAmount * chem.crate * actualLife;   // EC/s (kW)
                if (!chem.fixedOutput) dischargeRate *= ctx.engBonus;

                double share = ctx.totalVirtualCap > 1e-9 ? virtualCap / ctx.totalVirtualCap : 0.0;
                double ecRate = 0.0;   // + produced, - consumed (EC/s)
                bool discharged = false, charged = false;

                if (enabled && sc.amount > EPS && (chem.fixedOutput ||
                    netOthers < -EPS || ctx.ecAmount < ctx.ecCapacity * chem.lowLevel))
                {
                    // Offer the others' expected deficit with headroom, plus whatever refills the buffer.
                    double want = Math.Max(0.0, -netOthers) * KbgDischargeHeadroom
                                + Math.Max(0.0, ctx.ecCapacity - ctx.ecAmount) / elapsed_s;
                    double rate = chem.fixedOutput ? dischargeRate : Math.Min(dischargeRate, share * want);
                    rate = Math.Min(rate, sc.amount * EC2SCratio / elapsed_s);
                    if (rate > 1e-9)
                    {
                        double drawnSC = rate * elapsed_s / EC2SCratio;
                        sc.amount -= drawnSC;
                        ecRate = rate;
                        discharged = true;
                        if (!chem.infiniteCycles)
                            wear += chem.fixedOutput ? drawnSC : drawnSC / ctx.engBonus;
                    }
                }
                else if (enabled && !chem.fixedOutput && !chem.isPrimary && sc.amount < chargeCap - EPS &&
                         (netOthers > EPS || ctx.ecAmount > ctx.ecCapacity * chem.highLevel))
                {
                    double soc = sc.amount / sc.maxAmount;
                    double eff = chem.chargeEfficiency != null ? chem.chargeEfficiency.Evaluate((float)soc) : 1.0;
                    if (eff > EPS)
                    {
                        double supply = Math.Max(0.0, netOthers)
                                      + Math.Max(0.0, ctx.ecAmount - ctx.ecCapacity * chem.highLevel) / elapsed_s;
                        // As in the live path, efficiency caps the charge rate (not the energy ratio).
                        double rate = Math.Min(dischargeRate * eff, share * supply);
                        rate = Math.Min(rate, (chargeCap - sc.amount) * EC2SCratio / elapsed_s);
                        if (rate > 1e-9)
                        {
                            double gainedSC = rate * elapsed_s / EC2SCratio;
                            sc.amount += gainedSC;
                            ecRate = -rate;
                            charged = true;
                            if (!chem.infiniteCycles) wear += gainedSC / ctx.engBonus;
                        }
                    }
                }

                if (Math.Abs(ecRate) > 1e-12)
                    resourceChangeRequest.Add(new KeyValuePair<string, double>("ElectricCharge", ecRate));

                // Self-discharge / LifeDecay, same rules as the former ApplySnapshot pass 2.
                if (RealBatterySettings.EnableSelfDischarge)
                {
                    double secPerDay = ctx.hoursPerDay * 3600.0;
                    if (chem.lifeDecay)
                    {
                        if (wearOn)
                            life = Math.Max(0.0, life - chem.selfDischargeRate / secPerDay * elapsed_s);
                    }
                    else if (sc.amount > EPS)
                    {
                        bool idle = !discharged && !charged;
                        bool selfDischarge = disabled
                            || (chem.isPrimary && !discharged)
                            || (!chem.isPrimary && idle);
                        if (selfDischarge)
                        {
                            double lossPerSec = chem.selfDischargeRate / (actualLife > 0 ? actualLife : 1.0) / secPerDay;
                            sc.amount = Math.Max(0.0, sc.amount - lossPerSec * elapsed_s * sc.maxAmount);
                        }
                    }
                }

                // Wear -> BatteryLife (mirrors UpdateBatteryLife) and capacity clamp.
                if (wearOn && !chem.infiniteCycles && chem.cycleDurability > 0.0)
                {
                    double maxWear = sc.maxAmount * chem.cycleDurability * 2.0;
                    life = Math.Min(life, BatteryLifeCurve.Evaluate((float)(wear / maxWear)));
                    if (sc.amount > sc.maxAmount * life) sc.amount = sc.maxAmount * life;
                }
                sc.amount = Math.Max(0.0, Math.Min(sc.amount, sc.maxAmount));

                PSet(module_snapshot, "BatteryLife", life);
                PSet(module_snapshot, "WearCounter", wear);
                PSet(module_snapshot, "SC_SOC", sc.maxAmount > 0 ? sc.amount / sc.maxAmount : 0.0);
                PSet(module_snapshot, KbgEcKey, ctx.ecAmount);
                PSet(module_snapshot, KbgUtKey, ctx.ut);
                PSet(module_snapshot, KbgRateKey, ecRate);
                PSet(module_snapshot, KbgDtKey, elapsed_s);
                if (!double.IsNaN(ctx.sunNow)) PSet(module_snapshot, KbgSunKey, ctx.sunNow);

                if (RBLog.VerboseEnabled && (Math.Abs(ecRate) > 1e-12 || ctx.ecAmount < ctx.ecCapacity * chem.lowLevel))
                    RBLog.Verbose($"[KerbalismBG] '{v.vesselName}' / '{part_snapshot.partInfo?.title}': " +
                                  $"EC {ecRate:+0.000;-0.000} EC/s over {elapsed_s:F2}s, vesselEC={ctx.ecAmount:F2}/{ctx.ecCapacity:F2}, " +
                                  $"SC={sc.amount:F3}/{sc.maxAmount:F3} kWh, netOthers={netOthers:F3}" +
                                  $"{(ctx.netOthersKnown ? "" : " (stored)")}{(ctx.measuredValid ? $" measured={ctx.measuredOthers:F3}" : "")}, " +
                                  $"share={share:P0}, refund={refundSC:F4} kWh, sun={ctx.sunNow:F2}x{ctx.sunScale:F2}, brokers=[{KerbalismBridge.DescribeBrokers(v, "ElectricCharge")}]");
            }
            catch (Exception ex)
            {
                RBLog.Warn($"[KerbalismBG] BackgroundUpdate failed on '{v?.vesselName}': {ex.GetType().Name} {ex.Message}");
            }
            return KerbalismBridge.BrokerId;
        }

        private static KbgVesselContext GetBackgroundContext(Vessel v, Dictionary<string, double> availableResources, double elapsedHint)
        {
            double ut = Planetarium.GetUniversalTime();
            if (_kbgCtx != null && _kbgCtx.vesselId == v.id && _kbgCtx.ut == ut) return _kbgCtx;

            var ctx = new KbgVesselContext { vesselId = v.id, ut = ut };
            bool wearOn = RealBatterySettings.EnableBatteryWear;

            double ecPrev = double.NaN, utPrev = double.NaN, dtStored = double.NaN, sunPrev = double.NaN, ownRatePrev = 0.0, producedPrev = 0.0;
            if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
                {
                    ProtoPartResourceSnapshot sc = FindProtoResource(p, "StoredCharge");
                    if (sc == null || sc.maxAmount <= 0.0) continue;
                    foreach (ProtoPartModuleSnapshot m in p.modules)
                    {
                        if (m.moduleName != nameof(RealBattery)) continue;
                        ownRatePrev += PGet(m, KbgRateKey, 0.0);
                        producedPrev += Math.Max(0.0, PGet(m, KbgRateKey, 0.0));
                        if (double.IsNaN(ecPrev))
                        {
                            ecPrev = PGet(m, KbgEcKey, double.NaN);
                            utPrev = PGet(m, KbgUtKey, double.NaN);
                            dtStored = PGet(m, KbgDtKey, double.NaN);
                            sunPrev = PGet(m, KbgSunKey, double.NaN);
                        }
                        if (PGetBool(m, "BatteryDisabled", false) || PGetBool(m, "forcedRunawayActive", false)) break;
                        ctx.totalVirtualCap += sc.maxAmount * (wearOn ? PGet(m, "BatteryLife", 1.0) : 1.0);
                        break;
                    }
                }
            }

            if (availableResources == null || !availableResources.TryGetValue("ElectricCharge", out ctx.ecAmount))
                ctx.ecAmount = KerbalismBridge.Amount(v, "ElectricCharge");
            ctx.ecCapacity = KerbalismBridge.Capacity(v, "ElectricCharge");
            ctx.netOthers = KerbalismBridge.NetRateOfOthers(v, "ElectricCharge", out ctx.netOthersKnown);

            // Measured balance of everything but RB over the previous step: valid only if that
            // step is the one just before this (contiguous background stepping, no loaded gap)
            // and EC was not clamped at full (an overflow would look like consumption).
            if (!double.IsNaN(ecPrev) && !double.IsNaN(utPrev))
            {
                double dtPrev = ut - utPrev;
                if (dtPrev > 1e-3 && Math.Abs(dtPrev - elapsedHint) <= 0.01 * elapsedHint + 1.0
                    && ctx.ecCapacity > 0.0 && ctx.ecAmount < ctx.ecCapacity - 1e-6)
                {
                    ctx.measuredOthers = (ctx.ecAmount - ecPrev) / dtPrev - ownRatePrev;
                    ctx.measuredValid = true;
                }
            }
            // Surplus of the previous step: what RB offered minus what the EC level shows was taken.
            // The bookkeeping keys are dropped whenever the vessel gets loaded (unknown module
            // values are not re-saved), so finding them means the previous step was a background one.
            if (ctx.netOthersKnown && producedPrev > 1e-12 && !double.IsNaN(ecPrev) && !double.IsNaN(dtStored) && dtStored > 0.0)
            {
                double offered = producedPrev * dtStored;
                double accepted = (ctx.ecAmount - ecPrev) - (ctx.netOthers + (ownRatePrev - producedPrev)) * dtStored;
                ctx.surplusPrevEc = Math.Max(0.0, Math.Min(offered, offered - accepted));
                ctx.producedPrevRate = producedPrev;
            }
            ctx.sunNow = KerbalismBridge.SunlightFactor(v);
            ctx.sunScale = 1.0;
            if (!double.IsNaN(ctx.sunNow))
                ctx.sunScale = (!double.IsNaN(sunPrev) && sunPrev > 0.01) ? Math.Min(1.0, ctx.sunNow / sunPrev) : Math.Min(1.0, ctx.sunNow);
            ctx.engBonus = 0.95 + 0.06 * ModuleEnergyEstimator.GetMaxSpecialistLevel(v, "Engineer");
            ctx.hoursPerDay = RealBatterySettings.GetHoursPerDay();

            _kbgCtx = ctx;
            return ctx;
        }

        private static KbgChemistry ResolveBackgroundChemistry(ProtoPartSnapshot p, ProtoPartModuleSnapshot m, RealBattery prefab)
        {
            string id = m.moduleValues.GetValue("ChemistryID");
            if (string.IsNullOrEmpty(id) && prefab != null) id = prefab.ChemistryID;
            if (string.IsNullOrEmpty(id))
            {
                // Saves written before ChemistryID became persistent: the B9PS subtype name equals
                // the ChemistryID by project convention (see EVAService).
                foreach (ProtoPartModuleSnapshot other in p.modules)
                {
                    if (other.moduleName != "ModuleB9PartSwitch") continue;
                    if (other.moduleValues.GetValue("moduleID") != "batterySwitch") continue;
                    id = other.moduleValues.GetValue("currentSubtype");
                    break;
                }
            }

            RealBatteryChemistry db = RealBatteryChemistryDB.Get(id);
            var c = new KbgChemistry();
            if (db != null)
            {
                c.crate = db.Crate; c.highLevel = db.HighEClevel; c.lowLevel = db.LowEClevel;
                c.cycleDurability = db.CycleDurability; c.selfDischargeRate = db.SelfDischargeRate;
                c.fixedOutput = db.FixedOutput; c.infiniteCycles = db.InfiniteCycles; c.lifeDecay = db.LifeDecay;
                c.chargeEfficiency = db.ChargeEfficiencyCurve;
            }
            else if (prefab != null)
            {
                c.crate = prefab.Crate; c.highLevel = prefab.HighEClevel; c.lowLevel = prefab.LowEClevel;
                c.cycleDurability = prefab.CycleDurability; c.selfDischargeRate = prefab.SelfDischargeRate;
                c.fixedOutput = prefab.FixedOutput; c.infiniteCycles = prefab.InfiniteCycles; c.lifeDecay = prefab.LifeDecay;
                c.chargeEfficiency = prefab.ChargeEfficiencyCurve;
            }
            else
            {
                c.crate = RBDefaults.Crate; c.highLevel = RBDefaults.HighEClevel; c.lowLevel = RBDefaults.LowEClevel;
                c.cycleDurability = RBDefaults.CycleDurability; c.selfDischargeRate = RBDefaults.SelfDischargeRate;
                c.fixedOutput = RBDefaults.FixedOutput; c.infiniteCycles = RBDefaults.InfiniteCycles; c.lifeDecay = RBDefaults.LifeDecay;
            }
            // Same three signals as RealBattery.IsPrimary.
            float eff0 = c.chargeEfficiency != null ? c.chargeEfficiency.Evaluate(0f) : 1f;
            c.isPrimary = c.cycleDurability <= 1.0 || c.highLevel > 1f || eff0 <= (float)EPS;
            return c;
        }

        private static ProtoPartResourceSnapshot FindProtoResource(ProtoPartSnapshot p, string name)
        {
            for (int i = 0; i < p.resources.Count; i++)
                if (p.resources[i].resourceName == name) return p.resources[i];
            return null;
        }

        private static double PGet(ProtoPartModuleSnapshot m, string key, double def)
        {
            string s = m.moduleValues.GetValue(key);
            return !string.IsNullOrEmpty(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
        }

        private static bool PGetBool(ProtoPartModuleSnapshot m, string key, bool def)
        {
            string s = m.moduleValues.GetValue(key);
            return !string.IsNullOrEmpty(s) && bool.TryParse(s, out bool v) ? v : def;
        }

        private static void PSet(ProtoPartModuleSnapshot m, string key, double value)
        {
            m.moduleValues.SetValue(key, value.ToString("R", CultureInfo.InvariantCulture), true);
        }
    }
}
