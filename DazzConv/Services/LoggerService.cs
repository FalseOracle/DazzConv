using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Diagnostics;

namespace DazzConv.Services
{
    public class LoggerService
    {
        private readonly string _logPath;
        private readonly object _lock = new object();
        private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB

        public LoggerService()
        {
            try
            {
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DazzConv");
                Directory.CreateDirectory(appData);
                _logPath = Path.Combine(appData, "app.log");

                // Ротация: если лог больше 5 MB, переименовываем
                RotateLogIfNeeded();
            }
            catch (Exception ex)
            {
                // Fallback - логируем в консоль если не можем создать файл
                Console.WriteLine($"[LoggerService] Ошибка инициализации: {ex.Message}");
                _logPath = Path.Combine(Path.GetTempPath(), "DazzConv_app.log");
            }
        }

        private void RotateLogIfNeeded()
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    var fileInfo = new FileInfo(_logPath);
                    if (fileInfo.Length > MaxLogSizeBytes)
                    {
                        var backupPath = _logPath + ".old";
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                        File.Move(_logPath, backupPath);
                    }
                }
            }
            catch { }
        }

        public void Log(string message, LogLevel level = LogLevel.Information)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

                lock (_lock)
                {
                    File.AppendAllText(_logPath, logEntry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggerService] Ошибка записи: {ex.Message}");
            }
        }

        public void LogError(string message, Exception? ex = null)
        {
            var fullMessage = ex != null
                ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}"
                : message;
            Log(fullMessage, LogLevel.Error);
        }

        public void LogInfo(string message) => Log(message, LogLevel.Information);
        public void LogWarning(string message) => Log(message, LogLevel.Warning);

        public void OpenLogFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{_logPath}\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggerService] Ошибка открытия папки: {ex.Message}");
            }
        }

        public string GetLogPath() => _logPath;

        public string ReadLastLines(int lines = 100)
        {
            try
            {
                if (!File.Exists(_logPath)) return "Лог-файл не найден";

                var allLines = File.ReadAllLines(_logPath);
                var lastLines = allLines.Skip(Math.Max(0, allLines.Length - lines));
                return string.Join(Environment.NewLine, lastLines);
            }
            catch (Exception ex)
            {
                return $"Ошибка чтения лога: {ex.Message}";
            }
        }
    }
}