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

namespace TQArchive_Wrapper
{
    public class ArzWriter
    {
        private readonly string filePath;
        private readonly ILogger? logger;

        private readonly object stringDictionaryLock = new();
        private readonly IDictionary<string, int> combinedStrings;

        public ArzWriter(string filePath, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            this.logger = logger;
            combinedStrings = new Dictionary<string, int>();
            if (!File.Exists(filePath))
                File.Create(filePath).Dispose();
        }

        public event Action<string>? FileDone;

        public void Write(IEnumerable<DBRFile> files)
        {
            //var strEntries = new List<string>();
            combinedStrings.Clear();
            var binaryEntries = new ConcurrentBag<(DBRFileInfo, MemoryStream)>();

            foreach (var file in files)
            //Parallel.ForEach(files, file =>
            {
                using var fileVarsStream = new MemoryStream();

                var fileNameID = AddStrGetIndex(file.FilePath[file.FilePath.IndexOf("records")..]);

                // write templateName as variable, it's excluded in DBRFile entries
                WriteValue(fileVarsStream, (short)2);
                WriteValue(fileVarsStream, (short)1);
                WriteValue(fileVarsStream, AddStrGetIndex("templateName", false));
                WriteValue(fileVarsStream, AddStrGetIndex(file.TemplateRoot.FileName));

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
                    var stringID = AddStrGetIndex(entry.Name, false);

                    string[] arraySplit = new string[] { entry.Value };
                    bool isArray = entry.Template.Class == TQDB_Parser.VariableClass.array;
                    if (isArray)
                        arraySplit = entry.Value.Split(';');
                    short numElements = (short)arraySplit.Length;

                    short typeID = -1;
                    var values = new object[numElements];
                    bool error = false;
                    // type as int16
                    switch (entry.Template.Type)
                    {
                        case TQDB_Parser.VariableType.@int:
                            if (!TryGetIntValues(arraySplit, out values))
                                error = true;
                            typeID = 0;
                            break;
                        case TQDB_Parser.VariableType.real:
                            typeID = 1;
                            if (!TryGetFloatValues(arraySplit, out values))
                                error = true;
                            break;
                        case TQDB_Parser.VariableType.file:
                            typeID = 2;
                            for (var i = 0; i < numElements; i++)
                            {
                                var element = arraySplit[i];
                                values[i] = AddStrGetIndex(element);
                            }
                            break;
                        case TQDB_Parser.VariableType.@string:
                        case TQDB_Parser.VariableType.equation:
                            typeID = 2;
                            for (var i = 0; i < numElements; i++)
                            {
                                var element = arraySplit[i];
                                values[i] = AddStrGetIndex(element, false);
                            }
                            break;
                        case TQDB_Parser.VariableType.@bool:
                            typeID = 3;
                            if (!TryGetIntValues(arraySplit, out values))
                                error = true;
                            break;
                    }
                    if (error)
                    {
                        combinedStrings.Remove(entry.Name);
                        continue;
                    }

                    WriteVariable(fileVarsStream, stringID, typeID, values);
                }
                // compress the values using ZLib
                var myBinaryValuesStream = CompressValues(fileVarsStream);

                // dbr file info
                DBRFileInfo fileInfo = CreateFileInfo(file, fileNameID, (int)myBinaryValuesStream.Length);

                binaryEntries.Add((fileInfo, myBinaryValuesStream));

                FileDone?.Invoke(file.FilePath);
            }
            //);
            using var binaryValuesStream = new MemoryStream();
            using var combinedDBREntries = new MemoryStream();
            foreach ((var info, var stream) in binaryEntries)
            {
                var entry = info;
                entry.Offset = (int)binaryValuesStream.Position;
                entry.WriteTo(combinedDBREntries);

                var dbrEntry = stream;
                WriteMemoryStream(dbrEntry, binaryValuesStream);
                dbrEntry.Dispose();
            }

            // database entries table header
            int dbtableStart = (int)binaryValuesStream.Length;
            dbtableStart += ArzHeader.DBTableBaseOffset; // add 24 bytes for header

            combinedDBREntries.Flush();
            // database record files header
            int dbbyteSize = (int)combinedDBREntries.Length;
            int dbnumEntries = files.Count();

            // string table header
            int strtableStart = dbtableStart + dbbyteSize;
            // (4 bytes) int32 for length of string
            int strbyteSize = combinedStrings.Sum(x => Constants.Encoding1252.GetBytes(x.Key).Length + 4);

            int strnumEntries = combinedStrings.Count;
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
                WriteStringTable(combinedStrings.OrderBy(x => x.Value).Select(x => x.Key), strnumEntries, stream);
            }
            catch (IOException e)
            {
                logger?.LogError(e, "Error writing to arz archive {archive}", filePath);
            }
        }

        private DBRFileInfo CreateFileInfo(DBRFile file, int nameID, int length)
        {
            // dbr file info
            var currDBRNameID = nameID;
            var filePath = file.FilePath;
            // timestap for comparison
            var time = File.GetLastWriteTimeUtc(filePath).ToFileTimeUtc();

            var fileInfo = new DBRFileInfo
            {
                Class = file["Class"].Value,
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

        private bool TryGetFloatValues(string[] elements, out object[]? values)
        {
            values = new object[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (!TQNumberString.TryParseTQString(element, out float fVal))
                {
                    logger?.LogError("{value} is not a valid float", element);
                    values = null;
                    return false;
                }
                values[i] = fVal;
            }

            return true;
        }

        private bool TryGetIntValues(string[] elements, out object[]? values)
        {
            values = new object[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (!TQNumberString.TryParseTQString(element, out int iVal))
                {
                    logger?.LogError("{value} is not a valid float", element);
                    values = null;
                    return false;
                }
                values[i] = iVal;
            }

            return true;
        }

        private static void WriteVariable(MemoryStream stream, int stringID, short typeID, object[] values)
        {
            // write type id as int16
            WriteValue(stream, typeID);
            // write number of values as int16
            WriteValue(stream, values.Length);
            // write the id of the name as int32
            WriteValue(stream, stringID);

            // write the values continuously
            foreach (var value in values)
            {
                WriteValue(stream, value);
            }
        }

        private int AddStrGetIndex(string str, bool ignoreCase = true)
        {
            // ignoreCase can save a bit of size, ArtManager is really inconsistent
            if (ignoreCase)
                str = str.ToLowerInvariant();
            lock (stringDictionaryLock)
            {
                if (!combinedStrings.TryGetValue(str, out int idx))
                {
                    idx = combinedStrings.Count;
                    combinedStrings.Add(str, idx);
                }
                return idx;
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
