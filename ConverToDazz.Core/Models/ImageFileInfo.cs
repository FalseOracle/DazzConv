using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace ConverToDazz.Core.Models
{
    public class ImageFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string Format { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }

        public static ImageFileInfo FromFile(string filePath)
        {
            var fi = new FileInfo(filePath);
            return new ImageFileInfo
            {
                FileName = fi.Name,
                Size = fi.Length,
                LastModified = fi.LastWriteTime,
                Format = Path.GetExtension(filePath).TrimStart('.').ToLower()
            };
        }
    }
}