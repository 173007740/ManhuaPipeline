/* 给页面已有的顶部导航套上统一皮肤（project-editor.html 那一版）
   ------------------------------------------------------------------
   为什么用 JS 做：站点里三十来个页面，各自的 navbar 是一段写死的 HTML，
   逐个去改等于把这个皮肤复制三十遍——以后改一处就得改三十处。
   这里在运行时给 .navbar 加一套类、把当前导航项高亮、把右上角的用户区换成同样的状态灯样式。
   链接本身一个都不动，所以「内容不改」：改的只是长得像不像同一套系统。

   页面里没有 .navbar 时（login / register 之类）什么都不做。 */
(function () {
  var nav = document.querySelector('.navbar');
  if (!nav) return;

  nav.classList.add('hc-topnav');

  // 当前页高亮：文件名对得上就算当前项（?后面的参数不算）
  var here = location.pathname.split('/').pop() || 'dashboard.html';
  Array.prototype.forEach.call(nav.querySelectorAll('.nav-links a'), function (a) {
    var href = (a.getAttribute('href') || '').split('/').pop();
    if (href && href === here) a.classList.add('hc-active');
  });

  /* 右上角：用户区（#navUser）与访客区（#navGuest）套上 .hc-system，
     并前置一个呼吸点。里面的字与链接保持原样。 */
  ['navUser', 'navGuest'].forEach(function (id) {
    var el = document.getElementById(id);
    if (!el) return;
    el.classList.add('hc-system');
    if (!el.querySelector('.hc-dot')) {
      var i = document.createElement('i');
      i.className = 'hc-dot';
      el.insertBefore(i, el.firstChild);
    }
  });
})();
