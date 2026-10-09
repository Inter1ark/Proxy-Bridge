/* ProxyBridge: buy proxies on the website.
   Same store as the "Get proxy" tab of the app. Website buyers have no license key: the browser
   keeps a secret cabinet token ("W-" + 30 url-safe chars) and sends it instead.
   Russian texts live here (RU), English ones in /i18n.js under kp.* keys. */
(function () {
  'use strict';

  var API = (window.PB_API || '') + '/api/store/';
  var CAB_RE = /^W-[A-Za-z0-9_-]{30}$/;
  var LS_CAB = 'pb_cabinet';
  var LS_USED = 'pb_cabinets_used';
  var SITE = 'https://www.proxybridge.org';
  var POLL_MS = 3000;
  var POLL_MAX_MS = 40 * 60 * 1000;

  // ------------------------------------------------------------------
  // Texts
  // ------------------------------------------------------------------
  var RU = {
    'kp.loadingGeo': 'Загружаем...',
    'kp.anyRegion': 'Любой регион',
    'kp.anyCity': 'Любой город',
    'kp.pickRegionFirst': 'Сначала выбери регион',
    'kp.perGb': '{0} ₽<span> / ГБ</span>',
    'kp.fromPerGb': 'от {0} ₽<span> / ГБ</span>',
    'kp.availChecking': 'Проверяем наличие...',
    'kp.availNo': 'Такого типа в этой стране сейчас нет. Выбери другую страну или тип.',
    'kp.gbHint': 'От {0} до {1} ГБ. Оплаченный трафик не сгорает по времени.',
    'kp.pay': 'Оплатить',
    'kp.payN': 'Оплатить {0} ₽',
    'kp.prodDc': 'Выделенный прокси',
    'kp.prodTraffic': 'Прокси за трафик',
    'kp.prodRenew': 'Продление прокси',
    'kp.prodTopup': 'Докупка трафика',
    'kp.sumTypeDc': 'Датацентр, HTTP и SOCKS5',
    'kp.volDc': '{0} дней, трафик без лимита',
    'kp.volGb': '{0} ГБ, без срока',
    'kp.capsRenew': 'Продление на {0} дней',
    'kp.capsTopup': 'Докупить трафик',
    'kp.kindDc': 'Выделенный',
    'kp.type.mobile': 'Мобильные',
    'kp.type.residential': 'Резидентские',
    'kp.type.datacenter': 'Датацентр',
    'kp.rot.static': 'статичный IP',
    'kp.rot.request': 'новый IP на каждый запрос',
    'kp.rot.interval': 'смена IP раз в {0} мин',
    'kp.st.active': 'Работает',
    'kp.st.exhausted': 'Трафик закончился',
    'kp.st.expired': 'Срок истёк',
    'kp.st.provisioning': 'Создаётся',
    'kp.usage': '{0} из {1} ГБ',
    'kp.until': 'до {0}',
    'kp.untilRenewed': 'до {0}, продление оплачено',
    'kp.noteProvisioning': 'Прокси создаётся. Данные для подключения появятся здесь через минуту, нажми «Обновить».',
    'kp.noteExhausted': 'Трафик закончился, прокси на паузе. Докупи ГБ, и он снова заработает.',
    'kp.noteExpired': 'Срок истёк. Купи новый прокси выше.',
    'kp.lineString': 'Строкой',
    'kp.topup': 'Докупить ГБ',
    'kp.renew': 'Продлить',
    'kp.newIp': 'Сменить IP',
    'kp.copy': 'Копировать',
    'kp.copied': 'Скопировано',
    'kp.ipChanged': 'IP сменится в течение нескольких секунд.',
    'kp.listEmpty': 'Здесь появятся прокси, купленные в этом браузере.',
    'kp.noStorage': 'Браузер не даёт сохранить данные сайта. Скопируй ссылку на кабинет до оплаты, иначе после оплаты прокси здесь не появится.',
    'kp.creating': 'Создаём заказ...',
    'kp.redirect': 'Переходим на страницу оплаты...',
    'kp.oCheckTitle': 'Проверяем заказ',
    'kp.oPendingTitle': 'Ждём подтверждение оплаты',
    'kp.oPendingText': 'Обычно это занимает несколько секунд. Страница обновится сама, не закрывай её.',
    'kp.oProcTitle': 'Оплата получена',
    'kp.oProcText': 'Создаём прокси, это займёт до минуты.',
    'kp.oDoneTitle': 'Готово.',
    'kp.oDoneText': 'Прокси готов. Он сохранён в разделе «Мои прокси» ниже.',
    'kp.oDoneRenew': 'Продление оплачено. Срок прокси продлится в день окончания.',
    'kp.oDoneTopup': 'Трафик добавлен. Если прокси стоял на паузе, он снова работает.',
    'kp.oFailTitle': 'Не получилось создать прокси',
    'kp.oFailRefunded': 'Деньги вернутся на карту в течение нескольких дней.',
    'kp.oFailSupport': 'Напиши на support@proxybridge.org и укажи время оплаты, вернём деньги.',
    'kp.oCanceledTitle': 'Оплата отменена',
    'kp.oCanceledText': 'Деньги не списаны. Можно оформить заказ заново.',
    'kp.oNotFoundTitle': 'Заказ не найден',
    'kp.oNotFoundText': 'В этом браузере нет такого заказа. Если ты оплачивал на другом устройстве, открой ссылку на кабинет оттуда.',
    'kp.oTimeoutTitle': 'Оплата пока не пришла',
    'kp.oTimeoutText': 'Если ты оплатил, прокси появится в разделе «Мои прокси». Нажми «Обновить» через пару минут.',
    'kp.oNetText': 'Нет связи с сервером ProxyBridge. Проверь интернет и нажми «Проверить ещё раз».',
    'kp.err.network': 'Нет связи с сервером ProxyBridge. Проверь интернет и попробуй ещё раз.',
    'kp.err.out_of_stock': 'Сейчас не можем выдать такой прокси. Попробуй позже или выбери другой вариант.',
    'kp.err.unavailable': 'Такого типа в этой стране сейчас нет. Выбери другую страну или тип.',
    'kp.err.renew_pending': 'Продление уже оплачено.',
    'kp.err.cannot_renew': 'Этот прокси уже нельзя продлить. Купи новый.',
    'kp.err.cannot_topup': 'К этому прокси нельзя докупить трафик.',
    'kp.err.cannot_refresh': 'Сейчас IP у этого прокси сменить нельзя.',
    'kp.err.too_often': 'Слишком часто. Подожди полминуты.',
    'kp.err.payment': 'Платёжный сервис не ответил. Попробуй ещё раз или выбери другой способ оплаты.',
    'kp.err.bad_gb': 'Укажи количество ГБ целым числом от {0} до {1}.',
    'kp.err.bad_ttl': 'Укажи интервал смены IP в минутах, от 1 до {0}.',
    'kp.err.disabled': 'Магазин временно закрыт.',
    'kp.err.empty': 'Магазин сейчас недоступен. Попробуй позже.',
    'kp.err.server': 'Ошибка сервера. Попробуй ещё раз.',
    'kp.err.pick_country': 'Выбери страну.',
    'kp.err.proxy_not_found': 'Прокси не найден в этом кабинете.'
  };

  function lang() { return (window.PB_I18N && window.PB_I18N.lang() === 'en') ? 'en' : 'ru'; }
  function t(k) {
    var s = null;
    if (lang() === 'en' && window.PB_I18N) s = window.PB_I18N.t(k);
    if (s === undefined || s === null) s = RU[k];
    if (s === undefined || s === null) s = k;
    for (var i = 1; i < arguments.length; i++) s = s.split('{' + (i - 1) + '}').join(String(arguments[i]));
    return s;
  }
  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function rub(n) { return Number(n).toLocaleString('ru-RU'); }
  function $(id) { return document.getElementById(id); }
  function show(el, on) { el.classList.toggle('hidden', !on); }
  function track(name, params) {
    try { if (window.pbTrack) window.pbTrack.goal(name, params || {}); } catch (e) { /* analytics is optional */ }
  }

  var ERR = {
    network: 'kp.err.network', out_of_stock: 'kp.err.out_of_stock', unavailable: 'kp.err.unavailable',
    renew_pending: 'kp.err.renew_pending', cannot_renew: 'kp.err.cannot_renew', cannot_topup: 'kp.err.cannot_topup',
    cannot_refresh: 'kp.err.cannot_refresh', too_often: 'kp.err.too_often', provider_error: 'kp.err.payment',
    bad_gb: 'kp.err.bad_gb', bad_ttl: 'kp.err.bad_ttl', store_disabled: 'kp.err.disabled',
    proxy_not_found: 'kp.err.proxy_not_found'
  };
  function errKey(code) { return ERR[code] || 'kp.err.server'; }

  // ------------------------------------------------------------------
  // Cabinet token
  // ------------------------------------------------------------------
  var storageOk = true;
  function lsGet(k) { try { return localStorage.getItem(k); } catch (e) { storageOk = false; return null; } }
  function lsSet(k, v) { try { localStorage.setItem(k, v); } catch (e) { storageOk = false; } }

  function newToken() {
    var abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'; // 64 chars: uniform with & 63
    var bytes = new Uint8Array(30);
    window.crypto.getRandomValues(bytes);
    var s = 'W-';
    for (var i = 0; i < bytes.length; i++) s += abc.charAt(bytes[i] & 63);
    return s;
  }

  function usedList() {
    try {
      var v = JSON.parse(lsGet(LS_USED) || '[]');
      return Array.isArray(v) ? v.filter(function (x) { return CAB_RE.test(x); }) : [];
    } catch (e) { return []; }
  }
  function markUsed(token) {
    var list = usedList().filter(function (x) { return x !== token; });
    list.unshift(token);
    lsSet(LS_USED, JSON.stringify(list.slice(0, 5)));
  }

  var cabinet = lsGet(LS_CAB);
  if (!CAB_RE.test(cabinet || '')) cabinet = null;
  if (window.PB_CABINET_FROM_LINK && CAB_RE.test(window.PB_CABINET_FROM_LINK)) {
    cabinet = window.PB_CABINET_FROM_LINK;
    lsSet(LS_CAB, cabinet);
    markUsed(cabinet);
  }
  if (!cabinet) {
    cabinet = newToken();
    lsSet(LS_CAB, cabinet);
  }
  // write test: some browsers allow reads but refuse writes
  if (lsGet(LS_CAB) !== cabinet) storageOk = false;

  function cabinetUrl() { return SITE + '/kupit-proksi/#c=' + cabinet; }

  // ------------------------------------------------------------------
  // API
  // ------------------------------------------------------------------
  function api(path, body) {
    body = body || {};
    body.cabinet = cabinet;
    return fetch(API + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    }).then(function (r) {
      return r.json().catch(function () { return { ok: false, error: r.status >= 500 ? 'server' : 'network' }; });
    }, function () {
      return { ok: false, error: 'network' };
    }).then(function (d) {
      if (!d || d.ok === false) {
        var e = new Error((d && d.error) || 'server');
        e.code = (d && d.error) || 'server';
        throw e;
      }
      return d;
    });
  }

  // ------------------------------------------------------------------
  // State
  // ------------------------------------------------------------------
  var S = {
    catalog: null,
    mode: 'dc',            // dc | traffic | renew | topup
    returnMode: 'dc',
    target: null,          // proxy object for renew / topup
    dcCountry: 'DE',
    country: 'DE',
    states: null,          // null = not loaded, [] = none
    cities: null,
    stateId: 0,
    cityId: 0,
    geoVersion: 0,
    type: 'residential',
    rotation: 'static',
    method: 'yookassa',
    avail: '',             // '' | checking | yes | no
    price: null,
    quoteSeq: 0,
    paying: false,
    status: null,          // {key, args, kind}
    listStatus: null,
    proxies: [],
    order: null            // order panel state
  };
  var quoteTimer = null;

  var q = new URLSearchParams(location.search);
  if (q.get('mode') === 'traffic' || q.get('mode') === 'dc') S.mode = S.returnMode = q.get('mode');

  // ------------------------------------------------------------------
  // Catalog and geo
  // ------------------------------------------------------------------
  function countryName(c) { return c ? (lang() === 'en' ? c.en : c.ru) || c.code : ''; }
  function sortByName(list) {
    var l = lang();
    return list.slice().sort(function (a, b) { return countryName(a).localeCompare(countryName(b), l); });
  }
  function findCountry(list, code) {
    for (var i = 0; i < (list || []).length; i++) if (list[i].code === code) return list[i];
    return null;
  }
  function trafficCountry() { return S.catalog && S.catalog.traffic ? findCountry(S.catalog.traffic.countries, S.country) : null; }
  function dcCountry() { return S.catalog && S.catalog.dc ? findCountry(S.catalog.dc.countries, S.dcCountry) : null; }

  function fillCountrySelect(sel, list, current) {
    var html = '';
    sortByName(list).forEach(function (c) {
      html += '<option value="' + esc(c.code) + '"' + (c.code === current ? ' selected' : '') + '>' + esc(countryName(c)) + '</option>';
    });
    sel.innerHTML = html;
  }

  function fillGeoSelect(sel, list, current, anyKey, loading) {
    var html = '<option value="0">' + esc(loading ? t('kp.loadingGeo') : t(anyKey)) + '</option>';
    (list || []).forEach(function (g) {
      html += '<option value="' + g.id + '"' + (g.id === current ? ' selected' : '') + '>' + esc(g.name) + '</option>';
    });
    sel.innerHTML = html;
  }

  function renderGeo() {
    fillGeoSelect($('kpState'), S.states, S.stateId, 'kp.anyRegion', S.states === null);
    var city = $('kpCity');
    if (!S.stateId) {
      city.innerHTML = '<option value="0">' + esc(t('kp.pickRegionFirst')) + '</option>';
      city.disabled = true;
    } else {
      fillGeoSelect(city, S.cities, S.cityId, 'kp.anyCity', S.cities === null);
      city.disabled = S.cities === null || !S.cities.length;
    }
    $('kpState').disabled = S.states === null || !S.states.length;
  }

  function loadStates() {
    var v = ++S.geoVersion;
    S.states = null; S.cities = []; S.stateId = 0; S.cityId = 0;
    renderGeo();
    var c = trafficCountry();
    if (!c || !c.id) { S.states = []; renderGeo(); return; }
    api('states', { country_id: c.id }).then(function (d) {
      if (v !== S.geoVersion) return;
      S.states = d.states || [];
      renderGeo();
    }, function () {
      if (v !== S.geoVersion) return;
      S.states = []; // regions are optional: the country alone still works
      renderGeo();
    });
  }

  function loadCities() {
    var v = ++S.geoVersion;
    S.cities = S.stateId ? null : []; S.cityId = 0;
    renderGeo();
    var c = trafficCountry();
    if (!c || !S.stateId) return;
    api('cities', { country_id: c.id, state_id: S.stateId }).then(function (d) {
      if (v !== S.geoVersion) return;
      S.cities = d.cities || [];
      renderGeo();
    }, function () {
      if (v !== S.geoVersion) return;
      S.cities = [];
      renderGeo();
    });
  }

  function checkAvailability() {
    if (!S.catalog || !S.catalog.traffic) return;
    var code = S.country, type = S.type;
    S.avail = 'checking';
    renderAvail();
    api('availability', { country: code, type: type }).then(function (d) {
      if (S.country === code && S.type === type) { S.avail = d.available ? 'yes' : 'no'; renderAvail(); renderSummary(); }
    }, function () {
      if (S.country === code && S.type === type) { S.avail = 'yes'; renderAvail(); renderSummary(); }
    });
  }

  function renderAvail() {
    var el = $('kpAvail');
    el.className = 'kp-avail' + (S.avail === 'no' ? ' no' : '');
    el.textContent = S.avail === 'checking' ? t('kp.availChecking') : S.avail === 'no' ? t('kp.availNo') : '';
  }

  function loadCatalog() {
    show($('kpLoading'), true);
    show($('kpLoadErr'), false);
    show($('kpFormBody'), false);
    api('catalog').then(function (d) {
      S.catalog = d;
      show($('kpLoading'), false);
      if (!d.dc && !d.traffic) {
        $('kpLoadErrText').textContent = t('kp.err.empty');
        show($('kpLoadErr'), true);
        renderSummary();
        return;
      }
      if (!d.dc && S.mode === 'dc') S.mode = S.returnMode = 'traffic';
      if (!d.traffic && S.mode === 'traffic') S.mode = S.returnMode = 'dc';
      if (d.dc && !findCountry(d.dc.countries, S.dcCountry)) S.dcCountry = (sortByName(d.dc.countries)[0] || {}).code || '';
      if (d.traffic && !findCountry(d.traffic.countries, S.country)) S.country = (sortByName(d.traffic.countries)[0] || {}).code || '';
      var t0 = $('kpTtl');
      if (d.traffic) {
        t0.max = d.traffic.ttl_max;
        $('kpGb').min = d.traffic.min_gb;
        $('kpGb').max = d.traffic.max_gb;
        $('kpGb').value = Math.max(d.traffic.min_gb, 1);
      }
      show($('kpFormBody'), true);
      renderAll();
      if (d.traffic) { loadStates(); checkAvailability(); }
    }, function (e) {
      show($('kpLoading'), false);
      $('kpLoadErrText').textContent = t(errKey(e.code));
      show($('kpLoadErr'), true);
    });
  }

  // ------------------------------------------------------------------
  // Form values and price
  // ------------------------------------------------------------------
  function minGb() { return S.catalog && S.catalog.traffic ? S.catalog.traffic.min_gb : 1; }
  function maxGb() { return S.catalog && S.catalog.traffic ? S.catalog.traffic.max_gb : 100; }
  function ttlMax() { return S.catalog && S.catalog.traffic ? S.catalog.traffic.ttl_max : 1440; }
  function dcDays() { return S.catalog && S.catalog.dc ? S.catalog.dc.days : 30; }
  function dcPrice() { return S.catalog && S.catalog.dc ? S.catalog.dc.price_rub : 400; }
  function intOf(v) { return /^\s*\d+\s*$/.test(String(v)) ? parseInt(v, 10) : NaN; }
  function gb() { return intOf($('kpGb').value); }
  function ttl() { return intOf($('kpTtl').value); }
  function gbPrice(type) {
    var p = S.catalog && S.catalog.traffic && S.catalog.traffic.gb_price_rub;
    return p && p[type] != null ? p[type] : null;
  }

  /** Returns [key, args...] for the first problem, or null when the form is complete. */
  function validate() {
    if (!S.catalog) return ['kp.err.server'];
    var g = gb();
    var gbOk = g >= minGb() && g <= maxGb();
    switch (S.mode) {
      case 'dc': return dcCountry() ? null : ['kp.err.pick_country'];
      case 'renew': return S.target ? null : ['kp.err.pick_country'];
      case 'topup': return !S.target ? ['kp.err.pick_country'] : gbOk ? null : ['kp.err.bad_gb', minGb(), maxGb()];
      default:
        if (!trafficCountry()) return ['kp.err.pick_country'];
        if (!gbOk) return ['kp.err.bad_gb', minGb(), maxGb()];
        if (S.rotation === 'interval') {
          var x = ttl();
          if (!(x >= 1 && x <= ttlMax())) return ['kp.err.bad_ttl', ttlMax()];
        }
        return null;
    }
  }

  function buildOrder() {
    switch (S.mode) {
      case 'dc': return { product: 'dc', params: { country: S.dcCountry } };
      case 'renew': return { product: 'dc_renew', params: { proxy_id: S.target.id } };
      case 'topup': return { product: 'topup', params: { proxy_id: S.target.id, gb: gb() } };
      default:
        var p = { country: S.country, type: S.type, rotation: S.rotation, gb: gb() };
        if (S.rotation === 'interval') p.ttl = ttl();
        if (S.stateId) p.state_id = S.stateId;
        if (S.stateId && S.cityId) p.city_id = S.cityId;
        return { product: 'traffic', params: p };
    }
  }

  function estimate() {
    if (validate()) return null;
    if (S.mode === 'dc' || S.mode === 'renew') return dcPrice();
    var type = S.mode === 'topup' ? S.target.type : S.type;
    var p = gbPrice(type);
    return p == null ? null : Math.ceil(p * gb() - 1e-9);
  }

  /** Local estimate right away, then the exact server price. */
  function updatePrice() {
    S.price = estimate();
    renderSummary();
    var seq = ++S.quoteSeq;
    clearTimeout(quoteTimer);
    if (S.price == null) return;
    quoteTimer = setTimeout(function () {
      var o = buildOrder();
      api('quote', o).then(function (d) {
        if (seq !== S.quoteSeq) return;
        S.price = d.amount_rub;
        renderSummary();
      }, function (e) {
        if (seq !== S.quoteSeq) return;
        if (e.code === 'unavailable' && S.mode === 'traffic') { S.avail = 'no'; renderAvail(); renderSummary(); }
        // otherwise keep the local estimate; the order call reports real problems
      });
    }, 350);
  }

  function canPay() {
    return !!S.catalog && !S.paying && S.price != null && !validate() && !(S.mode === 'traffic' && S.avail === 'no');
  }

  // ------------------------------------------------------------------
  // Rendering
  // ------------------------------------------------------------------
  function setStatus(which, key, kind, args, hint) {
    S[which] = key ? { key: key, kind: kind || '', args: args || [], hint: !!hint } : null;
    renderStatus(which);
  }
  function renderStatus(which) {
    var el = $(which === 'status' ? 'kpStatus' : 'kpListStatus');
    var st = S[which];
    el.className = 'kp-status' + (st && st.kind ? ' ' + st.kind : '');
    el.textContent = st ? t.apply(null, [st.key].concat(st.args)) : '';
  }

  function typeText(p) { return p.kind === 'dc' ? t('kp.kindDc') : t('kp.type.' + p.type); }
  function rotText(rotation, ttlMin) {
    if (rotation === 'interval') return t('kp.rot.interval', ttlMin || 0);
    return t(rotation === 'request' ? 'kp.rot.request' : 'kp.rot.static');
  }
  function proxyTitle(p) {
    var parts = [countryName(p.country)];
    if (p.city) parts.push(p.city); else if (p.state) parts.push(p.state);
    return parts.join(', ');
  }
  function proxySub(p) {
    return p.kind === 'traffic' ? typeText(p) + ' · ' + rotText(p.rotation, p.ttl) : typeText(p) + ' · HTTP, SOCKS5';
  }

  function renderModes() {
    var c = S.catalog || {};
    var pick = S.mode === 'dc' || S.mode === 'traffic';
    show($('kpModeBlock'), pick);
    show($('kpTarget'), !pick);
    show($('kpDc'), S.mode === 'dc');
    show($('kpTraffic'), S.mode === 'traffic');
    show($('kpGbBlock'), S.mode === 'traffic' || S.mode === 'topup');
    document.querySelectorAll('#kpModes label').forEach(function (l) {
      var m = l.getAttribute('data-mode');
      l.classList.toggle('on', m === S.mode);
      l.classList.toggle('off', !c[m]);
    });
    document.querySelectorAll('#kpTypes label').forEach(function (l) { l.classList.toggle('on', l.getAttribute('data-type') === S.type); });
    document.querySelectorAll('#kpRot label').forEach(function (l) { l.classList.toggle('on', l.getAttribute('data-rot') === S.rotation); });
    document.querySelectorAll('#kpMethods label').forEach(function (l) { l.classList.toggle('on', l.getAttribute('data-method') === S.method); });
    show($('kpTtlRow'), S.rotation === 'interval');

    if (c.dc) $('kpDcPriceTag').textContent = rub(c.dc.price_rub) + ' ₽';
    if (c.traffic) {
      var prices = c.traffic.gb_price_rub, min = null;
      Object.keys(prices).forEach(function (k) { var v = Math.ceil(prices[k]); if (min === null || v < min) min = v; });
      $('kpGbPriceTag').innerHTML = t('kp.fromPerGb', rub(min));
      document.querySelectorAll('#kpTypes [data-price]').forEach(function (el) {
        var v = prices[el.getAttribute('data-price')];
        el.innerHTML = v == null ? '' : t('kp.perGb', rub(Math.ceil(v)));
      });
    }
    $('kpGbHint').textContent = t('kp.gbHint', minGb(), maxGb());

    if (S.target) {
      $('kpTargetCaps').textContent = S.mode === 'renew' ? t('kp.capsRenew', dcDays()) : t('kp.capsTopup');
      $('kpTargetTitle').textContent = proxyTitle(S.target);
      $('kpTargetSub').textContent = proxySub(S.target);
    }
  }

  function renderCountries() {
    if (!S.catalog) return;
    if (S.catalog.dc) fillCountrySelect($('kpDcCountry'), S.catalog.dc.countries, S.dcCountry);
    if (S.catalog.traffic) fillCountrySelect($('kpCountry'), S.catalog.traffic.countries, S.country);
  }

  function geoName(list, id) {
    for (var i = 0; i < (list || []).length; i++) if (list[i].id === id) return list[i].name;
    return '';
  }

  function renderSummary() {
    var product = '-', where = '-', type = '-', volume = '-';
    if (S.mode === 'dc') {
      product = t('kp.prodDc');
      where = countryName(dcCountry()) || '-';
      type = t('kp.sumTypeDc');
      volume = t('kp.volDc', dcDays());
    } else if (S.mode === 'traffic') {
      product = t('kp.prodTraffic');
      var parts = [countryName(trafficCountry())];
      if (S.stateId) parts.push(geoName(S.states, S.stateId));
      if (S.stateId && S.cityId) parts.push(geoName(S.cities, S.cityId));
      where = parts.filter(Boolean).join(', ') || '-';
      type = t('kp.type.' + S.type) + ', ' + rotText(S.rotation, ttl());
      volume = gb() > 0 ? t('kp.volGb', gb()) : '-';
    } else if (S.target) {
      product = t(S.mode === 'renew' ? 'kp.prodRenew' : 'kp.prodTopup');
      where = proxyTitle(S.target);
      type = typeText(S.target);
      volume = S.mode === 'renew' ? t('kp.volDc', dcDays()) : (gb() > 0 ? t('kp.volGb', gb()) : '-');
    }
    $('kpSumProduct').textContent = product;
    $('kpSumWhere').textContent = where;
    $('kpSumType').textContent = type;
    $('kpSumVolume').textContent = volume;
    $('kpSumMethod').textContent = S.method === 'cryptobot' ? 'CryptoBot' : (lang() === 'en' ? 'YooKassa' : 'ЮKassa');
    $('kpSumTotal').textContent = S.price != null ? rub(S.price) + ' ₽' : '--';
    $('kpPayText').textContent = S.price != null ? t('kp.payN', rub(S.price)) : t('kp.pay');
    $('kpPay').disabled = !canPay();

    // validation hints share the status line with order errors
    var v = S.catalog ? validate() : null;
    if (v && v[0] !== 'kp.err.pick_country' && !S.paying) setStatus('status', v[0], 'warn', v.slice(1), true);
    else if (S.status && S.status.hint) setStatus('status', null);
    $('kpGb').classList.toggle('bad', !!v && v[0] === 'kp.err.bad_gb');
    $('kpTtl').classList.toggle('bad', !!v && v[0] === 'kp.err.bad_ttl');
  }

  function renderAll() {
    renderModes();
    renderCountries();
    renderGeo();
    renderAvail();
    updatePrice();
    renderStatus('status');
    renderStatus('listStatus');
    renderCabinet();
    renderOrder();
  }

  // ------------------------------------------------------------------
  // Form events
  // ------------------------------------------------------------------
  function setMode(m) {
    if (S.mode === m) return;
    S.mode = m;
    if (m === 'dc' || m === 'traffic') { S.returnMode = m; S.target = null; }
    setStatus('status', null);
    renderModes();
    updatePrice();
    if (m === 'traffic' && S.avail === '') checkAvailability();
  }

  $('kpModes').addEventListener('click', function (e) {
    var l = e.target.closest('label'); if (!l || l.classList.contains('off')) return;
    setMode(l.getAttribute('data-mode'));
  });
  $('kpTypes').addEventListener('click', function (e) {
    var l = e.target.closest('label'); if (!l) return;
    var v = l.getAttribute('data-type'); if (v === S.type) return;
    S.type = v; renderModes(); updatePrice(); checkAvailability();
  });
  $('kpRot').addEventListener('click', function (e) {
    var l = e.target.closest('label'); if (!l) return;
    S.rotation = l.getAttribute('data-rot'); renderModes(); updatePrice();
  });
  $('kpMethods').addEventListener('click', function (e) {
    var l = e.target.closest('label'); if (!l) return;
    S.method = l.getAttribute('data-method'); renderModes(); renderSummary();
  });
  $('kpDcCountry').addEventListener('change', function () { S.dcCountry = this.value; updatePrice(); });
  $('kpCountry').addEventListener('change', function () {
    S.country = this.value; loadStates(); checkAvailability(); updatePrice();
  });
  $('kpState').addEventListener('change', function () {
    S.stateId = parseInt(this.value, 10) || 0; loadCities(); updatePrice();
  });
  $('kpCity').addEventListener('change', function () { S.cityId = parseInt(this.value, 10) || 0; updatePrice(); });
  $('kpGb').addEventListener('input', updatePrice);
  $('kpTtl').addEventListener('input', updatePrice);
  function stepGb(d) {
    var g = gb();
    if (isNaN(g)) g = minGb();
    $('kpGb').value = Math.min(maxGb(), Math.max(minGb(), g + d));
    updatePrice();
  }
  $('kpGbMinus').addEventListener('click', function () { stepGb(-1); });
  $('kpGbPlus').addEventListener('click', function () { stepGb(1); });
  $('kpBack').addEventListener('click', function () { S.target = null; setMode(S.returnMode); });
  $('kpRetry').addEventListener('click', loadCatalog);

  $('kpForm').addEventListener('submit', function (e) {
    e.preventDefault();
    var v = validate();
    if (v) { setStatus('status', v[0], 'warn', v.slice(1)); return; }
    if (!canPay()) return;
    var o = buildOrder();
    o.method = S.method;
    S.paying = true;
    setStatus('status', 'kp.creating', '');
    renderSummary();
    track('proxy_buy_click', { product: o.product, method: S.method, amount: S.price });
    api('order', o).then(function (d) {
      markUsed(cabinet);
      S.price = d.amount_rub;
      setStatus('status', 'kp.redirect', '');
      renderSummary();
      track('proxy_checkout_redirect', { product: o.product, method: S.method, amount: d.amount_rub });
      setTimeout(function () { location.href = d.pay_url; }, 250);
    }, function (err) {
      S.paying = false;
      if (err.code === 'unavailable' && S.mode === 'traffic') { S.avail = 'no'; renderAvail(); }
      var args = err.code === 'bad_gb' ? [minGb(), maxGb()] : err.code === 'bad_ttl' ? [ttlMax()] : [];
      setStatus('status', errKey(err.code), 'err', args);
      renderSummary();
    });
  });

  // ------------------------------------------------------------------
  // Proxy cards
  // ------------------------------------------------------------------
  function fmtGb(v) { return Number(v || 0).toLocaleString(lang() === 'en' ? 'en-US' : 'ru-RU', { maximumFractionDigits: 2 }); }
  function fmtDate(iso) {
    var d = new Date(iso);
    if (isNaN(d)) return '';
    function z(n) { return (n < 10 ? '0' : '') + n; }
    return z(d.getDate()) + '.' + z(d.getMonth() + 1) + '.' + d.getFullYear();
  }
  function lines(p) {
    var u = encodeURIComponent(p.login), w = encodeURIComponent(p.password);
    return [
      ['SOCKS5', 'socks5://' + u + ':' + w + '@' + p.host + ':' + p.port],
      ['HTTP', 'http://' + u + ':' + w + '@' + p.host + ':' + p.port],
      [t('kp.lineString'), p.host + ':' + p.port + ':' + p.login + ':' + p.password]
    ];
  }

  function proxyCard(p, withActions) {
    var st = p.status;
    var badge = st === 'active' ? 'ok' : st === 'provisioning' ? 'wait' : 'bad';
    var h = '<article class="kp-proxy" data-id="' + p.id + '">';
    h += '<div class="kp-proxy-top"><div><b>' + esc(proxyTitle(p)) + '</b><small>' + esc(proxySub(p)) + '</small></div>';
    h += '<span class="kp-badge ' + badge + '">' + esc(t('kp.st.' + st)) + '</span></div>';
    if (p.kind === 'traffic' && p.gb_total) {
      var pct = Math.min(100, (p.gb_used || 0) * 100 / p.gb_total);
      h += '<div class="kp-usage"><div class="kp-bar"><i style="width:' + pct.toFixed(1) + '%"></i></div><span>' +
        esc(t('kp.usage', fmtGb(p.gb_used), p.gb_total)) + '</span></div>';
    } else if (p.expires_at) {
      h += '<div class="kp-usage"><span>' + esc(t(p.renew_pending ? 'kp.untilRenewed' : 'kp.until', fmtDate(p.expires_at))) + '</span></div>';
    }
    if (st === 'provisioning') h += '<p class="kp-note">' + esc(t('kp.noteProvisioning')) + '</p>';
    if (st === 'exhausted') h += '<p class="kp-note warn">' + esc(t('kp.noteExhausted')) + '</p>';
    if (st === 'expired') h += '<p class="kp-note warn">' + esc(t('kp.noteExpired')) + '</p>';
    if (p.host && st !== 'expired') {
      h += '<div class="kp-creds">';
      lines(p).forEach(function (l) {
        h += '<div class="kp-cred"><span class="kp-cred-k">' + esc(l[0]) + '</span><code class="kp-code ym-hide-content">' + esc(l[1]) +
          '</code><button type="button" class="kp-copy-sm" data-copy="' + esc(l[1]) + '">' + esc(t('kp.copy')) + '</button></div>';
      });
      h += '</div>';
    }
    if (withActions && (p.can_topup || p.can_renew || p.can_refresh_ip)) {
      h += '<div class="kp-actions">';
      if (p.can_topup) h += '<button type="button" class="btn btn-small btn-white" data-act="topup">' + esc(t('kp.topup')) + '</button>';
      if (p.can_renew) h += '<button type="button" class="btn btn-small btn-white" data-act="renew">' + esc(t('kp.renew')) + '</button>';
      if (p.can_refresh_ip) h += '<button type="button" class="btn btn-small kp-ghost" data-act="ip">' + esc(t('kp.newIp')) + '</button>';
      h += '</div>';
    }
    return h + '</article>';
  }

  function copyText(text, btn) {
    function done() {
      if (!btn) return;
      if (!btn.hasAttribute('data-label')) btn.setAttribute('data-label', btn.textContent);
      btn.textContent = t('kp.copied');
      btn.classList.add('done');
      clearTimeout(btn._t);
      btn._t = setTimeout(function () { btn.textContent = btn.getAttribute('data-label'); btn.removeAttribute('data-label'); btn.classList.remove('done'); }, 1600);
    }
    function fallback() {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.position = 'fixed'; ta.style.top = '0'; ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); done(); } catch (e) { /* nothing else to try */ }
      document.body.removeChild(ta);
    }
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done, fallback);
    else fallback();
  }

  document.addEventListener('click', function (e) {
    var b = e.target.closest('.kp-copy-sm');
    if (b) copyText(b.getAttribute('data-copy'), b);
  });

  // ------------------------------------------------------------------
  // Cabinet
  // ------------------------------------------------------------------
  function hasOrders() { return usedList().indexOf(cabinet) >= 0; }

  function renderCabinet() {
    var visible = S.proxies.length > 0 || hasOrders() || !!S.order || !storageOk;
    show($('kpCabinet'), visible);
    show($('kpJump'), visible);
    if (!visible) return;
    $('kpCabLink').textContent = cabinetUrl();
    var list = $('kpList');
    if (!S.proxies.length) list.innerHTML = '<p class="kp-empty">' + esc(t('kp.listEmpty')) + '</p>';
    else list.innerHTML = S.proxies.map(function (p) { return proxyCard(p, true); }).join('');
    var prev = usedList().filter(function (x) { return x !== cabinet; })[0];
    show($('kpPrev'), !!prev);
    if (!storageOk && !S.listStatus) setStatus('listStatus', 'kp.noStorage', 'warn');
  }

  var listLoading = false;
  function loadProxies(quiet) {
    if (listLoading) return Promise.resolve();
    listLoading = true;
    $('kpRefresh').disabled = true;
    return api('proxies').then(function (d) {
      S.proxies = d.proxies || [];
      if (S.proxies.length) markUsed(cabinet);
      if (!quiet && S.listStatus && S.listStatus.kind === 'err') setStatus('listStatus', null);
      renderCabinet();
    }, function (e) {
      if (!quiet || hasOrders()) setStatus('listStatus', errKey(e.code), 'err');
      renderCabinet();
    }).then(function () { listLoading = false; $('kpRefresh').disabled = false; });
  }

  $('kpRefresh').addEventListener('click', function () { setStatus('listStatus', null); loadProxies(false); });
  $('kpCabCopy').addEventListener('click', function () { copyText(cabinetUrl(), this); });
  $('kpPrev').addEventListener('click', function (e) {
    e.preventDefault();
    var prev = usedList().filter(function (x) { return x !== cabinet; })[0];
    if (!prev) return;
    lsSet(LS_CAB, prev);
    markUsed(prev);
    location.href = location.pathname;
  });

  $('kpList').addEventListener('click', function (e) {
    var b = e.target.closest('button[data-act]');
    if (!b) return;
    var card = b.closest('.kp-proxy');
    var id = parseInt(card.getAttribute('data-id'), 10);
    var p = S.proxies.filter(function (x) { return x.id === id; })[0];
    if (!p) return;
    var act = b.getAttribute('data-act');
    if (act === 'ip') {
      b.disabled = true;
      api('proxy/refresh-ip', { proxy_id: id }).then(function () {
        setStatus('listStatus', 'kp.ipChanged', 'ok');
      }, function (err) {
        setStatus('listStatus', errKey(err.code), 'err');
      }).then(function () { b.disabled = false; });
      return;
    }
    if (!S.catalog) return;
    S.target = p;
    if (act === 'topup') $('kpGb').value = Math.max(minGb(), 1);
    S.mode = '';
    setMode(act === 'renew' ? 'renew' : 'topup');
    $('kpForm').scrollIntoView({ behavior: 'smooth', block: 'start' });
  });

  // ------------------------------------------------------------------
  // Order after payment (?order=TOKEN)
  // ------------------------------------------------------------------
  var orderToken = q.get('order');
  if (!/^[A-Za-z0-9_-]{8,64}$/.test(orderToken || '')) orderToken = null;
  var pollTimer = null, pollStarted = 0, pollFails = 0;

  function renderOrder() {
    var o = S.order;
    show($('kpOrder'), !!o);
    if (!o) return;
    var title = 'kp.oCheckTitle', text = '', spin = true, check = false, proxy = '';
    switch (o.view) {
      case 'pending': title = 'kp.oPendingTitle'; text = t('kp.oPendingText'); break;
      case 'processing': title = 'kp.oProcTitle'; text = t('kp.oProcText'); break;
      case 'done':
        title = 'kp.oDoneTitle'; spin = false;
        text = t(o.product === 'dc_renew' ? 'kp.oDoneRenew' : o.product === 'topup' ? 'kp.oDoneTopup' : 'kp.oDoneText');
        if (o.proxy && (o.product === 'dc' || o.product === 'traffic')) proxy = proxyCard(o.proxy, false);
        break;
      case 'failed': title = 'kp.oFailTitle'; spin = false; text = t(o.refunded ? 'kp.oFailRefunded' : 'kp.oFailSupport'); break;
      case 'canceled': title = 'kp.oCanceledTitle'; spin = false; text = t('kp.oCanceledText'); break;
      case 'not_found': title = 'kp.oNotFoundTitle'; spin = false; text = t('kp.oNotFoundText'); break;
      case 'timeout': title = 'kp.oTimeoutTitle'; spin = false; check = true; text = t('kp.oTimeoutText'); break;
      case 'network': title = 'kp.oCheckTitle'; spin = false; check = true; text = t('kp.oNetText'); break;
    }
    $('kpOrder').className = 'kp-order' + (o.view === 'done' ? ' done' : (o.view === 'failed' || o.view === 'not_found') ? ' bad' : '');
    $('kpOrderTitle').textContent = t(title);
    $('kpOrderText').textContent = text;
    show($('kpOrderSpin'), spin);
    show($('kpOrderCheck'), check);
    $('kpOrderProxy').innerHTML = proxy;
  }

  function stopPolling() { clearTimeout(pollTimer); pollTimer = null; }

  function pollOrder() {
    stopPolling();
    api('order/status', { token: orderToken }).then(function (d) {
      pollFails = 0;
      markUsed(cabinet);
      S.order = { view: d.state, product: d.product, refunded: d.refunded, proxy: d.proxy, amount: d.amount_rub };
      renderOrder();
      if (d.state === 'done') {
        var flag = 'pb-proxy-purchase-' + orderToken;
        if (!lsGet(flag)) {
          lsSet(flag, '1');
          track('proxy_purchase', { product: d.product, revenue: d.amount_rub });
        }
        loadProxies(true);
        return;
      }
      if (d.state === 'failed' || d.state === 'canceled') { loadProxies(true); return; }
      if (Date.now() - pollStarted > POLL_MAX_MS) { S.order.view = 'timeout'; renderOrder(); return; }
      pollTimer = setTimeout(pollOrder, POLL_MS);
    }, function (e) {
      if (e.code === 'not_found' || e.code === 'bad_request') {
        S.order = { view: 'not_found' }; renderOrder(); renderCabinet(); return;
      }
      if (++pollFails >= 20) { S.order = S.order || {}; S.order.view = 'network'; renderOrder(); return; }
      if (!S.order) { S.order = { view: 'check' }; renderOrder(); }
      pollTimer = setTimeout(pollOrder, POLL_MS);
    });
  }

  function startOrderWatch() {
    S.order = { view: 'check' };
    pollStarted = Date.now();
    pollFails = 0;
    renderOrder();
    renderCabinet();
    pollOrder();
  }

  $('kpOrderCheck').addEventListener('click', function () {
    pollStarted = Date.now(); pollFails = 0;
    S.order.view = 'check'; renderOrder();
    pollOrder();
    loadProxies(true);
  });
  $('kpOrderClose').addEventListener('click', function () {
    stopPolling();
    S.order = null;
    renderOrder();
    try { history.replaceState(null, '', location.pathname); } catch (e) { /* ignore */ }
  });

  // ------------------------------------------------------------------
  // Start
  // ------------------------------------------------------------------
  document.addEventListener('langchange', function () {
    if (!S.catalog) { renderStatus('status'); renderOrder(); return; }
    renderAll();
  });

  renderModes();
  renderSummary();
  renderCabinet();
  loadCatalog();
  loadProxies(true);
  if (orderToken) startOrderWatch();
})();
