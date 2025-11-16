using VPM.Models;

namespace VPM.Services
{
    public interface IPackageMetadataProvider
    {
        VarMetadata GetCachedPackageMetadata(string packageName);
    }
}
