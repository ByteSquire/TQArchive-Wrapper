using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TQArchive_Wrapper
{
    /*Usage: archiveTool <file> <command> [command arguments] commands:
    -add <directory> <base> [compression (0: min, 9: max)]: add a file or directory
        relative to the base directory. If a file is already in the archive it will not be added.
    -replace <directory> <base> [compression (0: min, 9: max)]: replace a file or directory
        relative to the base directory. If a file is already in the archive it will be overwritten.
    -update <directory> <base> [compression (0: min, 9: max)]: update a file or directory
        relative to the base directory. Files will only be added if they are newer than
        those already in the archive.
    -remove <file> : remove a file from the archive.
    -extract <location> [file] : extract the files or specified file to the
        specified location.
    -removeMissing <file> <base> : remove the files that are not in the specified directory.
    -compact : compact the archive removing unused files.
    -list : list the files in the archive.
    -stats : display the archive statistics.
    */

    public class ArchiveTool
    {
        private readonly string path;

        private readonly IList<string> errors;

        private readonly IList<string> output;

        public ArchiveTool(string toolPath)
        {
            path = toolPath;
            errors = new List<string>();
            output = new List<string>();
            if (!toolPath.EndsWith(".exe"))
                throw new ArgumentException(string.Format("The toolPath {0} is invalid because the file must be an executable with .exe file extension", toolPath));
            if (!File.Exists(toolPath))
                throw new FileNotFoundException(string.Format("The toolPath {0} is invalid because the file does not exist", toolPath));
        }

        private int RunArchiveTool(params string[] args)
        {
            var pInfo = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var arg in args)
                if (arg != string.Empty)
                    pInfo.ArgumentList.Add(arg);

            using var process = new Process()
            {
                EnableRaisingEvents = true,
                StartInfo = pInfo
            };
            process.OutputDataReceived += (sender, args) => { if (args.Data is not null) output.Add(args.Data); };
            process.ErrorDataReceived += (sender, args) => { if (args.Data is not null) errors.Add(args.Data); };
            bool processStarted = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit(10000);

            return process.ExitCode;
        }

        public IEnumerable<string> GetErrorLines()
        {
            return errors;
        }

        public IEnumerable<string> GetOutputLines()
        {
            return output;
        }

        public int Add(string archivePath, string toAdd, string baseDir, int compressionLevel = 0) =>
            RunArchiveTool(archivePath, "-add", toAdd, baseDir, Math.Clamp(compressionLevel, 0, 9).ToString());

        public int Replace(string archivePath, string toReplace, string baseDir, int compressionLevel = 0) =>
            RunArchiveTool(archivePath, "-replace", toReplace, baseDir, Math.Clamp(compressionLevel, 0, 9).ToString());

        public int Update(string archivePath, string toUpdate, string baseDir, int compressionLevel = 0) =>
            RunArchiveTool(archivePath, "-update", toUpdate, baseDir, Math.Clamp(compressionLevel, 0, 9).ToString());

        public int Remove(string archivePath, string filePath) =>
            RunArchiveTool(archivePath, "-remove", filePath);

        public int Extract(string archivePath, string extractionPath, string filePath = "") =>
            RunArchiveTool(archivePath, "-extract", extractionPath, filePath);

        public int RemoveMissing(string archivePath, string baseDir) =>
            RunArchiveTool(archivePath, "-removeMissing", baseDir);

        public int Compact(string archivePath) =>
            RunArchiveTool(archivePath, "-compact");

        public (int, IEnumerable<string>) List(string archivePath) =>
            (RunArchiveTool(archivePath, "-list"), GetOutputLines());

        public (int, IEnumerable<string>) Stats(string archivePath) =>
            (RunArchiveTool(archivePath, "-stats"), GetOutputLines());
    }

    public class ArchiveToolArchive : ArchiveTool
    {
        private readonly string archivePath;

        public ArchiveToolArchive(string toolPath, string archivePath) : base(toolPath)
        {
            this.archivePath = archivePath;
            var dir = Path.GetDirectoryName(archivePath);
            if (dir != string.Empty && !Directory.Exists(dir))
                throw new DirectoryNotFoundException($"The directory specified by your path {archivePath} doesn't exist!");
        }

        public int Add(string toAdd, string baseDir, int compressionLevel = 0) => Add(archivePath, toAdd, baseDir, compressionLevel);

        public int Replace(string toReplace, string baseDir, int compressionLevel = 0) => Replace(archivePath, toReplace, baseDir, compressionLevel);

        public int Update(string toUpdate, string baseDir, int compressionLevel = 0) => Update(archivePath, toUpdate, baseDir, compressionLevel);

        public int Remove(string filePath) => Remove(archivePath, filePath);

        public int Extract(string extractionPath, string filePath = "") => Extract(archivePath, extractionPath, filePath);

        public int RemoveMissing(string baseDir) => RemoveMissing(archivePath, baseDir);

        public int Compact() => Compact(archivePath);

        public (int, IEnumerable<string>) List() => List(archivePath);

        public (int, IEnumerable<string>) Stats() => Stats(archivePath);
    }

    //TODO: find out how baseDir really works.
    public class ArchiveToolArchiveBase : ArchiveToolArchive
    {
        private readonly string baseDir;

        public ArchiveToolArchiveBase(string toolPath, string archivePath, string baseDir) : base(toolPath, archivePath)
        {
            this.baseDir = baseDir;
            if (!Directory.Exists(baseDir))
                throw new DirectoryNotFoundException($"The specified directory {baseDir} doesn't exist");
        }

        public int Add(string toAdd, int compressionLevel = 0) => Add(toAdd, baseDir, compressionLevel);

        public int Replace(string toReplace, int compressionLevel = 0) => Replace(toReplace, baseDir, compressionLevel);

        public int Update(string toUpdate, int compressionLevel = 0) => Update(toUpdate, baseDir, compressionLevel);

        public int RemoveMissing() => RemoveMissing(baseDir);
    }
}