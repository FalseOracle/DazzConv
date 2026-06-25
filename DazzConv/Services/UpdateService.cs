using Velopack;
using Velopack.Sources;

namespace DazzConv.Services;

public class UpdateService
{
    private readonly LoggerService _logger;
    public const string CurrentVersion = "1.1.0";
    private const string RepoUrl = "https://github.com/FalseOracle/DazzConv";

    public UpdateService(LoggerService logger)
    {
        _logger = logger;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false, downloader: null);
            var mgr = new UpdateManager(source);

            if (!mgr.IsInstalled)
            {
                _logger.LogWarning("Приложение не установлено через Velopack. Обновления недоступны.");
                _logger.LogWarning($"Путь запуска: {Environment.ProcessPath}");
                return null;
            }

            _logger.LogInfo($"Velopack: приложение установлено, текущая версия: {mgr.CurrentVersion}");

            var update = await mgr.CheckForUpdatesAsync();
            if (update != null)
            {
                _logger.LogInfo($"Найдена новая версия: {update.TargetFullRelease.Version}");
                return update;
            }

            _logger.LogInfo("Установлена последняя версия");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Ошибка проверки обновлений: {ex.Message}", ex);
        }
        return null;
    }

    public async Task<bool> DownloadAndInstallAsync(UpdateInfo update)
    {
        try
        {
            var source = new GithubSource(
                repoUrl: RepoUrl,
                accessToken: null,
                prerelease: false,
                downloader: null);

            var mgr = new UpdateManager(source);

            _logger.LogInfo($"Загрузка обновления {update.TargetFullRelease.Version}...");
            await mgr.DownloadUpdatesAsync(update);

            _logger.LogInfo("Применение обновления и перезапуск...");
            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Ошибка установки обновления", ex);
            return false;
        }
    }
}