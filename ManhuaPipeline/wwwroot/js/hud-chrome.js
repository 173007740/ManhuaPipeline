/* 给页面已有的顶部导航换上 project-editor.html 那一版的皮
   ------------------------------------------------------------------
   为什么不逐个页面改 HTML：站点里三十来个页面，各自一段写死的 navbar，
   真改等于把同一套头部复制三十遍——以后改一处就得改三十处。这里运行时统一换。

   换什么（照 /pages/project-editor.html 的顶栏，一字一句对过来）：
     - 左侧品牌：40px 青色切角方块 + 一个「幕」字，右边双行 MANGA ENGINE / AI COMIC DIRECTOR OS
     - 中间导航：链接一个都不动，只是换成等宽字、宽⣹分隔、hover 变青、当前项底部红线
     - 最右：呼吸的圆点 + AUTOSAVE ONLINE
   不换的：链接地址与文字、页面主体、原来的 sticky 定位（页面没预留 padding，改 fixed 会压住内容）。

   时机：脚本在 </head> 里同步引入，此刻 body 还没解析，
   必须等 DOM 就绪再动手，否则 querySelector('.navbar') 只能拿到 null。 */
(function () {
  // 品牌区的字，跟 project-editor.html 顶栏完全一致
  var BRAND_HTML = '<i>幕</i><span><b>MANGA ENGINE</b><small>AI COMIC DIRECTOR OS</small></span>';

  function boot() {
    var nav = document.querySelector('.navbar');
    if (!nav) return;                       // 页面没有这一套导航（例如 project-editor.html），不动
    nav.classList.add('hc-topnav');

    // 导航项可能包在 .nav-inner 里，也可能直接在 .navbar 下，两种都要能处理
    var inner = nav.querySelector('.nav-inner') || nav;

    /* 品牌区：整体替换成 project-editor 那一版。
       原来的 logo 结构各个页面都不一样（有的带 img、有的两行小字、
       有的用渐变文字），改样式去凑永远凑不齐，干脆换同一个 HTML。
       跳转地址保留原来的（有的回首页、有的回工作台）。 */
    var oldBrand = nav.querySelector('.logo, .brand, .navbar-brand');
    var href = (oldBrand && oldBrand.getAttribute('href')) || '/pages/dashboard.html';
    var brand = document.createElement('a');
    brand.className = 'hc-brand';
    brand.setAttribute('href', href);
    brand.innerHTML = BRAND_HTML;
    if (oldBrand) oldBrand.parentNode.replaceChild(brand, oldBrand);
    else inner.insertBefore(brand, inner.firstChild);

    /* 内联样式是这里的死敌：各页的导航 <a> 上写着 style="padding:8px 16px;font-size:14px..."，
       内联比任何 CSS 选择器都大，不摘掉上面那些「等宽字 / 分隔线 / 当前项红线」一条都不会生效。
       只清链接上的内联，不动 #navUser 本身 —— 它上面那句 display:none 是登录状态在管。 */
    Array.prototype.forEach.call(
      nav.querySelectorAll('.nav-links > a, #navUser a, #navGuest a, #userName'),
      function (el) { el.removeAttribute('style'); });

    // 当前页高亮：只看文件名，?后面的参数不算
    var here = location.pathname.split('/').pop() || 'dashboard.html';
    Array.prototype.forEach.call(nav.querySelectorAll('.nav-links > a'), function (a) {
      var h = (a.getAttribute('href') || '').split('/').pop();
      if (h && h === here) a.classList.add('hc-active');
    });

    /* 最右的状态区：放在导航那一组之后（用户区后面），
       跟 project-editor 一样挂在顶栏右端。已经有就不重复插。 */
    if (!nav.querySelector('.hc-status')) {
      var st = document.createElement('div');
      st.className = 'hc-status';
      st.innerHTML = '<i></i>AUTOSAVE ONLINE';
      inner.appendChild(st);   // 挂在最后：导航那一组是 margin-left:auto，它自然就落到最右端
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();
