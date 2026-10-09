/* ProxyBridge site: navigation, screenshot tabs and the interactive app demo */
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  /* ---------- Navigation ---------- */
  var nav = $('nav');
  var burger = $('navBurger');
  if (burger) {
    burger.addEventListener('click', function () { nav.classList.toggle('open'); });
    nav.querySelectorAll('.nav-links a').forEach(function (a) {
      a.addEventListener('click', function () { nav.classList.remove('open'); });
    });
  }

  /* ---------- Screenshot tabs ---------- */
  var shotTabs = $('shotTabs');
  var shotNames = { home: 'Главная', proxies: 'Прокси', settings: 'Настройки', activation: 'Активация' };
  var shotNamesEn = { home: 'Home', proxies: 'Proxies', settings: 'Settings', activation: 'Activation' };
  var shotNotesRu = {
    home: 'какие программы через какой прокси, подключение одной кнопкой',
    proxies: 'проверка прокси: флаг, город, IP и задержка',
    settings: 'язык, автозапуск, обновления, лицензия',
    activation: 'ключ вводится один раз, до 2 устройств'
  };
  var currentShot = 'home';
  function renderShotNote() {
    var t = (window.PB_I18N && window.PB_I18N.lang() === 'en') ? window.PB_I18N.t('shot.' + currentShot) : shotNotesRu[currentShot];
    var en = window.PB_I18N && window.PB_I18N.lang() === 'en';
    $('shotNote').innerHTML = '<b>' + (en ? shotNamesEn : shotNames)[currentShot] + '</b> · ' + (t || '');
  }
  if (shotTabs) {
    shotTabs.addEventListener('click', function (e) {
      var btn = e.target.closest('button');
      if (!btn) return;
      currentShot = btn.getAttribute('data-shot');
      shotTabs.querySelectorAll('button').forEach(function (b) { b.classList.toggle('active', b === btn); });
      $('shotFrame').querySelectorAll('img').forEach(function (img) {
        img.classList.toggle('active', img.getAttribute('data-shot') === currentShot);
      });
      renderShotNote();
    });
    document.addEventListener('langchange', renderShotNote);
  }

  /* ---------- Demo: scaling ---------- */
  var app = $('app');
  var wrap = $('demoWrap');
  var scaleBox = $('demoScale');
  if (!app) return;

  function fit() {
    var w = wrap.clientWidth;
    var scale = Math.min(1, w / 900);
    scaleBox.style.transform = 'scale(' + scale + ')';
    wrap.style.height = Math.round(600 * scale) + 'px';
  }
  fit();
  window.addEventListener('resize', fit);

  /* ---------- Demo: texts (RU / EN) ---------- */
  var L = {
    ru: {
      off: 'Отключено', busy: 'Подключение...', on: 'Подключено', connect: 'Подключить', disconnect: 'Отключить',
      others: 'Все остальные программы', othersSub: 'всё, что не указано выше', direct: 'Напрямую',
      running: 'Запущенные программы', pickFile: 'Выбрать файл...', proxiesCap: 'Сохранённые прокси',
      via: ' через ', directW: ' напрямую', restDirect: 'остальное напрямую', restVia: 'остальное через ',
      allDirect: 'Все программы идут напрямую, прокси не используется.', wholeVia: 'Весь компьютер через ',
      applied: 'Правила применены сразу', maxRows: 'В демо можно добавить до 4 программ',
      notChecked: 'не проверено', checking: 'проверка...', demoOnly: 'в демо не проверяется', ms: ' мс',
      check: 'Проверить', added: 'Добавлено: ', bad: 'Не распознано строк: ', dup: 'Такие прокси уже есть в списке',
      empty: 'Вставь хотя бы один адрес прокси', imported: 'Импортировано из файла: ', importedNone: 'Прокси из файла уже в списке',
      placeholder: 'socks5://user:pass@host:port\nМожно вставить сразу несколько, по одному в строке',
      logs: 'В программе откроется папка с журналами', updWait: 'проверка обновлений...', updOk: 'У тебя последняя версия 3.4.0',
      unbind: 'В программе это освободит место для другого устройства', rename: 'Переименовать', del: 'Удалить',
      B: 'Б', KB: 'КБ', MB: 'МБ', GB: 'ГБ', dec: ',', ya: 'Яндекс Браузер', power: 'Подключить или отключить'
    },
    en: {
      off: 'Disconnected', busy: 'Connecting...', on: 'Connected', connect: 'Connect', disconnect: 'Disconnect',
      others: 'All other programs', othersSub: 'everything not listed above', direct: 'Direct',
      running: 'Running programs', pickFile: 'Choose file...', proxiesCap: 'Saved proxies',
      via: ' via ', directW: ' direct', restDirect: 'everything else direct', restVia: 'everything else via ',
      allDirect: 'All programs go direct, no proxy in use.', wholeVia: 'The whole computer via ',
      applied: 'Rules applied right away', maxRows: 'Up to 4 programs in the demo',
      notChecked: 'not checked', checking: 'checking...', demoOnly: 'not checked in the demo', ms: ' ms',
      check: 'Check', added: 'Added: ', bad: 'Lines not recognized: ', dup: 'These proxies are already in the list',
      empty: 'Paste at least one proxy address', imported: 'Imported from file: ', importedNone: 'Proxies from the file are already in the list',
      placeholder: 'socks5://user:pass@host:port\nPaste several at once, one per line',
      logs: 'The app opens the folder with log files', updWait: 'checking for updates...', updOk: 'You have the latest version 3.4.0',
      unbind: 'In the app this frees a slot for another device', rename: 'Rename', del: 'Delete',
      B: 'B', KB: 'KB', MB: 'MB', GB: 'GB', dec: '.', ya: 'Yandex Browser', power: 'Connect or disconnect'
    }
  };
  function lang() { return (window.PB_I18N && window.PB_I18N.lang() === 'en') ? 'en' : 'ru'; }
  function T(k) { return L[lang()][k]; }
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function rnd(a, b) { return a + Math.random() * (b - a); }

  /* ---------- Demo: data (documentation IPs only) ---------- */
  var GEO = {
    nl: { ru: ['Нидерланды', 'Амстердам'], en: ['Netherlands', 'Amsterdam'] },
    de: { ru: ['Германия', 'Франкфурт-на-Майне'], en: ['Germany', 'Frankfurt am Main'] },
    pl: { ru: ['Польша', 'Варшава'], en: ['Poland', 'Warsaw'] },
    at: { ru: ['Австрия', 'Вена'], en: ['Austria', 'Vienna'] },
    ee: { ru: ['Эстония', 'Таллин'], en: ['Estonia', 'Tallinn'] }
  };
  var proxies = [
    { id: 'p1', name: 'Amsterdam', type: 'SOCKS5', host: '203.0.113.24', port: '1080', cc: 'nl', base: 42, st: null },
    { id: 'p2', name: 'Frankfurt', type: 'SOCKS5', host: '198.51.100.10', port: '1080', cc: 'de', base: 37, st: null },
    { id: 'p3', name: 'Warsaw', type: 'HTTP', host: '192.0.2.55', port: '8080', cc: 'pl', base: 51, st: null }
  ];
  var fromFile = [
    { id: 'p4', name: 'Vienna', type: 'SOCKS5', host: '198.51.100.77', port: '1080', cc: 'at', base: 33, st: null },
    { id: 'p5', name: 'Tallinn', type: 'HTTP', host: '192.0.2.140', port: '3128', cc: 'ee', base: 58, st: null }
  ];
  var PROGS = [
    { id: 'chrome', name: 'Chrome', ico: 'chrome' },
    { id: 'telegram', name: 'Telegram', ico: 'tg' },
    { id: 'firefox', name: 'Firefox', ico: 'ff' },
    { id: 'python', name: 'Python', ico: 'py' },
    { id: 'yandex', name: 'ya', ico: 'ya' }
  ];
  var FILE_PROG = { id: 'file', name: 'parser.exe', ico: 'exe' };
  var rules = [
    { prog: 'chrome', proxy: 'p1', on: true },
    { prog: 'telegram', proxy: 'p2', on: true }
  ];
  var others = 'direct';
  var nextId = 6;

  function progById(id) {
    if (id === 'file') return FILE_PROG;
    for (var i = 0; i < PROGS.length; i++) if (PROGS[i].id === id) return PROGS[i];
    return PROGS[0];
  }
  function progName(p) { return p.name === 'ya' ? T('ya') : p.name; }
  function proxyById(id) {
    for (var i = 0; i < proxies.length; i++) if (proxies[i].id === id) return proxies[i];
    return null;
  }
  function icoHtml(p) {
    var inner = p.ico === 'ya' ? 'Я' : (p.ico === 'exe' ? 'EXE' : '');
    return '<span class="pb-ico ' + p.ico + '">' + inner + '</span>';
  }
  function flagHtml(px) { return '<span class="flag ' + (px && px.cc ? px.cc : 'none') + '"></span>'; }
  var CHEV = '<svg class="chev" viewBox="0 0 12 12"><path d="M2 4l4 4 4-4"/></svg>';
  var TRASH = '<svg viewBox="0 0 24 24"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6"/></svg>';
  var PEN = '<svg viewBox="0 0 24 24"><path d="M4 20l4-1 11-11-3-3L5 16l-1 4z"/></svg>';

  /* ---------- Demo: tabs ---------- */
  function showPanel(name) {
    closeMenus();
    app.querySelectorAll('.pb-tab').forEach(function (t) { t.classList.toggle('active', t.getAttribute('data-panel') === name); });
    app.querySelectorAll('.pb-panel').forEach(function (p) { p.classList.toggle('active', p.getAttribute('data-panel') === name); });
  }
  app.querySelectorAll('.pb-tab').forEach(function (tab) {
    tab.addEventListener('click', function () { showPanel(tab.getAttribute('data-panel')); });
  });

  /* ---------- Demo: toast ---------- */
  var toastEl = $('pbToast');
  var toastT = 0;
  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    clearTimeout(toastT);
    toastT = setTimeout(function () { toastEl.classList.remove('show'); }, 1800);
  }

  /* ---------- Demo: dropdowns ---------- */
  function closeMenus() { app.querySelectorAll('.pb-dd.open').forEach(function (d) { d.classList.remove('open'); }); }
  document.addEventListener('click', function (e) { if (!e.target.closest('.pb-dd')) closeMenus(); });

  function makeDD(btnHtml, menuHtml, onPick) {
    var dd = document.createElement('div');
    dd.className = 'pb-dd';
    dd.innerHTML = '<button type="button" class="pb-ddbtn">' + btnHtml + CHEV + '</button><div class="pb-menu">' + menuHtml + '</div>';
    dd.querySelector('.pb-ddbtn').addEventListener('click', function () {
      var open = dd.classList.contains('open');
      closeMenus();
      if (open) return;
      var a = app.getBoundingClientRect(), b = dd.getBoundingClientRect();
      var k = a.height / 600 || 1;
      dd.classList.toggle('up', (a.bottom - b.bottom) / k < 200);
      dd.classList.add('open');
    });
    dd.querySelector('.pb-menu').addEventListener('click', function (e) {
      var it = e.target.closest('button[data-v]');
      if (!it) return;
      closeMenus();
      onPick(it.getAttribute('data-v'));
    });
    return dd;
  }
  function proxyMenu(cur) {
    var h = '<div class="cap">' + esc(T('proxiesCap')) + '</div>';
    proxies.forEach(function (px) {
      h += '<button type="button" data-v="' + px.id + '"' + (cur === px.id ? ' class="sel"' : '') + '>' + flagHtml(px) + esc(px.name) + '<span class="ip">' + esc(px.host + ':' + px.port) + '</span></button>';
    });
    h += '<div class="sep"></div><button type="button" data-v="direct"' + (cur === 'direct' ? ' class="sel"' : '') + '><span class="pb-ico direct"></span>' + esc(T('direct')) + '</button>';
    return h;
  }
  function proxyBtn(id) {
    var px = proxyById(id);
    if (!px) return '<span class="pb-ico direct"></span><span class="nm">' + esc(T('direct')) + '</span>';
    return flagHtml(px) + '<span class="nm">' + esc(px.name) + '</span><span class="ip">' + esc(px.host + ':' + px.port) + '</span>';
  }

  /* ---------- Demo: rules table ---------- */
  var rowsEl = $('pbRows');
  var othersEl = $('pbOthers');

  function changed() {
    renderRoute();
    if (connected) { toast(T('applied')); updateExit(); }
  }

  function renderRules() {
    rowsEl.innerHTML = '';
    rules.forEach(function (r, i) {
      var row = document.createElement('div');
      row.className = 'pb-row' + (r.on ? '' : ' off');
      var p = progById(r.prog);
      var used = rules.map(function (x) { return x.prog; });
      var pm = '<div class="cap">' + esc(T('running')) + '</div>';
      PROGS.forEach(function (q) {
        if (q.id !== r.prog && used.indexOf(q.id) >= 0) return;
        pm += '<button type="button" data-v="' + q.id + '"' + (q.id === r.prog ? ' class="sel"' : '') + '>' + icoHtml(q) + esc(progName(q)) + '</button>';
      });
      if (r.prog === 'file' || used.indexOf('file') < 0) pm += '<div class="sep"></div><button type="button" data-v="file">' + icoHtml(FILE_PROG) + esc(T('pickFile')) + '</button>';
      row.appendChild(makeDD(icoHtml(p) + '<span class="nm">' + esc(progName(p)) + '</span>', pm, function (v) { r.prog = v; renderRules(); changed(); }));
      var ar = document.createElement('span'); ar.className = 'pb-arrow'; ar.textContent = '→';
      row.appendChild(ar);
      row.appendChild(makeDD(proxyBtn(r.proxy), proxyMenu(r.proxy), function (v) { r.proxy = v; renderRules(); changed(); }));
      var sw = document.createElement('button');
      sw.type = 'button'; sw.className = 'pb-switch' + (r.on ? ' on' : ''); sw.setAttribute('aria-label', 'on/off');
      sw.addEventListener('click', function () { r.on = !r.on; renderRules(); changed(); });
      row.appendChild(sw);
      var del = document.createElement('button');
      del.type = 'button'; del.className = 'pb-del'; del.innerHTML = TRASH; del.setAttribute('aria-label', T('del'));
      del.addEventListener('click', function () { rules.splice(i, 1); renderRules(); changed(); });
      row.appendChild(del);
      rowsEl.appendChild(row);
    });
    othersEl.innerHTML = '<div class="pb-oth"><span class="ico"><svg viewBox="0 0 16 16"><rect x="2" y="2" width="5" height="5"/><rect x="9" y="2" width="5" height="5"/><rect x="2" y="9" width="5" height="5"/><rect x="9" y="9" width="5" height="5"/></svg></span><div><b>' + esc(T('others')) + '</b><small>' + esc(T('othersSub')) + '</small></div></div><span class="pb-arrow">→</span>';
    var odd = makeDD(proxyBtn(others), proxyMenu(others), function (v) { others = v; renderRules(); changed(); });
    odd.style.gridColumn = '3 / 4';
    othersEl.appendChild(odd);
  }

  $('pbAddRow').addEventListener('click', function () {
    if (rules.length >= 4) { toast(T('maxRows')); return; }
    var used = rules.map(function (x) { return x.prog; });
    var free = PROGS.filter(function (q) { return used.indexOf(q.id) < 0; });
    var prog = free.length ? free[0].id : 'file';
    var px = proxies[Math.min(rules.length, proxies.length - 1)];
    rules.push({ prog: prog, proxy: px ? px.id : 'direct', on: true });
    renderRules();
    changed();
  });

  /* ---------- Demo: how traffic goes ---------- */
  var routeEl = $('pbRoute');
  var exitEl = $('pbExit');
  function activeRules() { return rules.filter(function (r) { return r.on; }); }
  function renderRoute() {
    var act = activeRules();
    var oth = proxyById(others);
    var t;
    if (!act.length) {
      t = oth ? T('wholeVia') + oth.name + '.' : T('allDirect');
    } else {
      var parts = act.map(function (r) {
        var px = proxyById(r.proxy);
        return progName(progById(r.prog)) + (px ? T('via') + px.name : T('directW'));
      });
      parts.push(oth ? T('restVia') + oth.name : T('restDirect'));
      t = parts.join(', ') + '.';
    }
    routeEl.textContent = t;
  }
  function firstUsedProxy() {
    var act = activeRules();
    for (var i = 0; i < act.length; i++) { var px = proxyById(act[i].proxy); if (px) return px; }
    return proxyById(others);
  }
  function updateExit() {
    var px = connected ? firstUsedProxy() : null;
    if (!px) { exitEl.className = 'pb-exit'; exitEl.innerHTML = ''; return; }
    var g = GEO[px.cc] ? GEO[px.cc][lang()] : null;
    exitEl.innerHTML = flagHtml(px) + '<span>' + esc(g ? g[0] + ', ' + g[1] : px.name) + ' · ' + esc(px.host) + '</span>';
    exitEl.className = 'pb-exit show';
  }

  /* ---------- Demo: connect ---------- */
  var powerBtn = $('connectBtn');
  var connBtn = $('pbConnBtn');
  var stateEl = $('pbState');
  var connected = false, busy = false, timers = [], rx = 0, tx = 0;

  function fmtBytes(n) {
    var d = T('dec');
    if (n < 1024) return Math.round(n) + ' ' + T('B');
    if (n < 1024 * 1024) return Math.round(n / 1024) + ' ' + T('KB');
    if (n < 1024 * 1024 * 1024) { var m = n / 1048576; return (m < 10 ? m.toFixed(2) : m < 100 ? m.toFixed(1) : Math.round(m) + '').replace('.', d) + ' ' + T('MB'); }
    return (n / 1073741824).toFixed(2).replace('.', d) + ' ' + T('GB');
  }
  function renderConn() {
    powerBtn.classList.toggle('on', connected);
    powerBtn.classList.toggle('busy', busy);
    powerBtn.setAttribute('aria-label', T('power'));
    stateEl.className = 'pb-state' + (connected ? ' on' : busy ? ' busy' : '');
    stateEl.textContent = connected ? T('on') : busy ? T('busy') : T('off');
    connBtn.textContent = connected ? T('disconnect') : T('connect');
    connBtn.classList.toggle('primary', !connected);
    if (!connected) {
      $('stPing').textContent = '-';
      $('stDn').textContent = fmtBytes(0);
      $('stUp').textContent = fmtBytes(0);
    } else {
      $('stDn').textContent = fmtBytes(rx);
      $('stUp').textContent = fmtBytes(tx);
    }
    updateExit();
  }
  function tickPing() {
    var px = firstUsedProxy();
    $('stPing').textContent = px ? Math.round(px.base + rnd(-5, 7)) + T('ms') : '-';
  }
  function connect() {
    if (busy || connected) return;
    window.pbTrack && window.pbTrack.goal('demo_connect');
    busy = true;
    renderConn();
    setTimeout(function () {
      busy = false;
      connected = true;
      rx = 0; tx = 0;
      renderConn();
      tickPing();
      timers.push(setInterval(tickPing, 1600));
      timers.push(setInterval(function () {
        if (!firstUsedProxy()) return;
        var d = rnd(90, 760) * 1024;
        rx += d;
        tx += d * rnd(0.05, 0.12);
        $('stDn').textContent = fmtBytes(rx);
        $('stUp').textContent = fmtBytes(tx);
      }, 700));
    }, 900);
  }
  function disconnect() {
    timers.forEach(clearInterval);
    timers = [];
    connected = false;
    renderConn();
  }
  function toggleConn() { if (connected) disconnect(); else connect(); }
  powerBtn.addEventListener('click', toggleConn);
  connBtn.addEventListener('click', toggleConn);

  /* ---------- Demo: proxies tab ---------- */
  var pxRows = $('pbPxRows');
  var input = $('proxyInput');
  var addMsg = $('pbAddMsg');

  function statusHtml(px) {
    if (px.st === 'wait') return '<div class="pb-st wait">' + esc(T('checking')) + '</div>';
    if (px.st === 'demo') return '<div class="pb-st muted">' + esc(T('demoOnly')) + '</div>';
    if (px.st && px.st.ms) {
      var g = GEO[px.cc][lang()];
      return '<div class="pb-st"><span class="ms">' + px.st.ms + esc(T('ms')) + '</span>' + esc(g[0] + ', ' + g[1]) + '<br><span class="ip">IP ' + esc(px.host) + '</span></div>';
    }
    return '<div class="pb-st muted">' + esc(T('notChecked')) + '</div>';
  }
  function renderProxies() {
    $('pbCount').textContent = proxies.length;
    pxRows.innerHTML = '';
    proxies.forEach(function (px) {
      var row = document.createElement('div');
      row.className = 'pb-pxrow';
      row.innerHTML = flagHtml(px) +
        '<div class="pb-pxname"><div class="n"><span class="nmv">' + esc(px.name) + '</span><button type="button" class="pb-pen" aria-label="' + esc(T('rename')) + '">' + PEN + '</button></div>' +
        '<div class="a"><span class="t">' + px.type + '</span>' + esc(px.host + ':' + px.port) + ' <span class="u">demo:••••</span></div></div>' +
        statusHtml(px) +
        '<button type="button" class="pb-btn sm chk">' + esc(T('check')) + '</button>' +
        '<button type="button" class="pb-del" aria-label="' + esc(T('del')) + '">' + TRASH + '</button>';
      row.querySelector('.chk').addEventListener('click', function () { checkProxy(px); });
      row.querySelector('.pb-del').addEventListener('click', function () { removeProxy(px.id); });
      row.querySelector('.pb-pen').addEventListener('click', function () { startRename(row, px); });
      pxRows.appendChild(row);
    });
  }
  function startRename(row, px) {
    var n = row.querySelector('.n');
    n.innerHTML = '<input type="text" maxlength="24" value="' + esc(px.name) + '">';
    var inp = n.querySelector('input');
    inp.focus(); inp.select();
    var done = false;
    function finish(save) {
      if (done) return; done = true;
      var v = inp.value.trim();
      if (save && v) px.name = v;
      renderProxies(); renderRules(); renderRoute(); updateExit();
    }
    inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') finish(true); if (e.key === 'Escape') finish(false); });
    inp.addEventListener('blur', function () { finish(true); });
  }
  function checkProxy(px, silent) {
    if (!silent) window.pbTrack && window.pbTrack.goal('demo_verify');
    if (px.st === 'wait') return;
    px.st = 'wait';
    renderProxies();
    setTimeout(function () {
      px.st = GEO[px.cc] ? { ms: Math.round(px.base + rnd(-6, 8)) } : 'demo';
      renderProxies();
    }, rnd(600, 1300));
  }
  function removeProxy(id) {
    proxies = proxies.filter(function (p) { return p.id !== id; });
    rules.forEach(function (r) { if (r.proxy === id) r.proxy = 'direct'; });
    if (others === id) others = 'direct';
    renderProxies(); renderRules(); changed();
  }
  $('pbCheckAll').addEventListener('click', function () {
    window.pbTrack && window.pbTrack.goal('demo_verify');
    proxies.forEach(function (px, i) { setTimeout(function () { checkProxy(px, true); }, i * 180); });
  });

  /* Accepts: scheme://user:pass@host:port, user:pass@host:port, host:port:user:pass, host:port */
  function parseProxy(raw) {
    var s = (raw || '').trim();
    if (!s) return null;
    var type = 'HTTP';
    var m = s.match(/^(socks5h?|socks4|https?):\/\/(.*)$/i);
    if (m) { type = m[1].toLowerCase().indexOf('socks') === 0 ? 'SOCKS5' : 'HTTP'; s = m[2]; }
    var user = '', host = '', port = '';
    var at = s.lastIndexOf('@');
    if (at >= 0) { user = s.slice(0, at); s = s.slice(at + 1); }
    var parts = s.split(':');
    if (parts.length === 4 && !user) { host = parts[0]; port = parts[1]; }
    else if (parts.length === 2) { host = parts[0]; port = parts[1]; }
    else return null;
    if (!/^[a-z0-9.\-]+$/i.test(host) || !/^\d{1,5}$/.test(port)) return null;
    return { type: type, host: host, port: port };
  }
  function exists(host, port) { return proxies.some(function (p) { return p.host === host && p.port === port; }); }
  $('pbAddPx').addEventListener('click', function () {
    var lines = input.value.split(/\r?\n/).map(function (x) { return x.trim(); }).filter(Boolean);
    if (!lines.length) { addMsg.textContent = T('empty'); return; }
    var ok = 0, bad = 0, dup = 0;
    lines.forEach(function (ln) {
      var p = parseProxy(ln);
      if (!p) { bad++; return; }
      if (exists(p.host, p.port)) { dup++; return; }
      proxies.push({ id: 'p' + (nextId++), name: p.host, type: p.type, host: p.host, port: p.port, cc: '', base: 0, st: null });
      ok++;
    });
    var msg = [];
    if (ok) msg.push(T('added') + ok);
    if (bad) msg.push(T('bad') + bad);
    if (!ok && !bad && dup) msg.push(T('dup'));
    addMsg.textContent = msg.join(' · ');
    if (ok) { input.value = ''; renderProxies(); renderRules(); }
  });
  $('pbImport').addEventListener('click', function () {
    var n = 0;
    fromFile.forEach(function (p) { if (!exists(p.host, p.port)) { proxies.push(p); n++; } });
    addMsg.textContent = n ? T('imported') + n : T('importedNone');
    renderProxies(); renderRules();
  });

  /* ---------- Demo: settings ---------- */
  app.querySelectorAll('.pb-sw').forEach(function (s) {
    s.addEventListener('click', function () { s.classList.toggle('on'); });
  });
  var seg = $('pbLang');
  function renderSeg() { seg.querySelectorAll('button').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-lang') === lang()); }); }
  seg.addEventListener('click', function (e) {
    var b = e.target.closest('button');
    if (b && window.PB_I18N) window.PB_I18N.set(b.getAttribute('data-lang'));
  });
  $('pbLogs').addEventListener('click', function () { toast(T('logs')); });
  $('pbUnbind').addEventListener('click', function () { toast(T('unbind')); });
  $('pbUpd').addEventListener('click', function () {
    var m = $('pbUpdMsg');
    m.textContent = T('updWait');
    setTimeout(function () { m.textContent = T('updOk'); }, 900);
  });

  /* ---------- "How to start" button: check the sample proxy in the demo ---------- */
  var startCheck = $('startCheck');
  if (startCheck) {
    startCheck.addEventListener('click', function () {
      var p = parseProxy(startCheck.previousElementSibling.textContent);
      var px = null;
      proxies.forEach(function (x) { if (p && x.host === p.host && x.port === p.port) px = x; });
      if (!px && p) {
        px = { id: 'p' + (nextId++), name: p.host, type: p.type, host: p.host, port: p.port, cc: '', base: 0, st: null };
        proxies.push(px);
        renderRules();
      }
      showPanel('proxies');
      wrap.scrollIntoView({ behavior: 'smooth', block: 'center' });
      if (px) checkProxy(px);
    });
  }

  /* ---------- Render ---------- */
  function renderAll() {
    input.setAttribute('placeholder', T('placeholder'));
    renderRules(); renderRoute(); renderConn(); renderProxies(); renderSeg();
  }
  document.addEventListener('langchange', renderAll);
  renderAll();
})();
