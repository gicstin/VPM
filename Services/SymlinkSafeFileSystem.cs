using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPM.Services
{
    public static class SymlinkSafeFileSystem
    {
        /// <summary>
        /// Resolves a path to its final physical location, following all symlinks.
        /// Optimized to minimize allocations for non-reparse points.
        /// </summary>
        public static string GetRealPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            
            try
            {
                // Quick check for reparse point before doing expensive resolution
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                {
                    FileSystemInfo info = attr.HasFlag(FileAttributes.Directory) 
                        ? new DirectoryInfo(path) 
                        : new FileInfo(path);
                    
                    // Resolve to the final target (true means follow entire chain)
                    return info.ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
                }
            }
            catch
            {
                // Fallback if attributes can't be read
            }
            
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Checks if a path is a symbolic link or junction that has a valid link target.
        /// </summary>
        public static bool IsSymlink(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                if (!attr.HasFlag(FileAttributes.ReparsePoint)) return false;

                // Ensure it's actually a link and not another type of reparse point (like OneDrive placeholder)
                FileSystemInfo info = attr.HasFlag(FileAttributes.Directory) 
                    ? new DirectoryInfo(path) 
                    : new FileInfo(path);
                
                return info.LinkTarget != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Enumerates files safely by avoiding infinite loops caused by circular directory symlinks.
        /// </summary>
        public static IEnumerable<string> EnumerateFilesSafe(string rootPath, string searchPattern, bool recursive = true)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) yield break;

            var pending = new Stack<string>();
            pending.Push(rootPath);
            var visitedDirectoryRealPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string realPath = GetRealPath(current);

                if (!visitedDirectoryRealPaths.Add(realPath))
                {
                    // Already visited this physical directory through another path, skip recursion
                    continue;
                }

                // Enumerate files in current directory
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    // Skip inaccessible directories
                    continue;
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                if (recursive)
                {
                    // Enumerate subdirectories
                    IEnumerable<string> directories;
                    try
                    {
                        directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var dir in directories)
                    {
                        pending.Push(dir);
                    }
                }
            }
        }

        /// <summary>
        /// Safely moves a file or directory, preserving symlinks if both source and destination are on the same volume.
        /// If cross-volume, it recreates the link at the destination, resolving relative targets to absolute paths.
        /// </summary>
        public static void MoveFileSafe(string sourcePath, string destinationPath)
        {
            try
            {
                var attr = File.GetAttributes(sourcePath);
                bool isDirectory = attr.HasFlag(FileAttributes.Directory);
                bool isReparsePoint = attr.HasFlag(FileAttributes.ReparsePoint);

                FileSystemInfo sourceInfo = isDirectory ? new DirectoryInfo(sourcePath) : new FileInfo(sourcePath);
                string linkTarget = sourceInfo.LinkTarget;

                if (isReparsePoint && linkTarget != null)
                {
                    bool sameVolume = false;
                    try
                    {
                        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
                        var destRoot = Path.GetPathRoot(Path.GetFullPath(destinationPath));
                        sameVolume = string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }

                    if (sameVolume)
                    {
                        SafeDelete(destinationPath);
                        if (isDirectory) Directory.Move(sourcePath, destinationPath);
                        else File.Move(sourcePath, destinationPath);
                    }
                    else
                    {
                        // Cross-volume move of a link
                        // IMPORTANT: Resolve relative targets to absolute paths to prevent broken links
                        var resolvedTarget = sourceInfo.ResolveLinkTarget(false)?.FullName ?? linkTarget;

                        SafeDelete(destinationPath);
                        if (isDirectory) Directory.CreateSymbolicLink(destinationPath, resolvedTarget);
                        else File.CreateSymbolicLink(destinationPath, resolvedTarget);
                        
                        SafeDelete(sourcePath);
                    }
                }
                else
                {
                    SafeDelete(destinationPath);
                    if (isDirectory) Directory.Move(sourcePath, destinationPath);
                    else File.Move(sourcePath, destinationPath);
                }
            }
            catch (IOException)
            {
                // Rethrow IO exceptions as they might be due to locking or permissions
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to move '{sourcePath}' to '{destinationPath}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Safely deletes a file or directory link without deleting the target content of a junction/symlink.
        /// </summary>
        public static void SafeDelete(string path)
        {
            if (Directory.Exists(path))
            {
                // Directory.Delete on a junction or directory symlink only removes the link point on Windows
                Directory.Delete(path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
