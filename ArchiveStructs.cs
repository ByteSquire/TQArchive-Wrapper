
namespace TQArchive_Wrapper
{
    public struct ArzHeader
    {
        public const int headerStart = 0x30004;
        public static int DBTableBaseOffset => 24;

        public int DBTableStart { get; set; }
        public int DBByteSize { get; set; }
        public int DBNumEntries { get; set; }

        public int StrTableStart { get; set; }
        public int StrTableByteSize { get; set; }

        public void WriteTo(BinaryWriter writer)
        {
            // first int32 is constant
            writer.Write(headerStart);

            // database table header
            writer.Write(DBTableStart);
            writer.Write(DBByteSize);
            writer.Write(DBNumEntries);

            // string table header
            writer.Write(StrTableStart);
            writer.Write(StrTableByteSize);
        }

        public void WriteTo(Stream stream)
        {
            using var writer = new BinaryWriter(stream, Constants.Encoding1252, leaveOpen: true);
            WriteTo(writer);
        }

        public static ArzHeader ReadFrom(Stream stream)
        {
            using var reader = new BinaryReader(stream, Constants.Encoding1252, leaveOpen: true);
            return ReadFrom(reader);
        }

        public static ArzHeader ReadFrom(BinaryReader reader)
        {
            // skip constant
            reader.ReadInt32();

            return new ArzHeader
            {
                // database table header
                DBTableStart = reader.ReadInt32(),
                DBByteSize = reader.ReadInt32(),
                DBNumEntries = reader.ReadInt32(),

                // string table header
                StrTableStart = reader.ReadInt32(),
                StrTableByteSize = reader.ReadInt32(),
            };
        }
    }

    public struct DBRFileInfo
    {
        public int NameID { get; set; }
        public string Class { get; set; }
        public int Offset { get; set; }
        public int CompressedLength { get; set; }
        public long TimeStamp { get; set; }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(NameID);
            writer.Write(Class);
            writer.Write(Offset);
            writer.Write(CompressedLength);
            writer.Write(TimeStamp);
        }

        public void WriteTo(Stream stream)
        {
            using var writer = new BinaryWriter(stream, Constants.Encoding1252, leaveOpen: true);
            WriteTo(writer);
        }

        public static DBRFileInfo ReadFrom(Stream stream)
        {
            using var reader = new BinaryReader(stream, Constants.Encoding1252, leaveOpen: true);
            return ReadFrom(reader);
        }

        public static DBRFileInfo ReadFrom(BinaryReader reader)
        {
            return new DBRFileInfo
            {
                NameID = reader.ReadInt32(),
                Class = reader.ReadString(),
                Offset = reader.ReadInt32(),
                CompressedLength = reader.ReadInt32(),
                TimeStamp = reader.ReadInt64(),
            };
        }
    }
}
