using System;
using System.Collections.Generic;
using System.IO;

namespace VPM.Services
{
    public static class SafeFileEnumerator
    {
        public static IEnumerable<string> EnumerateFiles(string rootPath, string searchPattern, bool recursive)
        {
            return SymlinkSafeFileSystem.EnumerateFilesSafe(rootPath, searchPattern, recursive);
        }
    }
}
