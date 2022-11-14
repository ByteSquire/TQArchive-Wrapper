using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TQArchive_Wrapper
{
    public static class BinaryReaderExtensions
    {
        public static string ReadCString(this BinaryReader reader)
        {
            var byteLength = reader.ReadInt32();
            var stringBytes = reader.ReadBytes(byteLength);
            return Constants.Encoding1252.GetString(stringBytes);
        }

        public static void WriteCString(this Stream stream, string str)
        {
            var encoding = Constants.Encoding1252;
            var bytes = encoding.GetBytes(str);
            var len = bytes.Length;
            stream.Write(BitConverter.GetBytes(len));
            stream.Write(bytes);
        }
    }
}
