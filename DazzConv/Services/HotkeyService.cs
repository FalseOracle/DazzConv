using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace DazzConv.Services
{
    public class HotkeyService : IDisposable
    {
        private readonly IJSRuntime _jsRuntime;
        private DotNetObjectReference<HotkeyService>? _dotNetRef;

        public event Func<Task>? OnOpenRequested;
        public event Func<Task>? OnSaveRequested;
        public event Func<Task>? OnPasteRequested;
        public event Func<Task>? OnPauseRequested;
        public event Func<Task>? OnCancelRequested;
        public event Func<Task>? OnClearRequested;

        public HotkeyService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task InitializeAsync()
        {
            _dotNetRef ??= DotNetObjectReference.Create(this);
            await _jsRuntime.InvokeVoidAsync("DazzConvHotkeys.init", _dotNetRef);
        }

        [JSInvokable] public Task OnHotkeyOpen() => Raise(OnOpenRequested);
        [JSInvokable] public Task OnHotkeySave() => Raise(OnSaveRequested);
        [JSInvokable] public Task OnHotkeyPaste() => Raise(OnPasteRequested);
        [JSInvokable] public Task OnHotkeyPause() => Raise(OnPauseRequested);
        [JSInvokable] public Task OnHotkeyCancel() => Raise(OnCancelRequested);
        [JSInvokable] public Task OnHotkeyClear() => Raise(OnClearRequested);

        // Раньше — 6 одинаковых методов с if + await. Поддержаны и несколько подписчиков.
        private static async Task Raise(Func<Task>? handlers)
        {
            if (handlers == null) return;
            foreach (Func<Task> h in handlers.GetInvocationList())
                await h();
        }

        public void Dispose()
        {
            _dotNetRef?.Dispose();
            _dotNetRef = null;
        }
    }
}
