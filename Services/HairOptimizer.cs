using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using Console = System.Console;

namespace VPM.Services
{
    /// <summary>
    /// Optimizes hair settings in scene JSON files
    /// </summary>
    public class HairOptimizer
    {
        /// <summary>
        /// Shadow resolution options for lights
        /// </summary>
        public enum ShadowResolutionOption
        {
            Keep = -1,
            Off = 0,
            Resolution512 = 512,
            Resolution1024 = 1024,
            Resolution2048 = 2048
        }

        /// <summary>
        /// Information about a light item
        /// </summary>
        public class LightInfo : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private ShadowResolutionOption _selectedShadowOption;
            private bool _castShadows;
            private int _shadowResolution;

            public string PackageName { get; set; }
            public string LightId { get; set; }
            public string LightName { get; set; }
            public string LightType { get; set; }
            public string SceneFile { get; set; }
            
            public bool CastShadows
            {
                get => _castShadows;
                set
                {
                    _castShadows = value;
                    OnPropertyChanged(nameof(CastShadows));
                    OnPropertyChanged(nameof(CurrentShadowStatus));
                    SetDefaultShadowOption();
                }
            }
            
            public int ShadowResolution
            {
                get => _shadowResolution;
                set
                {
                    _shadowResolution = value;
                    OnPropertyChanged(nameof(ShadowResolution));
                    OnPropertyChanged(nameof(CurrentShadowStatus));
                    SetDefaultShadowOption();
                }
            }

            public string CurrentShadowStatus => CastShadows ? $"{ShadowResolution}" : "Off";

            public ShadowResolutionOption SelectedShadowOption
            {
                get => _selectedShadowOption;
                set
                {
                    if (_selectedShadowOption == value) return;
                    _selectedShadowOption = value;
                    OnPropertyChanged(nameof(SelectedShadowOption));
                    OnPropertyChanged(nameof(KeepUnchanged));
                    OnPropertyChanged(nameof(SetShadowsOff));
                    OnPropertyChanged(nameof(SetShadows512));
                    OnPropertyChanged(nameof(SetShadows1024));
                    OnPropertyChanged(nameof(SetShadows2048));
                    OnPropertyChanged(nameof(HasShadowChangeSelected));
                    OnPropertyChanged(nameof(HasActualShadowConversion));
                }
            }

            // Binding properties for UI radio buttons
            public bool KeepUnchanged
            {
                get => _selectedShadowOption == ShadowResolutionOption.Keep;
                set { if (value) SelectedShadowOption = ShadowResolutionOption.Keep; }
            }

            public bool SetShadowsOff
            {
                get => _selectedShadowOption == ShadowResolutionOption.Off;
                set { if (value) SelectedShadowOption = ShadowResolutionOption.Off; }
            }

            public bool SetShadows512
            {
                get => _selectedShadowOption == ShadowResolutionOption.Resolution512;
                set { if (value) SelectedShadowOption = ShadowResolutionOption.Resolution512; }
            }

            public bool SetShadows1024
            {
                get => _selectedShadowOption == ShadowResolutionOption.Resolution1024;
                set { if (value) SelectedShadowOption = ShadowResolutionOption.Resolution1024; }
            }

            public bool SetShadows2048
            {
                get => _selectedShadowOption == ShadowResolutionOption.Resolution2048;
                set { if (value) SelectedShadowOption = ShadowResolutionOption.Resolution2048; }
            }

            public bool CanSetShadows => true;
            public bool HasShadowChangeSelected => _selectedShadowOption != ShadowResolutionOption.Keep;
            
            /// <summary>
            /// Check if the selected shadow option is different from the current shadow setting
            /// </summary>
            public bool HasActualShadowConversion
            {
                get
                {
                    if (_selectedShadowOption == ShadowResolutionOption.Keep)
                        return false;
                    
                    // If shadows are off
                    if (!CastShadows)
                    {
                        // Only a conversion if we're NOT selecting Off
                        return _selectedShadowOption != ShadowResolutionOption.Off;
                    }
                    
                    // If shadows are on, check if selected resolution differs from current
                    if (_selectedShadowOption == ShadowResolutionOption.Off)
                        return true; // Turning shadows off is a conversion
                    
                    // Check if selected resolution matches current resolution
                    int selectedResolution = (int)_selectedShadowOption;
                    return selectedResolution != ShadowResolution;
                }
            }
            
