using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ConverToDazz.Core.Services;

public record YtDlpToolsStatus(
    string? YtDlpVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool YtDlpOld,          // версия старше 90 дней (yt-dlp сам об этом предупреждает)
    bool PotInstalled,
    bool PotRunning);

/// <summary>
/// Управляет внешними инструментами загрузчика: обновляет yt-dlp и ставит/запускает поставщика PO-токенов
/// (bgutil-pot + плагин для yt-dlp), без которых YouTube отдаёт только 360p (формат 18).
/// Всё хранится в %AppData%\DazzConv\tools — туда можно писать даже при установке в Program Files.
/// </summary>
public class YtDlpToolsService : IDisposable
{
    public const int PotPort = 4416;

    // Свой мини-логгер: этот класс живёт в ConverToDazz.Core и не видит LoggerService из проекта приложения.
    // Пишет в тот же %AppData%\DazzConv\app.log.
    private sealed class ToolsLogger
    {
        private static readonly object Gate = new();
        private void Write(string level, string message)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DazzConv");
                Directory.CreateDirectory(dir);
                lock (Gate)
                    File.AppendAllText(Path.Combine(dir, "app.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
            catch { }
        }
        public void LogInfo(string message) => Write("Information", message);
        public void LogWarning(string message) => Write("Warning", message);
        public void LogError(string message, Exception? ex = null) =>
            Write("Error", ex == null ? message : $"{message} | Exception: {ex.GetType().Name}: {ex.Message}");
    }

    // Общий экземпляр: страницы берут его отсюда, поэтому регистрация в DI (MauiProgram) не обязательна.
    // Если инжекция не настроена, раньше страница падала с исключением, и Blazor переставал переключать вкладки.
    private static readonly Lazy<YtDlpToolsService> _instance = new(() => new YtDlpToolsService());
    public static YtDlpToolsService Instance => _instance.Value;

    private const string YtDlpRepo = "yt-dlp/yt-dlp";
    private const string PotRepo = "jim60105/bgutil-ytdlp-pot-provider-rs";
    private const string PotBinaryAsset = "bgutil-pot-windows-x86_64.exe";
    private const string PotPluginAsset = "bgutil-ytdlp-pot-provider-rs.zip";

    private HttpClient _http = CreateHttp(null);
    private HttpClient Http => _http;

    // для проверки локального сервера токенов — всегда напрямую, мимо прокси
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly IWebProxy OriginalDefaultProxy = HttpClient.DefaultProxy;

    private readonly ToolsLogger _logger = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _potLock = new();
    private Process? _potProcess;

    public string ToolsDir { get; }
    public string PluginsDir => Path.Combine(ToolsDir, "plugins");
    public string PotBinaryPath => Path.Combine(ToolsDir, "bgutil-pot.exe");
    private string ManagedYtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");
    private string PluginDir => Path.Combine(PluginsDir, "bgutil-ytdlp-pot-provider-rs");

    private YtDlpToolsService()
    {
        ToolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DazzConv", "tools");
        Directory.CreateDirectory(ToolsDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopPotServer();

        // остатки прошлого обновления (запущенный exe нельзя удалить, его переименовывают в .old)
        try { File.Delete(ManagedYtDlpPath + ".old"); } catch { }
        try { File.Delete(ManagedFfmpegPath + ".old"); File.Delete(ManagedFfprobePath + ".old"); File.Delete(ManagedDenoPath + ".old"); } catch { }

        _proxySettings = LoadProxySettings();
        RebuildHttp();
    }

    private static HttpClient CreateHttp(IWebProxy? proxy)
    {
        var handler = new SocketsHttpHandler();
        if (proxy != null) { handler.UseProxy = true; handler.Proxy = proxy; }
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DazzConv", "1.0")); // GitHub API требует User-Agent
        return c;
    }

    // ───────────────────────── пути и аргументы для YtDlpService ─────────────────────────

    /// <summary>Какой yt-dlp.exe использовать: управляемая копия, иначе поставляемая с приложением.</summary>
    public string YtDlpPath
    {
        get
        {
            if (File.Exists(ManagedYtDlpPath)) return ManagedYtDlpPath;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var p in new[]
            {
                Path.Combine(baseDir, "Resources", "yt-dlp", "yt-dlp.exe"),
                Path.Combine(baseDir, "Resources", "Tools", "yt-dlp.exe"),
                Path.Combine(baseDir, "yt-dlp.exe"),
                Path.Combine(baseDir, "Tools", "yt-dlp.exe")
            })
                if (File.Exists(p)) return p;

            return ManagedYtDlpPath;
        }
    }

