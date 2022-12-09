using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Compression;
using TQDB_Parser.DBR;
using Microsoft.Extensions.Logging;
using TQDB_Parser.Extensions;
using System.Collections.Concurrent;
using System.Collections;

namespace TQArchive_Wrapper
{
    public struct LockableDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        private readonly IDictionary<TKey, TValue> dictionary;
        public IDictionary<TKey, TValue> Dictionary => dictionary;

        public LockableDictionary(IDictionary<TKey, TValue> dict)
        {
            dictionary = dict;
        }

        private readonly object syncRoot = new();
        public object SyncRoot => syncRoot;

        public ICollection<TKey> Keys => dictionary.Keys;

        public ICollection<TValue> Values => dictionary.Values;

        public int Count => dictionary.Count;

        public bool IsReadOnly => dictionary.IsReadOnly;

        public TValue this[TKey key] { get => dictionary[key]; set => dictionary[key] = value; }

        public void Add(TKey key, TValue value)
        {
            dictionary.Add(key, value);
        }

        public bool ContainsKey(TKey key)
        {
            return dictionary.ContainsKey(key);
        }

        public bool Remove(TKey key)
        {
            return dictionary.Remove(key);
        }

        public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
        {
            return dictionary.TryGetValue(key, out value);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            dictionary.Add(item);
        }

