/* انتخابگر تاریخ شمسی — برای هر ‎<input data-jdate>‎.
 *
 * تا امروز بازهٔ تاریخ در «گزارش عملکرد»، «نمودارهای مدیریتی»، «برگه‌های
 * کارشناسی» و «ردّ حسابرسی» فقط یک فیلدِ خالی با راهنمای «۱۴۰۵/۰۶/۰۱» بود:
 * کاربر باید تاریخ را از حفظ و با قالبِ درست تایپ می‌کرد، و اگر اشتباه می‌نوشت
 * فرم بی‌هیچ خطایی به بازهٔ پیش‌فرض برمی‌گشت.
 *
 * چرا ‎<input type="date">‎ نه: تقویمِ مرورگر میلادی است و کاربر باید در ذهنش
 * تبدیل کند — همان چیزی که این صفحه‌ها از اول از آن پرهیز کرده بودند.
 *
 * چرا کتابخانهٔ آماده نه: هیچ اسکریپت خارجی در این پروژه بار نمی‌شود (jsdelivr و
 * مانندش در ایران باز نمی‌شوند).
 *
 * تبدیل شمسی↔میلادی دستی نوشته نشده. حسابِ سال کبیسهٔ شمسی جای اشتباهِ ساکت
 * دارد، پس مبنا ‎Intl‎ با تقویم ‎persian‎ است — همانی که خودِ مرورگر پیاده کرده — و
 * تاریخِ میلادیِ متناظر با جست‌وجوی کوتاه پیدا و *با همان Intl راستی‌آزمایی*
 * می‌شود. یعنی خروجی یا دقیقاً درست است یا اصلاً ساخته نمی‌شود.
 */