    public bool PotInstalled =>
        File.Exists(PotBinaryPath) && Directory.Exists(PluginDir) &&
        Directory.GetFiles(PluginDir, "*.py", SearchOption.AllDirectories).Length > 0;

    /// <summary>Доп. аргументы командной строки yt-dlp (пусто, если плагин не установлен).</summary>
    public string BuildExtraArguments() =>
        PotInstalled ? $"--plugin-dirs default --plugin-dirs \"{PluginsDir}\"" : "";

    public void AddExtraArguments(ProcessStartInfo psi)
    {
        if (!PotInstalled) return;
        psi.ArgumentList.Add("--plugin-dirs"); psi.ArgumentList.Add("default");
        psi.ArgumentList.Add("--plugin-dirs"); psi.ArgumentList.Add(PluginsDir);
    }


    // ═══════════════════════════ прокси для обновлений ═══════════════════════════

    public record UpdateProxySettings(bool Enabled, string Address);

    private UpdateProxySettings _proxySettings = new(false, "127.0.0.1:1080");
    private string ProxyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DazzConv", "update_proxy.json");

    public UpdateProxySettings ProxySettings => _proxySettings;

    private UpdateProxySettings LoadProxySettings()
    {
        try
        {
            if (File.Exists(ProxyFile))
            {
                var loaded = JsonSerializer.Deserialize<UpdateProxySettings>(File.ReadAllText(ProxyFile));
                if (loaded != null) return loaded with { Address = loaded.Address ?? "127.0.0.1:1080" };
            }
        }
        catch (Exception ex) { _logger.LogWarning($"Прокси обновлений: не удалось прочитать настройки: {ex.Message}"); }
        return new UpdateProxySettings(false, "127.0.0.1:1080");
    }

