namespace RealBattery
{
    // Marker-only PartModule. Presence on a part (added by an ex-ante MM patch,
    // e.g. patches/00_Ignore/RB_Ignore_List.cfg) lets every RealBattery-adding
    // patch opt the part out via a "!HAS[@MODULE[RB_ignore]]" condition. No logic.
    public class RB_ignore : PartModule
    {
    }
}
