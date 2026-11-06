using ConverToDazz.Core.Services;
using System.Collections.Generic;
using System.Linq;

namespace DazzConv.Services
{
    /// <summary>
    /// Диалоги выбора файла для конвертеров. Фильтр строится из InputFormats — единого списка входных форматов,
    /// который совпадает с выходными. Раньше страница изображений использовала FilePickerFileType.Images
    /// (в Windows там нет WebP, AVIF, HEIC и др.), поэтому такие файлы нельзя было даже выбрать.
    /// </summary>
    public static class InputPickers
    {
        public static PickOptions Video(string title = "Выберите видео файл") => Build(title, InputFormats.Video);

        public static PickOptions AudioOrVideo(string title = "Выберите аудио или видео файл") =>
            Build(title, InputFormats.Audio.Concat(InputFormats.Video));

        public static PickOptions Image(string title = "Выберите изображение") => Build(title, InputFormats.Image);

        public static PickOptions Any(string title = "Выберите файл") =>
            Build(title, InputFormats.Audio.Concat(InputFormats.Video).Concat(InputFormats.Image));

        private static PickOptions Build(string title, IEnumerable<string> extensions)
        {
            var options = new PickOptions { PickerTitle = title };

            // Список расширений для диалога Windows. На других платформах фильтр не задаём — проверка идёт по расширению после выбора.
            if (DeviceInfo.Platform == DevicePlatform.WinUI)
            {
                var dotted = extensions.Distinct().Select(e => "." + e).ToArray();
                options.FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, dotted }
                });
            }
            return options;
        }
    }
}
