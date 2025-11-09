using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VPM.Models;

namespace VPM.Services
{
    public class DependencyRemover
    {
        public class RemovalResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
            public int RemovedCount { get; set; }
            public List<string> RemovedDependencies { get; set; } = new List<string>();
        }

        public RemovalResult RemoveDependenciesFromPackage(string packagePath, List<string> dependenciesToRemove)
        {
            var result = new RemovalResult { Success = false };

            try
            {
                if (string.IsNullOrEmpty(packagePath) || dependenciesToRemove == null || !dependenciesToRemove.Any())
                {
                    result.ErrorMessage = "Invalid parameters";
                    return result;
                }

                if (File.Exists(packagePath))
                {
                    result = RemoveFromVarFile(packagePath, dependenciesToRemove);
                }
                else if (Directory.Exists(packagePath))
                {
                    result = RemoveFromUnpackedFolder(packagePath, dependenciesToRemove);
                }
                else
                {
                    result.ErrorMessage = "Package path does not exist";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error removing dependencies: {ex.Message}";
            }

            return result;
        }

        private RemovalResult RemoveFromVarFile(string varPath, List<string> dependenciesToRemove)
        {
            var result = new RemovalResult();
            string tempPath = varPath + ".tmp";

            try
            {
                using (var sourceArchive = ZipFile.OpenRead(varPath))
                using (var destFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var destArchive = new ZipArchive(destFileStream, ZipArchiveMode.Create, false))
                {
                    foreach (var entry in sourceArchive.Entries)
                    {
                        // Smart compression: use NoCompression for already-compressed formats
                        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                        bool isAlreadyCompressed = extension == ".jpg" || extension == ".jpeg" || 
                                                  extension == ".png" || extension == ".mp3" || 
                                                  extension == ".mp4" || extension == ".ogg" ||
                                                  extension == ".assetbundle";
                        
                        var compression = isAlreadyCompressed ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        
                        if (entry.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                        {
                            using var stream = entry.Open();
                            using var reader = new StreamReader(stream);
                            var metaJson = reader.ReadToEnd();

                            var modifiedJson = RemoveDependenciesFromJson(metaJson, dependenciesToRemove, result);

                            var newEntry = destArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                            newEntry.LastWriteTime = entry.LastWriteTime;
                            using var writer = new StreamWriter(newEntry.Open());
                            writer.Write(modifiedJson);
                        }
                        else
                        {
                            var newEntry = destArchive.CreateEntry(entry.FullName, compression);
                            newEntry.LastWriteTime = entry.LastWriteTime;
                            using var sourceStream = entry.Open();
                            using var destStream = newEntry.Open();
                            sourceStream.CopyTo(destStream);
                        }
                    }
                }

                File.Delete(varPath);
                File.Move(tempPath, varPath);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error modifying VAR file: {ex.Message}";
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            return result;
        }

        private RemovalResult RemoveFromUnpackedFolder(string folderPath, List<string> dependenciesToRemove)
        {
            var result = new RemovalResult();

            try
            {
                var metaPath = Path.Combine(folderPath, "meta.json");
                if (!File.Exists(metaPath))
                {
                    result.ErrorMessage = "No meta.json found in unpacked folder";
                    return result;
                }

                var metaJson = File.ReadAllText(metaPath);
                var modifiedJson = RemoveDependenciesFromJson(metaJson, dependenciesToRemove, result);

                File.WriteAllText(metaPath, modifiedJson);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error modifying unpacked folder: {ex.Message}";
            }

            return result;
        }

        private string RemoveDependenciesFromJson(string metaJson, List<string> dependenciesToRemove, RemovalResult result)
        {
            var modifiedJson = metaJson;

            foreach (var depName in dependenciesToRemove)
            {
                var escapedDepName = Regex.Escape(depName);
                
                var pattern = $@"""{ escapedDepName}""[ \t]*:[ \t]*\{{[^}}]*?(\{{[^}}]*?\}}[^}}]*?)*?\}},?[ \t]*\r?\n?";
                
                var regex = new Regex(pattern, RegexOptions.Multiline);
                var match = regex.Match(modifiedJson);
                
                if (match.Success)
                {
                    modifiedJson = regex.Replace(modifiedJson, "", 1);
                    result.RemovedDependencies.Add(depName);
                    result.RemovedCount++;
                }
            }

            modifiedJson = CleanupTrailingCommas(modifiedJson);

            return modifiedJson;
        }

        private string CleanupTrailingCommas(string json)
        {
            var commaBeforeClosingBrace = new Regex(@",(\s*\})", RegexOptions.Multiline);
            json = commaBeforeClosingBrace.Replace(json, "$1");

            var commaBeforeClosingBracket = new Regex(@",(\s*\])", RegexOptions.Multiline);
            json = commaBeforeClosingBracket.Replace(json, "$1");

            return json;
        }
    }
}

