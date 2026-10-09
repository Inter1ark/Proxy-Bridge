/* Yandex Metrika for proxybridge.org.
   The counter loads only after the visitor accepts analytics cookies (152-FZ).
   Consent is stored in localStorage 'pb-cookie-consent': 'all' | 'necessary'.
   Goals (create them in Metrika as "JavaScript event" goals with the same identifiers):
     download_win, download_mac, buy_click, pay_method_click, checkout_submit, checkout_redirect,
     purchase, copy_key, partner_click, github_click, telegram_click, email_click, forum_click,
     demo_connect, demo_verify, lang_en, seo_cta
   E-commerce: detail / add / purchase are pushed to window.dataLayer. */
(function () {
  'use strict';
  var ID = 113586753, KEY = 'pb-cookie-consent';
  window.dataLayer = window.dataLayer || [];

  function get() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
  function set(v) { try { localStorage.setItem(KEY, v); } catch (e) { /* storage blocked */ } }

  function protectForms() {
    // Webvisor must never record what people type (email, license keys, proxy credentials)
    var els = document.querySelectorAll('input, textarea, .key, #key');
    for (var i = 0; i < els.length; i++) els[i].classList.add('ym-disable-keys', 'ym-hide-content');
  }

  var loaded = false;
  function load() {
    if (loaded) return;
    loaded = true;
    protectForms();
    (function (m, e, t, r, i, k, a) {
      m[i] = m[i] || function () { (m[i].a = m[i].a || []).push(arguments); };
      m[i].l = 1 * new Date();
      for (var j = 0; j < document.scripts.length; j++) { if (document.scripts[j].src === r) { return; } }
      k = e.createElement(t), a = e.getElementsByTagName(t)[0], k.async = 1, k.src = r, a.parentNode.insertBefore(k, a);
    })(window, document, 'script', 'https://mc.yandex.ru/metrika/tag.js?id=' + ID, 'ym');
    window.ym(ID, 'init', {
      ssr: true, webvisor: true, clickmap: true, ecommerce: 'dataLayer',
      referrer: document.referrer, url: location.href, accurateTrackBounce: true, trackLinks: true
    });
  }

  function goal(name, params) {
    if (window.ym && loaded) window.ym(ID, 'reachGoal', name, params || {});
  }

  var PLANS = {
    month: { name: 'ProxyBridge 1 месяц', price: 99 },
    '3months': { name: 'ProxyBridge 3 месяца', price: 249 },
    lifetime: { name: 'ProxyBridge Навсегда', price: 699 }
  };
  function product(plan) {
    var p = PLANS[plan] || PLANS.month;
    return { id: plan in PLANS ? plan : 'month', name: p.name, price: p.price, brand: 'ProxyBridge', category: 'Лицензия', quantity: 1 };
  }
  function ecommerce(action, plan, actionField) {
    var payload = { currencyCode: 'RUB' };
    payload[action] = { products: [product(plan)] };
    if (actionField) payload[action].actionField = actionField;
    window.dataLayer.push({ ecommerce: payload });
  }

  window.pbTrack = {
    goal: goal,
    detail: function (plan) { ecommerce('detail', plan); },
    add: function (plan) { ecommerce('add', plan); },
    purchase: function (orderId, plan) {
      var flag = 'pb-purchase-' + orderId;
      try { if (localStorage.getItem(flag)) return; localStorage.setItem(flag, '1'); } catch (e) { /* ignore */ }
      ecommerce('purchase', plan, { id: orderId, revenue: (PLANS[plan] || PLANS.month).price });
      goal('purchase', { plan: plan, revenue: (PLANS[plan] || PLANS.month).price });
    }
  };

  var PARTNERS = ['sx.org', 'proxy6.net', 'proxys.io', 'webshare.io', 'brightdata.com', 'psbproxy.io'];
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[href], [data-goal]');
    if (!a) return;
    var g = a.getAttribute('data-goal');
    if (g) { goal(g, { page: location.pathname }); }
    var href = a.getAttribute('href') || '';
    if (/ProxyBridge-Setup/i.test(href)) return goal('download_win', { page: location.pathname });
    if (/macOS-(arm64|x64)\.zip/i.test(href)) return goal('download_mac', { arch: /arm64/.test(href) ? 'arm64' : 'x64' });
    if (/buy\.html/.test(href)) {
      var m = href.match(/[?&](plan|method)=([\w]+)/);
      return goal(/method=/.test(href) ? 'pay_method_click' : 'buy_click', { page: location.pathname, value: m ? m[2] : '' });
    }
    for (var i = 0; i < PARTNERS.length; i++) if (href.indexOf(PARTNERS[i]) > -1) return goal('partner_click', { partner: PARTNERS[i] });
    if (/github\.com\/Inter1ark/i.test(href)) return goal('github_click');
    if (/t\.me\//.test(href)) return goal('telegram_click');
    if (/^mailto:/.test(href)) return goal('email_click');
    if (/lolz\.live/.test(href)) return goal('forum_click');
  }, true);

  // Consent banner
  function banner() {
    if (document.getElementById('pbCookie')) return;
    var d = document.createElement('div');
    d.id = 'pbCookie';
    d.className = 'cookie';
    d.setAttribute('role', 'dialog');
    d.setAttribute('aria-label', 'Согласие на cookie');
    d.innerHTML = '<p>Мы используем cookie и Яндекс Метрику, чтобы понимать, как работает сайт. ' +
      'Подробнее в <a href="/legal/privacy.html#cookie">политике конфиденциальности</a>.</p>' +
      '<div class="cookie-actions"><button type="button" class="btn btn-blue btn-small" data-c="all">Принять все</button>' +
      '<button type="button" class="btn btn-small cookie-min" data-c="necessary">Только необходимые</button></div>';
    d.addEventListener('click', function (e) {
      var b = e.target.closest('button[data-c]');
      if (!b) return;
      window.siteConsent.set(b.getAttribute('data-c'));
      d.remove();
    });
    document.body.appendChild(d);
  }

  window.siteConsent = {
    get: get,
    open: banner,
    set: function (v) {
      var was = get();
      set(v);
      if (v === 'all') load();
      else if (was === 'all') location.reload(); // withdrawing consent: reload without the counter
    }
  };

  function start() {
    var c = get();
    if (c === 'all') load();
    else if (!c) banner();
    var links = document.querySelectorAll('[data-cookie-settings]');
    for (var i = 0; i < links.length; i++) links[i].addEventListener('click', function (e) { e.preventDefault(); banner(); });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
