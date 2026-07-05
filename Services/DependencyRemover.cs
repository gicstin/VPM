using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
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
                using (var sourceArchive = SharpCompressHelper.OpenForRead(varPath))
                using (var destArchive = ZipArchive.CreateArchive())
                {
                    foreach (var entry in sourceArchive.Entries)
                    {
                        if (entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                        {
                            using var stream = entry.OpenEntryStream();
                            using var reader = new StreamReader(stream);
                            var metaJson = reader.ReadToEnd();

                            var modifiedJson = RemoveDependenciesFromJson(metaJson, dependenciesToRemove, result);
                            destArchive.AddEntry(entry.Key, new MemoryStream(Encoding.UTF8.GetBytes(modifiedJson)));
                        }
                        else
                        {
                            destArchive.AddEntry(entry.Key, entry.OpenEntryStream());
                        }
                    }
                    
                    // Save the archive inside the using block
                    using (var destFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        // BestCompression for smaller output; level lives on ZipWriterOptions in SharpCompress 0.49+
                        destArchive.SaveTo(destFileStream, new SharpCompress.Writers.Zip.ZipWriterOptions(CompressionType.Deflate, SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression));
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

