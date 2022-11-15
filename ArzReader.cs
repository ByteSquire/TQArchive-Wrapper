using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;
using TQDB_Parser.Extensions;

namespace TQArchive_Wrapper
{
    public class ArzReader
    {
        private readonly string filePath;
        private readonly ILogger? logger;

        private readonly List<string> stringList;
        private readonly List<DBRFileInfo> fileInfos;
        private readonly Dictionary<string, DBRFileInfo> mappedFileInfos;
        private readonly Dictionary<string, RawDBRFile> files;
        private long lastStringOffset;
        private int numStrings;
        private long lastFileInfoOffset;
        private bool headerInitialised;
        private ArzHeader header;

        public ArzReader(string filePath, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                var exc = new FileNotFoundException("Error opening arz file for reading", filePath);
                logger?.LogError(exc, "File {file}", filePath);
                throw exc;
            }
            if (new FileInfo(filePath).Length == 0)
            {
                var exc = new ArgumentException("Trying to read an empty file", nameof(filePath));
                logger?.LogError(exc, "File {file}", filePath);
                throw exc;
            }
            this.logger = logger;

            stringList = new();
            fileInfos = new();
            mappedFileInfos = new();
            files = new();
            headerInitialised = false;
            lastStringOffset = 0;
            numStrings = -1;
            lastFileInfoOffset = 0;
        }

        private void ReadHeader(BinaryReader reader)
        {
            try
            {
                header = ArzHeader.ReadFrom(reader);
                lastStringOffset = header.StrTableStart;
                lastFileInfoOffset = header.DBTableStart;
                fileInfos.Capacity = header.DBNumEntries;
                mappedFileInfos.EnsureCapacity(header.DBNumEntries);
                headerInitialised = true;
            }
            catch (IOException e)
            {
                logger?.LogError(e, "File {file} has invalid header", filePath);
                throw new ArgumentException("Trying to read a file with an invalid header");
            }
        }