        public void Clear()
        {
            dictionary.Clear();
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return dictionary.Contains(item);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            dictionary.CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return dictionary.Remove(item);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }
    }

    public class ArzWriter
    {
        private readonly string filePath;
        private readonly string databasePath;
        private readonly ILogger? logger;

        //private readonly object stringDictionaryLock = new();
        //private readonly IDictionary<string, int> combinedStrings;

        public ArzWriter(string filePath, string databasePath, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            this.databasePath = databasePath;
            this.logger = logger;
            //combinedStrings = new Dictionary<string, int>();
            if (!File.Exists(filePath))
                File.Create(filePath).Dispose();
        }

        public void Write(IEnumerable<DBRFile> files, bool useParallel = false)
        {
            //var strEntries = new List<string>();
            //combinedStrings.Clear();
            IEnumerable<(DBRFileInfo, MemoryStream)> binaryEntries;
            var strings = new LockableDictionary<string, int>(new Dictionary<string, int>());

            if (useParallel)
            {
                var parallelBinaryEntries = new ConcurrentBag<(DBRFileInfo, MemoryStream)>();
                binaryEntries = parallelBinaryEntries;
                Parallel.ForEach(files, file => parallelBinaryEntries.Add(CompressAndCreateInfo(WriteFileToStream(file, strings, databasePath, logger), strings, databasePath, file.FilePath, file["Class"].Value)));
            }
            else
            {
                var linearBinaryEntries = new List<(DBRFileInfo, MemoryStream)>();
                binaryEntries = linearBinaryEntries;
                foreach (var file in files)
                    linearBinaryEntries.Add(CompressAndCreateInfo(WriteFileToStream(file, strings, databasePath, logger), strings, databasePath, file.FilePath, file["Class"].Value));
            }

            (var binaryValuesStream, var combinedDBREntries) = CreateArchiveStreams(binaryEntries);

            WriteArchive(files.Count(), strings.OrderBy(x => x.Value).Select(x => x.Key), binaryValuesStream, combinedDBREntries);
        }

        public void Write(IEnumerable<(DBRFileInfo, MemoryStream)> files, IDictionary<string, int> strings)
        {
            (var binaryValuesStream, var combinedDBREntries) = CreateArchiveStreams(files);

            WriteArchive(files.Count(), strings.OrderBy(x => x.Value).Select(x => x.Key), binaryValuesStream, combinedDBREntries);
        }

        private static (MemoryStream, MemoryStream) CreateArchiveStreams(IEnumerable<(DBRFileInfo Info, MemoryStream Stream)> binaryEntries)
        {
            var binaryValuesStream = new MemoryStream();
            var combinedDBREntries = new MemoryStream();
            foreach (var entry in binaryEntries)
            {
                var info = entry.Info;
                info.Offset = (int)binaryValuesStream.Position;
                info.WriteTo(combinedDBREntries);

                var dbrEntry = entry.Stream;
                WriteMemoryStream(dbrEntry, binaryValuesStream);
                dbrEntry.Dispose();
            }
            return (binaryValuesStream, combinedDBREntries);
        }

        private void WriteArchive(int numFiles, IEnumerable<string> strings, MemoryStream binaryValuesStream, MemoryStream combinedDBREntries)
        {
            // database entries table header
            int dbtableStart = (int)binaryValuesStream.Length;
            dbtableStart += ArzHeader.DBTableBaseOffset; // add 24 bytes for header

            // database record files header
            int dbbyteSize = (int)combinedDBREntries.Length;
            int dbnumEntries = numFiles;

            // string table header
            int strtableStart = dbtableStart + dbbyteSize;
            // (4 bytes) int32 for length of string
            int strbyteSize = strings.Sum(x => Constants.Encoding1252.GetBytes(x).Length + 4);

            int strnumEntries = strings.Count();
            strbyteSize += BitConverter.GetBytes(strnumEntries).Length; // add length of numentries

            var arzHeader = new ArzHeader
            {
                DBTableStart = dbtableStart,
                DBByteSize = dbbyteSize,
                DBNumEntries = dbnumEntries,
                StrTableStart = strtableStart,
                StrTableByteSize = strbyteSize,
            };

            try
            {
                using var stream = new FileStream(filePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream, Constants.Encoding1252, leaveOpen: true);

                arzHeader.WriteTo(writer);

                // database entries compressed
                WriteMemoryStream(binaryValuesStream, stream);
                // database record file infos
                WriteMemoryStream(combinedDBREntries, stream);
                // strings uncompressed
                WriteStringTable(strings, strnumEntries, stream);
            }
            catch (IOException e)
            {
                logger?.LogError(e, "Error writing to arz archive {archive}", filePath);
            }
            binaryValuesStream.Dispose();
            combinedDBREntries.Dispose();
        }

        //public void AddFile(DBRFile file, IEnumerable<(DBRFileInfo info, MemoryStream stream)> existing, IList<string> existingStrings)
        //{
        //    var existingList = existing.ToList();
        //    var matchingIndex = existingList.FindIndex(x => existingStrings[x.info.NameID] == file.FilePath);

        //    if (matchingIndex > -1)
        //    {
        //        var ourTime = File.GetLastWriteTimeUtc(file.FilePath).ToFileTimeUtc();
        //        var theirTime = existingList[matchingIndex].info.TimeStamp;
        //        if (theirTime >= ourTime)
        //            return;
        //        (DBRFileInfo, MemoryStream) entry = CreateNewEntry(file, existingStrings);
        //        existingList[matchingIndex] = entry;
        //    }
        //    else
        //    {
        //        (DBRFileInfo, MemoryStream) entry = CreateNewEntry(file, existingStrings);
        //        existingList.Add(entry);
        //    }

        //    (var dataStream, var dbrInfoStream) = CreateArchiveStreams(existingList);

        //    WriteArchive(existingList.Count, dataStream, dbrInfoStream);


        //    (DBRFileInfo, MemoryStream) CreateNewEntry(DBRFile file, IList<string> existingStrings)
        //    {
        //        combinedStrings.Clear();
        //        foreach (var existingStr in existingStrings)
        //            combinedStrings.Add(existingStr, combinedStrings.Count);
        //        var entry = WriteFileToStreamCreateInfo(file);
        //        return entry;
        //    }
        //}

        public static MemoryStream WriteFileToStream(DBRFile file, LockableDictionary<string, int> strings, string databasePath, ILogger? logger = null)
        {
            var fileVarsStream = new MemoryStream();

            // write templateName as variable, it's excluded in DBRFile entries
            WriteValue(fileVarsStream, (short)2);
            WriteValue(fileVarsStream, (short)1);

            int fileNameID;
            //lock (stringDictionaryLock)
            //{
            fileNameID = AddStrGetIndex(strings, Path.GetRelativePath(databasePath, file.FilePath));
            WriteValue(fileVarsStream, AddStrGetIndex(strings, "templateName", false));
            WriteValue(fileVarsStream, AddStrGetIndex(strings, file.TemplateRoot.FileName));
            //}

            // filter entries, eqnVariables are internal and includes should be resolved by now
            var entries = file.Entries
                .Where(x => x.Template.Type != TQDB_Parser.VariableType.eqnVariable)
                .Where(x => x.Template.Type != TQDB_Parser.VariableType.include)
                // in a perfect world I'd enable that but a lot of the templates have errors :(
                //.Where(x => x.IsValid())
                // ignore empty values, maybe whitespace as well?
                .Where(x => !string.IsNullOrEmpty(x.Value))
                .ToList();
            // ArtManager orders this way
            entries = entries.OrderBy(x => x.Name[0]).ThenBy(x => x.Name).ToList();

            foreach (var entry in entries)
            {
                // name of the variable as int index in string table
                var stringID = AddStrGetIndex(strings, entry.Name, false);

                string[] arraySplit = new string[] { entry.Value };
                bool isArray = entry.Template.Class == TQDB_Parser.VariableClass.array;
                if (isArray)
                    arraySplit = entry.Value.Split(';');
                short numElements = (short)arraySplit.Length;

                short typeID = -1;
                object? values = null;
                bool error = false;
                // type as int16
                switch (entry.Template.Type)
                {
                    case TQDB_Parser.VariableType.@int:
                        typeID = 0;
                        if (!TryGetIntValues(arraySplit, out values, logger))
                            error = true;
                        break;
                    case TQDB_Parser.VariableType.real:
                        typeID = 1;
                        if (!TryGetFloatValues(arraySplit, out values, logger))
                            error = true;
                        break;
                    case TQDB_Parser.VariableType.file:
                        typeID = 2;
                        values = AddStrsGetIndicesAsync(strings, arraySplit, true);
                        break;
                    case TQDB_Parser.VariableType.@string:
                    case TQDB_Parser.VariableType.equation:
                        typeID = 2;
                        values = AddStrsGetIndicesAsync(strings, arraySplit, false);
                        break;
                    case TQDB_Parser.VariableType.@bool:
                        typeID = 3;
                        if (!TryGetIntValues(arraySplit, out values, logger))
                            error = true;
                        break;
                }
                if (error || values is null)
                {
                    //combinedStrings.Remove(entry.Name);
                    continue;
                }

                WriteVariable(fileVarsStream, stringID, typeID, values);
            }

            return fileVarsStream;
        }

        public static (DBRFileInfo, MemoryStream) CompressAndCreateInfo(MemoryStream stream, LockableDictionary<string, int> strings, string databasePath, string filePath, string fileClass)
        {
            // compress the values using ZLib
            var myBinaryValuesStream = CompressValues(stream);

            // dbr file info
            DBRFileInfo fileInfo = CreateFileInfo(databasePath, filePath, fileClass, strings, (int)myBinaryValuesStream.Length);

            return (fileInfo, myBinaryValuesStream);
        }

        private static DBRFileInfo CreateFileInfo(string databasePath, string filePath, string fileClass, LockableDictionary<string, int> strings, int length)
        {
            // dbr file info
            var currDBRNameID = AddStrGetIndex(strings, Path.GetRelativePath(databasePath, filePath));
            // timestap for comparison
            var time = File.GetLastWriteTimeUtc(filePath).ToFileTimeUtc();

            var fileInfo = new DBRFileInfo
            {
                Class = fileClass,
                NameID = currDBRNameID,
                CompressedLength = length,
                TimeStamp = time,
            };
            return fileInfo;
        }

        private static MemoryStream CompressValues(MemoryStream fileVarsStream)
        {
            var myBinaryValuesStream = new MemoryStream();

            // compress the values using ZLib
            using var binaryValuesWriter = new ZLibStream(myBinaryValuesStream, CompressionLevel.SmallestSize, true);
            WriteMemoryStream(fileVarsStream, binaryValuesWriter);

            return myBinaryValuesStream;
        }

        private static bool TryGetFloatValues(string[] elements, out object? values, ILogger? logger = null)
        {
            var iValues = new object[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (!TQNumberString.TryParseTQString(element, out float fVal))
                {
                    logger?.LogError("{value} is not a valid float", element);
                    values = null;
                    return false;
                }
                iValues[i] = fVal;
            }

            values = iValues;
            return true;
        }

        private static bool TryGetIntValues(string[] elements, out object? values, ILogger? logger = null)
        {
            var fValues = new object[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (!TQNumberString.TryParseTQString(element, out int iVal))
                {
                    logger?.LogError("{value} is not a valid integer", element);
                    values = null;
                    return false;
                }
                fValues[i] = iVal;
            }

            values = fValues;
            return true;
        }

        private static void WriteVariableHeader(MemoryStream stream, int stringID, short typeID, short numElements)
        {
            // write type id as int16
            WriteValue(stream, typeID);
            // write number of values as int16
            WriteValue(stream, numElements);
            // write the id of the name as int32
            WriteValue(stream, stringID);
        }

        private static void WriteVariable(MemoryStream stream, int stringID, short typeID, object oValues)
        {
            object[] valuesArr;
            if (oValues is object[] passedArr)
            {
                valuesArr = passedArr;
            }
            else if (oValues is Task<object[]> valuesTask)
            {
                valuesTask.Wait();
                valuesArr = valuesTask.Result;
            }
            else
                throw new NotImplementedException();

            WriteVariableHeader(stream, stringID, typeID, (short)valuesArr.Length);
            WriteVariableValues(stream, valuesArr);
        }

        private static void WriteVariableValues(MemoryStream stream, object[] values)
        {
            // write the values continuously
            foreach (var value in values)
            {
                WriteValue(stream, value);
            }
        }

        private static Task<object[]> AddStrsGetIndicesAsync(LockableDictionary<string, int> mappedStrings, string[] strs, bool ignoreCase)
        {
            var ret = new Task<object[]>(() => AddStrsGetIndices(mappedStrings, strs, ignoreCase));
            ret.Start();
            return ret;
        }

        private static object[] AddStrsGetIndices(LockableDictionary<string, int> mappedStrings, string[] strs, bool ignoreCase)
        {
            // ignoreCase can save a bit of size, ArtManager is really inconsistent
            if (ignoreCase)
                strs = strs.Select(x => x.ToLowerInvariant()).ToArray();

            var ret = new object[strs.Length];
            //lock (stringDictionaryLock)
            //{
            for (int i = 0; i < strs.Length; i++)
                ret[i] = AddStrGetIndex(mappedStrings, strs[i], false);
            //}
            return ret;
        }

        private static int AddStrGetIndex(LockableDictionary<string, int> mappedStrings, string str, bool ignoreCase = true)
        {
            return AddStrGetIndexLocked(mappedStrings, str, ignoreCase);
        }

        private static int AddStrGetIndexInternal(LockableDictionary<string, int> mappedStrings, string str)
        {
            if (!mappedStrings.TryGetValue(str, out int idx))
            {
                idx = mappedStrings.Count;
                mappedStrings.Add(str, idx);
            }
            return idx;
        }

        private static int AddStrGetIndexLocked(LockableDictionary<string, int> mappedStrings, string str, bool ignoreCase = true)
        {
            if (ignoreCase)
                str = str.ToLowerInvariant();

            lock (mappedStrings.SyncRoot)
            {
                return AddStrGetIndexInternal(mappedStrings, str);
            }
        }

        private static void WriteValue<T>(Stream writer, T value)
        {
            switch (value)
            {
                case int v:
                    writer.Write(BitConverter.GetBytes(v));
                    break;
                case float v:
                    writer.Write(BitConverter.GetBytes(v));
                    break;
                case short v:
                    writer.Write(BitConverter.GetBytes(v));
                    break;
                case long v:
                    writer.Write(BitConverter.GetBytes(v));
                    break;
            }
        }

        private static void WriteStringTable(IEnumerable<string> entries, int numstrings, Stream stream)
        {
            stream.Write(BitConverter.GetBytes(numstrings));

            foreach (var entry in entries)
                stream.WriteCString(entry);
        }

        private static void WriteMemoryStream(MemoryStream input, Stream output)
        {
            input.Position = 0;
            input.CopyTo(output);
            //output.Write(input.ToArray());
        }
    }
}
