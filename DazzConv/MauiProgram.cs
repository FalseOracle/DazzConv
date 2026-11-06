using ConverToDazz.Core.Services;
using DazzConv.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;  
using Velopack;  

namespace DazzConv
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // Velopack должен стартовать как можно раньше: при установке/обновлении он обрабатывает
            // служебные аргументы запуска и сразу завершает процесс.
            VelopackApp.Build().Run();

            // прокси для обновлений (настраивается в «Настройках»): применяем до любых сетевых запросов, в т.ч. Velopack
            YtDlpToolsService.Instance.ApplyProcessProxy();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureLifecycleEvents(events =>
                {
#if WINDOWS
                    events.AddWindows(windows => windows
                        .OnWindowCreated((window) =>
                        {
                            // yt-dlp: автообновление (раз в сутки) и запуск сервера PO-токенов — не блокирует запуск окна
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(5000);
                                    await YtDlpToolsService.Instance.AutoMaintainAsync();
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Ошибка автообслуживания yt-dlp: {ex.Message}");
                                }
                            });

                            //фоновая проверка обновлений через 10 секунд после создания окна
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(10000); 

                                    var services = IPlatformApplication.Current!.Services;
                                    var updateService = services.GetRequiredService<UpdateService>();
                                    var update = await updateService.CheckForUpdateAsync();

                                    if (update != null)
                                    {
                                        var logger = services.GetRequiredService<LoggerService>();
                                        logger.LogInfo($"🔄 Найдено обновление: {update.TargetFullRelease.Version}");

                                        await MainThread.InvokeOnMainThreadAsync(async () =>
                                        {
                                            try
                                            {
                                                var currentPage = Application.Current?.Windows.FirstOrDefault()?.Page;
                                                if (currentPage != null)
                                                {
                                                    bool confirm = await currentPage.DisplayAlert(
                                                        "Доступно обновление",
                                                        $"Найдена новая версия {update.TargetFullRelease.Version}. Обновить сейчас?",
                                                        "Обновить",
                                                        "Позже");

                                                    if (confirm)
                                                    {
                                                        await updateService.DownloadAndInstallAsync(update);
                                                    }
                                                }
                                            }
                                            catch { }
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Ошибка автопроверки: {ex.Message}");
                                }
                            });
                        }));
#endif
                })
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton<FFmpegPathService>();
            builder.Services.AddSingleton<AudioConverterService>();
            builder.Services.AddSingleton<ImageConverterService>();

            builder.Services.AddSingleton<IVideoConverterService, FfmpegVideoConverterService>();

            builder.Services.AddSingleton<VideoConverterService>();

            builder.Services.AddSingleton<VideoInfoService>();
            builder.Services.AddSingleton<YtDlpService>();
            builder.Services.AddSingleton<LoggerService>();
            builder.Services.AddSingleton<HotkeyService>();
            builder.Services.AddSingleton<RecentOperationsService>();
            builder.Services.AddSingleton<UpdateService>();
            builder.Services.AddSingleton(YtDlpToolsService.Instance);


            return builder.Build();
        }
    }
}