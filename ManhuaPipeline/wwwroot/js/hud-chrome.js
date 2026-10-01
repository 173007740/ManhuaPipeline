/* 全站顶部导航：统一长得一样、统一放哪几项
   ------------------------------------------------------------------
   为什么要一个脚本管：站点里三十来个页面，各自一段写死的 navbar，
   有的五个入口、有的八个，名字还不一致（系统配置 / 个人设置 / 我的资产…）。
   逐个去改等于把同一套头部复制三十遍，以后调整还得改三十遍。

   定下来的样子（2026-10 定的规矩）：
     左：40px 青色切角方块 + MANGA ENGINE / AI COMIC DIRECTOR OS
     中：首页 · 我的工作台 · 系统设置      ← 就这三项，各页多出来的入口一律收掉，
                                            页面之间从工作台 / 项目页进去
     右：已登录 = 用户名 + 退出；未登录 = 登录 / 注册
   页面自己的链接、跳转、功能一个都没少，少的只是顶栏上堆着的那串入口。

   登录态谁管：各页底部引的 /js/app.js 已经在管（GET /api/auth/me → 显示 #navUser 或 #navGuest）。
   这里重写用户区的内部，但保留 #navUser / #navGuest / #userName / #btnLogout 这几个 id，
   app.js 照旧认识；万一某页没引 app.js，末尾那段兜底自己拉一次登录态、自己绑退出。

   时机：脚本在 </head> 里同步引入，此刻 body 还没解析，必须等 DOM 就绪，
   否则 querySelector('.navbar') 只能拿到 null —— 上一版就是这样「改了却看不见」。 */
(function () {
  var BRAND_HTML = '<i>幕</i><span><b>MANGA ENGINE</b><small>AI COMIC DIRECTOR OS</small></span>';

  // 页面在 pages/ 下就用同级路径，在根目录就加 pages/ —— 跟 /js/app.js 里的判定一致
  var inPages = location.pathname.indexOf('/pages/') >= 0;
  var P = inPages ? '' : 'pages/';
  var HOME = inPages ? '../index.html' : 'index.html';

  var ITEMS = [
    { href: HOME, icon: 'home', text: '首页' },
    { href: P + 'dashboard.html', icon: 'grid_view', text: '我的工作台' },
    { href: P + 'system-config.html', icon: 'tune', text: '系统设置' }
  ];

  function icon(name) {
    return '<span class="material-icons" aria-hidden="true">' + name + '</span>';
  }

  function boot() {
    var nav = document.querySelector('.navbar');
    if (!nav) return;                       // 页面没有这一套导航（例如 project-editor.html），不动
    nav.classList.add('hc-topnav');

    var inner = nav.querySelector('.nav-inner') || nav;

    /* 品牌区：整体替换成 project-editor 那一版。
       各页原来的 logo 结构互不相同（带 img 的、两行小字的、渐变文字的），
       靠 CSS 去掰永远掰不齐，干脆换同一个 HTML。跳转地址沿用原来的。 */
    var oldBrand = nav.querySelector('.logo, .brand, .navbar-brand');
    var brand = document.createElement('a');
    brand.className = 'hc-brand';
    brand.setAttribute('href', (oldBrand && oldBrand.getAttribute('href')) || HOME);
    brand.innerHTML = BRAND_HTML;
    if (oldBrand) oldBrand.parentNode.replaceChild(brand, oldBrand);
    else inner.insertBefore(brand, inner.firstChild);

    /* 用户区先挪出来：它原本长在 .nav-links 里（有的页面是），
       待会儿要清空那一格，不先挪就跟着一起没了。元素本身保留，只换里面的内容。 */
    var links = nav.querySelector('.nav-links');
    if (!links) { links = document.createElement('div'); links.className = 'nav-links'; inner.appendChild(links); }
    var host = links.parentNode || inner;
    var userBox = document.getElementById('navUser');
    var guestBox = document.getElementById('navGuest');
    if (guestBox) host.insertBefore(guestBox, links.nextSibling);
    if (userBox) host.insertBefore(userBox, links.nextSibling);

    /* 导航项：只放定下来的这三项。
       内联样式是这里的死敌 —— 各页的 <a> 上写着 style="padding:8px 16px;font-size:14px..."，
       内联比任何选择器都大，不清掉等宽字和那些分隔线一条都不会生效。
       这里直接重写整格，新元素本来就没有内联。 */
    links.innerHTML = ITEMS.map(function (it) {
      return '<a href="' + it.href + '">' + icon(it.icon) + it.text + '</a>';
    }).join('');

    /* 用户区内部：按规矩重写。
       id 一个都不能改 —— #userName 是 app.js 塞用户名的地方，
       #btnLogout 是它（document 上委托）绑退出的地方。 */
    if (userBox) {
      userBox.innerHTML = '<span id="userName"></span>'
        + '<a href="#" id="btnLogout">' + icon('logout') + '退出</a>';
    }
    if (guestBox) {
      guestBox.innerHTML = '<a href="' + P + 'login.html">' + icon('account_circle') + '登录</a>'
        + '<a href="' + P + 'register.html">' + icon('person_add') + '注册</a>';
    }

    // 当前页高亮：只看文件名，?后面的参数不算
    var here = location.pathname.split('/').pop() || 'index.html';
    Array.prototype.forEach.call(links.children, function (a) {
      var h = (a.getAttribute('href') || '').split('/').pop();
      if (h && h === here) a.classList.add('hc-active');
    });

    /* 最右的状态：呼吸的点 + AUTOSAVE ONLINE，跟 project-editor 顶栏同一句 */
    if (!nav.querySelector('.hc-status')) {
      var st = document.createElement('div');
      st.className = 'hc-status';
      st.innerHTML = '<i></i>AUTOSAVE ONLINE';
      host.appendChild(st);
    }

    applyLoginState(userBox, guestBox);
  }

  /* 登录态。app.js 已经在做，这里是兜底：页面没引 app.js 时也得有。
     两者做法一致（同一个接口、同样的显示切换），重复一次没有副作用。 */
  function applyLoginState(userBox, guestBox) {
    if (!userBox && !guestBox) return;
    fetch('/api/auth/me', { credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(function (u) {
        var ok = u && u.userId;
        if (userBox) userBox.style.display = ok ? 'flex' : 'none';
        if (guestBox) guestBox.style.display = ok ? 'none' : 'flex';
        var nm = document.getElementById('userName');
        if (ok && nm && !nm.textContent.trim())
          nm.textContent = (u.nickname || u.username || '').trim();
      })
      .catch(function () { });   // 拉不到就维持现状，app.js 那边若跑起来自己会补
  }

  // 退出：跟 app.js 一样 POST /api/auth/logout 后回首页（委托，元素后来重建也有效）
  document.addEventListener('click', function (e) {
    var t = e.target && e.target.closest ? e.target.closest('#btnLogout') : null;
    if (!t) return;
    e.preventDefault();
    fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' })
      .then(function () { location.href = '/'; })
      .catch(function () { location.href = '/'; });
  });

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();
