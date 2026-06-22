using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace DazzConv.Services
{
    public class HotkeyService
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
            if (_dotNetRef == null)
            {
                _dotNetRef = DotNetObjectReference.Create(this);
            }
            await _jsRuntime.InvokeVoidAsync("DazzConvHotkeys.init", _dotNetRef);
        }

        [JSInvokable]
        public async Task OnHotkeyOpen()
        {
            if (OnOpenRequested != null)
                await OnOpenRequested();
        }

        [JSInvokable]
        public async Task OnHotkeySave()
        {
            if (OnSaveRequested != null)
                await OnSaveRequested();
        }

        [JSInvokable]
        public async Task OnHotkeyPaste()
        {
            if (OnPasteRequested != null)
                await OnPasteRequested();
        }

        [JSInvokable]
        public async Task OnHotkeyPause()
        {
            if (OnPauseRequested != null)
                await OnPauseRequested();
        }

        [JSInvokable]
        public async Task OnHotkeyCancel()
        {
            if (OnCancelRequested != null)
                await OnCancelRequested();
        }

        [JSInvokable]
        public async Task OnHotkeyClear()
        {
            if (OnClearRequested != null)
                await OnClearRequested();
        }
    }
}