using System.Collections.Generic;
using System.IO;

namespace MinecraftClone3API.IO
{
    public class FileSystemRaw : FileSystem
    {
        private readonly DirectoryInfo _dir;

        public FileSystemRaw(DirectoryInfo dir) : base(dir.Name)
        {
            _dir = dir;
        }

        public override List<string> GetFiles()
        {
            var list = new List<string>();
            AddDir(list, _dir, _dir);
            return list;
        }

        public override byte[] ReadFile(string path)
        {
            var fullPath = Path.Combine(_dir.FullName, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("File could not be found in the raw file system!", fullPath);
            return File.ReadAllBytes(fullPath);
        }


        private void AddDir(List<string> list, DirectoryInfo currentDir, DirectoryInfo rootDir)
        {
            foreach (var file in currentDir.EnumerateFiles())
            {
                var relativePath = file.FullName.Substring(rootDir.FullName.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                list.Add(relativePath);
            }

            foreach (var dir in currentDir.EnumerateDirectories())
                AddDir(list, dir, rootDir);
        }
    }
}
