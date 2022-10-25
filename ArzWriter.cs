using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Compression;
using TQDB_Parser.DBR;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace TQArchive_Wrapper
{
    public class ArzWriter
    {
        private readonly string filePath;
        private readonly Encoding enc1252;
        private readonly IFormatProvider invariant;
        private readonly ILogger? logger;

        public ArzWriter(string filePath, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(1252);
            if (encoding is null)
                throw new Exception("Could not load Windows-1252 encoding");
            enc1252 = encoding;
            invariant = CultureInfo.InvariantCulture;
            this.logger = logger;
            if (!File.Exists(filePath))
                File.Create(filePath).Dispose();
        }

        private const int headerStart = 0x30004;
        private const int dbrBaseOffset = 24;

        public event Action<string>? FileDone;

        public void Write(IEnumerable<DBRFile> files)
        {
            //var strEntries = new List<string>();
            var strEntries = new Dictionary<string, int>();
            var dbrEntries = new MemoryStream();

            using var binaryValuesStream = new MemoryStream();

            var dbrOffset = 0;
            foreach (var file in files)
            {
                using var fileVarsStream = new MemoryStream();
                // dbr file info
                var currDBRNameID = AddStrGetIndex(strEntries, file.FilePath[file.FilePath.IndexOf("records")..]);

                // write templateName as variable, it's excluded in DBRFile entries
                WriteValue(fileVarsStream, (short)2);
                WriteValue(fileVarsStream, (short)1);
                WriteValue(fileVarsStream, AddStrGetIndex(strEntries, "templateName", false));
                WriteValue(fileVarsStream, AddStrGetIndex(strEntries, file.TemplateRoot.FileName));

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
                    var stringID = AddStrGetIndex(strEntries, entry.Name, false);

                    bool isArray = entry.Template.Class == TQDB_Parser.VariableClass.array;
                    string[] arraySplit = new string[] { entry.Value };
                    if (isArray)
                        arraySplit = entry.Value.Split(';');
                    short numElements = (short)arraySplit.Length;

                    short typeID = -1;
                    var error = false;
                    // write type as int16
                    switch (entry.Template.Type)
                    {
                        case TQDB_Parser.VariableType.@int:
                            typeID = 0;
                            break;
                        case TQDB_Parser.VariableType.real:
                            typeID = 1;
                            break;
                        case TQDB_Parser.VariableType.file:
                        case TQDB_Parser.VariableType.@string:
                        case TQDB_Parser.VariableType.equation:
                            typeID = 2;
                            break;
                        case TQDB_Parser.VariableType.@bool:
                            typeID = 3;
                            break;
                    }

                    var values = new object[numElements];
                    // write the value(s) as a byte array
                    for (var i = 0; i < numElements; i++)
                    {
                        var element = arraySplit[i];
                        try
                        {
                            switch (entry.Template.Type)
                            {
                                case TQDB_Parser.VariableType.@int:
                                case TQDB_Parser.VariableType.@bool:
                                    {
                                        values[i] = int.Parse(element, invariant);
                                        break;
                                    }
                                case TQDB_Parser.VariableType.real:
                                    {
                                        values[i] = float.Parse(element, invariant);
                                        break;
                                    }
                                case TQDB_Parser.VariableType.file:
                                    {
                                        values[i] = AddStrGetIndex(strEntries, element);
                                        break;
                                    }
                                case TQDB_Parser.VariableType.@string:
                                case TQDB_Parser.VariableType.equation:
                                    {
                                        values[i] = AddStrGetIndex(strEntries, element, false);
                                        break;
                                    }
                            }
                        }
                        catch (FormatException e)
                        {
                            logger?.LogError(e, "{value}", element);
                            error = true;
                        }
                    }
                    // could consider skipping single entries in an array
                    if (error)
                        continue;

                    // write type id as int16
                    WriteValue(fileVarsStream, typeID);
                    // write number of values as int16
                    WriteValue(fileVarsStream, numElements);
                    // write the id of the name as int32
                    WriteValue(fileVarsStream, stringID);

                    // write the values continuously
                    foreach (var value in values)
                    {
                        WriteValue(fileVarsStream, value);
                    }
                }
                // compress the values using ZLib
                using var binaryValuesWriter = new ZLibStream(binaryValuesStream, CompressionLevel.SmallestSize, true);
                WriteMemoryStream(fileVarsStream, binaryValuesWriter);
                binaryValuesWriter.Dispose();

                // dbr file info
                var currDBRLength = (int)binaryValuesStream.Length - dbrOffset;
                var currDBRClass = file["Class"].Value;

                WriteValue(dbrEntries, currDBRNameID);
                WriteString(dbrEntries, currDBRClass, enc1252);
                WriteValue(dbrEntries, dbrOffset);
                // compressed size
                WriteValue(dbrEntries, currDBRLength);
                // timestap for comparison
                var time = File.GetLastWriteTimeUtc(file.FilePath);
                WriteValue(dbrEntries, time.ToFileTimeUtc());

                dbrOffset += currDBRLength;

                FileDone?.Invoke(file.FilePath);
            }

            // database entries table header
            int dbtableStart = (int)binaryValuesStream.Length;
            dbtableStart += dbrBaseOffset; // add 24 bytes header

            dbrEntries.Flush();
            // database record files header
            int dbbyteSize = (int)dbrEntries.Length;
            int dbnumEntries = files.Count();

            // string table header
            int strtableStart = dbtableStart + dbbyteSize;
            int strbyteSize = strEntries.Sum(x => enc1252.GetBytes(x.Key).Length + 4); // (4 bytes) int32 for length of string

            int strnumEntries = strEntries.Count;
            strbyteSize += BitConverter.GetBytes(strnumEntries).Length; // add length of numentries

            try
            {
                using var stream = new FileStream(filePath, FileMode.Truncate, FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream, enc1252, leaveOpen: true);
                // first int32 is constant
                writer.Write(headerStart);

                // database table header
                writer.Write(dbtableStart);
                writer.Write(dbbyteSize);
                writer.Write(dbnumEntries);

                // string table header
                writer.Write(strtableStart);
                writer.Write(strbyteSize);

                // database entries compressed
                WriteMemoryStream(binaryValuesStream, stream);
                // database record file infos
                WriteMemoryStream(dbrEntries, stream);
                // strings uncompressed
                WriteStringTable(strEntries.Keys, strnumEntries, stream, enc1252);
            }
            catch (IOException e)
            {
                logger?.LogError(e, "Error writing to arz archive {archive}", filePath);
            }
        }

        private static int AddStrGetIndex(IDictionary<string, int> strings, string str, bool ignoreCase = true)
        {
            // ignoreCase can save a bit of size, ArtManager is really inconsistent
            if (ignoreCase)
                str = str.ToLowerInvariant();
            if (!strings.TryGetValue(str, out int idx))
            {
                idx = strings.Count;
                strings.Add(str, idx);
            }
            return idx;
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

        private static void WriteStringTable(IEnumerable<string> entries, int numstrings, Stream stream, Encoding? encoding = null)
        {
            stream.Write(BitConverter.GetBytes(numstrings));

            foreach (var entry in entries)
                WriteString(stream, entry, encoding);
        }

        private static void WriteString(Stream stream, string str, Encoding? encoding = null)
        {
            encoding ??= Encoding.Default;
            var bytes = encoding.GetBytes(str);
            var len = bytes.Length;
            stream.Write(BitConverter.GetBytes(len));
            stream.Write(bytes);
        }

        private static void WriteMemoryStream(MemoryStream input, Stream output)
        {
            output.Write(input.ToArray());
        }
    }
}
