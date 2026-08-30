using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VPM.Services.Vpb
{
    public static class VpbPresence
    {
        public static bool IsPluginInstalled(string vamRoot)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return false;
            try { return File.Exists(VpbPaths.VpbDllPath(vamRoot)); }
            catch { return false; }
        }

        public static bool IsVaMRunning()
        {
            try
            {
                return Process.GetProcessesByName("VaM").Length > 0
                    || Process.GetProcessesByName("VaM_OpenVR").Length > 0
                    || Process.GetProcessesByName("VaM_OpenXR").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
