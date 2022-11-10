using System.Text;

namespace TQArchive_Wrapper
{
    public static class Constants
    {
        public static Encoding Encoding1252
        {
            get
            {
                var encoding = CodePagesEncodingProvider.Instance.GetEncoding(1252);
                if (encoding is null)
                    throw new Exception("Could not load Windows-1252 encoding");
                return encoding;
            }
        }
    }
}
