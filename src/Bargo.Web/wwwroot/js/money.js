/* قالب‌بندیِ مبلغ حین تایپ — «۲۰۰۰۰۰» را همان لحظه «200,000» می‌کند.
 *
 * چرا لازم است: عددِ بی‌جداکننده را کسی نمی‌تواند بخواند. کاربر «۲۰۰۰۰۰۰» را تایپ
 * می‌کند و نمی‌داند دو میلیون زده یا بیست میلیون — و در فرمِ شارژ کیف‌پول، این
 * اشتباه یعنی پولِ واقعی.
 *
 * چرا سمت سرور کافی نبود: قالب‌بندیِ فرهنگ فقط روی «نمایش» اثر دارد. چیزی که کاربر
 * در فیلد تایپ می‌کند تا لحظهٔ ارسال هیچ‌جا از سرور رد نمی‌شود، پس باید همین‌جا
 * انجام شود.
 *
 * ⚠️ ویرگول‌ها را هنگام ارسال پاک نمی‌کنیم و لازم هم نیست:
 * SettingsService.TryParseLong خودش ‎,‎ و ‎٬‎ و فاصله و نیم‌فاصله را دور می‌ریزد.
 * پاک‌کردنشان در جاوااسکریپت یعنی یک نقطهٔ شکستِ اضافه اگر اسکریپت بار نشود.
 */
(function () {
    'use strict';

    var SELECTOR = 'input[name="amountToman"], input.money, input[data-money]';

    // ارقام فارسی/عربی → لاتین، و هر چیزِ غیررقم بیرون
    function digitsOnly(s) {
        return String(s)
            .replace(/[۰-۹]/g, function (d) { return '۰۱۲۳۴۵۶۷۸۹'.indexOf(d); })
            .replace(/[٠-٩]/g, function (d) { return '٠١٢٣٤٥٦٧٨٩'.indexOf(d); })
            .replace(/\D/g, '');
    }

    function group(digits) {
        return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    }

    function format(el) {
        var before = el.value;
        var caret = el.selectionStart;

        // تعداد رقم‌های سمت چپِ مکان‌نما — مبنای بازگرداندنِ مکان‌نما پس از قالب‌بندی.
        // بدون این، مکان‌نما با هر ویرگولِ تازه به آخر می‌پرد و تایپ در وسطِ عدد
        // غیرممکن می‌شود.
        var digitsBeforeCaret = digitsOnly(before.slice(0, caret)).length;

        var digits = digitsOnly(before);
        if (digits.length > 15) digits = digits.slice(0, 15);   // سدّ عددِ بی‌معنا
        var next = digits ? group(digits) : '';
        if (next === before) return;

        el.value = next;

        // مکان‌نما را بعد از همان تعداد رقم می‌گذاریم، نه در همان اندیس
        var pos = 0, seen = 0;
        while (pos < next.length && seen < digitsBeforeCaret) {
            if (/\d/.test(next[pos])) seen++;
            pos++;
        }
        try { el.setSelectionRange(pos, pos); } catch (e) { /* بعضی type‌ها اجازه نمی‌دهند */ }
    }

    function attach(el) {
        if (el.dataset.moneyBound) return;
        el.dataset.moneyBound = '1';

        // type=number ویرگول را نمی‌پذیرد و مقدار را خالی می‌کند؛ به text تبدیلش
        // می‌کنیم و صفحه‌کلید عددی را با inputmode نگه می‌داریم.
        if (el.type === 'number') { el.type = 'text'; }
        if (!el.getAttribute('inputmode')) el.setAttribute('inputmode', 'numeric');
        el.setAttribute('dir', 'ltr');
        el.style.textAlign = el.style.textAlign || 'left';

        el.addEventListener('input', function () { format(el); });
        el.addEventListener('blur', function () { format(el); });
        if (el.value) format(el);              // مقدارِ از پیش پرشده هم قالب بگیرد
    }

    function scan(root) {
        (root || document).querySelectorAll(SELECTOR).forEach(attach);
    }

    /* پرکردنِ فیلد از سمت کد (مثلاً هنگام ویرایشِ یک رکورد) با همان قالب‌بندی.
       بدون این، مقدارِ ریخته‌شده تا اولین کلیدِ کاربر بی‌جداکننده می‌ماند. */
    window.setMoney = function (el, value) {
        if (!el) return;
        attach(el);
        el.value = group(digitsOnly(String(value == null ? '' : value)));
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { scan(); });
    } else {
        scan();
    }

    // فرم‌هایی که بعداً به صفحه اضافه می‌شوند (مودال، بارگذاری با fetch) هم پوشش
    // داده شوند، وگرنه فقط فیلدهای لحظهٔ بارگذاری قالب می‌گیرند.
    if (window.MutationObserver) {
        new MutationObserver(function (muts) {
            muts.forEach(function (m) {
                m.addedNodes.forEach(function (n) {
                    if (n.nodeType !== 1) return;
                    if (n.matches && n.matches(SELECTOR)) attach(n);
                    else scan(n);
                });
            });
        }).observe(document.documentElement, { childList: true, subtree: true });
    }
})();
