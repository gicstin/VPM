using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPM.Services.Vpb
{
    public sealed class VpbUpdateConfigFile
    {
        public const string FileName = "vpb_update_config.json";
        public const string DefaultBranch = "main";

        private readonly JsonObject _raw;

        private VpbUpdateConfigFile(JsonObject raw, string vamRoot)
        {
            _raw = raw ?? new JsonObject();
            VamRoot = vamRoot ?? "";
        }

        public string VamRoot { get; }

        public string Channel { get; set; } = DefaultBranch;

        public bool Pinned { get; private set; }
        public string PinnedVersion { get; private set; } = "";
        public string PinnedTag { get; private set; } = "";
        public int PinnedSchema { get; private set; }

        public bool AutoCheck { get; set; }
        public string LastCheckUtc { get; set; } = "";
        public string LastStagedVersion { get; set; } = "";

        public bool IsNew { get; private init; }

        public string EffectiveRef =>
            Pinned && !string.IsNullOrEmpty(PinnedTag)
                ? PinnedTag
                : (string.IsNullOrWhiteSpace(Channel) ? DefaultBranch : Channel);

        public static string PathFor(string vamRoot) =>
            Path.Combine(vamRoot ?? "", FileName);

        public static VpbUpdateConfigFile Load(string vamRoot)
        {
            var path = PathFor(vamRoot);

            JsonObject raw = null;
            var existed = false;
            try
            {
                if (File.Exists(path))
                {
                    existed = true;
                    raw = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                }
            }
            catch
            {
                raw = null;
            }

            var config = new VpbUpdateConfigFile(raw, vamRoot) { IsNew = !existed };
            if (raw == null)
                return config;

            config.AutoCheck = GetBool(raw, "autoCheck");
            config.LastCheckUtc = GetString(raw, "lastCheckUtc");
            config.LastStagedVersion = GetString(raw, "lastStagedVersion");
            config.Pinned = GetBool(raw, "pinned");
            config.PinnedVersion = GetString(raw, "pinnedVersion");
            config.PinnedTag = GetString(raw, "pinnedTag");
            config.PinnedSchema = GetInt(raw, "pinnedSchema");

            var channel = GetString(raw, "channel");
            if (string.IsNullOrWhiteSpace(channel))
                channel = GetString(raw, "branch");
            if (!string.IsNullOrWhiteSpace(channel))
                config.Channel = channel;

            if (config.Pinned && string.IsNullOrEmpty(config.PinnedTag))
                config.Pinned = false;
            if (config.Pinned && string.Equals(config.Channel, config.PinnedTag, StringComparison.Ordinal))
                config.Channel = DefaultBranch;

            return config;
        }

        public void Pin(VpbRelease release)
        {
            if (release == null)
                throw new ArgumentNullException(nameof(release));
            if (string.IsNullOrWhiteSpace(release.Tag))
                throw new ArgumentException("Release has no tag to pin to.", nameof(release));

            Pinned = true;
            PinnedTag = release.Tag;
            PinnedVersion = release.Version ?? "";
            PinnedSchema = release.Schema;
        }

        public void Unpin()
        {
            Pinned = false;
            PinnedTag = "";
            PinnedVersion = "";
            PinnedSchema = 0;
        }

        public void SetChannel(string branch)
        {
            Channel = string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch;
            Unpin();
        }

        public void Save()
        {
            var node = _raw ?? new JsonObject();

            node["branch"] = EffectiveRef;
            node["channel"] = string.IsNullOrWhiteSpace(Channel) ? DefaultBranch : Channel;
            node["autoCheck"] = AutoCheck;
            node["lastCheckUtc"] = LastCheckUtc ?? "";
            node["lastStagedVersion"] = LastStagedVersion ?? "";
            node["pinned"] = Pinned;
            node["pinnedVersion"] = PinnedVersion ?? "";
            node["pinnedTag"] = PinnedTag ?? "";
            node["pinnedSchema"] = PinnedSchema;

            var path = PathFor(VamRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }

        private static string GetString(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var n) || n == null)
                return "";
            try { return n.GetValue<string>() ?? ""; }
            catch { return n.ToString(); }
        }

        private static bool GetBool(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var n) || n == null)
                return false;
            try { return n.GetValue<bool>(); }
            catch { return false; }
        }

        private static int GetInt(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var n) || n == null)
                return 0;
            try { return n.GetValue<int>(); }
            catch { return 0; }
        }
    }
}