            /// <summary>
            /// Auto-select the checkbox matching the light's current shadow setting
            /// </summary>
            private void SetDefaultShadowOption()
            {
                // Determine the appropriate default based on current shadow state
                if (!CastShadows)
                {
                    // If shadows are off, select Off checkbox
                    _selectedShadowOption = ShadowResolutionOption.Off;
                }
                else
                {
                    // If shadows are on, select the matching resolution
                    switch (ShadowResolution)
                    {
                        case 2048:
                            _selectedShadowOption = ShadowResolutionOption.Resolution2048;
                            break;
                        case 1024:
                            _selectedShadowOption = ShadowResolutionOption.Resolution1024;
                            break;
                        case 512:
                            _selectedShadowOption = ShadowResolutionOption.Resolution512;
                            break;
                        default:
                            // If unknown resolution, default to 1024
                            _selectedShadowOption = ShadowResolutionOption.Resolution1024;
                            break;
                    }
                }
                
                // Notify all related properties
                OnPropertyChanged(nameof(SelectedShadowOption));
                OnPropertyChanged(nameof(KeepUnchanged));
                OnPropertyChanged(nameof(SetShadowsOff));
                OnPropertyChanged(nameof(SetShadows512));
                OnPropertyChanged(nameof(SetShadows1024));
                OnPropertyChanged(nameof(SetShadows2048));
                OnPropertyChanged(nameof(HasShadowChangeSelected));
                OnPropertyChanged(nameof(HasActualShadowConversion));
            }

            public void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// Information about a mirror item
        /// </summary>
        public class MirrorInfo : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private bool _disable;
            private bool _enable;

            public string PackageName { get; set; }
            public string MirrorId { get; set; }
            public string MirrorName { get; set; }
            public string SceneFile { get; set; }
            public bool IsCurrentlyOn { get; set; }

            public bool Disable
            {
                get => _disable;
                set
                {
                    if (_disable == value) return;
                    _disable = value;
                    if (value) _enable = false; // Can't be both
                    OnPropertyChanged(nameof(Disable));
                    OnPropertyChanged(nameof(Enable));
                }
            }

            public bool Enable
            {
                get => _enable;
                set
                {
                    if (_enable == value) return;
                    _enable = value;
                    if (value) _disable = false; // Can't be both
                    OnPropertyChanged(nameof(Enable));
                    OnPropertyChanged(nameof(Disable));
                }
            }

            public string CurrentStatus => IsCurrentlyOn ? "On" : "Off";

            public void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// Hair curve density options
        /// </summary>
        public enum HairDensityOption
        {
            None = 0,
            Keep = -1,
            Density8 = 8,
            Density16 = 16,
            Density24 = 24,
            Density32 = 32
        }

        /// <summary>
        /// Information about a hair item
        /// </summary>
        public class HairInfo : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private int _curveDensity;
            private HairDensityOption _selectedDensityOption = HairDensityOption.None;

            public string PackageName { get; set; }
            public string HairId { get; set; }
            public string HairName { get; set; }
            public string SceneFile { get; set; }
            public bool HasCurveDensity { get; set; } // Track if curveDensity was originally present

            public int CurveDensity
            {
                get => _curveDensity;
                set
                {
                    _curveDensity = value;
                    OnPropertyChanged(nameof(CurveDensity));
                    OnPropertyChanged(nameof(CurveDensityFormatted));
                    
                    // Auto-select current density
                    UpdateDefaultSelection();
                }
            }

            public string CurveDensityFormatted => _curveDensity > 0 ? _curveDensity.ToString() : "-";

            public HairDensityOption SelectedDensityOption
            {
                get => _selectedDensityOption;
                set
                {
                    if (_selectedDensityOption == value) return;
                    _selectedDensityOption = value;
                    OnPropertyChanged(nameof(SelectedDensityOption));
                    OnPropertyChanged(nameof(KeepUnchanged));
                    OnPropertyChanged(nameof(ConvertTo32));
                    OnPropertyChanged(nameof(ConvertTo24));
                    OnPropertyChanged(nameof(ConvertTo16));
                    OnPropertyChanged(nameof(ConvertTo8));
                    OnPropertyChanged(nameof(HasConversionSelected));
                }
            }

            // Binding properties for UI radio buttons
            public bool ConvertTo32
            {
                get => _selectedDensityOption == HairDensityOption.Density32;
                set { if (value) SelectedDensityOption = HairDensityOption.Density32; }
            }

            public bool ConvertTo24
            {
                get => _selectedDensityOption == HairDensityOption.Density24;
                set { if (value) SelectedDensityOption = HairDensityOption.Density24; }
            }

