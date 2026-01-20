using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Inspects remote .var packages without downloading the entire file.
    /// Uses HTTP Range requests to read the Central Directory and specific files (meta.json).
    /// </summary>
    public class RemotePackageInspector
    {
        private readonly HttpClient _httpClient;

        public RemotePackageInspector(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<List<string>> GetPackageDependenciesAsync(string downloadUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Resolve final URL and get content length
                var (finalUrl, contentLength) = await ResolveUrlAndLengthAsync(downloadUrl, cancellationToken);
                
                if (contentLength <= 0) 
                {
                    return null;
                }

                // 2. Read End of Central Directory (EOCD) - Last 64KB
                // EOCD is at least 22 bytes. Comment can be up to 64KB.
                int readSize = (int)Math.Min(contentLength, 65536);
                long start = contentLength - readSize;
                
                var eocdBytes = await ReadRangeAsync(finalUrl, start, contentLength - 1, cancellationToken);
                if (eocdBytes == null) 
                {
                    return null;
                }

                // 3. Find EOCD signature
                int eocdOffset = FindEocdOffset(eocdBytes);
                if (eocdOffset == -1) 
                {
                    return null;
                }

                // 4. Parse EOCD to get Central Directory offset and size
                using var eocdStream = new MemoryStream(eocdBytes);
                eocdStream.Position = eocdOffset;
                using var reader = new BinaryReader(eocdStream);

                // Skip signature (4), disk num (2), disk w/ CD (2), entries disk (2), total entries (2)
                reader.ReadInt32(); // Signature
                reader.ReadInt16();
                reader.ReadInt16();
                reader.ReadInt16(); 
                reader.ReadInt16();

                int cdSize = reader.ReadInt32();
                int cdOffset = reader.ReadInt32();
                
                // 5. Read Central Directory
                // If CD is huge, this might be large, but usually it's reasonably small for var packages.
                // We could read it in chunks, but for now let's try reading it all.
                // Note: cdOffset is from the beginning of the file.
                
                // Safety check: Limit Central Directory size to 16MB to prevent excessive memory usage
                if (cdSize < 0 || cdSize > 16 * 1024 * 1024) 
                {
                    return null;
                }

                var cdBytes = await ReadRangeAsync(finalUrl, cdOffset, cdOffset + cdSize - 1, cancellationToken);
                if (cdBytes == null) 
                {
                    return null;
                }

                // 6. Find meta.json entry in Central Directory
                var metaEntry = FindMetaJsonEntry(cdBytes);
                if (metaEntry == null) 
                {
                    return null;
                }
                
                // 7. Read meta.json Local File Header + Data
                // We need to read the Local File Header first to know the variable fields length (filename + extra)
                // The CD entry gives us the Local Header offset.
                
                // Read a chunk that surely contains the Local Header (30 bytes + filename + extra)
                // Then we can determine where the data starts.
                // We'll read the header + compressed size.
                // metaEntry.CompressedSize is known.
                
                // Let's read header (estimate 128 bytes) + data.
                // If data is large, we might need two requests, but meta.json is small.
                int estimatedHeaderSize = 30 + metaEntry.FilenameLength + metaEntry.ExtraFieldLength + 100; // +100 safety
                long dataStart = metaEntry.LocalHeaderOffset;
                long readEnd = dataStart + estimatedHeaderSize + metaEntry.CompressedSize; // approximate
                
                // Actually, let's just read the header first to be precise about data start
                var headerBytes = await ReadRangeAsync(finalUrl, dataStart, dataStart + 512, cancellationToken); // 512 bytes should cover header
                if (headerBytes == null) 
                {
                    return null;
                }

                using var headerStream = new MemoryStream(headerBytes);
                using var headerReader = new BinaryReader(headerStream);
                
                if (headerReader.ReadInt32() != 0x04034b50) 
                {
                    return null; // Invalid signature
                }
                
                headerStream.Position += 22; // Skip to filename length
                int fileNameLen = headerReader.ReadInt16();
                int extraLen = headerReader.ReadInt16();
                
                long actualDataStart = dataStart + 30 + fileNameLen + extraLen;
                
                // Now read the actual compressed data
                var dataBytes = await ReadRangeAsync(finalUrl, actualDataStart, actualDataStart + metaEntry.CompressedSize - 1, cancellationToken);
                if (dataBytes == null) 
                {
                    return null;
                }

                // 8. Decompress and parse
                using var compressedStream = new MemoryStream(dataBytes);
                Stream decompressedStream = compressedStream;
                
                if (metaEntry.CompressionMethod == 8) // Deflate
                {
                    decompressedStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
                }
                
                using var jsonDoc = await JsonDocument.ParseAsync(decompressedStream, cancellationToken: cancellationToken);
                var deps = ParseDependencies(jsonDoc.RootElement);
                
                return deps;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<(string Url, long Length)> ResolveUrlAndLengthAsync(string url, CancellationToken token)
        {
            // Use HEAD request to follow redirects and get content length
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            
            if (!response.IsSuccessStatusCode)
            {
                // Fallback to GET with Range 0-0 if HEAD fails (some servers block HEAD)
                var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                getRequest.Headers.Range = new RangeHeaderValue(0, 0);
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, token);
                
                if (!getResponse.IsSuccessStatusCode) return (null, 0);
                
                // If Content-Range is present, it gives total length: bytes 0-0/12345
                if (getResponse.Content.Headers.ContentRange?.Length.HasValue == true)
                {
                    return (getResponse.RequestMessage.RequestUri.ToString(), getResponse.Content.Headers.ContentRange.Length.Value);
                }
                
                return (null, 0);
            }

            return (response.RequestMessage.RequestUri.ToString(), response.Content.Headers.ContentLength ?? 0);
        }

        private async Task<byte[]> ReadRangeAsync(string url, long start, long end, CancellationToken token)
        {
            if (start > end)
            {
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            
            return await response.Content.ReadAsByteArrayAsync(token);
        }

        private int FindEocdOffset(byte[] buffer)
        {
            // Scan backwards for signature 0x06054b50
            for (int i = buffer.Length - 22; i >= 0; i--)
            {
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                {
                    return i;
                }
            }
            return -1;
        }

        private CentralDirectoryEntry FindMetaJsonEntry(byte[] cdBytes)
        {
            using var stream = new MemoryStream(cdBytes);
            using var reader = new BinaryReader(stream);

            while (stream.Position < stream.Length - 46) // Min header size
            {
                int sig = reader.ReadInt32();
                if (sig != 0x02014b50) break; // Not a CD header, maybe end of CD

                reader.ReadInt16(); // Version made by
                reader.ReadInt16(); // Version needed
                reader.ReadInt16(); // Flags
                short compressionMethod = reader.ReadInt16();
                reader.ReadInt32(); // Last mod time/date
                reader.ReadInt32(); // CRC32
                uint compressedSize = reader.ReadUInt32();
                uint uncompressedSize = reader.ReadUInt32();
                short fileNameLen = reader.ReadInt16();
                short extraLen = reader.ReadInt16();
                short commentLen = reader.ReadInt16();
                reader.ReadInt16(); // Disk start
                reader.ReadInt16(); // Internal attrs
                reader.ReadInt32(); // External attrs
                uint localHeaderOffset = reader.ReadUInt32();

                byte[] nameBytes = reader.ReadBytes(fileNameLen);
                string name = Encoding.UTF8.GetString(nameBytes);

                if (string.Equals(name, "meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    long finalCompressedSize = compressedSize;
                    long finalUncompressedSize = uncompressedSize;
                    long finalLocalHeaderOffset = localHeaderOffset;

                    // Zip64 Check
                    if (compressedSize == 0xFFFFFFFF || uncompressedSize == 0xFFFFFFFF || localHeaderOffset == 0xFFFFFFFF)
                    {
                        // Parse Extra Field to find Zip64 Extended Information (0x0001)
                        long posBeforeExtra = stream.Position;
                        long extraEnd = posBeforeExtra + extraLen;
                        
                        while (stream.Position < extraEnd)
                        {
                            // Ensure we have enough bytes for header (4 bytes)
                            if (stream.Position + 4 > extraEnd) break;
                            
                            ushort tag = reader.ReadUInt16();
                            ushort size = reader.ReadUInt16();
                            
                            if (tag == 0x0001) // Zip64 Extended Information
                            {
                                // Fields are present ONLY if the corresponding header value is -1 (0xFFFFFFFF) or 0xFFFF for disk
                                if (uncompressedSize == 0xFFFFFFFF)
                                    finalUncompressedSize = reader.ReadInt64();
                                    
                                if (compressedSize == 0xFFFFFFFF)
                                    finalCompressedSize = reader.ReadInt64();
                                    
                                if (localHeaderOffset == 0xFFFFFFFF)
                                    finalLocalHeaderOffset = reader.ReadInt64();
                                    
                                // Disk start number check (not needed for logic but part of struct if diskStart == 0xFFFF)
                                break; 
                            }
                            else
                            {
                                stream.Position += size;
                            }
                        }
                    }

                    return new CentralDirectoryEntry
                    {
                        CompressionMethod = compressionMethod,
                        CompressedSize = finalCompressedSize,
                        UncompressedSize = finalUncompressedSize,
                        LocalHeaderOffset = finalLocalHeaderOffset,
                        FilenameLength = fileNameLen,
                        ExtraFieldLength = extraLen
                    };
                }

                // Skip extra and comment
                stream.Position += extraLen + commentLen;
            }

            return null;
        }

        private List<string> ParseDependencies(JsonElement root)
        {
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("dependencies", out var depsProp))
            {
                ParseDependenciesRecursive(depsProp, dependencies);
            }

            return dependencies.ToList();
        }

        private void ParseDependenciesRecursive(JsonElement deps, HashSet<string> dependenciesSet)
        {
            switch (deps.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var dep in deps.EnumerateArray())
                    {
                        if (dep.ValueKind == JsonValueKind.String)
                        {
                            var s = dep.GetString();
                            if (!string.IsNullOrEmpty(s)) dependenciesSet.Add(s);
                        }
                        else if (dep.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in dep.EnumerateObject())
                            {
                                if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("packageName", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("package", StringComparison.OrdinalIgnoreCase))
                                {
                                    var s = prop.Value.GetString();
                                    if (!string.IsNullOrEmpty(s)) dependenciesSet.Add(s);
                                }
                            }
                        }
                    }
                    break;

                case JsonValueKind.Object:
                    foreach (var prop in deps.EnumerateObject())
                    {
                        dependenciesSet.Add(prop.Name);
                        // Recursively parse subdependencies
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("dependencies", out var subDeps))
                        {
                            ParseDependenciesRecursive(subDeps, dependenciesSet);
                        }
                    }
                    break;

                case JsonValueKind.String:
                    var single = deps.GetString();
                    if (!string.IsNullOrEmpty(single)) dependenciesSet.Add(single);
                    break;
            }
        }

        private class CentralDirectoryEntry
        {
            public short CompressionMethod { get; set; }
            public long CompressedSize { get; set; }
            public long UncompressedSize { get; set; }
            public long LocalHeaderOffset { get; set; }
            public int FilenameLength { get; set; }
            public int ExtraFieldLength { get; set; }
        }
    }
}
