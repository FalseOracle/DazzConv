using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DazzConv.Services
{
    public static class Win32FolderPicker
    {
        private const int TimeoutMs = 60000; // 60 секунд

        public static async Task<string?> PickFolderAsync(string title = "Выберите папку")
        {
            var tcs = new TaskCompletionSource<string?>();
            Exception? capturedException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = title,
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true,
                        AutoUpgradeEnabled = true
                    };

                    var result = dialog.ShowDialog();

                    if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        tcs.TrySetResult(dialog.SelectedPath);
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    tcs.TrySetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            // Таймаут - если диалог не закрылся за 60 секунд
            using var cts = new CancellationTokenSource(TimeoutMs);
            using var registration = cts.Token.Register(() =>
            {
                tcs.TrySetResult(null);
            });

            try
            {
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public static async Task<string?> PickFileAsync(string title = "Выберите файл", string filter = "Все файлы|*.*")
        {
            var tcs = new TaskCompletionSource<string?>();

            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = title,
                        Filter = filter,
                        CheckFileExists = true,
                        CheckPathExists = true
                    };

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        tcs.TrySetResult(dialog.FileName);
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return await tcs.Task;
        }
    }
}