            public bool ConvertTo16
            {
                get => _selectedDensityOption == HairDensityOption.Density16;
                set { if (value) SelectedDensityOption = HairDensityOption.Density16; }
            }

            public bool ConvertTo8
            {
                get => _selectedDensityOption == HairDensityOption.Density8;
                set { if (value) SelectedDensityOption = HairDensityOption.Density8; }
            }

            public bool KeepUnchanged
            {
                get => _selectedDensityOption == HairDensityOption.Keep;
                set { if (value) SelectedDensityOption = HairDensityOption.Keep; }
            }

            // Enable conversion options always (can write curveDensity even if not present)
            public bool CanConvertTo32 => true;
            public bool CanConvertTo24 => true;
            public bool CanConvertTo16 => true;
            public bool CanConvertTo8 => true;

            public bool HasConversionSelected => _selectedDensityOption != HairDensityOption.None && _selectedDensityOption != HairDensityOption.Keep;

            public bool HasActualConversion
            {
                get
                {
                    if (!HasConversionSelected) return false;
                    int targetDensity = ConvertTo32 ? 32 : ConvertTo24 ? 24 : ConvertTo16 ? 16 : ConvertTo8 ? 8 : 0;
                    return targetDensity != CurveDensity;
                }
            }

            private void UpdateDefaultSelection()
            {
                // Try to match current density if it's one of our standard options
                _selectedDensityOption = CurveDensity switch
                {
                    32 => HairDensityOption.Density32,
                    24 => HairDensityOption.Density24,
                    16 => HairDensityOption.Density16,
                    8 => HairDensityOption.Density8,
                    _ => HairDensityOption.Keep // Default to Keep for non-standard densities
                };
                
                // Notify all related properties
                OnPropertyChanged(nameof(SelectedDensityOption));
                OnPropertyChanged(nameof(KeepUnchanged));
                OnPropertyChanged(nameof(ConvertTo32));
                OnPropertyChanged(nameof(ConvertTo24));
                OnPropertyChanged(nameof(ConvertTo16));
                OnPropertyChanged(nameof(ConvertTo8));
                OnPropertyChanged(nameof(HasConversionSelected));
                OnPropertyChanged(nameof(HasActualConversion));
            }

            public void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// Result of hair optimization scan
        /// </summary>
        public class OptimizationResult
        {
            public List<HairInfo> HairItems { get; set; } = new List<HairInfo>();
            public List<MirrorInfo> MirrorItems { get; set; } = new List<MirrorInfo>();
            public List<LightInfo> LightItems { get; set; } = new List<LightInfo>();
            public string ErrorMessage { get; set; }
            public int TotalHairItems => HairItems.Count;
            public int TotalMirrorItems => MirrorItems.Count;
            public int TotalLightItems => LightItems.Count;
        }

