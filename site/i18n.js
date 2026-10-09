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
    'nav.buyProxy': 'Buy proxies',
    'nav.download': 'Download',

    'hero.eyebrow': 'Proxy client for Windows and macOS',
    'hero.title': 'Connection on<br><span class="mark">your</span> terms.',
    'hero.sub': 'The whole system or selected apps.<br>One client for all your proxies.',
    'hero.download': 'Download for Windows',
    'hero.github': 'View on GitHub',

    'demo.badge': 'Visualization',
    'demo.tHome': 'Home', 'demo.tProxies': 'Proxies', 'demo.tSettings': 'Settings',
    'demo.homeTitle': 'Which programs use which proxy',
    'demo.homeSub': 'Changes are saved right away. While connected they apply automatically.',
    'demo.colProg': 'Program', 'demo.colProxy': 'Proxy', 'demo.colOn': 'On', 'demo.colProxies': 'Proxy', 'demo.colStatus': 'Status',
    'demo.addProg': 'Add program',
    'demo.conn': 'Connection',
    'demo.route': 'How traffic goes',
    'demo.stPing': 'Latency', 'demo.stDn': 'Received', 'demo.stUp': 'Sent',
    'demo.pxTitle': 'Proxies',
    'demo.pxSub': 'Saved proxies. Rename any of them, the check shows latency and country.',
    'demo.checkAll': 'Check all',
    'demo.addPx': 'Add proxy', 'demo.add': 'Add', 'demo.import': 'Import from file', 'demo.formats': 'Formats:',
    'demo.checkTitle': 'Check',
    'demo.checkText': 'The check goes through the proxy itself (HTTP and SOCKS5) and does not touch the computer traffic. It shows latency, exit IP, country and city.',
    'demo.sGeneral': 'General', 'demo.sLangSub': 'Interface language. Switches instantly.',
    'demo.sAuto': 'Start with Windows', 'demo.sAutoSub': 'ProxyBridge opens when you sign in.',
    'demo.sTray': 'Minimize to tray', 'demo.sTraySub': 'When the window is closed, ProxyBridge keeps running in the tray.',
    'demo.sNotif': 'Notifications', 'demo.sNotifSub': 'Show a notification on connect and disconnect.',
    'demo.sConn': 'Connection',
    'demo.sStart': 'Connect at startup', 'demo.sStartSub': 'On start ProxyBridge connects with the saved rules.',
    'demo.sLogs': 'Support log', 'demo.sLogsSub': 'Logs for 7 days. Proxy passwords are never written.', 'demo.sLogsBtn': 'Open logs folder',
    'demo.sUpd': 'Updates', 'demo.sUpdAuto': 'Check for updates automatically', 'demo.sUpdAutoSub': 'ProxyBridge checks for a new version on launch.',
    'demo.sVer': 'Current version', 'demo.sUpdNow': 'Check now',
    'demo.sLic': 'License', 'demo.sPlan': 'Plan', 'demo.sPlanVal': 'Lifetime', 'demo.sTerm': 'Expires', 'demo.sTermVal': 'Never',
    'demo.sKey': 'License key', 'demo.sUnbind': 'Unlink device',
    'demo.lic': 'Lifetime license',
    'demo.note': 'This is an HTML visualization of the interface. Real screenshots of the app are below.',
    'demo.steps': '<span>1. Add a proxy</span><span>2. Pick programs</span><span>3. Connect</span>',

    'band.title': 'YOUR PROXY. YOUR ROUTE.',

    'routes.title': 'One client.<br>Different routes.',
    'routes.sub': 'Route the whole system or assign a separate proxy to every program.',
    'routes.sysTitle': 'Whole computer',
    'routes.sysTag': 'All programs',
    'routes.apps': 'Apps',
    'routes.yourProxy': 'Your proxy',
    'routes.sysFoot': 'Pick a proxy in the All other programs row: all TCP traffic of the computer goes through it.',
    'routes.splitTitle': 'Selected programs',
    'routes.splitTag': 'Own proxy each',
    'routes.proxy1': 'Proxy 1',
    'routes.proxy2': 'Proxy 2',
    'routes.others': 'Everything else',
    'routes.direct': 'Direct',
    'routes.splitFoot': 'A separate proxy for each program. Changes apply right away, even while connected.',

    'f1.title': 'Check through the proxy',
    'f1.text': 'HTTP and SOCKS5: latency, exit IP, country, city and flag. Check all proxies at once.',
    'f2.title': 'List and import',
    'f2.text': 'Paste several proxies at once or import them from a file. Give any of them your own name.',
    'f3.title': 'Autostart and updates',
    'f3.text': 'Starts with Windows, runs from the tray, connects at startup. New versions install after you confirm.',

    'inside.title': 'Inside the app.',
    'inside.sub': 'Real ProxyBridge 3.3 screenshots. Four screens, nothing extra.',
    'shot.tabHome': 'Home', 'shot.tabProxies': 'Proxies', 'shot.tabSettings': 'Settings',
    'shot.home': 'which apps use which proxy, connect with one button',
    'shot.proxies': 'proxy check: flag, city, IP and latency',
    'shot.tabAct': 'Activation',
    'shot.activation': 'enter the key once, up to 2 devices',
    'shot.settings': 'language, autostart, updates, license',
    'shot.help': 'short guide and contacts',

    'start.eyebrow': 'Getting started',
    'start.sub': 'steps to connect',
    'start.steps': '<li>Add a proxy on the Proxies tab.</li><li>Choose which programs go through it.</li><li>Press Connect.</li>',
    'start.check': 'Check',
    'start.note': 'You need your own proxy server. ProxyBridge does not provide one. No need to turn off your VPN: the app works together with WireGuard, AmneziaWG and similar clients.',

    'pricing.title': 'Subscription.',
    'pricing.sub': 'One key, up to two devices. The key arrives right after payment and is activated in the app window on launch.',
    'plan.m': '1 month', 'plan.mTag': '30 days', 'plan.mSub': 'Try it out', 'plan.mFoot': 'month',
    'plan.q': '3 months', 'plan.qTag': 'Best value', 'plan.qSub': '83 ₽ per month, save 16%', 'plan.qFoot': '90 days',
    'plan.l': 'Lifetime', 'plan.lTag': 'No expiry', 'plan.lSub': 'One payment, pays off in 7 months', 'plan.lFoot': 'Lifetime license',
    'plan.list': '<li>All app features</li><li>Up to 2 devices per key</li><li>Updates and support</li>',
    'plan.buy': 'Buy',
    'pricing.legal': 'By paying you accept the <a href="legal/offer.html">public offer</a>, <a href="legal/privacy.html">privacy policy</a> and <a href="legal/refund.html">refund terms</a>.',
    'foot.offer': 'Offer', 'foot.privacy': 'Privacy', 'foot.refund': 'Refunds',
    'buy.eyebrow': 'Checkout', 'buy.title': 'Get a key.', 'buy.sub': 'Pick a plan and a payment method. The key appears on the next page right after payment.',
    'buy.plan': 'Plan', 'buy.email': 'Email for the key', 'buy.method': 'Payment method', 'buy.summary': 'Order', 'buy.total': 'Total', 'buy.pay': 'Go to payment',
    'buy.fine': 'By clicking the button you accept the <a href="legal/offer.html">offer</a> and the <a href="legal/privacy.html">privacy policy</a>. Digital product, delivered instantly.',
    'buy.errEmail': 'Enter a valid email', 'buy.errServer': 'Payment service is unavailable, try again later', 'buy.wait': 'Creating payment...',
    'buy.m': '1 month', 'buy.mSub': '30 days of access', 'buy.q': '3 months', 'buy.qSub': '90 days, save 16%', 'buy.l': 'Lifetime', 'buy.lSub': 'No expiry',
    'suc.title': 'Thank you.', 'suc.waiting': 'Waiting for payment confirmation. This page updates automatically, do not close it.',
    'suc.paid': 'Payment received. Your license key:', 'suc.copy': 'Copy key', 'suc.copied': 'Copied',
    'suc.steps': '<li>1. Install ProxyBridge and run it as administrator.</li><li>2. Paste the key into the activation window and press Activate.</li><li>3. The key works on up to 2 devices. Save it: you need it when reinstalling.</li>',
    'suc.check': 'I have paid, check now', 'suc.canceled': 'Payment was canceled. You can start over.', 'suc.back': 'Back to checkout', 'sp.title': 'Payment received.', 'sp.text': 'Go back to ProxyBridge: the proxy will appear in the Get proxy section within a minute. This page can be closed.', 'sp.note': 'If the proxy has not appeared in 5 minutes, write to support and include the time of payment.', 'suc.download': 'Download ProxyBridge',
    'pricing.planName': 'ProxyBridge Pro',
    'pricing.planTag': '1 month',
    'pricing.perMonth': 'per month',
    'pricing.buy': 'Subscribe',
    'pricing.foot': 'Cancel anytime · No auto-renewal',
    'pricing.payTitle': 'Payment methods',
    'pricing.payTag': 'Pick any',
    'pay.rub': 'In rubles',
    'pay.rubSub': 'Card, SBP and more via YooKassa',
    'pay.crypto': 'In crypto',
    'pay.cryptoSub': 'USDT, TON, BTC and more via CryptoBot',
    'pricing.payNote': 'The key appears on the page right after payment. Payment questions: support@proxybridge.org.',

    "ph.eyebrow": "Partners",
    "ph.title": "Where to get proxies.",
    "ph.sub": "You can buy proxies right in ProxyBridge, on the Buy proxies page, or from these providers: they work with the app out of the box.",
    "ph.more": "Four more trusted providers and a plan comparison",
    "pf.go": "Go to",
    "pf.sxTag": "ProxyBridge pick",
    "pf.sxLead": "Clean IPs in 235+ geos. Pay per traffic or take an unlimited plan.",
    "pf.sxList": "<li>More than 235 countries and regions</li><li>Pay per traffic or unlimited plans</li><li>HTTP and SOCKS5 work in ProxyBridge as is</li><li>Support around the clock</li>",
    "pf.sxPromo": "Promo code <b>bridge</b>: 3 GB for testing",
    "pf.psbTag": "Mobile and residential",
    "pf.psbLead": "Proxies for arbitrage, scraping and multi-accounting: 40+ million IPs in 200+ countries.",
    "pf.psbList": "<li>Mobile LTE/5G and residential IPs</li><li>Flexible rotation and sticky IPs</li><li>HTTP(S) and SOCKS5, API</li><li>99.9% SLA, support around the clock</li>",
    "pg.main": "Main partners",
    "pg.others": "Other trusted providers",
    "pg.compare": "Provider comparison",
    "pt.h0": "Provider",
    "pt.h1": "Strength",
    "pt.h2": "How to pay",
    "pt.h3": "Try it",
    "pt.sx1": "235+ geos, clean IPs",
    "pt.sx2": "Per traffic or unlimited",
    "pt.sx3": "3 GB with promo code bridge",
    "pt.psb1": "Mobile LTE/5G and residential, 40+ million IPs",
    "pt.psb2": "Flexible plans, rotation and sticky IPs",
    "pt.psb3": "Ask the provider",
    "pt.p61": "IPv4 and IPv6, HTTP, HTTPS, SOCKS5",
    "pt.p62": "Proxy packages, API",
    "pt.p63": "Ask the provider",
    "pt.pio1": "Private proxies up to 1 Gbps",
    "pt.pio2": "Proxy packages, IP rotation",
    "pt.pio3": "Ask the provider",
    "pt.ws1": "HTTP and SOCKS5, unlimited traffic",
    "pt.ws2": "Free start, paid packages",
    "pt.ws3": "10 proxies free, no card",
    "pt.bd1": "72M+ residential IPs, business tools",
    "pt.bd2": "Enterprise plans",
    "pt.bd3": "Ask the provider",
    "pg.how": "How to connect a provider's proxy",
    "pg.steps": "<li>Buy a proxy and copy the address, port, login and password from the provider's dashboard.</li><li>Open ProxyBridge and paste the line on the Proxies tab as <code>socks5://user:pass@ip:port</code> or <code>ip:port:user:pass</code>.</li><li>On the Home tab pick the programs for this proxy and press Connect: the whole computer or the selected apps go through it.</li>",
    "pg.format": "More about proxy formats",
    "pg.which": "Which proxies to choose",
    "pg.faq": "FAQ",
    "pq.q1": "Which provider should I pick for ProxyBridge?",
    "pq.a1": "SX.ORG fits most tasks: many geos and a test with promo code bridge. For arbitrage and multi-accounting that need mobile IPs, look at PSBProxy. Webshare free proxies are enough to get started.",
    "pq.q2": "Do I need to configure anything at the provider?",
    "pq.a2": "No. Take the address, port, login and password from the provider, paste the line into ProxyBridge as socks5://user:pass@ip:port or ip:port:user:pass and connect.",
    "pq.q3": "Why are the links affiliate links?",
    "pq.a3": "Buying through these links supports ProxyBridge development at no extra cost to you. Prices and terms at the provider stay the same.",
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
    'cta.macNote': 'The app is not signed by Apple: on first launch right-click it and choose Open. An administrator password is required. Whole computer only, no per-program rules.',
    'cta.note': 'Windows 10/11, 64-bit · Install as administrator',

    'pp.eyebrow': 'Proxy providers',
    'pp.title': 'Where to get a proxy.',
    'pp.sub': 'You can buy proxies right in ProxyBridge, on the Buy proxies page, or from the trusted HTTP and SOCKS5 providers below: the app works with them out of the box.',
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
    'pp.home': 'Home',

    // Buy proxies page (/kupit-proksi/): static texts
    'kp.metaTitle': 'Buy proxies: dedicated for 30 days or pay per GB | ProxyBridge',
    'kp.metaDesc': 'Buy HTTP and SOCKS5 proxies: a dedicated IP for 30 days at 400 RUB with unlimited traffic, or pay per GB and pick the country, city and IP rotation.',
    'kp.crumbHome': 'Home', 'kp.crumbHere': 'Buy proxies',
    'kp.eyebrow': 'Proxies from ProxyBridge',
    'kp.title': 'Buy proxies.',
    'kp.sub': 'HTTP and SOCKS5 with login and password. Take your own IP for 30 days or pay only for gigabytes. The proxy is created right after payment and works in any program.',
    'kp.jump': 'My proxies ↓',
    'kp.orderCheck': 'Check again', 'kp.orderClose': 'Hide',
    'kp.loading': 'Loading countries and prices...', 'kp.retry': 'Retry',
    'kp.back': '← Back to buying',
    'kp.what': 'What to buy',
    'kp.modeDc': '30 days', 'kp.modeDcSub': 'Dedicated datacenter IP, unlimited traffic, HTTP and SOCKS5',
    'kp.modeTraffic': 'Pay per GB', 'kp.modeTrafficSub': 'Country, region and city, IP type, IP change on a timer or on every request',
    'kp.dcAbout': 'A dedicated datacenter IP just for you. Unlimited traffic, works over HTTP and SOCKS5. After 30 days it can be renewed with the same IP.',
    'kp.country': 'Country', 'kp.region': 'Region', 'kp.city': 'City',
    'kp.type': 'IP type',
    'kp.tMobile': 'Mobile', 'kp.tMobileSub': 'IPs of mobile carriers',
    'kp.tResidential': 'Residential', 'kp.tResidentialSub': 'IPs of home internet providers',
    'kp.tDatacenter': 'Datacenter', 'kp.tDatacenterSub': 'Datacenter IPs, the cheapest',
    'kp.rotation': 'IP change',
    'kp.rStatic': 'Static', 'kp.rStaticSub': 'the IP stays while it is online',
    'kp.rInterval': 'Timer', 'kp.rIntervalSub': 'a new IP after the set time',
    'kp.rRequest': 'Every request', 'kp.rRequestSub': 'every connection from a new IP',
    'kp.ttlEvery': 'Change IP every', 'kp.ttlMin': 'min',
    'kp.gb': 'Traffic', 'kp.gbUnit': 'GB',
    'kp.sumProduct': 'Product', 'kp.sumWhere': 'Location', 'kp.sumType': 'Type', 'kp.sumVolume': 'Amount',
    'kp.fine': 'The proxy is created right after payment. If it cannot be created, you get your money back. Paid traffic does not expire. Proxies must not be used for spam or illegal activity. By clicking the button you accept the <a href="/legal/offer.html">offer</a> and the <a href="/legal/privacy.html">privacy policy</a>.',
    'kp.appNote': 'Using ProxyBridge for Windows? You can buy the same proxies right in the app, on the Get proxy tab: a ready proxy goes straight into your list.',
    'kp.cabTitle': 'My proxies', 'kp.refresh': 'Refresh',
    'kp.linkTitle': 'Link to your cabinet',
    'kp.linkText': 'Your proxies are tied to this browser. Save the link: it opens the cabinet on another device or after the browser data is cleared.',
    'kp.copy': 'Copy',
    'kp.linkWarn': 'Anyone with this link sees your proxies together with their passwords. Do not share it.',
    'kp.prev': 'Open the previous cabinet of this browser',
    'kp.faqTitle': 'FAQ.',
    'kp.q1': 'What is the difference between a 30 day proxy and a pay per GB proxy?',
    'kp.a1': '30 day proxy: a dedicated datacenter IP just for you, unlimited traffic, 400 RUB for 30 days, then it can be renewed with the same IP. Pay per GB proxy: you pay for gigabytes and pick the country, region, city, IP type and IP change yourself. Paid traffic does not expire over time.',
    'kp.q2': 'Which protocols are supported and where do the proxies work?',
    'kp.a2': 'HTTP and SOCKS5 with login and password. After payment you get the connection string as socks5://, http:// and host:port:login:password. The proxy works in ProxyBridge and in any other program or browser where you can set a proxy.',
    'kp.q3': 'What happens when the traffic runs out?',
    'kp.a3': 'The proxy is paused. Buy more GB with the Buy more GB button in My proxies and it works again.',
    'kp.q4': 'What if the proxy cannot be created?',
    'kp.a4': 'Then you get your money back. Card and SBP payments are refunded automatically, usually within a few days. For crypto payments write to support@proxybridge.org and we refund manually.',

    // Buy proxies page: dynamic texts (Russian ones are in /kupit-proksi/store.js)
    'kp.loadingGeo': 'Loading...',
    'kp.anyRegion': 'Any region', 'kp.anyCity': 'Any city', 'kp.pickRegionFirst': 'Pick a region first',
    'kp.perGb': '{0} ₽<span> / GB</span>', 'kp.fromPerGb': 'from {0} ₽<span> / GB</span>',
    'kp.availChecking': 'Checking availability...',
    'kp.availNo': 'This type is not available in this country right now. Pick another country or type.',
    'kp.gbHint': 'From {0} to {1} GB. Paid traffic does not expire over time.',
    'kp.pay': 'Pay', 'kp.payN': 'Pay {0} ₽',
    'kp.prodDc': 'Dedicated proxy', 'kp.prodTraffic': 'Pay per GB proxy', 'kp.prodRenew': 'Proxy renewal', 'kp.prodTopup': 'More traffic',
    'kp.sumTypeDc': 'Datacenter, HTTP and SOCKS5',
    'kp.volDc': '{0} days, unlimited traffic', 'kp.volGb': '{0} GB, no time limit',
    'kp.capsRenew': 'Renew for {0} days', 'kp.capsTopup': 'Buy more traffic',
    'kp.kindDc': 'Dedicated',
    'kp.type.mobile': 'Mobile', 'kp.type.residential': 'Residential', 'kp.type.datacenter': 'Datacenter',
    'kp.rot.static': 'static IP', 'kp.rot.request': 'new IP on every request', 'kp.rot.interval': 'new IP every {0} min',
    'kp.st.active': 'Working', 'kp.st.exhausted': 'Out of traffic', 'kp.st.expired': 'Expired', 'kp.st.provisioning': 'Being created',
    'kp.usage': '{0} of {1} GB', 'kp.until': 'until {0}', 'kp.untilRenewed': 'until {0}, renewal paid',
    'kp.noteProvisioning': 'The proxy is being created. Connection details appear here within a minute, press Refresh.',
    'kp.noteExhausted': 'Out of traffic, the proxy is paused. Buy more GB and it works again.',
    'kp.noteExpired': 'The term is over. Buy a new proxy above.',
    'kp.lineString': 'Line',
    'kp.topup': 'Buy more GB', 'kp.renew': 'Renew', 'kp.newIp': 'Change IP',
    'kp.copied': 'Copied',
    'kp.ipChanged': 'The IP will change in a few seconds.',
    'kp.listEmpty': 'Proxies bought in this browser will appear here.',
    'kp.noStorage': 'Your browser does not let the site save data. Copy the cabinet link before paying, otherwise the proxy will not show up here after payment.',
    'kp.creating': 'Creating the order...', 'kp.redirect': 'Opening the payment page...',
    'kp.oCheckTitle': 'Checking the order',
    'kp.oPendingTitle': 'Waiting for payment confirmation',
    'kp.oPendingText': 'This usually takes a few seconds. The page updates by itself, do not close it.',
    'kp.oProcTitle': 'Payment received', 'kp.oProcText': 'Creating the proxy, this takes up to a minute.',
    'kp.oDoneTitle': 'Done.',
    'kp.oDoneText': 'The proxy is ready. It is saved in My proxies below.',
    'kp.oDoneRenew': 'Renewal paid. The term is extended on the expiry day.',
    'kp.oDoneTopup': 'Traffic added. If the proxy was paused, it works again.',
    'kp.oFailTitle': 'The proxy could not be created',
    'kp.oFailRefunded': 'The money goes back to your card within a few days.',
    'kp.oFailSupport': 'Write to support@proxybridge.org with the time of payment and we will refund you.',
    'kp.oCanceledTitle': 'Payment canceled', 'kp.oCanceledText': 'No money was charged. You can place the order again.',
    'kp.oNotFoundTitle': 'Order not found',
    'kp.oNotFoundText': 'This browser has no such order. If you paid on another device, open the cabinet link from there.',
    'kp.oTimeoutTitle': 'The payment has not arrived yet',
    'kp.oTimeoutText': 'If you paid, the proxy will appear in My proxies. Press Refresh in a couple of minutes.',
    'kp.oNetText': 'No connection to the ProxyBridge server. Check your internet and press Check again.',
    'kp.err.network': 'No connection to the ProxyBridge server. Check your internet and try again.',
    'kp.err.out_of_stock': 'We cannot issue this proxy right now. Try later or pick another option.',
    'kp.err.unavailable': 'This type is not available in this country right now. Pick another country or type.',
    'kp.err.renew_pending': 'Renewal is already paid.',
    'kp.err.cannot_renew': 'This proxy can no longer be renewed. Buy a new one.',
    'kp.err.cannot_topup': 'Traffic cannot be added to this proxy.',
    'kp.err.cannot_refresh': 'The IP of this proxy cannot be changed right now.',
    'kp.err.too_often': 'Too often. Wait half a minute.',
    'kp.err.payment': 'The payment service did not respond. Try again or pick another payment method.',
    'kp.err.bad_gb': 'Enter a whole number of GB from {0} to {1}.',
    'kp.err.bad_ttl': 'Enter the IP change interval in minutes, 1 to {0}.',
    'kp.err.disabled': 'The store is closed for now.',
    'kp.err.empty': 'The store is not available right now. Try later.',
    'kp.err.server': 'Server error. Try again.',
    'kp.err.pick_country': 'Pick a country.',
    'kp.err.proxy_not_found': 'This proxy is not in this cabinet.'
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
    // pages with their own EN title: <html data-page="kp"> + EN['kp.metaTitle'] / EN['kp.metaDesc']
    var page = document.documentElement.getAttribute('data-page');
    var pageTitle = page && EN[page + '.metaTitle'];
    var pageDesc = page && EN[page + '.metaDesc'];
    if (lang === 'en') {
      document.title = pageTitle || (isPartners ? EN['meta.titlePartners'] : EN['meta.title']);
    } else {
      document.title = RU['meta.title'];
    }
    var d = document.querySelector('meta[name="description"]');
    if (d && !isPartners) d.setAttribute('content', lang === 'en' && pageDesc ? pageDesc : dict['meta.desc']);
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
      if (b) { apply(b.getAttribute('data-lang')); if (b.getAttribute('data-lang') === 'en' && window.pbTrack) window.pbTrack.goal('lang_en'); }
    });
  }
})();