        public ArzHeader GetHeader()
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            return header;
        }

        public IEnumerable<string> GetStringList()
        {
            if (stringList.Count > 0)
            {
                var stringListEnumerator = stringList.GetEnumerator();
                while (stringListEnumerator.MoveNext())
                {
                    yield return stringListEnumerator.Current;
                }
                if (stringList.Count == numStrings)
                    yield break;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            if (stringList.Count == 0)
            {
                stream.Seek(header.StrTableStart, SeekOrigin.Begin);
                numStrings = reader.ReadInt32();
            }
            else
                stream.Seek(lastStringOffset, SeekOrigin.Begin);

            for (int i = stringList.Count; i < numStrings; i++)
            {
                var currStr = string.Empty;

                currStr = reader.ReadCString();
                stringList.Add(currStr);

                yield return currStr;
            }
            // should be irrelevant, stream is read to end
            lastStringOffset = stream.Position;
        }

        private void CheckStringID(int id)
        {
            if (id < 0 || numStrings > -1 && id >= numStrings)
            {
                var exc = new ArgumentOutOfRangeException("ids", id, "The passed id is out of range for this archive");
                logger?.LogError(exc, "File {file}", filePath);
                throw exc;
            }
        }

        public IEnumerable<string> GetStrings(params int[] ids)
        {
            if (ids.Length == 1)
            {
                yield return GetString(ids[0]);
                yield break;
            }
            var orderedIDs = ids.OrderBy(x => x);
            var maxID = orderedIDs.Last();
            var idsEnumerator = orderedIDs.GetEnumerator();

            idsEnumerator.MoveNext();
            var id = idsEnumerator.Current;
            CheckStringID(id);
            while (stringList.Count > id)
            {
                yield return stringList[id];
                if (!idsEnumerator.MoveNext())
                    yield break;
                id = idsEnumerator.Current;
                CheckStringID(id);
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            stream.Seek(lastStringOffset, SeekOrigin.Begin);

            if (numStrings == -1)
                numStrings = reader.ReadInt32();

            CheckStringID(id);
            var preloadMax = Math.Min(numStrings, maxID + 6);

            for (int i = stringList.Count; i < preloadMax; i++)
            {
                var currStr = string.Empty;

                currStr = reader.ReadCString();
                stringList.Add(currStr);

                if (i == id)
                {
                    yield return currStr;
                    if (idsEnumerator.MoveNext())
                    {
                        id = idsEnumerator.Current;
                        CheckStringID(id);
                    }
                }
            }
            lastStringOffset = stream.Position;
        }

        //public string GetString(int index) => GetStrings(index).First();

        public string GetString(int index)
        {
            CheckStringID(index);
            if (stringList.Count > index)
            {
                return stringList[index];
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            stream.Seek(lastStringOffset, SeekOrigin.Begin);

            if (numStrings == -1)
                numStrings = reader.ReadInt32();

            CheckStringID(index);
            string ret = string.Empty;
            var preloadMax = Math.Min(numStrings, index + 6);

            for (int i = stringList.Count; i < preloadMax; i++)
            {
                var currStr = string.Empty;

                currStr = reader.ReadCString();
                stringList.Add(currStr);

                if (i == index)
                {
                    ret = currStr;
                }
            }
            lastStringOffset = stream.Position;
            return ret;
        }

        public IEnumerable<DBRFileInfo> GetDBRFileInfos()
        {
            if (headerInitialised && fileInfos.Count > 0)
            {
                var dbrFileInfoEnumerator = fileInfos.GetEnumerator();
                while (dbrFileInfoEnumerator.MoveNext())
                {
                    yield return dbrFileInfoEnumerator.Current;
                }
                if (fileInfos.Count == header.DBNumEntries)
                    yield break;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            stream.Seek(lastFileInfoOffset, SeekOrigin.Begin);

            for (int i = fileInfos.Count; i < header.DBNumEntries; i++)
            {
                var currInfo = DBRFileInfo.ReadFrom(reader);
                fileInfos.Add(currInfo);
                //mappedFileInfos.Add(GetString(currInfo.NameID), currInfo);
                yield return currInfo;
            }
            lastFileInfoOffset = stream.Position;
        }

        public IEnumerable<RawDBRFile> GetFiles()
        {
            return GetFiles(GetDBRFileInfos());
        }

        public IEnumerable<RawDBRFile> GetFiles(IEnumerable<DBRFileInfo> infos)
        {
            foreach (var info in infos)
            {
                yield return GetFile(info);
            }
        }

        public RawDBRFile GetFile(DBRFileInfo info)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            return ReadDBRFile(stream, info);
        }

        public MemoryStream GetCompressedFileStream(DBRFileInfo info)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            return ExtractCompressedFile(stream, info);
        }

        public IEnumerable<MemoryStream> GetCompressedFileStreams()
        {
            return GetCompressedFileStreams(GetDBRFileInfos());
        }

        public IEnumerable<MemoryStream> GetCompressedFileStreams(IEnumerable<DBRFileInfo> infos)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var info in infos)
            {
                yield return ExtractCompressedFile(stream, info);
            }
        }

        public MemoryStream GetDecompressedFileStream(DBRFileInfo info)
        {
            return CreateDecompressedStream(GetCompressedFileStream(info));
        }

        public IEnumerable<MemoryStream> GetDecompressedFileStreams()
        {
            return GetDecompressedFileStreams(GetDBRFileInfos());
        }

        public IEnumerable<MemoryStream> GetDecompressedFileStreams(IEnumerable<DBRFileInfo> infos)
        {
            return GetCompressedFileStreams(infos).Select(x => CreateDecompressedStream(x));
        }

        private RawDBRFile ReadDBRFile(Stream stream, DBRFileInfo info)
        {
            var fileName = GetString(info.NameID);
            var fileOffset = info.Offset + ArzHeader.DBTableBaseOffset;

            if (mappedFileInfos.TryGetValue(fileName, out var cachedInfo))
                if (info.Offset != cachedInfo.Offset)
                {
                    logger?.LogWarning("Trying to read a dbr file with the same name but a different offset to one already read");
                }

            if (files.TryGetValue(fileName, out var cachedFile))
                return cachedFile;

            try
            {
                using MemoryStream inflatedStream = ExtractCompressedFile(stream, info);

                using MemoryStream deflatedStream = CreateDecompressedStream(inflatedStream);

                var rawEntries = ReadRawEntries(deflatedStream);
                if (!rawEntries.TryGetValue(TQDB_Parser.Constants.TemplateKey, out var templateName))
                {
                    var exc = new Exception("Missing templateName");
                    throw exc;
                }
                rawEntries.Remove(TQDB_Parser.Constants.TemplateKey);

                return new RawDBRFile { FileName = fileName, TemplateName = templateName, RawEntries = rawEntries };
            }
            catch (Exception e)
            {
                logger?.LogError(e, "Error reading file at {offset}", fileOffset);
                throw;
            }
        }

        public RawDBRFile ReadDBRFile(Stream decompressedStream, string fileName)
        {
            var rawEntries = ReadRawEntries(decompressedStream);
            if (!rawEntries.TryGetValue(TQDB_Parser.Constants.TemplateKey, out var templateName))
            {
                var exc = new Exception("Missing templateName");
                throw exc;
            }
            rawEntries.Remove(TQDB_Parser.Constants.TemplateKey);

            return new RawDBRFile { FileName = fileName, TemplateName = templateName, RawEntries = rawEntries };
        }

        private Dictionary<string, string> ReadRawEntries(Stream deflatedStream)
        {
            // Create reader for decompressed data
            using var reader = new BinaryReader(deflatedStream);
            var rawEntries = new Dictionary<string, string>();

            // Read all decompressed data
            while (reader.PeekChar() >= 0)
            {
                // Read variable info
                var type = reader.ReadInt16();
                var numValues = reader.ReadInt16();
                var nameID = reader.ReadInt32();
                var varName = GetString(nameID);

                // Check Variable validity
                if (numValues < 1)
                {
                    logger?.LogWarning("Found variable {name} with less than 1 value, skipping!", varName);
                    // Might lead to reading unknown stuff
                    continue;
                }
                // Read variable value based on type
                string value;
                switch (type)
                {
                    case 0:
                        value = ReadIntValue(reader, numValues);
                        break;
                    case 1:
                        value = ReadFloatValue(reader, numValues);
                        break;
                    case 2:
                        value = ReadStringValue(reader, numValues);
                        break;
                    case 3:
                        value = ReadIntValue(reader, numValues);
                        break;
                    default:
                        var exc = new Exception("Error reading dbr compressed file");
                        logger?.LogError(exc, "Found variable {name} of invalid type {type}, skipping!", varName, type);
                        // May lead to reading unknown stuff
                        throw exc;
                }

                rawEntries.Add(varName, value);
            }
            return rawEntries;
        }

        private string ReadIntValue(BinaryReader reader, int numValues)
        {
            StringBuilder builder = new();
            builder.Append(reader.ReadInt32().ToTQString());

            for (int v = 1; v < numValues; v++)
            {
                builder.Append(';');
                builder.Append(reader.ReadInt32().ToTQString());
            }

            return builder.ToString();
        }

        private string ReadFloatValue(BinaryReader reader, int numValues)
        {
            StringBuilder builder = new();
            builder.Append(reader.ReadSingle().ToTQString());

            for (int v = 1; v < numValues; v++)
            {
                builder.Append(';');
                builder.Append(reader.ReadSingle().ToTQString());
            }

            return builder.ToString();
        }

        private string ReadStringValue(BinaryReader reader, int numValues)
        {
            StringBuilder builder = new();
            // strings are written as ints referencing indices in the string table
            var stringIDs = new int[numValues];
            for (int v = 0; v < numValues; v++)
            {
                stringIDs[v] = reader.ReadInt32();
            }

            var stringValues = GetStrings(stringIDs);
            //if (numValues > 1)
            builder.Append(string.Join(";", stringValues));
            //else
            //    builder.Append(stringValues.First());

            return builder.ToString();
        }

        private static MemoryStream CreateDecompressedStream(MemoryStream inflatedStream)
        {
            // Deflate/Decompress file into new buffer used in MemoryStream
            using var deflater = new ZLibStream(inflatedStream, CompressionMode.Decompress, true);
            var estimate = 1024;
            var deflatedStream = new MemoryStream(estimate);
            var deflatedBuffer = new byte[estimate];
            int len;
            while ((len = deflater.Read(deflatedBuffer)) > 0)
            {
                deflatedStream.Write(deflatedBuffer, 0, len);
            }
            deflater.Dispose();
            inflatedStream.Dispose();

            // Reset MemoryStream
            deflatedStream.Position = 0;
            return deflatedStream;
        }

        private MemoryStream ExtractCompressedFile(Stream stream, DBRFileInfo info)
        {
            var fileOffset = info.Offset + ArzHeader.DBTableBaseOffset;
            var compressedLength = info.CompressedLength;

            // Copy compressed file to buffer used in MemoryStream
            stream.Seek(fileOffset, SeekOrigin.Begin);
            var buffer = new byte[compressedLength];
            var bytesRead = stream.Read(buffer);
            // risky but efficient
            stream.Dispose();

            if (bytesRead != compressedLength)
            {
                var exc = new Exception("Error reading dbr compressed file");
                logger?.LogError(exc, "Error reading file at {offset}, reading out of bounds", fileOffset);
                throw exc;
            }
            var inflatedStream = new MemoryStream(buffer);
            return inflatedStream;
        }
    }
}
