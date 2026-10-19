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
    // шаг назад в истории браузера (закрыть карточку коллизии так же, как боковой кнопкой мыши)
    back: function () { history.back(); },
    // скачать текст файлом (CSV для Excel: с BOM — иначе Excel искажает кириллицу)
    download: function (name, text) {
        const blob = new Blob(['\ufeff' + text], { type: 'text/csv;charset=utf-8' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = name;
        document.body.appendChild(a);
        a.click();
        setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
    },
    // светлая / тёмная тема — переключатель в шапке, выбор запоминается в браузере
    toggleTheme: function () {
        const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
        document.documentElement.dataset.theme = next;
        try { localStorage.setItem('ras-theme', next); } catch (e) { }
    },
    // удобства одного зрителя (вид списка проектов и т. п.) — в браузере; нет доступа к хранилищу — по умолчанию
    load: function (key) {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    },
    save: function (key, value) {
        try { localStorage.setItem(key, value); } catch (e) { }
    },
    // карточка коллизии — в начало (открыта другая коллизия)
    top: function (el) {
        if (el) el.scrollTop = 0;
    },
    // выбранная строка списка — в видимую часть (стрелки ↑/↓)
    reveal: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ block: 'nearest' });
    }
};

// Мост в Revit (DECISIONS.md, «Мост в Revit…»): модуль RAS-Clash Analysis в Revit слушает 127.0.0.1 на компьютере
// пользователя; спрашивает браузер — сервер сайта до этого компьютера не достаёт. Индикатор в шапке — раз в 4 секунды;
// «Показать в Revit» — тому Revit, где открыта модель коллизии (открыты два — спрашиваем по очереди).
ras.revit = (function () {
    const ports = [5181, 5182, 5183, 5184, 5185];
    let live = [];

    async function ask(port, path, body, ms) {
        const ctl = new AbortController();
        const timer = setTimeout(function () { ctl.abort(); }, ms);
        try {
            // тело — простым текстом: без предварительного запроса браузера (JSON внутри мост читает сам)
            const r = await fetch('http://127.0.0.1:' + port + path, body === undefined
                ? { signal: ctl.signal, cache: 'no-store' }
                : { method: 'POST', body: JSON.stringify(body), signal: ctl.signal, cache: 'no-store' });
            return r.ok ? await r.json() : null;
        } catch (e) {
            return null;
        } finally {
            clearTimeout(timer);
        }
    }

    async function poll() {
        const found = [];
        await Promise.all(ports.map(async function (p) {
            const s = await ask(p, '/status', undefined, 800);
            if (s) found.push({ port: p, status: s });
        }));
        found.sort(function (a, b) { return a.port - b.port; });
        live = found;
        paint();
    }

    function paint() {
        const el = document.getElementById('revit-state');
        if (!el) return;
        if (live.length === 0) {
            el.className = 'revit-state off';
            el.textContent = 'Revit';
            el.title = 'Revit с мостом не запущен — «Показать в Revit» не работает';
            return;
        }
        const s = live[0].status;
        const docs = s.documents || [];
        const doc = docs.find(function (d) { return d.active; }) || docs[0];
        el.className = 'revit-state on';
        el.textContent = 'Revit ' + s.revit;
        el.title = 'Revit ' + s.revit + ' подключён' + (doc ? ' — открыта «' + doc.title + '»' : ' — модель не открыта')
            + (live.length > 1 ? ' (и ещё ' + (live.length - 1) + ')' : '');
    }

    async function show(request) {
        if (live.length === 0) await poll();
        if (live.length === 0) {
            warn('Revit не подключён', ['Запустите Revit 2026 с плагином RAS v. 2.0 и откройте модель коллизии.',
                'Revit уже открыт — перезапустите его: мост запускается вместе с Revit.']);
            return;
        }
        let failed = null;
        for (const b of live) {
            const r = await ask(b.port, '/show', request, 20000);
            if (!r) continue;
            if (r.ok) {
                if (r.pending) warn('Revit занят', ['Коллизия откроется, как только Revit освободится: закройте открытое в нём окно или дождитесь конца команды.']);
                else if (r.warnings && r.warnings.length) warn('Показано не всё', r.warnings);
                return;
            }
            failed = failed || r;
        }
        if (failed) warn('Не получилось показать в Revit', [failed.error].concat(failed.warnings || []).filter(Boolean));
        else warn('Revit не ответил', ['Возможно, в Revit открыто окно или идёт долгая команда — закройте его и нажмите ещё раз.']);
    }

    // предупреждение — окном по центру, всё остальное — как под тёмным стеклом (разработчик, 19.10.2026)
    function warn(title, lines) {
        close();
        const back = document.createElement('div');
        back.id = 'ras-modal';
        back.className = 'ras-modal-back';
        back.innerHTML = '<div class="ras-modal" role="alertdialog" aria-modal="true" aria-labelledby="ras-modal-title">'
            + '<svg class="ras-modal-icon" viewBox="0 0 64 56" aria-hidden="true"><path d="M32 4 L61 52 H3 Z" fill="#e5484d" stroke="#e5484d" stroke-width="5" stroke-linejoin="round"/>'
            + '<rect x="29" y="18" width="6" height="19" rx="3" fill="#fff"/><circle cx="32" cy="44" r="3.6" fill="#fff"/></svg>'
            + '<h2 id="ras-modal-title"></h2><ul></ul><button class="btn primary" type="button">Понятно</button></div>';
        back.querySelector('h2').textContent = title;
        const ul = back.querySelector('ul');
        lines.forEach(function (line) {
            const li = document.createElement('li');
            li.textContent = line;
            ul.appendChild(li);
        });
        back.addEventListener('click', function (e) { if (e.target === back) close(); });
        back.querySelector('button').addEventListener('click', close);
        document.body.appendChild(back);
        back.querySelector('button').focus();
    }

    function close() {
        const old = document.getElementById('ras-modal');
        if (old) old.remove();
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && document.getElementById('ras-modal')) close();
    });
    poll();
    setInterval(poll, 4000);
    // JS не ждут: ответ Revit показывается здесь же
    return { show: function (request) { show(request); }, warn: warn };
})();

// 1 · 2 · 3 — статус открытой коллизии: «Активная», «Исправлена», «Допущена» (кроме набора текста в полях)
document.addEventListener('keydown', function (e) {
    if (e.ctrlKey || e.altKey || e.metaKey || (e.key !== '1' && e.key !== '2' && e.key !== '3')) return;
    const t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
    const b = document.querySelector('.clash-card .state-buttons button[data-key="' + e.key + '"]');
    if (b && !b.disabled) {
        e.preventDefault();
        b.click();
    }
});

// ↑ ↓ в списке коллизий выбирают соседнюю коллизию (это делает страница) — список при этом не прокручивается сам
document.addEventListener('keydown', function (e) {
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && e.target.closest && e.target.closest('.list') && e.target.tagName !== 'SELECT') {
        e.preventDefault();
    }
});
