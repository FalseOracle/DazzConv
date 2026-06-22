window.DazzConvHotkeys = {
    init: function (dotNetRef) {
        if (window._dazzconvHotkeysInitialized) return;

        document.addEventListener('keydown', (e) => {
            const tag = document.activeElement?.tagName;
            const isInput = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';

            const key = e.key.toLowerCase();
            const ctrl = e.ctrlKey;
            const shift = e.shiftKey;
            const alt = e.altKey;

            // === ЅЋќ »–ќ¬ ј Ѕ–ј”«≈–Ќџ’ √ќ–я„»’  Ћј¬»Ў ===

            // Ctrl+P Ч печать
            if (ctrl && key === 'p') {
                e.preventDefault();
                return;
            }

            // Ctrl+F Ч поиск в браузере
            if (ctrl && key === 'f') {
                e.preventDefault();
                return;
            }

            // Ctrl+S Ч сохран€ем дл€ своего обработчика
            if (ctrl && key === 's') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnHotkeySave');
                return;
            }

            // Ctrl+O Ч открываем дл€ своего обработчика
            if (ctrl && key === 'o') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnHotkeyOpen');
                return;
            }

            // Ctrl+Shift+C Ч очистка
            if (ctrl && shift && key === 'c') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnHotkeyClear');
                return;
            }

            // F12 Ч DevTools (блокируем)
            if (key === 'f12') {
                e.preventDefault();
                return;
            }

            // F5 Ч обновление (блокируем)
            if (key === 'f5' && !ctrl) {
                e.preventDefault();
                return;
            }

            // Ctrl+R Ч обновление (блокируем)
            if (ctrl && key === 'r') {
                e.preventDefault();
                return;
            }

            // Ctrl+W Ч закрытие вкладки (блокируем)
            if (ctrl && key === 'w') {
                e.preventDefault();
                return;
            }

            // Ctrl+T Ч нова€ вкладка (блокируем)
            if (ctrl && key === 't') {
                e.preventDefault();
                return;
            }

            // Ctrl+N Ч новое окно (блокируем)
            if (ctrl && key === 'n') {
                e.preventDefault();
                return;
            }

            // Alt+F4 Ч закрытие (блокируем)
            if (alt && key === 'f4') {
                e.preventDefault();
                return;
            }

            // === —¬ќ» ќЅ–јЅќ“„» » ===

            // Space Ч пауза/возобновить (только не в пол€х ввода)
            if (key === ' ' && !ctrl && !isInput) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnHotkeyPause');
                return;
            }

            // Esc Ч отмена
            if (key === 'escape') {
                dotNetRef.invokeMethodAsync('OnHotkeyCancel');
                return;
            }

            // Ctrl+V Ч вставить (только не в пол€х ввода)
            if (ctrl && key === 'v' && !isInput) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnHotkeyPaste');
                return;
            }
        });

        // Ѕлокируем контекстное меню (правый клик)
        document.addEventListener('contextmenu', (e) => {
            // –азрешаем только в пол€х ввода
            const tag = e.target?.tagName;
            if (tag !== 'INPUT' && tag !== 'TEXTAREA') {
                e.preventDefault();
            }
        });

        window._dazzconvHotkeysInitialized = true;
        console.log('[DazzConv] √ор€чие клавиши активированы');
    }
};