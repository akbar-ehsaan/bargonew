/* ردیفِ جدول که با دابل‌کلیک (یا Enter) باز می‌شود.
 *
 * هر ردیفِ ‎<tr class="row-link" data-href="...">‎ خودبه‌خود کار می‌کند؛ لازم نیست
 * هر صفحه اسکریپت خودش را داشته باشد. پیش‌تر همین رفتار داخل ویوِ «کارشناسان»
 * نوشته شده بود و برای صفحهٔ دوم باید کپی می‌شد — و کپیِ سوم همان‌جایی است که
 * دو نسخه از هم جدا می‌افتند.
 *
 * چرا دابل‌کلیک و نه تک‌کلیک: داخل همین ردیف‌ها دکمه و لینک و فرم هست. با
 * تک‌کلیک، انتخابِ متن یا خطای دست هم صفحه را عوض می‌کرد.
 *
 * ⚠️ دابل‌کلیک روی لمس قابل اتکا نیست. برای همین هر ردیف باید یک لینکِ دیدنی هم
 * داشته باشد (نامِ ردیف)، وگرنه روی موبایل راهی به مقصد نمی‌ماند.
 */
(function () {
    'use strict';

    // کنش‌های داخلِ ردیف باید کار خودشان را بکنند، نه اینکه صفحه را عوض کنند.
    // ‎.no-nav‎ برای خانه‌هایی است که خودشان کنش نیستند ولی نباید پیمایش کنند —
    // مثل خانهٔ چک‌باکسِ انتخابِ گروهی، که فضای خالیِ کنارِ چک‌باکس هم باید امن باشد.
    var INTERACTIVE = 'a,button,input,select,textarea,label,form,[role="button"],.no-nav';

    function fromInteractive(e) {
        var t = e.target;
        return !!(t && t.closest && t.closest(INTERACTIVE));
    }

    function go(row) {
        var href = row.getAttribute('data-href');
        if (href) window.location.href = href;
    }

    function bind(row) {
        if (row.dataset.rowlinkBound) return;
        row.dataset.rowlinkBound = '1';

        row.addEventListener('dblclick', function (e) {
            if (fromInteractive(e)) return;
            // دابل‌کلیک متنِ ردیف را انتخاب می‌کند؛ قبل از رفتن پاکش می‌کنیم
            if (window.getSelection) {
                try { window.getSelection().removeAllRanges(); } catch (_) { }
            }
            go(row);
        });

        row.addEventListener('keydown', function (e) {
            if (e.key !== 'Enter') return;
            if (fromInteractive(e)) return;
            e.preventDefault();
            go(row);
        });
    }

    function scan() {
        document.querySelectorAll('tr.row-link[data-href]').forEach(bind);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scan);
    } else {
        scan();
    }
})();
