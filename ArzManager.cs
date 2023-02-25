using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TQDB_Parser;
using TQDB_Parser.DBR;

namespace TQArchive_Wrapper
{
    public class ArzManager
    {
        private readonly string filePath;

        private readonly string baseDir;
        private bool needsWriting;

        private readonly ILogger? logger;

        private readonly List<string> stringList;
        private readonly List<DBRFileInfo> fileInfoList;

        private readonly Dictionary<DBRFileInfo, Func<MemoryStream>> compressedFiles;

        private readonly Dictionary<string, DBRFileInfo> mappedFileInfos;
        //private readonly Dictionary<string, RawDBRFile> mappedFiles;
        private readonly Dictionary<string, int> mappedStrings;

        public event Action? FileDone;

        public ArzManager(string filePath, string baseDir, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            this.baseDir = baseDir;
            needsWriting = true;
            this.logger = logger;

            stringList = new();

            fileInfoList = new();
            compressedFiles = new();

            mappedFileInfos = new();
            mappedStrings = new();

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                ReadArchive();
        }

        private void ReadArchive()
        {
            var reader = new ArzReader(filePath, logger);
            foreach (var str in reader.GetStringList())
            {
                try
                {
                    mappedStrings.Add(str, stringList.Count);
                    stringList.Add(str);
                }
                catch (ArgumentException e)
                {
                    logger?.LogError(e, "Failed to map string");
                }
            }
            foreach (var fileInfo in reader.GetDBRFileInfos())
            {
                try
                {
                    mappedFileInfos.Add(stringList[fileInfo.NameID], fileInfo);
                    fileInfoList.Add(fileInfo);
                }
                catch (ArgumentException e)
                {
                    logger?.LogError(e, "Failed to map file");
                }
            }

            compressedFiles.AddRange(fileInfoList.ToDictionary(x => x,
                x => { MemoryStream ret() => reader.GetCompressedFileStream(x); return (Func<MemoryStream>)ret; }));
            //foreach (var fileInfo in fileInfoList)
            //{
            //    try
            //    {
            //        var fileName = stringList[fileInfo.NameID];
            //        try
            //        {
            //            var compressedDataStream = reader.GetCompressedFileStream(fileInfo);
            //            var decompressedDataStream = reader.GetDecompressedFileStream(fileInfo);
            //            compressedFiles.Add(fileInfo, compressedDataStream);
            //            mappedFiles.Add(fileName, reader.ReadDBRFile(decompressedDataStream, fileName));
            //        }
            //        catch (Exception e)
            //        {
            //            logger?.LogError(e, "Failed to read compressed file {fileName}", fileName);
            //        }
            //    }
            //    catch (ArgumentOutOfRangeException)
            //    {
            //        logger?.LogError("Failed to read compressed file, filename missing in stringlist");
            //    }
            //}

            needsWriting = false;
        }

        public void WriteToDisk()
        {
            if (needsWriting)
            {
                var writer = new ArzWriter(filePath, baseDir, logger);
                writer.Write(compressedFiles.Select(x => (x.Key, x.Value())), mappedStrings);
                needsWriting = false;
                logger?.LogInformation("Archive {path} written successfully", filePath);
            }
            else
            {
                logger?.LogInformation("Archive {path} is up to date", filePath);
            }
        }

        public void AddOrUpdateFile(string filePath, string baseDir, ConcurrentDictionary<string, DBRFileInfo> mappedFileInfos, ConcurrentDictionary<DBRFileInfo, MemoryStream> compressedFiles, LockableDictionary<string, int> mappedStrings, ConcurrentBag<string> stringList, Action fileDone, Func<DBRFile> file, ILogger? logger = null)
        {
            string message = "added";
            var relPath = Path.GetRelativePath(baseDir, filePath).ToLowerInvariant();
            if (mappedFileInfos.TryGetValue(relPath, out var existingInfo))
            {
                if (existingInfo.TimeStamp >= File.GetLastWriteTimeUtc(filePath).ToFileTimeUtc())
                {
                    //logger?.LogWarning("File {path} has been skipped (Newer in archive)");
                    fileDone?.Invoke();
                    return;
                }

                message = "updated";
            }
            var currStringCount = mappedStrings.Count;
            try
            {
                var dbrFile = file.Invoke();

                var additionalStream = ArzWriter.WriteFileToStream(dbrFile, mappedStrings, baseDir, logger);
                var countDiff = mappedStrings.Count - currStringCount;
                if (countDiff > 0)
                {
                    foreach (var newString in mappedStrings.TakeLast(countDiff).Select(x => x.Key))
                        stringList.Add(newString);
                }

                var (additionalInfo, compressedStream) = ArzWriter.CompressAndCreateInfo(additionalStream, mappedStrings, baseDir, dbrFile.FilePath, dbrFile["Class"].Value);
                mappedFileInfos[relPath] = additionalInfo;
                compressedFiles.TryAdd(additionalInfo, compressedStream);

                //mappedFiles[relPath] = RawDBRFile.From(dbrFile);

                logger?.LogInformation("File {path} has been {msg}", filePath, message);
                fileDone?.Invoke();
                needsWriting = true;
            }
            catch (Exception e)
            {
                logger?.LogError(e, "Failed to get file: {path}", filePath);
            }
        }

