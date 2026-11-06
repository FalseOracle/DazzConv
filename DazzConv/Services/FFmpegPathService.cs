using ConverToDazz.Core.Services;
using System;
using System.Diagnostics;
using System.IO;

namespace DazzConv.Services
{
    public class FFmpegPathService
    {
        private readonly Lazy<string> _ffmpeg;
        private readonly Lazy<string> _ffprobe;

        public FFmpegPathService()
        {
            _ffmpeg = new Lazy<string>(() => Resolve("ffmpeg.exe"));
            _ffprobe = new Lazy<string>(() => Resolve("ffprobe.exe"));
        }

        // Сначала обновляемая копия из %AppData%\DazzConv\tools (её можно обновить из «Настроек»),
        // затем встроенная из папки приложения. Проверка при каждом вызове — после обновления перезапуск не нужен.
        public string GetFFmpegPath()
        {
            var managed = YtDlpToolsService.Instance.ManagedFfmpegPath;
            return File.Exists(managed) ? managed : _ffmpeg.Value;
        }

        public string GetFFprobePath()
        {
            var managed = YtDlpToolsService.Instance.ManagedFfprobePath;
            return File.Exists(managed) ? managed : _ffprobe.Value;
        }

        // Один метод вместо двух копий GetFFmpegPath/GetFFprobePath + lock + повторных File.Exists на каждый вызов
        private static string Resolve(string exeName)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(basePath, "Resources", "ffmpeg", exeName),
                Path.Combine(basePath, "Resources", "Tools", exeName),
                Path.Combine(basePath, exeName),
                Path.Combine(basePath, "Tools", exeName)
            };

            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            // запасной вариант: ffmpeg из PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }

            return candidates[0]; // не найден — вызывающий код покажет понятную ошибку
        }

        public bool IsFFmpegAvailable() => File.Exists(GetFFmpegPath());

        public string GetFFmpegVersion()
        {
            try
            {
                if (!IsFFmpegAvailable()) return "FFmpeg не найден";

                var psi = new ProcessStartInfo
                {
                    FileName = GetFFmpegPath(),
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                var output = process?.StandardOutput.ReadLine() ?? "Не удалось получить версию";
                process?.WaitForExit(5000);
                return output;
            }
            catch (Exception ex)
            {
                return $"Ошибка: {ex.Message}";
            }
        }
    }
}
