using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DazzConv.Services
{
    /// <summary>
    /// Диалоги выбора папки/файла. Заменяет и FolderPickerHelper.cs (он нигде не использовался — файл можно удалить).
    /// </summary>
    public static class Win32FolderPicker
    {
        public static Task<string?> PickFolderAsync(string title = "Выберите папку")
        {
            return RunSta<string?>(() =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = title,
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    AutoUpgradeEnabled = true
                };

                return dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                    ? dialog.SelectedPath
                    : null;
            });
        }

        public static Task<string?> PickFileAsync(string title = "Выберите файл", string filter = "Все файлы|*.*")
        {
            return RunSta<string?>(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true,
                    CheckPathExists = true
                };

                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            });
        }

        // Общий запуск в STA-потоке. Таймаут 60 сек убран: он «закрывал» результат,
        // пока диалог ещё висел на экране, и выбранная папка терялась.
        private static Task<T> RunSta<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }
    }
}
