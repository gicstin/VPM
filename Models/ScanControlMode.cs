namespace VPM.Models
{
    /// <summary>How VPM decides what VaM indexes at boot. Auto: whitelist when VPB.dll is installed, otherwise AddonPackages/AllPackages file-move.</summary>
    public enum ScanControlMode
    {
        Auto = 0,
        Whitelist = 1,
        FileMove = 2
    }
}
