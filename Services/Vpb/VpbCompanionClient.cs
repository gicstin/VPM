using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace VPM.Services.Vpb
{
    public static class VpbCompanionClient
    {
        public const string PipeName = "VPB-companion";

        public static bool TryReloadWhitelist() => TrySend("RELOAD_WHITELIST");

        public static bool TryLoadScene(string pathOrUid) =>
            TrySend("LOAD_SCENE " + (pathOrUid ?? "").Replace('\n', ' ').Replace('\r', ' '));

        public static bool TryPing() => TrySend("PING");

        public static bool TrySend(string command, int timeoutMs = 800)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (!VpbPresence.IsVaMRunning()) return false;
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(timeoutMs);
                var bytes = Encoding.UTF8.GetBytes(command.Trim() + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
                var line = reader.ReadLine();
                return line != null && line.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
