using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Compression;
using System.Threading.Tasks;
using TQDB_Parser.DBR;
using TQDB_Parser.Blocks;
using System.Reflection.PortableExecutable;
using Microsoft.VisualBasic;
using System.Xml.Linq;

namespace TQArchive_Wrapper
{
    public class ArzWriter
    {
        private readonly string filePath;
        private readonly Encoding? enc1252;

        public ArzWriter(string filePath)
        {
            this.filePath = Path.GetFullPath(filePath);
            enc1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252);
            if (enc1252 is null)
                throw new Exception("Could not load Windows-1252 encoding");
            //if (!File.Exists(filePath))
            //    File.Create(filePath);
        }

        private const int headerStart = 0x30004;
        private const int dbrBaseOffset = 24;

        public void Write(IEnumerable<DBRFile> files)
        {
            var strEntries = new List<string>();
            var dbrInfos = new List<(string Class, int Offset, int DBRLength, int Pos)>();
            var dbrEntries = new MemoryStream();

            using var binaryValuesStream = new MemoryStream();
            using var binaryValuesWriter = new ZLibStream(binaryValuesStream, CompressionLevel.Fastest, true);

            var dbrOffset = 0;
            foreach (var file in files)
            {
                strEntries.Add(file.FilePath[file.FilePath.IndexOf("records")..]);
                var currDBRPos = strEntries.Count - 1;
                strEntries.Add("templateName");
                strEntries.Add(file.TemplateRoot.FileName);

                // write templateName as variable, it's excluded in DBRFile entries
                WriteValue(binaryValuesWriter, (short)2);
                WriteValue(binaryValuesWriter, (short)1);
                WriteValue(binaryValuesWriter, strEntries.Count - 2);
                WriteValue(binaryValuesWriter, strEntries.Count - 1);

                GroupBlock header = file.TemplateRoot.GetGroups().Single(x => x.Name == "Header");

                var entries = file.Entries
                    .Where(x => x.Template.Type != TQDB_Parser.VariableType.eqnVariable)
                    .Where(x => x.Template.Type != TQDB_Parser.VariableType.include)
                    .Where(x => x.IsValid())
                    .Where(x => !string.IsNullOrEmpty(x.Value))
                    .ToList();
                entries.Sort((a, b) =>
                {
                    var ret = a.Name.CompareTo(b.Name);
                    ret -= header.IsChild(a.Template).CompareTo(header.IsChild(b.Template)) * 2;
                    return ret;
                });

                foreach (var entry in entries)
                {
                    strEntries.Add(entry.Name);
                    var stringID = strEntries.Count - 1;

                    bool isArray = entry.Template.Class == TQDB_Parser.VariableClass.array;
                    string[] arraySplit = new string[] { entry.Value };
                    if (isArray)
                        arraySplit = entry.Value.Split(';');
                    short numElements = (short)arraySplit.Length;

                    // write type as int16
                    switch (entry.Template.Type)
                    {
                        case TQDB_Parser.VariableType.@int:
                            WriteValue(binaryValuesWriter, (short)0);
                            break;
                        case TQDB_Parser.VariableType.real:
                            WriteValue(binaryValuesWriter, (short)1);
                            break;
                        case TQDB_Parser.VariableType.file:
                        case TQDB_Parser.VariableType.@string:
                        case TQDB_Parser.VariableType.equation:
                            WriteValue(binaryValuesWriter, (short)2);
                            break;
                        case TQDB_Parser.VariableType.@bool:
                            WriteValue(binaryValuesWriter, (short)3);
                            break;
                    }

                    // write number of values and the id of the name as int32
                    WriteValue(binaryValuesWriter, numElements);
                    WriteValue(binaryValuesWriter, stringID);

                    // write the value(s) as a byte array
                    foreach (var element in arraySplit)
                    {
                        switch (entry.Template.Type)
                        {
                            case TQDB_Parser.VariableType.@int:
                                {
                                    var value = int.Parse(element);
                                    WriteValue(binaryValuesWriter, value);
                                    break;
                                }
                            case TQDB_Parser.VariableType.real:
                                {
                                    var value = float.Parse(element);
                                    WriteValue(binaryValuesWriter, value);
                                    break;
                                }
                            case TQDB_Parser.VariableType.file:
                            case TQDB_Parser.VariableType.@string:
                            case TQDB_Parser.VariableType.equation:
                                {
                                    strEntries.Add(element);
                                    WriteValue(binaryValuesWriter, stringID + 1);
                                    break;
                                }
                            case TQDB_Parser.VariableType.@bool:
                                {
                                    var value = int.Parse(element);
                                    WriteValue(binaryValuesWriter, value);
                                    break;
                                }
                        }
                    }
                }
                binaryValuesWriter.Flush();

                var currDBRLength = (int)binaryValuesStream.Length;
                var currDBRClass = header.GetVariables().Single(x => x.Name == "Class").Value;
                dbrInfos.Add((currDBRClass, dbrOffset, currDBRLength, currDBRPos));

                dbrOffset += currDBRLength;
            }

            foreach (var info in dbrInfos)
            {
                WriteValue(dbrEntries, info.Pos);
                WriteString(info.Class, dbrEntries);
                WriteValue(dbrEntries, info.Offset);
                WriteValue(dbrEntries, info.DBRLength);
                WriteValue(dbrEntries, 0); // something
                WriteValue(dbrEntries, 0); // something
            }

            //header
            binaryValuesWriter.Dispose();
            Console.WriteLine(binaryValuesStream.Length);
            int dbtableStart = (int)binaryValuesStream.Length + dbrBaseOffset; // 24 bytes header

            Console.WriteLine(dbrEntries.Length);
            int dbbyteSize = (int)dbrEntries.Length;
            int dbnumEntries = files.Count();

            int strtableStart = dbtableStart + dbbyteSize;
            int strbyteSize = strEntries.Sum(x => enc1252.GetBytes(x).Length + 4); // 4 bytes int32 length of string

            int strnumEntries = strEntries.Count;

            try
            {
                using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
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

                WriteMemoryStream(binaryValuesStream, stream);
                WriteMemoryStream(dbrEntries, stream);
                WriteStringTable(strEntries, strnumEntries, stream);

                // footer (irrelevant?)
                //writer.Write(strnumEntries);
                //writer.Write(0);
                //writer.Write(0);
                //writer.Write(0);
                //writer.Write(0);
            }
            catch (IOException e)
            {

            }
        }

        private void WriteValue<T>(Stream writer, T value)
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
                default:
                    break;
            }
        }

        private void WriteStringTable(IEnumerable<string> entries, int numstrings, Stream stream)
        {
            stream.Write(BitConverter.GetBytes(numstrings));

            foreach (var entry in entries)
                WriteString(entry, stream);
        }

        private void WriteString(string str, Stream stream)
        {
            var bytes = enc1252.GetBytes(str);
            var len = bytes.Length;
            stream.Write(BitConverter.GetBytes(len));
            stream.Write(bytes);
        }

        private void WriteMemoryStream(MemoryStream input, Stream output)
        {
            output.Write(input.ToArray());
        }
    }
}
