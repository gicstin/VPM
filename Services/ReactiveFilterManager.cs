using System;
using System.Collections.Generic;
using System.Linq;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Reactive filter manager that maintains cached counts and updates them live
    /// </summary>
    public class ReactiveFilterManager
    {
        private readonly FilterManager _filterManager;
        private readonly object _cacheLock = new object();
        private Dictionary<string, VarMetadata> _allPackages;
        
        // Cached counts - updated incrementally
        private Dictionary<string, int> _creatorCounts;
        private Dictionary<string, int> _categoryCounts;
        private Dictionary<string, int> _statusCounts;
        private Dictionary<string, int> _licenseCounts;
        private Dictionary<string, int> _fileSizeCounts;
        private Dictionary<string, int> _optimizationCounts;
        
        // Filtered package cache
        private Dictionary<string, VarMetadata> _filteredPackages;
        private bool _countsNeedUpdate = true;
        
        public ReactiveFilterManager(FilterManager filterManager)
        {
            _filterManager = filterManager ?? throw new ArgumentNullException(nameof(filterManager));
        }
        
        /// <summary>
        /// Initialize with all packages - call this once when packages are loaded
        /// </summary>
        public void Initialize(Dictionary<string, VarMetadata> allPackages)
        {
            if (allPackages == null) throw new ArgumentNullException(nameof(allPackages));
            
            lock (_cacheLock)
            {
                _allPackages = allPackages;
                _countsNeedUpdate = true;
                _filteredPackages = null;
            }
        }
        
        /// <summary>
        /// Get filtered packages based on current filter state
        /// </summary>
        public Dictionary<string, VarMetadata> GetFilteredPackages()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _filteredPackages == null)
                {
                    UpdateFilteredPackagesLocked();
                }
                // Return a reference to the cached dictionary (safe because it's only read)
                return _filteredPackages;
            }
        }
        
        /// <summary>
        /// Update filtered packages - must be called within lock
        /// </summary>
        private void UpdateFilteredPackagesLocked()
        {
            _filteredPackages = new Dictionary<string, VarMetadata>();
            
            if (_allPackages == null)
            {
                _countsNeedUpdate = false;
                return;
            }
            
            // MEMORY FIX: Create snapshot ONCE before the loop instead of per-package
            // This prevents creating 10+ new HashSets for every single package
            var filterSnapshot = _filterManager.GetSnapshot();
            
            foreach (var kvp in _allPackages)
            {
                if (_filterManager.MatchesFilters(kvp.Value, filterSnapshot, kvp.Key))
                {
                    _filteredPackages.Add(kvp.Key, kvp.Value);
                }
            }
            
            _countsNeedUpdate = false;
        }
        
        /// <summary>
        /// Mark counts as needing update - call this when filters change
        /// </summary>
        public void InvalidateCounts()
        {
            lock (_cacheLock)
            {
                _countsNeedUpdate = true;
            }
        }
        
        /// <summary>
        /// Get creator counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetCreatorCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _creatorCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _creatorCounts;
            }
        }
        
        /// <summary>
        /// Get category counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetCategoryCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _categoryCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _categoryCounts;
            }
        }
        
        /// <summary>
        /// Get status counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetStatusCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _statusCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _statusCounts;
            }
        }
        
        /// <summary>
        /// Get license counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetLicenseCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _licenseCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _licenseCounts;
            }
        }
        
        /// <summary>
        /// Get file size counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetFileSizeCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _fileSizeCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _fileSizeCounts;
            }
        }
        
        /// <summary>
        /// Get optimization status counts based on current filter state (cascade mode)
        /// </summary>
        public Dictionary<string, int> GetOptimizationCounts()
        {
            lock (_cacheLock)
            {
                if (_countsNeedUpdate || _optimizationCounts == null)
                {
                    UpdateAllCountsLocked();
                }
                return _optimizationCounts;
            }
        }
        
        /// <summary>
        /// Update all counts - must be called within lock
        /// </summary>
        private void UpdateAllCountsLocked()
        {
            // Ensure filtered packages are up to date
            if (_countsNeedUpdate || _filteredPackages == null)
            {
                UpdateFilteredPackagesLocked();
            }
            
            // Update all counts from the filtered packages
            _creatorCounts = _filterManager.GetCreatorCounts(_filteredPackages);
            _categoryCounts = _filterManager.GetCategoryCounts(_filteredPackages);
            _statusCounts = _filterManager.GetStatusCounts(_filteredPackages);
            _licenseCounts = _filterManager.GetLicenseCounts(_filteredPackages);
            _fileSizeCounts = _filterManager.GetFileSizeCounts(_filteredPackages);
            _optimizationCounts = _filterManager.GetOptimizationStatusCounts(_filteredPackages);
        }
    }
    
    public enum FilterType
    {
        Creator,
        Category,
        Status,
        License,
        FileSize,
        Optimization,
        Date
    }
}

