-- Three-page cycle for the BMS (RealBattery) bay on the MFD Extended shared monitor:
-- EPS -> BATT -> FLEET -> EPS. See MFDExt_BATT.cfg (MAS_LUA node) for how this file is
-- loaded, and MFDExtension's own HOSTING.md ("Overriding your own button") for the
-- mechanism this hooks into.
--
-- Extended 2026-09-15 from the original two-page BATT<->FLEET toggle to make room for
-- MFDExt_BATT_EPS as the new entry page. With only two pages, the default
-- MFDExt_Redirect behavior (pressing the bay's own button from any OTHER page jumps to
-- the bay's own default page) covered the "return" direction for free, and only the
-- BATT->FLEET advance needed an explicit override here. With three pages that shortcut
-- no longer applies cleanly to a cycle, so every page now gets its own explicit
-- override -- nothing is left to MFDExt_Redirect's default inside this bay.

MFDExt_OwnButtonOverrides = MFDExt_OwnButtonOverrides or {}

MFDExt_OwnButtonOverrides["MFDExt_BATT_EPS"] = function(monitorID)
	fc.SetPersistent(monitorID, "MFDExt_BATT")
end

MFDExt_OwnButtonOverrides["MFDExt_BATT"] = function(monitorID)
	fc.SetPersistent(monitorID, "MFDExt_BATT_Fleet")
end

MFDExt_OwnButtonOverrides["MFDExt_BATT_Fleet"] = function(monitorID)
	fc.SetPersistent(monitorID, "MFDExt_BATT_EPS")
end
