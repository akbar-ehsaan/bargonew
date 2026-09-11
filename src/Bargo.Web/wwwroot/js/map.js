/* نقشه‌های پنل — روی هر <div class="map" data-map='{...}'>.
 *
 * قالب data-map:
 *   { "center":[35.7,51.4], "zoom":6,
 *     "markers":[{ "lat":35.7, "lng":51.4, "kind":"truck|origin|dest|idle|stale", "label":"متن", "href":"/..." }],
 *     "line":[[lat,lng],...],
 *     "poll":"/Admin/Live/Data",   // اختیاری: هر ۳۰ ثانیه همان قالب JSON را برمی‌گرداند
 *     "fit":true }
 * آدرس کاشی از data-tiles می‌آید (تنظیم Map.TileUrl).
 * نشانگرها divIcon با bootstrap-icons‌اند تا به تصویرهای Leaflet وابسته نباشند.
 */
(function () {
    'use strict';
    if (!window.L) return;

    var ICON = { truck: 'bi-truck', origin: 'bi-box-arrow-up', dest: 'bi-flag-fill', idle: 'bi-truck', stale: 'bi-truck', driver: 'bi-person-fill' };

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

    function pin(kind) {
        return L.divIcon({ className: '', html: '<div class="map-pin ' + esc(kind || 'truck') + '"><i class="bi ' + (ICON[kind] || 'bi-geo-alt-fill') + '"></i></div>', iconSize: [30, 30], iconAnchor: [15, 30], popupAnchor: [0, -28] });
    }

    function draw(map, layer, data, fit) {
        layer.clearLayers();
        var bounds = [];
        (data.markers || []).forEach(function (m) {
            if (m.lat == null || m.lng == null) return;
            var mk = L.marker([m.lat, m.lng], { icon: pin(m.kind) }).addTo(layer);
            if (m.label) mk.bindPopup(esc(m.label).replace(/\n/g, '<br>') + (m.href ? '<br><a href="' + esc(m.href) + '">جزئیات</a>' : ''));
            bounds.push([m.lat, m.lng]);
        });
        if (data.line && data.line.length > 1) {
            L.polyline(data.line, { color: getComputedStyle(document.body).getPropertyValue('--primary').trim() || '#1d5aa6', weight: 4, opacity: .85 }).addTo(layer);
            data.line.forEach(function (p) { bounds.push(p); });
        }
        if (fit && bounds.length > 1) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 13 });
        else if (fit && bounds.length === 1) map.setView(bounds[0], 11);
    }

    document.querySelectorAll('.map[data-map]').forEach(function (el) {
        var data;
        try { data = JSON.parse(el.getAttribute('data-map')); } catch (_) { return; }
        var map = L.map(el, { zoomControl: true, attributionControl: false }).setView(data.center || [32.4, 53.7], data.zoom || 5);
        L.tileLayer(el.getAttribute('data-tiles') || 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18 }).addTo(map);
        var layer = L.layerGroup().addTo(map);
        draw(map, layer, data, data.fit !== false);

        if (data.poll) {
            setInterval(function () {
                fetch(data.poll, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (j) { if (j) draw(map, layer, j, false); })
                    .catch(function () { });
            }, 30000);
        }
    });
})();
