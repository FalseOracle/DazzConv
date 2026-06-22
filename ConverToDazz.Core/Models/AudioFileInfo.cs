using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Models
{
    public class AudioFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public int Bitrate { get; set; }

        public static async Task<AudioFileInfo> FromFileAsync(string ffmpegPath, string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Файл не найден", filePath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{filePath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();
            string output = process.StandardError.ReadToEnd();
            await process.WaitForExitAsync();

            TimeSpan duration = TimeSpan.Zero;
            int bitrate = 0;

            string durPrefix = "Duration: ";
            int durIndex = output.IndexOf(durPrefix);
            if (durIndex >= 0)
            {
                string durStr = output.Substring(durIndex + durPrefix.Length, 11);
                TimeSpan.TryParse(durStr, out duration);
            }

            string brPrefix = "bitrate: ";
            int brIndex = output.IndexOf(brPrefix);
            if (brIndex >= 0)
            {
                int brEnd = output.IndexOf(" kb/s", brIndex);
                if (brEnd > brIndex)
                {
                    string brStr = output.Substring(brIndex + brPrefix.Length, brEnd - (brIndex + brPrefix.Length));
                    int.TryParse(brStr, out bitrate);
                }
            }

            return new AudioFileInfo
            {
                FileName = Path.GetFileName(filePath),
                Duration = duration,
                Bitrate = bitrate
            };
        }
    }
}