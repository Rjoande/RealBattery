-- L1<->L2 toggle for the BMS (RealBattery) bay on the MFD Extended shared monitor.
-- See MFDExt_BATT.cfg (MAS_LUA node) for how this file is loaded, and MFDExtension's
-- own HOSTING.md ("Overriding your own button") for the mechanism this hooks into.
--
-- Default behavior (MFDExt_Redirect, host-side): pressing the bay's own button from
-- ANY other page (a host page, another bay, or our own L2 below) jumps straight to
-- MFDExt_BATT (L1) -- that's how pressing B again from L2 already returns to L1, no
-- code needed here for that direction. The only gap this file fills is the OTHER
-- direction: what happens when the button is pressed while ALREADY on MFDExt_BATT,
-- which otherwise defaults to doing nothing.

MFDExt_OwnButtonOverrides = MFDExt_OwnButtonOverrides or {}

MFDExt_OwnButtonOverrides["MFDExt_BATT"] = function(monitorID)
	fc.SetPersistent(monitorID, "MFDExt_BATT_Fleet")
end
