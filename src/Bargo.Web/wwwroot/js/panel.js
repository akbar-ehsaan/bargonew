/* رفتارهای مشترک پنل‌ها — بدون کتابخانه.
 *  ۱) کشوی منو و گروه‌های تاشو
 *  ۲) برچسب ستون روی سلول‌های جدول (برای نمای کارتیِ موبایل)
 *  ۳) ردیف قابل کلیک: <tr data-href="...">
 *  ۴) تأیید پیش از ارسال: <form data-confirm="متن">
 *  ۵) ارسال دوره‌ای موقعیت راننده (فقط پنل راننده و فقط اگر خاموش نشده باشد)
 */
(function () {
    'use strict';

    // ---- ۱. منو ----
    var sidebar = document.getElementById('sidebar');
    var ovl = document.getElementById('ovl');
    function openMenu() { sidebar && sidebar.classList.add('show'); ovl && ovl.classList.add('show'); }
    function closeMenu() { sidebar && sidebar.classList.remove('show'); ovl && ovl.classList.remove('show'); }
    document.querySelectorAll('[data-open-menu]').forEach(function (b) { b.addEventListener('click', openMenu); });
    if (ovl) ovl.addEventListener('click', closeMenu);
    addEventListener('resize', function () { if (innerWidth > 992) closeMenu(); });

    // آکاردئون: بازکردن یک گروه بقیه را می‌بندد؛ گروهِ صفحهٔ جاری از سرور باز می‌آید
    document.querySelectorAll('.sidebar .grp').forEach(function (g) {
        var h = g.querySelector('.grp-h'), body = g.querySelector('.grp-b');
        if (!h || !body) return;
        h.addEventListener('click', function () {
            var willOpen = body.hidden;
            if (willOpen) {
                document.querySelectorAll('.sidebar .grp').forEach(function (o) {
                    if (o === g) return;
                    var ob = o.querySelector('.grp-b'), oh = o.querySelector('.grp-h');
                    if (ob) ob.hidden = true;
                    if (oh) oh.setAttribute('aria-expanded', 'false');
                    o.classList.remove('open');
                });
            }
            body.hidden = !willOpen;
            h.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
            g.classList.toggle('open', willOpen);
        });
    });

    // ---- ۲. جدول واکنش‌گرا ----
    document.querySelectorAll('table.tbl').forEach(function (t) {
        var heads = Array.prototype.map.call(t.querySelectorAll('thead th'), function (th) { return th.textContent.trim(); });
        t.querySelectorAll('tbody tr').forEach(function (tr) {
            Array.prototype.forEach.call(tr.children, function (td, i) {
                if (!td.classList.contains('empty') && heads[i] && !td.hasAttribute('data-label')) td.setAttribute('data-label', heads[i]);
            });
        });
    });

    // ---- ۳. ردیف قابل کلیک ----
    document.querySelectorAll('tr[data-href]').forEach(function (tr) {
        tr.classList.add('row-link');
        tr.tabIndex = 0;
        function go(e) {
            if (e.target.closest('a,button,input,select,textarea,form')) return;
            location.href = tr.getAttribute('data-href');
        }
        tr.addEventListener('click', go);
        tr.addEventListener('keydown', function (e) { if (e.key === 'Enter') go(e); });
    });

    // ---- ۴. تأیید ----
    document.querySelectorAll('form[data-confirm]').forEach(function (f) {
        f.addEventListener('submit', function (e) {
            if (!confirm(f.getAttribute('data-confirm'))) e.preventDefault();
        });
    });

    // فرم ثبت بار: ?section=route → اسکرول به همان بخش
    var sec = new URLSearchParams(location.search).get('section');
    if (sec) {
        var el = document.getElementById('sec-' + sec);
        if (el) { el.scrollIntoView({ block: 'start' }); el.classList.add('focus'); }
    }

    // ---- ۵. موقعیت راننده ----
    // خاموش/روشن از صفحهٔ «اشتراک موقعیت» (localStorage: bg:track = off)
    if (document.body.getAttribute('data-role') !== 'driver' || !navigator.geolocation) return;
    var off = false;
    try { off = localStorage.getItem('bg:track') === 'off'; } catch (_) { }
    if (off) return;

    var token = document.querySelector('input[name="__RequestVerificationToken"]');
    function send() {
        navigator.geolocation.getCurrentPosition(function (pos) {
            fetch('/api/track', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                body: JSON.stringify({
                    lat: pos.coords.latitude, lng: pos.coords.longitude,
                    speed: pos.coords.speed != null ? pos.coords.speed * 3.6 : null,
                    accuracy: pos.coords.accuracy
                })
            }).then(function (r) { return r.ok ? r.json() : null; })
              .then(function (j) { if (j && j.intervalMin) schedule(j.intervalMin); })
              .catch(function () { });
        }, function () { }, { enableHighAccuracy: true, maximumAge: 60000, timeout: 20000 });
    }
    var timer = null;
    function schedule(min) {
        if (timer) clearInterval(timer);
        timer = setInterval(send, Math.max(1, min) * 60000);
    }
    void token;
    send();
    schedule(5);
})();
