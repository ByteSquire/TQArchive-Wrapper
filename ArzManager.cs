using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using TQDB_Parser;
using TQDB_Parser.DBR;

namespace TQArchive_Wrapper
{
    public class ArzManager
    {
        private readonly ArzWriter writer;
        private readonly string filePath;

        private readonly string baseDir;
        private bool needsWriting;

        private readonly ILogger? logger;

        private readonly List<string> stringList;
        private readonly List<DBRFileInfo> fileInfoList;

        private readonly Dictionary<DBRFileInfo, MemoryStream> compressedFiles;

        private readonly Dictionary<string, DBRFileInfo> mappedFileInfos;
        private readonly Dictionary<string, RawDBRFile> mappedFiles;
        private readonly Dictionary<string, int> mappedStrings;

        public ArzManager(string filePath, string baseDir, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            this.baseDir = baseDir;
            needsWriting = true;
            this.logger = logger;

            writer = new ArzWriter(filePath, baseDir, logger);

            stringList = new();

            fileInfoList = new();
            compressedFiles = new();

            mappedFileInfos = new();
            mappedFiles = new();
            mappedStrings = new();

            if (!File.Exists(filePath))
                File.Create(filePath).Dispose();
            else if (new FileInfo(filePath).Length > 0)
                ReadArchive();
        }

        private void ReadArchive()
        {
            var reader = new ArzReader(filePath, logger);
            foreach (var str in reader.GetStringList())
            {
                mappedStrings.Add(str, stringList.Count);
                stringList.Add(str);
            }
            foreach (var fileInfo in reader.GetDBRFileInfos())
            {
                fileInfoList.Add(fileInfo);
                mappedFileInfos.Add(stringList[fileInfo.NameID], fileInfo);
            }

            foreach (var fileInfo in fileInfoList)
            {
                var compressedDataStream = reader.GetCompressedFileStream(fileInfo);
                var decompressedDataStream = reader.GetDecompressedFileStream(fileInfo);
                var fileName = stringList[fileInfo.NameID];
                compressedFiles.Add(fileInfo, compressedDataStream);
                mappedFiles.Add(fileName, reader.ReadDBRFile(decompressedDataStream, fileName));
            }

            needsWriting = false;
        }

        public void WriteToDisk()
        {
            if (needsWriting)
            {
                writer.Write(compressedFiles.Select(x => (x.Key, x.Value)), mappedStrings);
                needsWriting = false;
                logger?.LogInformation("Archive {path} written successfully", filePath);
            }
            else
            {
                logger?.LogInformation("Archive {path} is up to date", filePath);
            }
        }

        public void AddOrUpdateFile(string filePath, Func<DBRFile> file)
        {
            string message = "added";
            var relPath = Path.GetRelativePath(baseDir, filePath).ToLowerInvariant();
            if (mappedFileInfos.TryGetValue(relPath, out var existingInfo))
            {
                if (existingInfo.TimeStamp >= File.GetLastWriteTimeUtc(filePath).ToFileTimeUtc())
                {
                    //logger?.LogWarning("File {path} has been skipped (Newer in archive)");
                    return;
                }

                compressedFiles.Remove(existingInfo);
                message = "updated";
            }
            var (additionalInfo, additionalStream) = writer.WriteFileToStreamCreateInfo(file.Invoke(), mappedStrings);
            mappedFileInfos[relPath] = additionalInfo;
            compressedFiles.Add(additionalInfo, additionalStream);

            logger?.LogInformation("File {path} has been {msg}", filePath, message);
            needsWriting = true;
            return;
        }

        public void SyncFiles(IEnumerable<string> filePaths, DBRParser parser)
        {
            foreach (var filePath in filePaths)
            {
                AddOrUpdateFile(filePath, () => parser.ParseFile(filePath));
            }
        }

        public void SyncFiles(DBRParser parser)
        {
            SyncFiles(Directory.EnumerateFiles(baseDir, "*.dbr", SearchOption.AllDirectories), parser);
        }
    }
}
