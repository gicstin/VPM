using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace VPM.Services
{
    /// <summary>
    /// Service for handling file renaming operations for packages, scenes, and presets
    /// Handles renaming of source files and all related files (.fav, .hide, image previews, etc.)
    /// </summary>
    public class FileRenamingService
    {
        private readonly string _pendingDeletionsFile;
        
        public FileRenamingService()
        {
            // File to store paths of files that need to be deleted on restart
            _pendingDeletionsFile = Path.Combine(Path.GetTempPath(), "VPM_PendingDeletions.txt");
        }

        /// <summary>
        /// Renames a package file and all its related files
        /// </summary>
        /// <param name="originalFilePath">Original file path (e.g., Package1.json)</param>
        /// <param name="newName">New name without extension (e.g., "package2")</param>
        /// <returns>New file path if successful, null if failed</returns>
        public async Task<string> RenamePackageAsync(string originalFilePath, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(originalFilePath) || !File.Exists(originalFilePath))
                    throw new ArgumentException("Original file does not exist");

                if (string.IsNullOrWhiteSpace(newName))
                    throw new ArgumentException("New name cannot be empty");

                // Validate new name
                if (!IsValidFileName(newName))
                    throw new ArgumentException("Invalid file name. File names cannot contain: \\ / : * ? \" < > |");

                var directory = Path.GetDirectoryName(originalFilePath);
                var extension = Path.GetExtension(originalFilePath);
                var originalNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFilePath);
                
                // Create new file path
                var newFilePath = Path.Combine(directory, newName + extension);
                
                // Check if target file already exists
                if (File.Exists(newFilePath))
                    throw new InvalidOperationException($"A file with the name '{newName}{extension}' already exists");

                // Rename the main file
                File.Move(originalFilePath, newFilePath);

                // Rename related files
                await RenameRelatedFilesAsync(directory, originalNameWithoutExtension, newName);

                return newFilePath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to rename package: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Renames a scene file and all its related files
        /// </summary>
        /// <param name="originalFilePath">Original scene file path (e.g., Scene1.json)</param>
        /// <param name="newName">New name without extension (e.g., "scene2")</param>
        /// <returns>New file path if successful, null if failed</returns>
        public async Task<string> RenameSceneAsync(string originalFilePath, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(originalFilePath) || !File.Exists(originalFilePath))
                    throw new ArgumentException("Original scene file does not exist");

                if (string.IsNullOrWhiteSpace(newName))
                    throw new ArgumentException("New name cannot be empty");

                // Validate new name
                if (!IsValidFileName(newName))
                    throw new ArgumentException("Invalid file name. File names cannot contain: \\ / : * ? \" < > |");

                var directory = Path.GetDirectoryName(originalFilePath);
                var extension = Path.GetExtension(originalFilePath);
                var originalNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFilePath);
                
                // Create new file path
                var newFilePath = Path.Combine(directory, newName + extension);
                
                // Check if target file already exists
                if (File.Exists(newFilePath))
                    throw new InvalidOperationException($"A scene with the name '{newName}{extension}' already exists");

                // Rename the main file
                File.Move(originalFilePath, newFilePath);

                // Rename related files
                await RenameRelatedFilesAsync(directory, originalNameWithoutExtension, newName);

                return newFilePath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to rename scene: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Renames a preset file (.vap) and all its related files
        /// </summary>
        /// <param name="originalFilePath">Original preset file path (e.g., Preset1.vap)</param>
        /// <param name="newName">New name without extension (e.g., "preset2")</param>
        /// <returns>New file path if successful, null if failed</returns>
        public async Task<string> RenamePresetAsync(string originalFilePath, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(originalFilePath) || !File.Exists(originalFilePath))
                    throw new ArgumentException("Original preset file does not exist");

                if (string.IsNullOrWhiteSpace(newName))
                    throw new ArgumentException("New name cannot be empty");

                // Validate new name
                if (!IsValidFileName(newName))
                    throw new ArgumentException("Invalid file name. File names cannot contain: \\ / : * ? \" < > |");

                var directory = Path.GetDirectoryName(originalFilePath);
                var extension = Path.GetExtension(originalFilePath);
                var originalNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFilePath);
                
                // Create new file path
                var newFilePath = Path.Combine(directory, newName + extension);
                
                // Check if target file already exists
                if (File.Exists(newFilePath))
                    throw new InvalidOperationException($"A preset with the name '{newName}{extension}' already exists");

                // Rename the main file
                File.Move(originalFilePath, newFilePath);

                // Rename related files (for presets, this includes .vap.fav, .vap.hide, and image previews)
                await RenameRelatedFilesAsync(directory, originalNameWithoutExtension, newName);

                return newFilePath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to rename preset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Renames all related files (.fav, .hide, image previews, etc.)
        /// </summary>
        private async Task RenameRelatedFilesAsync(string directory, string originalName, string newName)
        {
            var relatedExtensions = new[] { ".fav", ".hide" };
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            // Rename .fav and .hide files
            foreach (var ext in relatedExtensions)
            {
                var originalRelatedFile = Path.Combine(directory, originalName + ext);
                
                if (File.Exists(originalRelatedFile))
                {
                    var newRelatedFile = Path.Combine(directory, newName + ext);
                    File.Move(originalRelatedFile, newRelatedFile);
                }

                // Also check for compound extensions like .vap.fav, .json.fav
                var pattern = originalName + "*" + ext;
                var compoundFiles = Directory.GetFiles(directory, pattern);
                
                foreach (var compoundFile in compoundFiles)
                {
                    var fileName = Path.GetFileName(compoundFile);
                    var newFileName = fileName.Replace(originalName, newName);
                    var newCompoundFile = Path.Combine(directory, newFileName);
                    
                    if (!File.Exists(newCompoundFile))
                    {
                        File.Move(compoundFile, newCompoundFile);
                    }
                }
            }

            // Handle image preview files (special case - copy then mark for deletion)
            foreach (var imgExt in imageExtensions)
            {
                var originalImageFile = Path.Combine(directory, originalName + imgExt);
                
                if (File.Exists(originalImageFile))
                {
                    var newImageFile = Path.Combine(directory, newName + imgExt);
                    
                    // Copy the image file with the new name
                    File.Copy(originalImageFile, newImageFile, true);
                    
                    // Mark the original image file for deletion on restart
                    await MarkFileForDeletionOnRestartAsync(originalImageFile);
                    break; // Only process the first matching image extension
                }
            }
        }

        /// <summary>
        /// Marks a file for deletion on application restart
        /// This is needed for image files that might be in use by the application
        /// </summary>
        private async Task MarkFileForDeletionOnRestartAsync(string filePath)
        {
            try
            {
                // Append the file path to the pending deletions file
                await File.AppendAllTextAsync(_pendingDeletionsFile, filePath + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Log the error but don't fail the rename operation
                System.Diagnostics.Debug.WriteLine($"Failed to mark file for deletion: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes pending file deletions (should be called on application startup)
        /// </summary>
        public async Task ProcessPendingDeletionsAsync()
        {
            try
            {
                if (!File.Exists(_pendingDeletionsFile))
                    return;

                var filesToDelete = await File.ReadAllLinesAsync(_pendingDeletionsFile);
                var deletedFiles = new List<string>();

                foreach (var filePath in filesToDelete)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        continue;

                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            deletedFiles.Add(filePath);
                        }
                        else
                        {
                            // File doesn't exist anymore, consider it "deleted"
                            deletedFiles.Add(filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log individual file deletion failures but continue
                        System.Diagnostics.Debug.WriteLine($"Failed to delete file {filePath}: {ex.Message}");
                    }
                }

                // Remove successfully deleted files from the pending list
                if (deletedFiles.Count > 0)
                {
                    var remainingFiles = filesToDelete.Except(deletedFiles).ToArray();
                    if (remainingFiles.Length == 0)
                    {
                        // All files deleted, remove the pending deletions file
                        File.Delete(_pendingDeletionsFile);
                    }
                    else
                    {
                        // Update the file with remaining files
                        await File.WriteAllLinesAsync(_pendingDeletionsFile, remainingFiles);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to process pending deletions: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if a filename is valid for the file system
        /// </summary>
        private bool IsValidFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            // Check for invalid characters
            var invalidChars = Path.GetInvalidFileNameChars();
            if (fileName.Any(c => invalidChars.Contains(c)))
                return false;

            // Check for reserved names
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reservedNames.Contains(fileName.ToUpperInvariant()))
                return false;

            // Check if it ends with a dot or space
            if (fileName.EndsWith(".") || fileName.EndsWith(" "))
                return false;

            return true;
        }

        /// <summary>
        /// Gets all related files for a given base file path
        /// </summary>
        public List<string> GetRelatedFiles(string baseFilePath)
        {
            var relatedFiles = new List<string>();
            
            if (!File.Exists(baseFilePath))
                return relatedFiles;

            var directory = Path.GetDirectoryName(baseFilePath);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(baseFilePath);
            
            // Add the base file
            relatedFiles.Add(baseFilePath);

            // Look for related files
            var relatedExtensions = new[] { ".fav", ".hide" };
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            // Check for .fav and .hide files
            foreach (var ext in relatedExtensions)
            {
                var relatedFile = Path.Combine(directory, nameWithoutExtension + ext);
                if (File.Exists(relatedFile))
                    relatedFiles.Add(relatedFile);

                // Also check for compound extensions
                var pattern = nameWithoutExtension + "*" + ext;
                var compoundFiles = Directory.GetFiles(directory, pattern);
                relatedFiles.AddRange(compoundFiles.Where(f => !relatedFiles.Contains(f)));
            }

            // Check for image files
            foreach (var imgExt in imageExtensions)
            {
                var imageFile = Path.Combine(directory, nameWithoutExtension + imgExt);
                if (File.Exists(imageFile))
                {
                    relatedFiles.Add(imageFile);
                    break; // Only add the first matching image
                }
            }

            return relatedFiles;
        }
    }
}