    /// <summary>Сохраняет прокси (переживает перезапуск) и сразу применяет его ко всем обращениям к репозиториям.</summary>
    public void SetProxySettings(bool enabled, string address)
    {
        _proxySettings = new UpdateProxySettings(enabled, address ?? "");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProxyFile)!);
            File.WriteAllText(ProxyFile, JsonSerializer.Serialize(_proxySettings));
        }
        catch (Exception ex) { _logger.LogWarning($"Прокси обновлений: не удалось сохранить: {ex.Message}"); }

        RebuildHttp();
        ApplyProcessProxy();
    }

    /// <summary>Та же логика, что у прокси загрузок: адрес без схемы → socks5 или http по порту.</summary>
    public static string FormatProxyUrl(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        address = address.Trim();

        if (address.StartsWith("http://") || address.StartsWith("https://") || address.StartsWith("socks5://") ||
            address.StartsWith("socks4://") || address.StartsWith("socks5h://"))
            return address;

        var parts = address.Split(':');
        if (parts.Length == 2)
        {
            var host = parts[0]; var port = parts[1];
            if (port is "1080" or "9050" or "9150") return $"socks5://{host}:{port}";
            if (port is "8080" or "3128" or "8888" or "80") return $"http://{host}:{port}";
            if (port == "443") return $"https://{host}:{port}";
            return $"socks5://{host}:{port}";
        }
        return address;
    }

    private IWebProxy? BuildProxy()
    {
        if (!_proxySettings.Enabled || string.IsNullOrWhiteSpace(_proxySettings.Address)) return null;
        try
        {
            var uri = new Uri(FormatProxyUrl(_proxySettings.Address));
            var proxy = new WebProxy(uri) { BypassProxyOnLocal = true };
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var up = uri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(up[0]), up.Length > 1 ? Uri.UnescapeDataString(up[1]) : "");
            }
            return proxy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Прокси обновлений: неверный адрес «{_proxySettings.Address}»: {ex.Message}");
            return null;
        }
    }

    private void RebuildHttp()
    {
        var old = _http;
        _http = CreateHttp(BuildProxy());
        try { old.Dispose(); } catch { }
    }

    /// <summary>Прокси для всего процесса (HttpClient.DefaultProxy) — через него ходят клиенты, созданные позже, в т.ч. Velopack.
    /// Вызывается при старте приложения и при смене настроек.</summary>
    public void ApplyProcessProxy()
    {
        try { HttpClient.DefaultProxy = BuildProxy() ?? OriginalDefaultProxy; }
        catch (Exception ex) { _logger.LogWarning($"Прокси обновлений: не удалось применить к процессу: {ex.Message}"); }
    }

    /// <summary>Проверка: достучаться до GitHub через выбранный прокси.</summary>
    public async Task<(bool Ok, string Message)> TestProxyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var r = await Http.GetAsync("https://api.github.com/rate_limit", cts.Token);
            return r.IsSuccessStatusCode ? (true, "GitHub доступен") : (false, $"GitHub ответил: {(int)r.StatusCode}");
        }
        catch (Exception ex) { return (false, $"Нет доступа к GitHub: {ex.Message}"); }
    }

    // ═══════════════════════════ ffmpeg ═══════════════════════════

    private const string FfmpegRepo = "BtbN/FFmpeg-Builds";
    private const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl.zip";
    private string FfmpegStampFile => Path.Combine(ToolsDir, "ffmpeg.stamp");

    /// <summary>Обновляемая копия ffmpeg (если установлена). Приложение предпочитает её встроенной.</summary>
    public string ManagedFfmpegPath => Path.Combine(ToolsDir, "ffmpeg", "bin", "ffmpeg.exe");
    public string ManagedFfprobePath => Path.Combine(ToolsDir, "ffmpeg", "bin", "ffprobe.exe");

    private async Task<string?> FirstLineOfAsync(string exe, string arg, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return text.Split('\n').FirstOrDefault()?.Trim();
        }
        catch { return null; }
    }

    /// <summary>Скачивает свежую сборку ffmpeg (BtbN, GPL static), сверяет SHA-256 и ставит ffmpeg.exe и ffprobe.exe.
    /// Возвращает false, если уже стоит актуальная.</summary>
    public async Task<bool> UpdateFfmpegAsync(IProgress<string>? status = null, IProgress<int>? progress = null,
        bool onlyIfChanged = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            status?.Report("ffmpeg: проверяю версию...");
            var release = await GetReleaseByTagAsync(FfmpegRepo, "latest", ct);
            var zipUrl = release.Asset(FfmpegAsset) ?? throw new Exception($"В релизе ffmpeg нет {FfmpegAsset}");
            var sumsUrl = release.Asset("checksums.sha256") ?? throw new Exception("В релизе ffmpeg нет checksums.sha256");
            var stamp = release.Stamp(FfmpegAsset) ?? "";

            if (onlyIfChanged && File.Exists(ManagedFfmpegPath) && File.Exists(ManagedFfprobePath) &&
                File.Exists(FfmpegStampFile) && File.ReadAllText(FfmpegStampFile) == stamp)
            {
                _logger.LogInfo("ffmpeg: уже актуальная сборка");
                return false;
            }

            var expected = ParseSha256(await Http.GetStringAsync(sumsUrl, ct), FfmpegAsset)
                           ?? throw new Exception("Не нашёл контрольную сумму ffmpeg");

            status?.Report("ffmpeg: скачиваю (~160 МБ)...");
            var tmpZip = Path.Combine(Path.GetTempPath(), $"dazzconv_ffmpeg_{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(zipUrl, tmpZip, progress, ct);

                status?.Report("ffmpeg: проверяю контрольную сумму...");
                if (!(await Sha256Async(tmpZip, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Контрольная сумма ffmpeg не совпала — файл не установлен");

                status?.Report("ffmpeg: устанавливаю...");
                var binDir = Path.GetDirectoryName(ManagedFfmpegPath)!;
                Directory.CreateDirectory(binDir);

                using (var zip = ZipFile.OpenRead(tmpZip))
                {
                    foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                    {
                        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new Exception($"В архиве ffmpeg нет {name}");
                        var tmpExe = Path.Combine(binDir, name + ".new");
                        entry.ExtractToFile(tmpExe, overwrite: true);
                        ReplaceFile(tmpExe, Path.Combine(binDir, name));
                    }
                }
            }
            finally { TryDelete(tmpZip); }

            File.WriteAllText(FfmpegStampFile, stamp);
            _logger.LogInfo("ffmpeg обновлён");
            return true;
        }
        finally { _gate.Release(); }
    }

    // ═══════════════════════════ deno (JS-среда для YouTube) ═══════════════════════════

    private const string DenoRepo = "denoland/deno";
    private const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";

    public string ManagedDenoPath => Path.Combine(ToolsDir, "deno.exe");

    /// <summary>Ищет установленный Node.js (для расшифровки ссылок YouTube).</summary>
    public static string? FindNode()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "node", "node.exe")
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim('"'), "node.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public async Task<bool> UpdateDenoAsync(IProgress<string>? status = null, IProgress<int>? progress = null,
        bool onlyIfChanged = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            status?.Report("deno: проверяю версию...");
            var release = await GetLatestReleaseAsync(DenoRepo, ct);
            var zipUrl = release.Asset(DenoAsset) ?? throw new Exception($"В релизе deno нет {DenoAsset}");
            var sumUrl = release.Asset(DenoAsset + ".sha256sum");

            var installed = await FirstLineOfAsync(ManagedDenoPath, "--version", ct);   // "deno 2.1.4 (...)"
            if (onlyIfChanged && installed != null)
            {
                var v = installed.Split(' ').Skip(1).FirstOrDefault() ?? "";
                if (v.Length > 0 && CompareVersions(v, release.Tag) >= 0) { _logger.LogInfo("deno: уже последняя версия"); return false; }
            }

            string? expected = null;
            if (sumUrl != null)
            {
                var text = (await Http.GetStringAsync(sumUrl, ct)).Trim();
                expected = text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            if (string.IsNullOrEmpty(expected) || expected.Length != 64)
                throw new Exception("Не нашёл контрольную сумму deno — файл не установлен");

            status?.Report("deno: скачиваю...");
            var tmpZip = Path.Combine(Path.GetTempPath(), $"dazzconv_deno_{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(zipUrl, tmpZip, progress, ct);
                if (!(await Sha256Async(tmpZip, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Контрольная сумма deno не совпала — файл не установлен");

                using var zip = ZipFile.OpenRead(tmpZip);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new Exception("В архиве deno нет deno.exe");
                var tmpExe = ManagedDenoPath + ".new";
                entry.ExtractToFile(tmpExe, overwrite: true);
                ReplaceFile(tmpExe, ManagedDenoPath);
            }
            finally { TryDelete(tmpZip); }

            _logger.LogInfo($"deno обновлён до {release.Tag}");
            return true;
        }
        finally { _gate.Release(); }
    }

    // ═══════════════════════════ поставщик токенов: версия ═══════════════════════════

    private string PotTagFile => Path.Combine(ToolsDir, "pot.tag");
    private string? InstalledPotTag => File.Exists(PotTagFile) ? File.ReadAllText(PotTagFile).Trim() : null;

    // ═══════════════════════════ общий обзор и «обновить всё» ═══════════════════════════

    public record ToolInfo(string Key, string Name, string? Installed, string? Latest, bool UpdateAvailable, string Note);

    public async Task<List<ToolInfo>> GetOverviewAsync(bool checkLatest, CancellationToken ct = default)
    {
        var list = new List<ToolInfo>();

        // yt-dlp
        var ytInstalled = await GetInstalledYtDlpVersionAsync(ct);
        string? ytLatest = null;
        if (checkLatest) try { ytLatest = (await GetLatestReleaseAsync(YtDlpRepo, ct)).Tag; } catch { }
        list.Add(new ToolInfo("ytdlp", "yt-dlp (загрузка с сайтов)", ytInstalled, ytLatest,
            ytInstalled != null && ytLatest != null && CompareVersions(ytInstalled, ytLatest) < 0, ""));

        // ffmpeg
        var ffExe = File.Exists(ManagedFfmpegPath) ? ManagedFfmpegPath : null;
        var ffLine = await FirstLineOfAsync(ffExe ?? "", "-version", ct);
        string? ffLatest = null; var ffUpdate = false;
        if (checkLatest)
        {
            try
            {
                var rel = await GetReleaseByTagAsync(FfmpegRepo, "latest", ct);
                var stamp = rel.Stamp(FfmpegAsset) ?? "";
                ffLatest = stamp.Split('|')[0].Length >= 10 ? stamp.Split('|')[0][..10] : "актуальная сборка";
                ffUpdate = !File.Exists(FfmpegStampFile) || File.ReadAllText(FfmpegStampFile) != stamp;
            }
            catch { }
        }
        var ffInstalledText = ffExe == null ? "встроенная копия" : ShortFfmpegVersion(ffLine);
        list.Add(new ToolInfo("ffmpeg", "ffmpeg и ffprobe (конвертация)", ffInstalledText, ffLatest, ffUpdate,
            ffExe == null ? "Сейчас используется копия из папки приложения" : ""));

        // deno
        var denoLine = await FirstLineOfAsync(File.Exists(ManagedDenoPath) ? ManagedDenoPath : "", "--version", ct);
        var denoInstalled = denoLine?.Split(' ').Skip(1).FirstOrDefault();
        string? denoLatest = null;
        if (checkLatest) try { denoLatest = (await GetLatestReleaseAsync(DenoRepo, ct)).Tag; } catch { }
        list.Add(new ToolInfo("deno", "deno (расшифровка ссылок YouTube)",
            denoInstalled ?? (FindNode() != null ? "не нужен: найден Node.js" : null), denoLatest,
            denoInstalled != null && denoLatest != null && CompareVersions(denoInstalled, denoLatest) < 0, ""));

        // PO-провайдер
        string? potLatest = null;
        if (checkLatest) try { potLatest = (await GetLatestReleaseAsync(PotRepo, ct)).Tag; } catch { }
        var potInstalled = PotInstalled ? (InstalledPotTag ?? "установлен") : null;
        list.Add(new ToolInfo("pot", "Поставщик PO-токенов (YouTube в HD)", potInstalled, potLatest,
            PotInstalled && potLatest != null && InstalledPotTag != null && CompareVersions(InstalledPotTag, potLatest) < 0, ""));

        return list;
    }

    private static string ShortFfmpegVersion(string? line)
    {
        // "ffmpeg version N-118000-gabcdef-20260101 Copyright ..." → "N-118000-gabcdef"
        if (string.IsNullOrWhiteSpace(line)) return "установлена";
        var parts = line.Split(' ');
        return parts.Length >= 3 ? parts[2] : line;
    }

    /// <summary>Обновляет всё, что есть в «обзоре» и устарело. Возвращает короткий отчёт.</summary>
    public async Task<List<string>> UpdateAllAsync(IProgress<string>? status = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var report = new List<string>();

        async Task Step(string name, Func<Task<string>> action)
        {
            try { report.Add($"✅ {name}: {await action()}"); }
            catch (Exception ex) { report.Add($"❌ {name}: {ex.Message}"); _logger.LogError($"Обновление «{name}» не удалось", ex); }
        }

        await Step("yt-dlp", async () =>
        {
            status?.Report("yt-dlp: проверяю...");
            return "версия " + await UpdateYtDlpAsync(progress, ct);
        });
        await Step("ffmpeg", async () => await UpdateFfmpegAsync(status, progress, true, ct) ? "обновлён" : "уже актуален");

        if (File.Exists(ManagedDenoPath) || FindNode() == null)
            await Step("deno", async () => await UpdateDenoAsync(status, progress, true, ct) ? "обновлён" : "уже актуален");

        if (PotInstalled)
        {
            await Step("Поставщик токенов", async () =>
            {
                var latest = (await GetLatestReleaseAsync(PotRepo, ct)).Tag;
                if (InstalledPotTag != null && CompareVersions(InstalledPotTag, latest) >= 0) return "уже актуален";
                await InstallPotProviderAsync(new Progress<string>(m => status?.Report(m)), progress, ct);
                return "обновлён";
            });
        }
        return report;
    }

    // ───────────────────────── автообслуживание ─────────────────────────

    private string AutoUpdateOffFlag => Path.Combine(ToolsDir, "auto_update_off");
    private string LastAutoCheckFile => Path.Combine(ToolsDir, "last_auto_check.txt");

    /// <summary>Автообновление yt-dlp при запуске (раз в сутки). По умолчанию включено.</summary>
    public bool AutoUpdateEnabled
    {
        get => !File.Exists(AutoUpdateOffFlag);
        set
        {
            try
            {
                if (value) File.Delete(AutoUpdateOffFlag);
                else File.WriteAllText(AutoUpdateOffFlag, "off");
            }
            catch (Exception ex) { _logger.LogWarning($"yt-dlp: не удалось сохранить настройку автообновления: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Вызывается при старте приложения: запускает сервер PO-токенов (если установлен)
    /// и не чаще раза в сутки обновляет yt-dlp. Ошибки только пишутся в лог.
    /// </summary>
    public async Task AutoMaintainAsync(CancellationToken ct = default)
    {
        try
        {
            if (PotInstalled) await EnsurePotServerAsync(ct);
            if (!AutoUpdateEnabled) return;

            if (File.Exists(LastAutoCheckFile) && File.Exists(YtDlpPath) &&
                DateTime.TryParse(File.ReadAllText(LastAutoCheckFile), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                (DateTime.UtcNow - last).TotalHours < 24)
                return;

            var status = await GetStatusAsync(checkLatest: true, ct);
            if (status.YtDlpVersion == null || status.UpdateAvailable)
            {
                _logger.LogInfo($"yt-dlp: автообновление ({status.YtDlpVersion ?? "не найден"} → {status.LatestVersion})");
                await UpdateYtDlpAsync(null, ct);
            }

            File.WriteAllText(LastAutoCheckFile, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"yt-dlp: автообслуживание не удалось: {ex.Message}");
        }

        await AutoMaintainOthersAsync(ct);
    }

    private string AutoFfmpegOffFlag => Path.Combine(ToolsDir, "auto_ffmpeg_off");

    /// <summary>Автообновление ffmpeg (раз в 30 дней, файл ~160 МБ). По умолчанию включено.</summary>
    public bool AutoUpdateFfmpegEnabled
    {
        get => !File.Exists(AutoFfmpegOffFlag);
        set
        {
            try { if (value) File.Delete(AutoFfmpegOffFlag); else File.WriteAllText(AutoFfmpegOffFlag, "off"); }
            catch (Exception ex) { _logger.LogWarning($"ffmpeg: не удалось сохранить настройку автообновления: {ex.Message}"); }
        }
    }

    private bool DueEvery(string stampFile, TimeSpan period)
    {
        try
        {
            return !(File.Exists(stampFile) &&
                     DateTime.TryParse(File.ReadAllText(stampFile), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                     DateTime.UtcNow - last < period);
        }
        catch { return true; }
    }

    private async Task AutoMaintainOthersAsync(CancellationToken ct)
    {
        try
        {
            // ffmpeg — раз в 30 дней
            var ffStamp = Path.Combine(ToolsDir, "last_ffmpeg_check.txt");
            if (AutoUpdateFfmpegEnabled && DueEvery(ffStamp, TimeSpan.FromDays(30)))
            {
                await UpdateFfmpegAsync(null, null, onlyIfChanged: true, ct);
                File.WriteAllText(ffStamp, DateTime.UtcNow.ToString("O"));
            }

            // deno и поставщик токенов — раз в неделю
            var miscStamp = Path.Combine(ToolsDir, "last_misc_check.txt");
            if (AutoUpdateEnabled && DueEvery(miscStamp, TimeSpan.FromDays(7)))
            {
                // без Node.js YouTube не расшифровать — ставим deno сами
                if (File.Exists(ManagedDenoPath) || FindNode() == null)
                    await UpdateDenoAsync(null, null, onlyIfChanged: true, ct);

                if (PotInstalled)
                {
                    var latest = (await GetLatestReleaseAsync(PotRepo, ct)).Tag;
                    if (InstalledPotTag == null || CompareVersions(InstalledPotTag, latest) < 0)
                        await InstallPotProviderAsync(null, null, ct);
                }
                File.WriteAllText(miscStamp, DateTime.UtcNow.ToString("O"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Автообновление ffmpeg/deno/PO-провайдера не удалось: {ex.Message}");
        }
    }

    // ───────────────────────── статус ─────────────────────────

    public async Task<YtDlpToolsStatus> GetStatusAsync(bool checkLatest, CancellationToken ct = default)
    {
        var installed = await GetInstalledYtDlpVersionAsync(ct);
        string? latest = null;
        if (checkLatest)
        {
            try { latest = (await GetLatestReleaseAsync(YtDlpRepo, ct)).Tag; }
            catch (Exception ex) { _logger.LogWarning($"yt-dlp: не удалось узнать последнюю версию: {ex.Message}"); }
        }

        var update = installed != null && latest != null && CompareVersions(installed, latest) < 0;
        var old = installed != null && TryParseDate(installed, out var date) && (DateTime.UtcNow - date).TotalDays > 90;

        return new YtDlpToolsStatus(installed, latest, update, old, PotInstalled, await IsPotServerUpAsync(ct));
    }

    public async Task<string?> GetInstalledYtDlpVersionAsync(CancellationToken ct = default)
    {
        var path = YtDlpPath;
        if (!File.Exists(path)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");

            using var p = Process.Start(psi);
            if (p == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"yt-dlp: не удалось получить версию: {ex.Message}");
            return null;
        }
    }

    // ───────────────────────── обновление yt-dlp ─────────────────────────

    /// <summary>Скачивает свежий yt-dlp.exe, сверяет SHA-256 с файлом SHA2-256SUMS из того же релиза и подменяет exe.</summary>
    public async Task<string> UpdateYtDlpAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var release = await GetLatestReleaseAsync(YtDlpRepo, ct);
            var exe = release.Asset("yt-dlp.exe") ?? throw new Exception("В релизе yt-dlp нет yt-dlp.exe");
            var sums = release.Asset("SHA2-256SUMS") ?? throw new Exception("В релизе yt-dlp нет файла контрольных сумм SHA2-256SUMS");

            var installed = await GetInstalledYtDlpVersionAsync(ct);
            if (installed != null && CompareVersions(installed, release.Tag) >= 0 && File.Exists(ManagedYtDlpPath))
            {
                _logger.LogInfo($"yt-dlp: уже последняя версия ({installed})");
                return installed;
            }

            var expectedHash = ParseSha256(await Http.GetStringAsync(sums, ct), "yt-dlp.exe")
                               ?? throw new Exception("Не нашёл контрольную сумму yt-dlp.exe");

            var tmp = ManagedYtDlpPath + ".download";
            await DownloadAsync(exe, tmp, progress, ct);

            var actual = await Sha256Async(tmp, ct);
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp);
                throw new Exception("Контрольная сумма скачанного yt-dlp.exe не совпала — файл не установлен");
            }

            ReplaceFile(tmp, ManagedYtDlpPath);
            _logger.LogInfo($"yt-dlp обновлён до {release.Tag}");
            return release.Tag;
        }
        finally { _gate.Release(); }
    }

    // ───────────────────────── поставщик PO-токенов ─────────────────────────

    public async Task InstallPotProviderAsync(IProgress<string>? status = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            status?.Report("Получаю информацию о релизе...");
            var release = await GetLatestReleaseAsync(PotRepo, ct);
            var bin = release.Asset(PotBinaryAsset) ?? throw new Exception($"В релизе нет {PotBinaryAsset}");
            var plugin = release.Asset(PotPluginAsset) ?? throw new Exception($"В релизе нет {PotPluginAsset}");

            StopPotServer();   // exe нельзя заменить, пока он запущен

            status?.Report("Скачиваю поставщика токенов...");
            var tmpBin = PotBinaryPath + ".download";
            await DownloadAsync(bin, tmpBin, progress, ct);
            ReplaceFile(tmpBin, PotBinaryPath);

            status?.Report("Скачиваю плагин для yt-dlp...");
            var tmpZip = Path.Combine(Path.GetTempPath(), $"dazzconv_pot_{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(plugin, tmpZip, null, ct);

                if (Directory.Exists(PluginDir)) Directory.Delete(PluginDir, recursive: true);
                Directory.CreateDirectory(PluginDir);
                ZipFile.ExtractToDirectory(tmpZip, PluginDir, overwriteFiles: true);
            }
            finally { TryDelete(tmpZip); }

            status?.Report("Запускаю поставщика токенов...");
            if (!await EnsurePotServerAsync(ct))
                throw new Exception("Поставщик установлен, но не запустился (порт 4416 занят или заблокирован антивирусом)");

            try { File.WriteAllText(PotTagFile, release.Tag); } catch { }
            _logger.LogInfo($"PO-провайдер установлен: {release.Tag}");
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsPotServerUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var r = await LocalHttp.GetAsync($"http://127.0.0.1:{PotPort}/ping", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Запускает bgutil-pot в режиме сервера (если установлен и ещё не запущен). Закрывается вместе с приложением.</summary>
    public async Task<bool> EnsurePotServerAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PotBinaryPath)) return false;
        if (await IsPotServerUpAsync(ct)) return true;

        lock (_potLock)
        {
            if (_potProcess == null || _potProcess.HasExited)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = PotBinaryPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (var a in new[] { "server", "--host", "127.0.0.1", "--port", PotPort.ToString() })
                        psi.ArgumentList.Add(a);

                    _potProcess = Process.Start(psi);
                    // выводы дренируем, иначе заполненный буфер повесит сервер
                    _potProcess?.BeginOutputReadLine();
                    _potProcess?.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    _logger.LogError("Не удалось запустить bgutil-pot", ex);
                    return false;
                }
            }
        }

        for (var i = 0; i < 40; i++)   // до ~12 секунд
        {
            ct.ThrowIfCancellationRequested();
            if (await IsPotServerUpAsync(ct)) { _logger.LogInfo("bgutil-pot: сервер запущен"); return true; }
            await Task.Delay(300, ct);
        }
        return false;
    }

    public void StopPotServer()
    {
        lock (_potLock)
        {
            try { if (_potProcess is { HasExited: false }) _potProcess.Kill(entireProcessTree: true); } catch { }
            _potProcess?.Dispose();
            _potProcess = null;
        }
    }

    public void Dispose() => StopPotServer();

    // ───────────────────────── вспомогательное ─────────────────────────

    private record Release(string Tag, List<(string Name, string Url)> Assets, Dictionary<string, string>? Stamps = null)
    {
        public string? Asset(string name) =>
            Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Url;

        /// <summary>Отметка версии файла (дата обновления + размер) — для «плавающих» релизов вроде ffmpeg latest.</summary>
        public string? Stamp(string name) =>
            Stamps != null && Stamps.TryGetValue(name, out var v) ? v : null;
    }

    private Task<Release> GetLatestReleaseAsync(string repo, CancellationToken ct) =>
        GetReleaseAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);

    private Task<Release> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct) =>
        GetReleaseAsync($"https://api.github.com/repos/{repo}/releases/tags/{tag}", ct);

    private async Task<Release> GetReleaseAsync(string apiUrl, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(apiUrl, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var assets = new List<(string, string)>();
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            assets.Add((name, a.GetProperty("browser_download_url").GetString() ?? ""));
            var updated = a.TryGetProperty("updated_at", out var u) ? u.GetString() : null;
            var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
            stamps[name] = $"{updated}|{size}";
        }
        return new Release(tag.TrimStart('v'), assets, stamps);
    }

    private async Task DownloadAsync(string url, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long done = 0; int last = -1, read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    var pct = (int)(done * 100 / total);
                    if (pct != last) { last = pct; progress?.Report(pct); }
                }
            }
        }
    }

    /// <summary>Запущенный exe нельзя перезаписать, но можно переименовать — так обновление работает и во время работы.</summary>
    private static void ReplaceFile(string newFile, string target)
    {
        if (File.Exists(target))
        {
            var old = target + ".old";
            try { File.Delete(old); } catch { }
            try { File.Move(target, old); }
            catch (IOException) { throw new IOException("Файл сейчас используется — дождитесь окончания загрузок и повторите"); }
        }
        File.Move(newFile, target);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    private static string? ParseSha256(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>Версии yt-dlp вида 2026.06.09 или 2026.06.09.123456 (nightly).</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] Parse(string v) => v.Trim().TrimStart('v').Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parse(a); var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0; var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static bool TryParseDate(string version, out DateTime date)
    {
        date = default;
        var p = version.Split('.');
        return p.Length >= 3 && int.TryParse(p[0], out var y) && int.TryParse(p[1], out var m) && int.TryParse(p[2], out var d)
               && y > 2000 && m is >= 1 and <= 12 && d is >= 1 and <= 31
               && DateTime.TryParse($"{y}-{m:D2}-{d:D2}", out date);
    }
}