        /// <summary>
        /// Scans a package for hair settings in scene JSON files
        /// </summary>
        public OptimizationResult ScanPackageHair(string packagePath)
        {
            var result = new OptimizationResult();

            try
            {
                bool isVarFile = packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase);

                if (isVarFile)
                {
                    try
                    {
                        using (var archive = SharpCompressHelper.OpenForRead(packagePath))
                        {
                            // Find all scene JSON files in Saves/scene/
                            var sceneFiles = archive.Entries
                                .Where(e => e.Key.StartsWith("Saves/scene/", StringComparison.OrdinalIgnoreCase) &&
                                           e.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            foreach (var sceneFile in sceneFiles)
                            {
                                ProcessSceneFile(sceneFile, result);
                            }
                        }
                    }
                    catch (Exception archiveEx)
                    {
                        result.ErrorMessage = $"Failed to open archive: {archiveEx.Message}";
                    }
                }
                else
                {
                    // Unarchived package
                    string scenePath = Path.Combine(packagePath, "Saves", "scene");
                    if (Directory.Exists(scenePath))
                    {
                        var sceneFiles = Directory.GetFiles(scenePath, "*.json", SearchOption.TopDirectoryOnly);
                        
                        foreach (var sceneFile in sceneFiles)
                        {
                            ProcessSceneFile(sceneFile, result);
                        }
                    }
                }
                
                // Only show if hair items were found
                if (result.HairItems.Count > 0)
                {
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error scanning package: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Processes a scene file (from archive entry)
        /// </summary>
        private void ProcessSceneFile(IArchiveEntry entry, OptimizationResult result)
        {
            try
            {
                using (var stream = entry.OpenEntryStream())
                using (var reader = new StreamReader(stream))
                {
                    string jsonContent = reader.ReadToEnd();
                    ParseHairFromJson(jsonContent, Path.GetFileName(entry.Key), result);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Processes a scene file (from file path)
        /// </summary>
        private void ProcessSceneFile(string filePath, OptimizationResult result)
        {
            try
            {
                string jsonContent = File.ReadAllText(filePath);
                ParseHairFromJson(jsonContent, Path.GetFileName(filePath), result);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Parses hair data from scene JSON content
        /// </summary>
        private void ParseHairFromJson(string jsonContent, string sceneFileName, OptimizationResult result)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;

                    // Look for "atoms" array
                    if (!root.TryGetProperty("atoms", out var atoms))
                    {
                        return;
                    }

                    int atomCount = 0;
                    int atomsWithHair = 0;
                    foreach (var atom in atoms.EnumerateArray())
                    {
                        atomCount++;
                        
                        // Check if this atom is a ReflectiveSlate (mirror)
                        if (atom.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "ReflectiveSlate")
                        {
                            string mirrorId = atom.TryGetProperty("id", out var idProp) ? idProp.GetString() : "Unknown";
                            bool isOn = atom.TryGetProperty("on", out var onProp) && onProp.GetString() == "true";
                            
                            var mirrorInfo = new MirrorInfo
                            {
                                MirrorId = mirrorId,
                                MirrorName = mirrorId, // Use ID as name
                                SceneFile = sceneFileName,
                                IsCurrentlyOn = isOn,
                                Disable = false // Default to keeping mirrors On (not disabling)
                            };
                            
                            result.MirrorItems.Add(mirrorInfo);
                        }
                        
                        // Check if this atom is a light (InvisibleLight, SpotLight, or other light types)
                        if (atom.TryGetProperty("type", out var lightTypeProp))
                        {
                            string lightType = lightTypeProp.GetString();
                            // Check for all light types - VAM uses various light atom types
                            bool isLightAtom = lightType == "InvisibleLight" || 
                                             lightType == "SpotLight" || 
                                             lightType == "PointLight" ||
                                             lightType == "DirectionalLight" ||
                                             lightType == "AreaLight" ||
                                             lightType == "Light" ||
                                             lightType.Contains("Light"); // Catch any other light variations
                            
                            if (isLightAtom)
                            {
                                string lightId = atom.TryGetProperty("id", out var lightIdProp) ? lightIdProp.GetString() : "Unknown";
                                
                                // Parse shadow settings from storables
                                bool castShadows = false;
                                int shadowResolution = 0;
                                
                                if (atom.TryGetProperty("storables", out var lightStorables))
                                {
                                    foreach (var storable in lightStorables.EnumerateArray())
                                    {
                                        if (storable.TryGetProperty("id", out var storableId))
                                        {
                                            string storableIdStr = storableId.GetString();
                                            
                                            if (storableIdStr == "Light")
                                            {
                                                // Check shadowsOn property (VAM uses this instead of castShadows)
                                                if (storable.TryGetProperty("shadowsOn", out var shadowsOnProp))
                                                {
                                                    if (shadowsOnProp.ValueKind == JsonValueKind.String)
                                                    {
                                                        string val = shadowsOnProp.GetString();
                                                        castShadows = val == "true";
                                                    }
                                                    else if (shadowsOnProp.ValueKind == JsonValueKind.True)
                                                    {
                                                        castShadows = true;
                                                    }
                                                    else if (shadowsOnProp.ValueKind == JsonValueKind.False)
                                                    {
                                                        castShadows = false;
                                                    }
                                                }
                                                
                                                // Check shadowResolution property (VAM uses text values like "VeryHigh", "High", etc.)
                                                if (storable.TryGetProperty("shadowResolution", out var shadowResProp))
                                                {
                                                    if (shadowResProp.ValueKind == JsonValueKind.String)
                                                    {
                                                        string val = shadowResProp.GetString();
                                                        // Map VAM's text values to numeric resolutions
                                                        shadowResolution = val switch
                                                        {
                                                            "VeryHigh" => 2048,
                                                            "High" => 1024,
                                                            "Medium" => 512,
                                                            "Low" => 256,
                                                            _ => 0
                                                        };
                                                        // If shadowResolution is set, shadows are on
                                                        if (shadowResolution > 0)
                                                        {
                                                            castShadows = true;
                                                        }
                                                    }
                                                    else if (shadowResProp.ValueKind == JsonValueKind.Number)
                                                    {
                                                        shadowResolution = shadowResProp.GetInt32();
                                                        if (shadowResolution > 0)
                                                        {
                                                            castShadows = true;
                                                        }
                                                    }
                                                }
                                                
                                                break;
                                            }
                                        }
                                    }
                                }
                                
                                var lightInfo = new LightInfo
                                {
                                    LightId = lightId,
                                    LightName = lightId,
                                    LightType = lightType,
                                    SceneFile = sceneFileName,
                                    CastShadows = castShadows,
                                    ShadowResolution = shadowResolution
                                    // SelectedShadowOption will be auto-set to match current shadow setting by SetDefaultShadowOption()
                                };
                                
                                result.LightItems.Add(lightInfo);
                            }
                        }
                        
                        // First, get all hair items from the "hair" array inside the "geometry" storable
                        var validHairIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var hairNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        
                        // Look for the geometry storable which contains the hair array
                        JsonElement hairArray = default;
                        bool foundHairArray = false;
                        
                        if (atom.TryGetProperty("storables", out var storables))
                        {
                            foreach (var storable in storables.EnumerateArray())
                            {
                                if (storable.TryGetProperty("id", out var storableId) && 
                                    storableId.GetString() == "geometry" &&
                                    storable.TryGetProperty("hair", out hairArray))
                                {
                                    foundHairArray = true;
                                    break;
                                }
                            }
                        }
                        
                        if (foundHairArray)
                        {
                            atomsWithHair++;
                            foreach (var hairItem in hairArray.EnumerateArray())
                            {
                                if (hairItem.TryGetProperty("internalId", out var internalId))
                                {
                                    string id = internalId.GetString();
                                    validHairIds.Add(id);
                                    
                                    string name = id;
                                    // Try to get a friendly name from the id property
                                    if (hairItem.TryGetProperty("id", out var idProp))
                                    {
                                        string fullId = idProp.GetString();
                                        // Extract filename from path like "SELF:/Custom/Hair/Female/Ramsess/ramhair/ramhair_07reA.vam"
                                        if (fullId.Contains("/"))
                                        {
                                            name = Path.GetFileNameWithoutExtension(fullId.Split('/').Last());
                                        }
                                    }
                                    
                                    hairNameMap[id] = name;
                                }
                            }
                        }

                        // If no hair array found, skip this atom
                        if (validHairIds.Count == 0)
                            continue;

                        // Now search through storables to find settings for these hair items
                        if (!atom.TryGetProperty("storables", out var atomStorables))
                        {
                            continue;
                        }

                        int matchCount = 0;
                        foreach (var storable in atomStorables.EnumerateArray())
                        {
                            if (!storable.TryGetProperty("id", out var storableId))
                                continue;

                            string id = storableId.GetString();
                            
                            // Look for hair sim settings (ends with "Sim")
                            if (!id.EndsWith("Sim", StringComparison.OrdinalIgnoreCase))
                                continue;
                            
                            // Extract base hair ID (remove "Sim" suffix)
                            string baseId = id.EndsWith("Sim", StringComparison.OrdinalIgnoreCase) ? 
                                id.Substring(0, id.Length - 3) : id;
                            
                            // Only process if this is a valid hair item from the hair array
                            if (!validHairIds.Contains(baseId))
                                continue;
                            
                            matchCount++;

                            // Parse curveDensity value (may not exist) - it's a direct property
                            int curveDensity = 0;
                            if (storable.TryGetProperty("curveDensity", out var curveDensityProp))
                            {
                                if (curveDensityProp.ValueKind == JsonValueKind.String)
                                {
                                    if (int.TryParse(curveDensityProp.GetString(), out int parsed))
                                        curveDensity = parsed;
                                }
                                else if (curveDensityProp.ValueKind == JsonValueKind.Number)
                                {
                                    curveDensity = curveDensityProp.GetInt32();
                                }
                            }

                            // Get friendly name
                            string hairName = hairNameMap.ContainsKey(baseId) ? hairNameMap[baseId] : baseId;

                            var hairInfo = new HairInfo
                            {
                                HairId = id,
                                HairName = hairName,
                                SceneFile = sceneFileName,
                                CurveDensity = curveDensity,  // Will be 0 if not set
                                HasCurveDensity = curveDensity > 0
                            };

                            result.HairItems.Add(hairInfo);
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}

