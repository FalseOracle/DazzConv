using ConverToDazz.Core.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public class VideoInfoService
    {
        private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex VideoLineRegex = new(@"Stream #\S+.*?Video:\s*(\S+?)[,\s].*", RegexOptions.Compiled);
        private static readonly Regex ResolutionRegex = new(@"\b(\d{2,5}x\d{2,5})\b", RegexOptions.Compiled);
        private static readonly Regex FpsRegex = new(@"(\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);

        private readonly FFmpegPathService _ffmpegService;

        public VideoInfoService(FFmpegPathService ffmpegService)
        {
            _ffmpegService = ffmpegService;
        }

        public async Task<VideoInfo> GetInfoAsync(string filePath)
        {
            var info = new VideoInfo();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegService.GetFFmpegPath(),
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(filePath); // ArgumentList — корректно для путей с кавычками

                using var process = new Process { StartInfo = psi };
                process.Start();

                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var dm = DurationRegex.Match(errorOutput);
                if (dm.Success)
                {
                    var h = int.Parse(dm.Groups[1].Value);
                    var m = int.Parse(dm.Groups[2].Value);
                    var s = double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
                    info.Duration = $"{h:D2}:{m:D2}:{(int)s:D2}";
                    info.DurationSeconds = h * 3600 + m * 60 + s;
                }

                // Разрешение/fps ищем только в строке видеопотока, а не по всему выводу
                // (раньше могло подцепиться что-то из метаданных/обложки)
                foreach (var line in errorOutput.Split('\n'))
                {
                    if (!line.Contains("Video:")) continue;

                    var cm = VideoLineRegex.Match(line);
                    if (cm.Success) info.Codec = cm.Groups[1].Value.TrimEnd(',');

                    // убираем «0x...»-подобные куски вроде (0x31637661)
                    var cleaned = Regex.Replace(line, @"\(0x[0-9a-fA-F]+\)|0x[0-9a-fA-F]+", "");
                    var rm = ResolutionRegex.Match(cleaned);
                    if (rm.Success) info.Resolution = rm.Groups[1].Value;

                    var fm = FpsRegex.Match(line);
                    if (fm.Success) info.Fps = fm.Groups[1].Value;
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения информации: {ex.Message}");
            }

            return info;
        }
    }
}
