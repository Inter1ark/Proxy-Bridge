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
  var shotNames = { dashboard: 'Dashboard', proxylist: 'Proxy List', splittunnel: 'Split Tunnel', settings: 'Settings', help: 'Help', activation: 'Активация' };
  var shotNotesRu = {
    dashboard: 'вставь прокси, проверь, подключись',
    proxylist: 'список из файла, подключение одной кнопкой',
    splittunnel: 'свой прокси для каждой программы',
    settings: 'трей, автозапуск, автоподключение, DNS',
    help: 'короткая инструкция и контакты',
    activation: 'ключ вводится один раз, до 2 устройств'
  };
  var currentShot = 'dashboard';
  function renderShotNote() {
    var t = (window.PB_I18N && window.PB_I18N.lang() === 'en') ? window.PB_I18N.t('shot.' + currentShot) : shotNotesRu[currentShot];
    $('shotNote').innerHTML = '<b>' + shotNames[currentShot] + '</b> · ' + (t || '');
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

  /* ---------- Demo: tabs ---------- */
  app.querySelectorAll('.app-tab').forEach(function (tab) {
    tab.addEventListener('click', function () { showPanel(tab.getAttribute('data-panel')); });
  });
  function showPanel(name) {
    app.querySelectorAll('.app-tab').forEach(function (t) { t.classList.toggle('active', t.getAttribute('data-panel') === name); });
    app.querySelectorAll('.panel').forEach(function (p) { p.classList.toggle('active', p.getAttribute('data-panel') === name); });
  }

  /* ---------- Demo: state ---------- */
  var input = $('proxyInput');
  var status = $('status');
  var connectBtn = $('connectBtn');
  var geo = $('geo');
  var histEl = $('hist');
  var plistEl = $('plist');
  var assignEl = $('assign');

  var history = [
    'socks5://user:pass@185.199.110.15:1080',
    'http://user:pass@91.108.4.22:8080'
  ];
  var proxyList = [
    'socks5://user:pass@185.199.110.15:1080',
    'http://user:pass@91.108.4.22:8080',
    'socks5://user:pass@45.142.212.7:1080',
    'http://user:pass@194.26.29.80:3128',
    'socks5://user:pass@77.83.246.19:1080'
  ];
  var mappings = [
    { proc: 'chrome.exe', proxy: 'socks5://user:pass@185.199.110.15:1080' },
    { proc: 'telegram.exe', proxy: 'http://user:pass@91.108.4.22:8080' },
    { proc: 'discord.exe', proxy: 'socks5://user:pass@45.142.212.7:1080' }
  ];

  var connected = false;
  var busy = false;
  var timers = [];
  var startedAt = 0;

  function setStatus(text, kind) {
    status.textContent = text;
    status.className = 'status' + (kind ? ' ' + kind : '');
  }

  /* Accepts: scheme://user:pass@host:port, scheme://host:port, host:port:user:pass, user:pass@host:port, host:port */
  function parseProxy(raw) {
    var s = (raw || '').trim();
    if (!s) return null;
    var type = 'http';
    var m = s.match(/^(socks5h?|socks4|https?):\/\/(.*)$/i);
    if (m) { type = m[1].toLowerCase().indexOf('socks') === 0 ? 'socks5' : 'http'; s = m[2]; }
    var user = '', pass = '', host = '', port = '';
    var at = s.lastIndexOf('@');
    if (at >= 0) {
      var cred = s.slice(0, at).split(':');
      user = cred[0] || ''; pass = cred.slice(1).join(':');
      s = s.slice(at + 1);
    }
    var parts = s.split(':');
    if (parts.length === 4 && !user) { host = parts[0]; port = parts[1]; user = parts[2]; pass = parts[3]; }
    else if (parts.length === 2) { host = parts[0]; port = parts[1]; }
    else return null;
    if (!/^[a-z0-9.\-]+$/i.test(host) || !/^\d{1,5}$/.test(port)) return null;
    return { type: type, host: host, port: port, user: user, pass: pass };
  }

  function maskProxy(p) {
    var x = parseProxy(p);
    if (!x) return p;
    return x.type + '://' + x.host + ':' + x.port;
  }

  /* ---------- History ---------- */
  function renderHistory() {
    histEl.innerHTML = '';
    if (!history.length) {
      var e = document.createElement('div');
      e.className = 'empty';
      e.textContent = 'Пока пусто. Подключись к прокси, и он появится здесь.';
      histEl.appendChild(e);
      return;
    }
    history.forEach(function (h) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = h;
      b.addEventListener('click', function () {
        input.value = h;
        geo.className = 'geo';
        setStatus('Proxy parsed: ' + maskProxy(h));
      });
      histEl.appendChild(b);
    });
  }
  function pushHistory(p) {
    history = history.filter(function (h) { return h !== p; });
    history.unshift(p);
    if (history.length > 5) history.length = 5;
    renderHistory();
  }
  $('clearHist').addEventListener('click', function () { history = []; renderHistory(); });

  /* ---------- Proxy list ---------- */
  function renderList() {
    plistEl.innerHTML = '';
    if (!proxyList.length) {
      var e = document.createElement('div');
      e.style.cssText = 'color:#6c7086;font-size:12px;padding:8px 4px';
      e.textContent = 'Список пуст. Нажми Load from File.';
      plistEl.appendChild(e);
      return;
    }
    proxyList.forEach(function (p) {
      var row = document.createElement('div');
      row.className = 'row';
      var txt = document.createElement('span');
      txt.textContent = p;
      var btn = document.createElement('button');
      btn.className = 'btn-green';
      btn.type = 'button';
      btn.textContent = 'Connect';
      btn.addEventListener('click', function () {
        input.value = p;
        showPanel('dash');
        if (connected) disconnect();
        connect();
      });
      row.appendChild(txt);
      row.appendChild(btn);
      plistEl.appendChild(row);
    });
  }
  $('loadList').addEventListener('click', function () {
    var extra = ['http://user:pass@62.76.9.140:8080', 'socks5://user:pass@146.70.88.3:1080', 'http://user:pass@5.188.62.14:3128'];
    extra.forEach(function (p) { if (proxyList.indexOf(p) < 0) proxyList.push(p); });
    renderList();
    setStatus('Loaded ' + proxyList.length + ' proxies', 'ok');
  });
  $('clearList').addEventListener('click', function () { proxyList = []; renderList(); });

  /* ---------- Split tunnel ---------- */
  function renderAssign() {
    assignEl.innerHTML = '';
    if (!mappings.length) {
      var e = document.createElement('div');
      e.style.cssText = 'color:#6c7086;font-size:12px;padding:8px 4px';
      e.textContent = 'Назначений пока нет.';
      assignEl.appendChild(e);
      return;
    }
    mappings.forEach(function (m, i) {
      var row = document.createElement('div');
      row.className = 'row';
      var left = document.createElement('div');
      var b = document.createElement('b'); b.textContent = m.proc;
      var s = document.createElement('span'); s.textContent = maskProxy(m.proxy);
      left.appendChild(b); left.appendChild(s);
      var rm = document.createElement('button');
      rm.className = 'rm'; rm.type = 'button'; rm.textContent = 'Remove';
      rm.addEventListener('click', function () {
        mappings.splice(i, 1);
        renderAssign();
        $('splitStatus').textContent = 'Removed: ' + m.proc;
      });
      row.appendChild(left); row.appendChild(rm);
      assignEl.appendChild(row);
    });
  }
  $('mapAdd').addEventListener('click', function () {
    var proc = $('mapProc').value.trim();
    var proxy = $('mapProxy').value.trim();
    var st = $('splitStatus');
    if (!proc) { st.textContent = 'Укажи имя программы, например chrome.exe'; return; }
    if (!parseProxy(proxy)) { st.textContent = 'Неверный формат прокси'; return; }
    mappings = mappings.filter(function (m) { return m.proc.toLowerCase() !== proc.toLowerCase(); });
    mappings.push({ proc: proc, proxy: proxy });
    $('mapProc').value = ''; $('mapProxy').value = '';
    renderAssign();
    st.textContent = 'Added: ' + proc;
  });
  $('splitApply').addEventListener('click', function () {
    var st = $('splitStatus');
    var btn = $('splitApply');
    if (btn.dataset.on === '1') {
      btn.dataset.on = '0';
      btn.textContent = 'APPLY SPLIT TUNNELING';
      st.textContent = 'Split tunneling stopped.';
      return;
    }
    if (!mappings.length) { st.textContent = 'Сначала добавь хотя бы одну программу.'; return; }
    btn.dataset.on = '1';
    btn.textContent = 'STOP SPLIT TUNNELING';
    st.textContent = 'Active: ' + mappings.length + ' rules. Other traffic goes direct.';
  });

  /* ---------- Settings ---------- */
  app.querySelectorAll('.setting').forEach(function (s) {
    s.addEventListener('click', function () { s.classList.toggle('on'); });
  });

  /* ---------- Verify ---------- */
  var flags = { RU: 'Россия', NL: 'Нидерланды', DE: 'Германия', US: 'США', GB: 'Великобритания', FR: 'Франция', FI: 'Финляндия', SE: 'Швеция', PL: 'Польша', UA: 'Украина', KZ: 'Казахстан', TR: 'Турция', ES: 'Испания', IT: 'Италия', CA: 'Канада', JP: 'Япония', SG: 'Сингапур', HK: 'Гонконг', CZ: 'Чехия', LV: 'Латвия', LT: 'Литва', EE: 'Эстония', AT: 'Австрия', CH: 'Швейцария', BG: 'Болгария', RO: 'Румыния', MD: 'Молдова', GE: 'Грузия', AM: 'Армения', BY: 'Беларусь', IN: 'Индия', BR: 'Бразилия', AU: 'Австралия', IE: 'Ирландия', NO: 'Норвегия', DK: 'Дания' };

  function showGeo(cc, country, city, ip, ok) {
    geo.innerHTML = '';
    if (cc) {
      var img = document.createElement('img');
      img.src = 'https://flagcdn.com/24x18/' + cc.toLowerCase() + '.png';
      img.alt = cc;
      geo.appendChild(img);
    }
    var t = document.createElement('span');
    t.textContent = (country || 'Неизвестно') + (city ? ', ' + city : '') + ' · IP: ' + ip + (ok ? ' · Valid' : '');
    geo.appendChild(t);
    geo.className = 'geo show';
  }

  function lookup(host) {
    var ctrl = typeof AbortController !== 'undefined' ? new AbortController() : null;
    var t = setTimeout(function () { if (ctrl) ctrl.abort(); }, 6000);
    return fetch('https://ipapi.co/' + encodeURIComponent(host) + '/json/', { signal: ctrl ? ctrl.signal : undefined })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        clearTimeout(t);
        if (!d || d.error) throw new Error('no data');
        return { cc: d.country_code || '', country: flags[d.country_code] || d.country_name || '', city: d.city || '', ip: d.ip || host };
      });
  }

  $('verifyBtn').addEventListener('click', function () {
    window.pbTrack && window.pbTrack.goal('demo_verify');
    var p = parseProxy(input.value);
    if (!input.value.trim()) { setStatus('Please enter proxy details', 'warn'); return; }
    if (!p) { setStatus('Invalid proxy format. Use: ip:port:user:pass or socks5://user:pass@ip:port', 'warn'); return; }
    setStatus('Testing proxy...');
    geo.className = 'geo';
    lookup(p.host).then(function (g) {
      showGeo(g.cc, g.country, g.city, g.ip, true);
      setStatus('Proxy is valid and working!', 'ok');
    }).catch(function () {
      showGeo('', 'GEO недоступен', '', p.host, false);
      setStatus('Proxy parsed: ' + p.type + ' ' + p.host + ':' + p.port, 'ok');
    });
  });

  input.addEventListener('input', function () {
    if (connected) return;
    var p = parseProxy(input.value);
    if (!input.value.trim()) setStatus('ProxyBridge Stopped. Add proxy to start.');
    else if (p) setStatus('Proxy parsed: ' + p.host + ':' + p.port);
    else setStatus('Invalid proxy format. Use: ip:port:user:pass or socks5://user:pass@ip:port', 'warn');
  });
  input.addEventListener('keydown', function (e) { if (e.key === 'Enter') $('verifyBtn').click(); });

  /* ---------- Connect / disconnect ---------- */
  function fmtTime(ms) {
    var s = Math.floor(ms / 1000);
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), x = s % 60;
    return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m + ':' + (x < 10 ? '0' : '') + x;
  }
  function rnd(a, b) { return a + Math.random() * (b - a); }

  function connect() {
    if (busy) return;
    var raw = input.value.trim();
    var p = parseProxy(raw);
    if (!raw) { setStatus('Please enter proxy details', 'warn'); return; }
    if (!p) { setStatus('Invalid proxy format', 'warn'); return; }
    busy = true;
    connectBtn.classList.add('busy');
    connectBtn.textContent = '...';
    setStatus('Connecting to ' + p.type + ' ' + p.host + ':' + p.port);
    setTimeout(function () {
      busy = false;
      connected = true;
      startedAt = Date.now();
      connectBtn.classList.remove('busy');
      connectBtn.classList.add('on');
      connectBtn.textContent = 'DISCONNECT';
      setStatus('Connected successfully!', 'ok');
      pushHistory(raw);
      $('stPing').textContent = Math.round(rnd(38, 90)) + ' ms';
      timers.push(setInterval(function () {
        $('stTime').textContent = fmtTime(Date.now() - startedAt);
      }, 1000));
      timers.push(setInterval(function () {
        $('stUp').textContent = rnd(0.1, 1.4).toFixed(2);
        $('stDn').textContent = rnd(0.8, 6.2).toFixed(2);
      }, 900));
      timers.push(setInterval(function () {
        $('stPing').textContent = Math.round(rnd(38, 90)) + ' ms';
      }, 3000));
    }, 900);
  }

  function disconnect() {
    timers.forEach(clearInterval);
    timers = [];
    connected = false;
    connectBtn.classList.remove('on', 'busy');
    connectBtn.textContent = 'CONNECT';
    $('stUp').textContent = '0';
    $('stDn').textContent = '0';
    $('stTime').textContent = '00:00:00';
    $('stPing').textContent = '-- ms';
    setStatus('ProxyBridge Stopped. Add proxy to start.');
  }

  connectBtn.addEventListener('click', function () {
    if (!connected) window.pbTrack && window.pbTrack.goal('demo_connect');
    if (connected) disconnect(); else connect();
  });

  renderHistory();
  renderList();
  renderAssign();
})();
