// Прокрутка к элементу
window.scrollToElement = function (elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ 
            behavior: 'smooth', 
            block: 'center' 
        });
        // Подсветка элемента
        element.classList.add('result-highlight');
        setTimeout(() => {
            element.classList.remove('result-highlight');
        }, 2000);
    }
};

// Прокрутка наверх
window.scrollToTop = function () {
    window.scrollTo({ 
        top: 0, 
        behavior: 'smooth' 
    });
};