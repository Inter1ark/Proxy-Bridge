/* ProxyBridge site: RU / EN switch.
   Russian text lives in the HTML. English strings are listed here by data-i18n key. */
(function () {
  'use strict';

  var EN = {
    'meta.title': 'ProxyBridge: proxy client for Windows',
    'meta.desc': 'ProxyBridge routes traffic of the whole system or selected apps through HTTP and SOCKS5 proxies. Windows 10/11, open source.',
    'meta.titlePartners': 'ProxyBridge partners: proxy providers',

    'nav.product': 'Product',
    'nav.features': 'Features',
    'nav.inside': 'Interface',
    'nav.pricing': 'Pro',
    'nav.partners': 'Partners',
    'nav.download': 'Download',

    'hero.eyebrow': 'Proxy client for Windows',
    'hero.title': 'Connection on<br><span class="mark">your</span> terms.',
    'hero.sub': 'The whole system or selected apps.<br>One client for all your proxies.',
    'hero.download': 'Download for Windows',
    'hero.github': 'View on GitHub',

    'demo.badge': 'Visualization',
    'demo.hint': 'Supported formats: <code>http://user:pass@ip:port</code> or <code>socks5://user:pass@ip:port</code>',
    'demo.history': 'Proxy history',
    'demo.clear': 'Clear',
    'demo.listSub': 'Load a proxy list for quick switching',
    'demo.splitSub': 'A separate proxy for each program. Other traffic goes direct. TCP only.',
    'demo.settingsSub': 'App behavior',
    'demo.s1': 'Minimize the app to the system tray on close',
    'demo.s2': 'Launch ProxyBridge when Windows starts',
    'demo.s3': 'Automatically connect to the last used proxy',
    'demo.s4': 'Show connect and disconnect notifications',
    'demo.s5': 'Send DNS queries directly, no leaks',
    'demo.helpSub': 'Quick guide',
    'demo.howTitle': 'How to use ProxyBridge',
    'demo.howList': '<li>Enter proxy details<span>http://user:pass@ip:port or socks5://user:pass@ip:port</span></li><li>Press VERIFY<span>Checks availability and country of the proxy</span></li><li>Press the round CONNECT button<span>All TCP traffic goes through the proxy</span></li><li>Use Proxy List<span>Load a list from a file and switch with one click</span></li><li>Set up Split Tunnel<span>A separate proxy for each program</span></li>',
    'demo.contactsTitle': 'Contacts',
    'demo.note': 'This is an HTML visualization of the interface. Real screenshots of the app are below.',
    'demo.steps': '<span>1. Add a proxy</span><span>2. Verify</span><span>3. Connect</span>',

    'band.title': 'YOUR PROXY. YOUR ROUTE.',

    'routes.title': 'One client.<br>Different routes.',
    'routes.sub': 'Route the whole system or assign a separate proxy to every program.',
    'routes.sysTitle': 'System mode',
    'routes.sysTag': 'Whole PC',
    'routes.apps': 'Apps',
    'routes.yourProxy': 'Your proxy',
    'routes.sysFoot': 'System TCP traffic goes through the chosen proxy. DNS can stay direct.',
    'routes.splitTag': 'Per program',
    'routes.proxy1': 'Proxy 1',
    'routes.proxy2': 'Proxy 2',
    'routes.others': 'Everything else',
    'routes.direct': 'Direct',
    'routes.splitFoot': 'A separate route for each program. Rules apply with one click.',

    'f1.title': 'GEO check',
    'f1.text': 'Country, city and availability before you connect. Country flag right in the window.',
    'f2.title': 'History and import',
    'f2.text': 'Recent proxies are saved automatically. Lists load from a file and switch with one click.',
    'f3.title': 'Auto-connect',
    'f3.text': 'Starts with Windows, lives in the tray and reconnects to the last proxy on its own.',

    'inside.title': 'Inside the app.',
    'inside.sub': 'Real ProxyBridge 3.2 screenshots. Six screens, nothing extra.',
    'shot.tabAct': 'Activation',
    'shot.activation': 'enter the key once, up to 2 devices',
    'shot.dashboard': 'paste a proxy, verify, connect',
    'shot.proxylist': 'list from a file, one-click connect',
    'shot.splittunnel': 'own proxy for every program',
    'shot.settings': 'tray, autostart, auto-connect, DNS',
    'shot.help': 'short guide and contacts',

    'start.eyebrow': 'Getting started',
    'start.sub': 'steps to connect',
    'start.steps': '<li>Paste the proxy address.</li><li>Check it with <code>VERIFY</code>.</li><li>Press <code>CONNECT</code>.</li>',
    'start.note': 'You need your own proxy server. ProxyBridge does not provide one. Turn off VPN clients (WireGuard, Amnezia and similar) while it runs: two traffic interceptors at once conflict.',

    'pricing.title': 'Subscription.',
    'pricing.sub': 'One key, up to two devices. The key arrives right after payment and is activated in the app window on launch.',
    'plan.m': '1 month', 'plan.mTag': '30 days', 'plan.mSub': 'Try it out', 'plan.mFoot': 'month',
    'plan.q': '3 months', 'plan.qTag': 'Best value', 'plan.qSub': '83 ₽ per month, save 16%', 'plan.qFoot': '90 days',
    'plan.l': 'Lifetime', 'plan.lTag': 'No expiry', 'plan.lSub': 'One payment, pays off in 7 months', 'plan.lFoot': 'Lifetime license',
    'plan.list': '<li>All app features</li><li>Up to 2 devices per key</li><li>Updates and support</li>',
    'plan.buy': 'Buy',
    'pricing.legal': 'By paying you accept the <a href="legal/offer.html">public offer</a>, <a href="legal/privacy.html">privacy policy</a> and <a href="legal/refund.html">refund terms</a>.',
    'foot.offer': 'Offer', 'foot.privacy': 'Privacy', 'foot.refund': 'Refunds',
    'buy.eyebrow': 'Checkout', 'buy.title': 'Get a key.', 'buy.sub': 'Pick a plan and a payment method. The key appears on the next page right after payment and is also sent to your email.',
    'buy.plan': 'Plan', 'buy.email': 'Email for the key', 'buy.method': 'Payment method', 'buy.summary': 'Order', 'buy.total': 'Total', 'buy.pay': 'Go to payment',
    'buy.fine': 'By clicking the button you accept the <a href="legal/offer.html">offer</a> and the <a href="legal/privacy.html">privacy policy</a>. Digital product, delivered instantly.',
    'buy.errEmail': 'Enter a valid email', 'buy.errServer': 'Payment service is unavailable, try again later', 'buy.wait': 'Creating payment...',
    'buy.m': '1 month', 'buy.mSub': '30 days of access', 'buy.q': '3 months', 'buy.qSub': '90 days, save 16%', 'buy.l': 'Lifetime', 'buy.lSub': 'No expiry',
    'suc.title': 'Thank you.', 'suc.waiting': 'Waiting for payment confirmation. This page updates automatically, do not close it.',
    'suc.paid': 'Payment received. Your license key:', 'suc.copy': 'Copy key', 'suc.copied': 'Copied',
    'suc.steps': '<li>1. Install ProxyBridge and run it as administrator.</li><li>2. Paste the key into the activation window and press Activate.</li><li>3. The key works on up to 2 devices. It is also sent to your email.</li>',
    'suc.check': 'I have paid, check now', 'suc.canceled': 'Payment was canceled. You can start over.', 'suc.back': 'Back to checkout', 'suc.download': 'Download ProxyBridge',
    'pricing.planName': 'ProxyBridge Pro',
    'pricing.planTag': '1 month',
    'pricing.perMonth': 'per month',
    'pricing.list': '<li>Split Tunnel with no limit on the number of programs</li><li>Auto-check and rotation of proxies from the list</li><li>Priority support in Telegram</li><li>Early access to new versions</li>',
    'pricing.buy': 'Subscribe',
    'pricing.foot': 'Cancel anytime · No auto-renewal',
    'pricing.payTitle': 'Payment methods',
    'pricing.payTag': 'Pick any',
    'pay.rub': 'In rubles',
    'pay.rubSub': 'Card, SBP and more via YooKassa',
    'pay.crypto': 'In crypto',
    'pay.cryptoSub': 'USDT, TON, BTC and more via CryptoBot',
    'pricing.payNote': 'The key appears on the page after payment and is also sent by email. Payment questions: support@proxybridge.org.',

    'partners.title': 'Proxy providers',
    'partners.sub': 'Trusted sources for your proxies',
    'partners.all': 'All partners',

    'help.title': 'Need <span class="mark">help</span>?',
    'help.sub': 'Reach out the way you like',
    'help.tg': 'Ask a question or report a problem',
    'help.lz': 'Profile on the largest forum',
    'help.mail': 'Technical support and questions',

    'cta.title': 'Get in touch.',
    'cta.download': 'Download for Windows',
    'cta.mac': 'macOS 12+ (beta):',
    'cta.macNote': 'The app is not signed by Apple: on first launch right-click it and choose Open. An administrator password is required. System mode only, no Split Tunnel.',
    'cta.note': 'Windows 10/11, 64-bit · Install as administrator',

    'pp.eyebrow': 'Proxy providers',
    'pp.title': 'Where to get a proxy.',
    'pp.sub': 'ProxyBridge does not sell proxies. Below are trusted HTTP and SOCKS5 providers that work with the app out of the box.',
    'pp.sx': 'Clean IPs and 235+ geos. Pay per traffic or flexible unlimited plans. Promo code bridge gives 3 GB for testing. Support around the clock.',
    'pp.sxTag': '3 GB trial',
    'pp.p6': 'IPv4 and IPv6 proxies for any task. High speed and stability, HTTP, HTTPS and SOCKS5. Round-the-clock support and an API for automation.',
    'pp.pio': 'Private proxies with all popular protocols. Up to 1 Gbps, automatic IP rotation and a simple real-time dashboard.',
    'pp.pioTag': 'Up to 1 Gbps',
    'pp.ws': '10 free proxies forever. A good choice to start and test: unlimited traffic, HTTP and SOCKS5, no card required.',
    'pp.wsTag': '10 free',
    'pp.bd': 'The largest proxy network in the world: 72+ million residential IPs, full automation and business tools. 99.9% uptime.',
    'pp.psb': 'Mobile and residential proxies for arbitrage, scraping and multi-accounting. 40+ million IPs in 200+ countries, flexible rotation, HTTP(S) and SOCKS5, API.',
    'pp.go': 'Go to',
    'pp.note': 'These are affiliate links. Buying through them supports ProxyBridge development at no extra cost to you.',
    'pp.ready': 'Ready?',
    'pp.home': 'Home'
  };

  var RU = {};
  var current = 'ru';

  function collectRu() {
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var k = el.getAttribute('data-i18n');
      if (!(k in RU)) RU[k] = el.innerHTML;
    });
    RU['meta.title'] = document.title;
    var d = document.querySelector('meta[name="description"]');
    RU['meta.desc'] = d ? d.getAttribute('content') : '';
  }

  function apply(lang) {
    var dict = lang === 'en' ? EN : RU;
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var k = el.getAttribute('data-i18n');
      if (dict[k] !== undefined) el.innerHTML = dict[k];
    });
    var isPartners = /partners\.html$/i.test(location.pathname);
    if (lang === 'en') {
      document.title = isPartners ? EN['meta.titlePartners'] : EN['meta.title'];
    } else {
      document.title = RU['meta.title'];
    }
    var d = document.querySelector('meta[name="description"]');
    if (d && !isPartners) d.setAttribute('content', dict['meta.desc']);
    document.documentElement.lang = lang;
    document.querySelectorAll('#lang button').forEach(function (b) {
      b.classList.toggle('active', b.getAttribute('data-lang') === lang);
    });
    current = lang;
    try { localStorage.setItem('pb_lang', lang); } catch (e) {}
    document.dispatchEvent(new CustomEvent('langchange', { detail: lang }));
  }

  function detect() {
    var q = new URLSearchParams(location.search).get('lang');
    if (q === 'en' || q === 'ru') return q;
    try {
      var s = localStorage.getItem('pb_lang');
      if (s === 'en' || s === 'ru') return s;
    } catch (e) {}
    var nav = (navigator.language || 'ru').toLowerCase();
    return nav.indexOf('ru') === 0 ? 'ru' : 'en';
  }

  window.PB_I18N = {
    t: function (k) { return (current === 'en' ? EN : RU)[k]; },
    lang: function () { return current; },
    set: apply
  };

  collectRu();
  var start = detect();
  if (start !== 'ru') apply(start);
  else apply('ru');

  var sw = document.getElementById('lang');
  if (sw) {
    sw.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (b) apply(b.getAttribute('data-lang'));
    });
  }
})();
