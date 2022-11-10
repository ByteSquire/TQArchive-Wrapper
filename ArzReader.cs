using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TQDB_Parser;
using TQDB_Parser.DBR;

namespace TQArchive_Wrapper
{
    public class ArzReader
    {
        private readonly string filePath;
        private readonly Encoding enc1252;
        private readonly ILogger? logger;

        private readonly List<string> stringList;
        private readonly TemplateManager tplManager;
        private bool headerInitialised;
        private bool stringsRead;
        private ArzHeader header;

        public ArzReader(string filePath, TemplateManager templateManager, ILogger? logger = null)
        {
            this.filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Error opening arz file for reading", filePath);
            var encoding = Constants.Encoding1252;
            enc1252 = encoding;
            this.logger = logger;

            stringList = new();
            tplManager = templateManager;
            headerInitialised = false;
            stringsRead = false;
        }

        private void ReadHeader(BinaryReader reader)
        {
            header = ArzHeader.ReadFrom(reader);
            headerInitialised = true;
        }

        public IEnumerable<string> GetStringList()
        {
            if (stringsRead)
            {
                foreach (var str in stringList)
                    yield return str;

                yield break;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            stringsRead = true;
            stream.Seek(header.StrTableStart, SeekOrigin.Begin);

            var numStrings = reader.ReadInt32();
            for (int i = 0; i < numStrings; i++)
            {
                yield return reader.ReadString();
            }
        }

        public IEnumerable<string> GetStrings(params int[] ids)
        {
            foreach (var id in ids)
            {
                if (stringList.Count > id)
                    yield return stringList[id];
            }
        }

        public IEnumerable<DBRFileInfo> GetDBRFileInfos()
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Constants.Encoding1252, true);
            if (!headerInitialised)
                ReadHeader(reader);

            stream.Seek(header.DBTableStart, SeekOrigin.Begin);

            for (int i = 0; i < header.DBNumEntries; i++)
                yield return DBRFileInfo.ReadFrom(reader);
        }

        public IEnumerable<DBRFile> GetFiles(IEnumerable<DBRFileInfo> infos)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new ZLibStream(stream, CompressionLevel.SmallestSize, true);
            if (!headerInitialised)
            {
                throw new Exception("Cannot be uninitialised when passing infos");
            }

            foreach (var info in infos)
            {
                stream.Seek(info.Offset + ArzHeader.DBTableBaseOffset, SeekOrigin.Begin);
                yield return ReadDBRFile(reader, info);
            }
        }

        private DBRFile ReadDBRFile(ZLibStream reader, DBRFileInfo info)
        {
            var tplRoot = tplManager.GetRoot(GetStrings(info.NameID).Single());
            tplManager.ResolveIncludes(tplRoot);

            var entries = new Dictionary<string, DBREntry>();

            for (int i = 0; i < info.CompressedLength;)
            {
                var infoBytes = new byte[8];
                reader.Read(infoBytes, 0, infoBytes.Length);
                i += 8;

                var type = BitConverter.ToInt16(infoBytes, 0);
                var numValues = BitConverter.ToInt16(infoBytes, 2);
                var nameID = BitConverter.ToInt32(infoBytes, 4);


            }

            return new DBRFile(string.Empty, tplRoot, entries, logger);
        }
    }
}