(function () {
    'use strict';

    var MONTHS = ['فروردین', 'اردیبهشت', 'خرداد', 'تیر', 'مرداد', 'شهریور',
                  'مهر', 'آبان', 'آذر', 'دی', 'بهمن', 'اسفند'];
    // هفتهٔ ایرانی از شنبه شروع می‌شود، نه یکشنبه
    var WEEK = ['ش', 'ی', 'د', 'س', 'چ', 'پ', 'ج'];

    var FMT;
    try {
        FMT = new Intl.DateTimeFormat('en-u-ca-persian-nu-latn',
            { year: 'numeric', month: 'numeric', day: 'numeric' });
        // اگر تقویم پارسی پشتیبانی نشود، چیزی خراب نمی‌شود: فیلد همان فیلدِ
        // متنیِ قابل تایپ باقی می‌ماند.
        if (!FMT.formatToParts) FMT = null;
    } catch (_) { FMT = null; }
    if (!FMT) return;

    function faDigits(n) {
        return String(n).replace(/\d/g, function (d) { return '۰۱۲۳۴۵۶۷۸۹'[+d]; });
    }
    function enDigits(s) {
        return String(s)
            .replace(/[۰-۹]/g, function (d) { return '۰۱۲۳۴۵۶۷۸۹'.indexOf(d); })
            .replace(/[٠-٩]/g, function (d) { return '٠١٢٣٤٥٦٧٨٩'.indexOf(d); });
    }
    function pad2(n) { return (n < 10 ? '0' : '') + n; }

    /** میلادی → شمسی */
    function toJ(date) {
        var o = {};
        FMT.formatToParts(date).forEach(function (p) {
            if (p.type === 'year' || p.type === 'month' || p.type === 'day') {
                o[p.type] = parseInt(enDigits(p.value).replace(/\D/g, ''), 10);
            }
        });
        return (o.year && o.month && o.day) ? { y: o.year, m: o.month, d: o.day } : null;
    }

    /** شمسی → میلادی. اگر تاریخ وجود نداشته باشد (۳۱ اسفندِ غیرکبیسه) null. */
    function toG(y, m, d) {
        // حدسِ اولیه: ۱ فروردینِ سال y نزدیکِ ۲۱ مارسِ (y+621) است
        var g = new Date(y + 621, 2, 21);
        g.setDate(g.getDate() + Math.round((m - 1) * 30.44) + (d - 1));

        for (var i = 0; i < 40; i++) {
            var j = toJ(g);
            if (!j) return null;
            if (j.y === y && j.m === m && j.d === d) return g;
            // وزن‌ها فقط برای «جلوتر یا عقب‌تر» بودن‌اند، نه فاصلهٔ واقعی
            var diff = (j.y - y) * 372 + (j.m - m) * 31 + (j.d - d);
            g.setDate(g.getDate() + (diff > 0 ? -1 : 1));
        }
        return null;
    }

    function daysInMonth(y, m) {
        // به‌جای حفظ‌کردنِ قاعدهٔ کبیسه، از خودِ تقویم می‌پرسیم
        for (var d = 31; d >= 29; d--) if (toG(y, m, d)) return d;
        return 29;
    }

    function parseValue(raw) {
        var p = enDigits(raw || '').split(/[\/\-.]/).map(function (x) { return parseInt(x, 10); });
        if (p.length !== 3 || p.some(isNaN)) return null;
        if (p[0] < 1300 || p[0] > 1500 || p[1] < 1 || p[1] > 12 || p[2] < 1 || p[2] > 31) return null;
        return { y: p[0], m: p[1], d: p[2] };
    }

    // ---------- ساخت پنجره ----------

    var open = null;   // {box, input}

    function close() {
        if (!open) return;
        open.box.remove();
        open = null;
    }

    function build(input) {
        var today = toJ(new Date());
        var cur = parseValue(input.value) || today;

        var box = document.createElement('div');
        box.className = 'jdp';
        // کلیک داخل پنجره نباید آن را ببندد
        box.addEventListener('mousedown', function (e) { e.stopPropagation(); });

        var head = document.createElement('div');
        head.className = 'jdp-head';

        var selM = document.createElement('select');
        MONTHS.forEach(function (name, i) {
            var o = document.createElement('option');
            o.value = i + 1; o.textContent = name;
            selM.appendChild(o);
        });

        var selY = document.createElement('select');
        for (var y = today.y + 1; y >= today.y - 15; y--) {
            var o = document.createElement('option');
            o.value = y; o.textContent = faDigits(y);
            selY.appendChild(o);
        }

        head.appendChild(selM);
        head.appendChild(selY);
        box.appendChild(head);

        var wk = document.createElement('div');
        wk.className = 'jdp-wk';
        WEEK.forEach(function (w) {
            var s = document.createElement('span');
            s.textContent = w;
            wk.appendChild(s);
        });
        box.appendChild(wk);

        var grid = document.createElement('div');
        grid.className = 'jdp-grid';
        box.appendChild(grid);

        var foot = document.createElement('div');
        foot.className = 'jdp-foot';
        var bToday = document.createElement('button');
        bToday.type = 'button'; bToday.textContent = 'امروز';
        var bClear = document.createElement('button');
        bClear.type = 'button'; bClear.textContent = 'پاک کردن';
        foot.appendChild(bToday); foot.appendChild(bClear);
        box.appendChild(foot);

        function pick(y, m, d) {
            input.value = faDigits(y + '/' + pad2(m) + '/' + pad2(d));
            // فرم‌های دیگر ممکن است به تغییر گوش بدهند
            input.dispatchEvent(new Event('change', { bubbles: true }));
            close();
            input.focus();
        }

        function render() {
            var y = +selY.value, m = +selM.value;
            grid.textContent = '';

            var first = toG(y, m, 1);
            if (!first) return;
            // شنبه ستون اول: getDay شنبه ۶ است
            var lead = (first.getDay() + 1) % 7;
            for (var i = 0; i < lead; i++) {
                grid.appendChild(document.createElement('span'));
            }

            var n = daysInMonth(y, m);
            for (var d = 1; d <= n; d++) {
                (function (day) {
                    var b = document.createElement('button');
                    b.type = 'button';
                    b.textContent = faDigits(day);
                    if (y === today.y && m === today.m && day === today.d) b.className = 'now';
                    if (y === cur.y && m === cur.m && day === cur.d) b.className = 'on';
                    b.addEventListener('click', function () { pick(y, m, day); });
                    grid.appendChild(b);
                })(d);
            }
        }

        selY.value = cur.y;
        selM.value = cur.m;
        selY.addEventListener('change', render);
        selM.addEventListener('change', render);
        bToday.addEventListener('click', function () { pick(today.y, today.m, today.d); });
        bClear.addEventListener('click', function () {
            input.value = '';
            input.dispatchEvent(new Event('change', { bubbles: true }));
            close();
        });

        render();
        return box;
    }

    function show(input) {
        if (open && open.input === input) return;
        close();

        var box = build(input);
        document.body.appendChild(box);

        // موقعیت نسبت به صفحه (position:absolute روی body) تا داخل کارت‌هایی که
        // overflow دارند بریده نشود
        var r = input.getBoundingClientRect();
        var top = r.bottom + window.scrollY + 4;
        var left = r.left + window.scrollX;
        // اگر پایین جا نبود، بالای فیلد باز شود
        if (r.bottom + box.offsetHeight + 8 > window.innerHeight && r.top > box.offsetHeight) {
            top = r.top + window.scrollY - box.offsetHeight - 4;
        }
        // از لبهٔ راست بیرون نزند
        var maxLeft = window.scrollX + document.documentElement.clientWidth - box.offsetWidth - 6;
        box.style.top = top + 'px';
        box.style.left = Math.max(window.scrollX + 6, Math.min(left, maxLeft)) + 'px';

        open = { box: box, input: input };
    }

    document.addEventListener('mousedown', function (e) {
        if (!e.target.closest || !e.target.closest('input[data-jdate]')) close();
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') close();
    });
    window.addEventListener('resize', close);

    function bind(input) {
        if (input.dataset.jdateBound) return;
        input.dataset.jdateBound = '1';
        input.setAttribute('autocomplete', 'off');
        input.addEventListener('focus', function () { show(input); });
        input.addEventListener('click', function () { show(input); });
    }

    function scan() { document.querySelectorAll('input[data-jdate]').forEach(bind); }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scan);
    } else {
        scan();
    }
})();
