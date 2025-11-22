using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    public class VarIntegrityScanner
    {
        public class IntegrityResult
        {
            public bool IsDamaged { get; set; }
            public string DamageReason { get; set; } = "";
            public List<string> Issues { get; set; } = new List<string>();
        }

        public async Task<IntegrityResult> ScanPackageAsync(string varFilePath)
        {
            return await Task.FromResult(ScanPackage(varFilePath));
        }

        public IntegrityResult ScanPackage(string varFilePath)
        {
            var result = new IntegrityResult();
            
            // No actual scanning needed - PackageManager already validates:
            // 1. Archive can be opened (catches corrupt ZIP)
            // 2. meta.json can be read
            // 3. Entries are enumerated for content detection
            // If any of those fail, PackageManager sets IsCorrupted = true
            
            return result;
        }
        
        public IntegrityResult ValidateMetadata(VarMetadata metadata)
        {
            var result = new IntegrityResult();
            
            // Check if PackageManager already detected corruption
            if (metadata.IsCorrupted)
            {
                result.IsDamaged = true;
                result.DamageReason = "Corrupted or unreadable archive";
                return result;
            }
            
            // Check for essentially empty packages (just meta.json or less)
            if (metadata.FileCount <= 1)
            {
                result.IsDamaged = true;
                result.DamageReason = "Empty package (no content files)";
                return result;
            }
            
            // Check for missing meta.json by looking at multiple indicators
            // If meta.json was missing, these fields won't be populated:
            bool hasLicenseType = !string.IsNullOrEmpty(metadata.LicenseType);
            bool hasDescription = !string.IsNullOrEmpty(metadata.Description);
            bool hasDependencies = metadata.Dependencies != null && metadata.Dependencies.Count > 0;
            bool hasContentTypes = metadata.ContentTypes != null && metadata.ContentTypes.Count > 0;
            
            // If none of these meta.json-only fields are set, meta.json is likely missing
            if (!hasLicenseType && !hasDescription && !hasDependencies && !hasContentTypes)
            {
                result.IsDamaged = true;
                result.DamageReason = "Missing or empty meta.json";
                return result;
            }
            
            // Check for improper folder structure (no Custom or Saves folders)
            // This is a VAM requirement - packages must have content in these folders
            // Only check if ContentList is not null (null means we couldn't determine content)
            if (metadata.ContentList != null)
            {
                if (metadata.ContentList.Count == 0)
                {
                    result.IsDamaged = true;
                    result.DamageReason = "No Custom/ or Saves/ folders found";
                    return result;
                }
                
                var hasProperStructure = metadata.ContentList.Any(path =>
                    path.StartsWith("Custom/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase));
                    
                if (!hasProperStructure)
                {
                    result.IsDamaged = true;
                    result.DamageReason = "No Custom/ or Saves/ folders found";
                    return result;
                }
            }
            
            return result;
        }
    }
}

