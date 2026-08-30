namespace VPM.Services.Vpb
{
    /// <summary>Outcome of a write back into VPB's own stores.</summary>
    public sealed class VpbWriteResult
    {
        public bool Success { get; init; }

        /// <summary>How many packages actually changed. Zero with <see cref="Success"/> means a no-op.</summary>
        public int PackagesChanged { get; init; }

        /// <summary>Empty on a clean success; otherwise something worth showing the user.</summary>
        public string Message { get; init; } = "";

        public static VpbWriteResult Ok(int changed) =>
            new() { Success = true, PackagesChanged = changed };

        public static VpbWriteResult Failed(string message) =>
            new() { Success = false, Message = message ?? "" };
    }
}
