using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPM.Models;

namespace VPM.Services
{
    public class AutoInstallManager
    {
        private readonly string _autoInstallFilePath;
        private readonly string _shadowFilePath;
        private HashSet<string> _autoInstallNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _shadowChanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _shadowRemovals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isLoaded = false;
        private DateTime _lastMainFileWriteTime = DateTime.MinValue;
        private FileSystemWatcher _fileWatcher;

        public event EventHandler AutoInstallChanged;

        public AutoInstallManager(string vamFolderPath)
        {
            _autoInstallFilePath = Path.Combine(vamFolderPath, "Custom", "PluginData", "sfishere", "AutoInstall.txt");
            _shadowFilePath = Path.Combine(vamFolderPath, "Custom", "PluginData", "sfishere", "AutoInstall.shadow.txt");
            
            SetupFileWatcher();
        }

        private void SetupFileWatcher()
        {
            try
            {
                string directory = Path.GetDirectoryName(_autoInstallFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _fileWatcher = new FileSystemWatcher(directory)
                {
                    Filter = Path.GetFileName(_autoInstallFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnAutoInstallFileChanged;
            }
            catch (Exception)
            {
            }
        }

        private void OnAutoInstallFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                var currentWriteTime = File.GetLastWriteTime(_autoInstallFilePath);
                if (currentWriteTime <= _lastMainFileWriteTime)
                    return;

                _lastMainFileWriteTime = currentWriteTime;

                System.Threading.Thread.Sleep(100);

                ReloadAutoInstall();

                AutoInstallChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
        }

        public bool IsAutoInstall(string packageName)
        {
            if (!_isLoaded)
                LoadAutoInstall();

            return _autoInstallNames.Contains(packageName);
        }

        public void AddAutoInstall(string packageName)
        {
            if (!_isLoaded)
                LoadAutoInstall();

            if (_autoInstallNames.Add(packageName))
            {
                SaveAutoInstall();
            }
        }

        public void RemoveAutoInstall(string packageName)
        {
            if (!_isLoaded)
                LoadAutoInstall();

            if (_autoInstallNames.Remove(packageName))
            {
                SaveAutoInstall();
            }
        }

        public void AddAutoInstallBatch(IEnumerable<string> packageNames)
        {
            if (!_isLoaded)
                LoadAutoInstall();

            bool changed = false;
            foreach (var packageName in packageNames)
            {
                if (_autoInstallNames.Add(packageName))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveAutoInstall();
            }
        }

        public void RemoveAutoInstallBatch(IEnumerable<string> packageNames)
        {
            if (!_isLoaded)
                LoadAutoInstall();

            bool changed = false;
            foreach (var packageName in packageNames)
            {
                if (_autoInstallNames.Remove(packageName))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveAutoInstall();
            }
        }

        public void ToggleAutoInstall(string packageName)
        {
            if (IsAutoInstall(packageName))
                RemoveAutoInstall(packageName);
            else
                AddAutoInstall(packageName);
        }

        public HashSet<string> GetAllAutoInstall()
        {
            if (!_isLoaded)
                LoadAutoInstall();

            return new HashSet<string>(_autoInstallNames, StringComparer.OrdinalIgnoreCase);
        }

        public void LoadAutoInstall()
        {
            _autoInstallNames.Clear();
            _shadowChanges.Clear();
            _shadowRemovals.Clear();
            _isLoaded = true;

            try
            {
                if (File.Exists(_autoInstallFilePath))
                {
                    string json = File.ReadAllText(_autoInstallFilePath);
                    var data = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AutoInstallData);

                    if (data?.Names != null)
                    {
                        foreach (var name in data.Names)
                        {
                            _autoInstallNames.Add(name);
                        }
                    }
                }

                if (File.Exists(_shadowFilePath))
                {
                    string shadowJson = File.ReadAllText(_shadowFilePath);
                    var shadowData = JsonSerializer.Deserialize(shadowJson, JsonSourceGenerationContext.Default.ShadowAutoInstallData);

                    if (shadowData != null)
                    {
                        if (shadowData.Additions != null)
                        {
                            foreach (var name in shadowData.Additions)
                            {
                                _autoInstallNames.Add(name);
                                _shadowChanges.Add(name);
                            }
                        }

                        if (shadowData.Removals != null)
                        {
                            foreach (var name in shadowData.Removals)
                            {
                                _autoInstallNames.Remove(name);
                                _shadowRemovals.Add(name);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void SaveAutoInstall()
        {
            try
            {
                string directory = Path.GetDirectoryName(_autoInstallFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (TryWriteToMainFile())
                {
                    TryMergeShadowFile();
                }
                else
                {
                    WriteShadowFile();
                }
            }
            catch (Exception)
            {
            }
        }

        private bool TryWriteToMainFile()
        {
            try
            {
                var data = new AutoInstallData
                {
                    Names = _autoInstallNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                };

                string json = JsonSerializer.Serialize(data, JsonSourceGenerationContext.Default.AutoInstallData);
                
                using (var fileStream = new FileStream(_autoInstallFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.Write(json);
                }
                
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WriteShadowFile()
        {
            try
            {
                var mainAutoInstall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_autoInstallFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_autoInstallFilePath);
                        var data = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AutoInstallData);
                        if (data?.Names != null)
                        {
                            foreach (var name in data.Names)
                            {
                                mainAutoInstall.Add(name);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                var additions = new HashSet<string>(_autoInstallNames.Except(mainAutoInstall), StringComparer.OrdinalIgnoreCase);
                var removals = new HashSet<string>(mainAutoInstall.Except(_autoInstallNames), StringComparer.OrdinalIgnoreCase);

                additions.UnionWith(_shadowChanges);
                removals.UnionWith(_shadowRemovals);

                var shadowData = new ShadowAutoInstallData
                {
                    Additions = additions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    Removals = removals.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                };

                string shadowJson = JsonSerializer.Serialize(shadowData, JsonSourceGenerationContext.Default.ShadowAutoInstallData);
                File.WriteAllText(_shadowFilePath, shadowJson);

                _shadowChanges = additions;
                _shadowRemovals = removals;
            }
            catch (Exception)
            {
            }
        }

        private void TryMergeShadowFile()
        {
            try
            {
                if (File.Exists(_shadowFilePath))
                {
                    File.Delete(_shadowFilePath);
                    _shadowChanges.Clear();
                    _shadowRemovals.Clear();
                }
            }
            catch (Exception)
            {
            }
        }

        public void ReloadAutoInstall()
        {
            _isLoaded = false;
            LoadAutoInstall();
        }
    }
}

