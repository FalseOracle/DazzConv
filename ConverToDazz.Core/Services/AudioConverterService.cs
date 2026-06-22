using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Services
{
    public class AudioConverterService
    {
        public async Task ConvertAsync(string ffmpegPath, string inputPath, string outputPath,
            TimeSpan duration, IProgress<int> progress, CancellationToken token = default)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Файл не найден", inputPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{inputPath}\" -progress pipe:1 -nostats \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            _ = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream && !token.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line.StartsWith("out_time_ms="))
                    {
                        if (long.TryParse(line.Replace("out_time_ms=", ""), out long outMs))
                        {
                            if (duration.TotalMilliseconds > 0)
                            {
                                // ИСПРАВЛЕНО: ms → outMs
                                int percent = (int)Math.Min(100, Math.Max(0, (double)outMs / duration.TotalMilliseconds * 100));
                                progress.Report(percent);
                            }
                        }
                    }
                }
            }, token);

            _ = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream && !token.IsCancellationRequested)
                    await process.StandardError.ReadLineAsync();
            }, token);

            await process.WaitForExitAsync(token);
            progress?.Report(100);
        }
    }
}