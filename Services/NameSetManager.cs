using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>Set of names persisted to a file with shadow-file support. Shared by FavoritesManager.</summary>
    public abstract class NameSetManager : IDisposable
    {
        protected readonly string _mainFilePath;
        protected readonly string _shadowFilePath;
        protected HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        protected HashSet<string> _shadowAdditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        protected HashSet<string> _shadowRemovals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        protected bool _isLoaded = false;
        protected DateTime _lastMainFileWriteTime = DateTime.MinValue;
        protected FileSystemWatcher _fileWatcher;
        private bool _mainFileBackedUp;
        private bool _shadowFileBackedUp;

        public event EventHandler Changed;

        protected NameSetManager(string mainFilePath, string shadowFilePath)
        {
            _mainFilePath = mainFilePath;
            _shadowFilePath = shadowFilePath;
            SetupFileWatcher();
        }

        private void SetupFileWatcher()
        {
            try
            {
                string directory = Path.GetDirectoryName(_mainFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _fileWatcher = new FileSystemWatcher(directory)
                {
                    Filter = Path.GetFileName(_mainFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnFileChanged;
            }
            catch (Exception)
            {
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            _ = OnFileChangedAsync();
        }

        private async Task OnFileChangedAsync()
        {
            try
            {
                var currentWriteTime = File.GetLastWriteTime(_mainFilePath);
                if (currentWriteTime <= _lastMainFileWriteTime)
                    return;

                _lastMainFileWriteTime = currentWriteTime;
                await Task.Delay(100).ConfigureAwait(false);

                Reload();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
        }

        public bool Contains(string name)
        {
            if (!_isLoaded)
                Load();

            return _names.Contains(name);
        }

        public void Add(string name)
        {
            if (!_isLoaded)
                Load();

            if (_names.Add(name))
            {
                Save();
            }
        }

        public void Remove(string name)
        {
            if (!_isLoaded)
                Load();

            if (_names.Remove(name))
            {
                Save();
            }
        }

        public void AddBatch(IEnumerable<string> names)
        {
            if (!_isLoaded)
                Load();

            bool changed = false;
            foreach (var name in names)
            {
                if (_names.Add(name))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }
        }

        public void RemoveBatch(IEnumerable<string> names)
        {
            if (!_isLoaded)
                Load();

            bool changed = false;
            foreach (var name in names)
            {
                if (_names.Remove(name))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }
        }

        public void Toggle(string name)
        {
            if (Contains(name))
                Remove(name);
            else
                Add(name);
        }

        public HashSet<string> GetAll()
        {
            if (!_isLoaded)
                Load();

            return new HashSet<string>(_names, StringComparer.OrdinalIgnoreCase);
        }

        public void Load()
        {
            _names.Clear();
            _shadowAdditions.Clear();
            _shadowRemovals.Clear();

            try
            {
                if (File.Exists(_mainFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_mainFilePath);
                        var names = DeserializeMainFile(json);
                        if (names != null)
                        {
                            foreach (var name in names)
                            {
                                _names.Add(name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to deserialize main file: {ex.Message}");
                    }
                }

                if (File.Exists(_shadowFilePath))
                {
                    try
                    {
                        string shadowJson = File.ReadAllText(_shadowFilePath);
                        var (additions, removals) = DeserializeShadowFile(shadowJson);

                        if (additions != null)
                        {
                            foreach (var name in additions)
                            {
                                _names.Add(name);
                                _shadowAdditions.Add(name);
                            }
                        }

                        if (removals != null)
                        {
                            foreach (var name in removals)
                            {
                                _names.Remove(name);
                                _shadowRemovals.Add(name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to deserialize shadow file: {ex.Message}");
                    }
                }

                _isLoaded = true;
            }
            catch (Exception)
            {
                _isLoaded = true;
            }
        }

        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(_mainFilePath);
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

        private void EnsureBackupIfNeeded(string filePath, ref bool hasBackedUp)
        {
            if (hasBackedUp)
                return;

            try
            {
                if (!File.Exists(filePath))
                {
                    hasBackedUp = true;
                    return;
                }

                string directory = Path.GetDirectoryName(filePath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                string extension = Path.GetExtension(filePath);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(directory, $"{fileNameWithoutExtension}.bak.{timestamp}{extension}");

                File.Copy(filePath, backupPath, overwrite: false);
                hasBackedUp = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create backup for '{filePath}': {ex.Message}");
            }
        }

        private bool TryWriteToMainFile()
        {
            try
            {
                EnsureBackupIfNeeded(_mainFilePath, ref _mainFileBackedUp);
                var sortedNames = _names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                string json = SerializeMainFile(sortedNames);

                using (var fileStream = new FileStream(_mainFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write main file: {ex.Message}");
                return false;
            }
        }

        private void WriteShadowFile()
        {
            try
            {
                EnsureBackupIfNeeded(_shadowFilePath, ref _shadowFileBackedUp);
                var mainNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_mainFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_mainFilePath);
                        var names = DeserializeMainFile(json);
                        if (names != null)
                        {
                            foreach (var name in names)
                            {
                                mainNames.Add(name);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                var additions = new HashSet<string>(_names.Except(mainNames), StringComparer.OrdinalIgnoreCase);
                var removals = new HashSet<string>(mainNames.Except(_names), StringComparer.OrdinalIgnoreCase);

                additions.UnionWith(_shadowAdditions);
                removals.UnionWith(_shadowRemovals);

                var sortedAdditions = additions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var sortedRemovals = removals.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

                string shadowJson = SerializeShadowFile(sortedAdditions, sortedRemovals);
                File.WriteAllText(_shadowFilePath, shadowJson);

                _shadowAdditions = additions;
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
                    EnsureBackupIfNeeded(_shadowFilePath, ref _shadowFileBackedUp);
                    File.Delete(_shadowFilePath);
                    _shadowAdditions.Clear();
                    _shadowRemovals.Clear();
                }
            }
            catch (Exception)
            {
            }
        }

        public void Reload()
        {
            _isLoaded = false;
            Load();
        }

        // Abstract methods for JSON serialization - implemented by derived classes
        protected abstract List<string> DeserializeMainFile(string json);
        protected abstract (List<string> additions, List<string> removals) DeserializeShadowFile(string json);
        protected abstract string SerializeMainFile(List<string> names);
        protected abstract string SerializeShadowFile(List<string> additions, List<string> removals);
    }
}
