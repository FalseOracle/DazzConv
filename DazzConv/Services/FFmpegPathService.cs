using System;
using System.IO;

namespace DazzConv.Services
{
    public class FFmpegPathService
    {
        private string? _cachedPath;
        private readonly object _lock = new object();

        public string GetFFmpegPath()
        {
            if (_cachedPath != null && File.Exists(_cachedPath))
                return _cachedPath;

            lock (_lock)
            {
                if (_cachedPath != null && File.Exists(_cachedPath))
                    return _cachedPath;

                var basePath = AppDomain.CurrentDomain.BaseDirectory;

                // Список возможных путей (от наиболее вероятного к менее)
                var possiblePaths = new[]
                {
                    Path.Combine(basePath, "Resources", "ffmpeg", "ffmpeg.exe"),
                    Path.Combine(basePath, "Resources", "Tools", "ffmpeg.exe"),
                    Path.Combine(basePath, "ffmpeg.exe"),
                    Path.Combine(basePath, "Tools", "ffmpeg.exe")
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        _cachedPath = path;
                        return path;
                    }
                }

                // Если ничего не нашли - возвращаем первый путь (для диагностики)
                _cachedPath = possiblePaths[0];
                return _cachedPath;
            }
        }

        public bool IsFFmpegAvailable()
        {
            var path = GetFFmpegPath();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public string GetFFmpegVersion()
        {
            try
            {
                if (!IsFFmpegAvailable())
                    return "FFmpeg не найден";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = GetFFmpegPath(),
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
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