        private void OnFileDone()
        {
            FileDone?.Invoke();
        }

        //public void SyncFiles(IEnumerable<string> filePaths, DBRParser parser, bool useParallel = false)
        //{
        //    if (useParallel)
        //        Parallel.ForEach(filePaths, (x) => DoSync(x));
        //    else
        //        foreach (var filePath in filePaths)
        //        {
        //            DoSync(filePath);
        //        }

        //    void DoSync(string filePath)
        //    {
        //        AddOrUpdateFile(filePath, () => parser.ParseFile(filePath));
        //    }
        //}

        public void SyncFiles(IEnumerable<string> filePaths, TemplateManager manager, bool useParallel = false)
        {
            var mappedFileInfos = new ConcurrentDictionary<string, DBRFileInfo>(this.mappedFileInfos);
            var compressedFiles = new ConcurrentDictionary<DBRFileInfo, MemoryStream>();
            var mappedStrings = new LockableDictionary<string, int>(new ConcurrentDictionary<string, int>(this.mappedStrings));
            //var mappedFiles = new ConcurrentDictionary<string, RawDBRFile>(this.mappedFiles);
            var stringList = new ConcurrentBag<string>(this.stringList);
            if (useParallel)
                Parallel.ForEach(filePaths, (x) => DoSync(x));
            else
                foreach (var filePath in filePaths)
                {
                    DoSync(filePath);
                }

            this.mappedFileInfos.Clear();
            this.mappedFileInfos.AddRange(mappedFileInfos);
            //this.compressedFiles.Clear();
            this.compressedFiles.AddRangeKeepBy(compressedFiles.Select(x => new KeyValuePair<DBRFileInfo, Func<MemoryStream>>(x.Key, () => x.Value)), (a, b) => a.Key.TimeStamp < b.Key.TimeStamp);
            this.mappedStrings.Clear();
            this.mappedStrings.AddRange(mappedStrings);
            //this.mappedFiles.Clear();
            //this.mappedFiles.AddRange(mappedFiles);
            this.stringList.Clear();
            this.stringList.AddRange(stringList);


            void DoSync(string filePath)
            {
                AddOrUpdateFile(filePath, baseDir, mappedFileInfos, compressedFiles, mappedStrings, stringList, OnFileDone, () => new DBRParser(manager, logger).ParseFile(filePath), logger);
            }
        }

        //public void SyncFiles(DBRParser parser)
        //{
        //    SyncFiles(Directory.EnumerateFiles(baseDir, "*.dbr", SearchOption.AllDirectories), parser);
        //}

        public void SyncFiles(TemplateManager manager, bool useParallel = false)
        {
            SyncFiles(Directory.EnumerateFiles(baseDir, "*.dbr", SearchOption.AllDirectories), manager, useParallel);
        }
    }

    public static class DictionaryExtensions
    {
        public static void AddRange<TKey, TValue>(this IDictionary<TKey, TValue> me, IDictionary<TKey, TValue> other)
        {
            foreach (var pair in other)
                me.Add(pair);
        }

        public static void AddRangeKeepBy<TKey, TValue>(this IDictionary<TKey, TValue> me, IEnumerable<KeyValuePair<TKey, TValue>> other, Func<KeyValuePair<TKey, TValue>, KeyValuePair<TKey, TValue>, bool> shouldReplace)
        {
            foreach (var pair in other)
            {
                if (me.ContainsKey(pair.Key) && shouldReplace(new KeyValuePair<TKey, TValue>(pair.Key, me[pair.Key]), pair))
                    me[pair.Key] = pair.Value;
                else
                    me.Add(pair);
            }
        }
    }
}
