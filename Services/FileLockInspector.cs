using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VPM.Services
{
    /// <summary>Which processes hold an open handle to a file, via Windows Restart Manager. Turns "used by another process" into e.g. "locked by VaM.exe".</summary>
    public static class FileLockInspector
    {
        private const int RmRebootReasonNone = 0;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;
        private const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
                                                      uint nFiles,
                                                      string[] rgsFilenames,
                                                      uint nApplications,
                                                      [In] RM_UNIQUE_PROCESS[] rgApplications,
                                                      uint nServices,
                                                      string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle,
                                            out uint pnProcInfoNeeded,
                                            ref uint pnProcInfo,
                                            [In, Out] RM_PROCESS_INFO[] rgAffectedApps,
                                            ref uint lpdwRebootReasons);

        /// <summary>Processes holding <paramref name="filePath"/>. Empty when none or the query is unavailable. Never throws.</summary>
        public static List<(int ProcessId, string Name, bool IsCurrentProcess)> GetLockingProcesses(string filePath)
        {
            var result = new List<(int, string, bool)>();
            if (string.IsNullOrEmpty(filePath))
                return result;

            uint sessionHandle = 0;
            bool sessionStarted = false;

            try
            {
                if (RmStartSession(out sessionHandle, 0, Guid.NewGuid().ToString("N")) != 0)
                    return result;

                sessionStarted = true;

                if (RmRegisterResources(sessionHandle, 1, new[] { filePath }, 0, null, 0, null) != 0)
                    return result;

                uint procInfo = 0;
                uint rebootReasons = RmRebootReasonNone;
                int rc = RmGetList(sessionHandle, out uint procInfoNeeded, ref procInfo, null, ref rebootReasons);

                if (rc == ERROR_MORE_DATA && procInfoNeeded > 0)
                {
                    var processInfo = new RM_PROCESS_INFO[procInfoNeeded];
                    procInfo = procInfoNeeded;

                    if (RmGetList(sessionHandle, out procInfoNeeded, ref procInfo, processInfo, ref rebootReasons) != 0)
                        return result;

                    int currentPid = Environment.ProcessId;

                    for (int i = 0; i < procInfo; i++)
                    {
                        int pid = processInfo[i].Process.dwProcessId;
                        string name = processInfo[i].strAppName;

                        // strAppName is a friendly description; prefer the real process name when reachable.
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            name = process.ProcessName;
                        }
                        catch
                        {
                            // Process already exited or not accessible - keep the Restart Manager name.
                        }

                        if (string.IsNullOrWhiteSpace(name))
                            name = "unknown";

                        result.Add((pid, name, pid == currentPid));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FileLockInspector failed for '{filePath}': {ex.Message}");
            }
            finally
            {
                if (sessionStarted)
                {
                    try { RmEndSession(sessionHandle); } catch { }
                }
            }

            return result;
        }

        /// <summary>Human-readable summary of who is holding the file, or null when nothing was found.</summary>
        public static string DescribeLockingProcesses(string filePath)
        {
            var holders = GetLockingProcesses(filePath);
            if (holders.Count == 0)
                return null;

            return string.Join(", ", holders
                .Select(h => h.IsCurrentProcess
                    ? $"VPM itself (PID {h.ProcessId})"
                    : $"{h.Name} (PID {h.ProcessId})")
                .Distinct());
        }
    }
}
