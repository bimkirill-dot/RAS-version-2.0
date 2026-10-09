// Мелочи, которых нет в Blazor: копирование в буфер обмена и прокрутка к строке.
window.ras = {
    // Буфер обмена браузер даёт только на «безопасном» адресе (localhost — да, http://имя-компьютера — нет):
    // там — запасной способ через скрытое поле.
    copy: async function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch (e) { }
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch (e) { }
        document.body.removeChild(ta);
        return ok;
    },
    // выбранная строка списка — в видимую часть (стрелки ↑/↓)
    reveal: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ block: 'nearest' });
    }
};

// ↑ ↓ в списке коллизий выбирают соседнюю коллизию (это делает страница) — список при этом не прокручивается сам
document.addEventListener('keydown', function (e) {
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && e.target.closest && e.target.closest('.list') && e.target.tagName !== 'SELECT') {
        e.preventDefault();
    }
});
