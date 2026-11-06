using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace DazzConv.Services;

public class UpdateService
{
    private const string RepoUrl = "https://github.com/FalseOracle/DazzConv";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(45);

    private readonly LoggerService _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;

    public UpdateService(LoggerService logger)
    {
        _logger = logger;
    }

    // Один UpdateManager на всё приложение (раньше создавался заново при каждой проверке и установке)
    private UpdateManager Manager =>
        _manager ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false, downloader: null));

    /// <summary>
    /// Реальная версия. Раньше была захардкоженная константа "1.1.0": после обновления
    /// UI продолжал показывать старую версию, и казалось, что обновление «не сработало».
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                if (Manager.IsInstalled && Manager.CurrentVersion != null)
                    return Manager.CurrentVersion.ToString();
            }
            catch { }

            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrEmpty(info) ? "dev" : info.Split('+')[0];
        }
    }

    /// <summary>После смены прокси: следующий запрос создаст UpdateManager заново.</summary>
    public void ResetManager() => _manager = null;

    public bool IsInstalled
    {
        get { try { return Manager.IsInstalled; } catch { return false; } }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            _logger.LogWarning("Проверка обновлений уже выполняется");
            return null;
        }

        try
        {
            var mgr = Manager;

            if (!mgr.IsInstalled)
            {
                _logger.LogWarning("Приложение не установлено через Velopack (запуск из IDE/папки). Обновления недоступны.");
                _logger.LogWarning($"Путь запуска: {Environment.ProcessPath}");
                return null;
            }

            _logger.LogInfo($"Velopack: текущая версия {mgr.CurrentVersion}");

            // Без таймаута зависший GitHub/прокси подвешивал кнопку «Проверить» навсегда
            var update = await mgr.CheckForUpdatesAsync().WaitAsync(NetworkTimeout);
            if (update != null)
            {
                _logger.LogInfo($"Найдена новая версия: {update.TargetFullRelease.Version}");
                return update;
            }

            _logger.LogInfo("Установлена последняя версия");
        }
        catch (TimeoutException)
        {
            _logger.LogError("Проверка обновлений: превышено время ожидания GitHub");
            throw new Exception("GitHub не ответил за 45 секунд. Проверьте интернет/VPN.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Ошибка проверки обновлений: {ex.Message}", ex);
            throw; // раньше ошибка глоталась и UI писал «обновлений нет» — теперь пользователь видит причину
        }
        finally
        {
            _gate.Release();
        }
        return null;
    }

    public async Task<bool> DownloadAndInstallAsync(UpdateInfo update, Action<int>? onProgress = null)
    {
        try
        {
            var mgr = Manager;

            _logger.LogInfo($"Загрузка обновления {update.TargetFullRelease.Version}...");
            await mgr.DownloadUpdatesAsync(update, onProgress);

            _logger.LogInfo("Применение обновления и перезапуск...");
            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Ошибка установки обновления", ex);
            return false;
        }
    }
}
