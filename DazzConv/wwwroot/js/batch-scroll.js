window.BatchScroll = {
    scrollLeft: function (elementId) {
        try {
            const el = document.getElementById(elementId);
            if (el) {
                el.scrollBy({ left: -300, behavior: 'smooth' });
                return true;
            }
            return false;
        } catch (e) {
            console.error('BatchScroll.scrollLeft error:', e);
            return false;
        }
    },

    scrollRight: function (elementId) {
        try {
            const el = document.getElementById(elementId);
            if (el) {
                el.scrollBy({ left: 300, behavior: 'smooth' });
                return true;
            }
            return false;
        } catch (e) {
            console.error('BatchScroll.scrollRight error:', e);
            return false;
        }
    }
};