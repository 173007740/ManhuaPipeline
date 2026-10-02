/**
 * 全站 HUD 背景：粒子星图
 * 依赖：页面里先有 <div id="particleWrap"><canvas id="particleCanvas"></canvas></div>
 * 最初只放在 dashboard.html 内联，现在抽到公共文件，避免各页各抄一份。
 */
(function () {
    var canvas = document.getElementById('particleCanvas');
    if (!canvas) return;
    canvas.style.top = '0';
    canvas.style.width = '100%';
    canvas.style.pointerEvents = 'none';
    var ctx = canvas.getContext('2d');
    var W, H;
    function resize() { W = canvas.width = window.innerWidth; H = canvas.height = window.innerHeight; }
    resize();
    window.addEventListener('resize', resize);
    var particles = [];
    var COUNT = 70;
    var colors = ['rgba(5,232,240,', 'rgba(16,219,230,', 'rgba(83,247,255,'];
    for (var i = 0; i < COUNT; i++) {
        particles.push({
            x: Math.random() * W, y: Math.random() * H, r: Math.random() * 2 + 1.2,
            dx: (Math.random() - 0.5) * 0.4, dy: (Math.random() - 0.5) * 0.4 - 0.15,
            color: colors[Math.floor(Math.random() * colors.length)],
            alpha: Math.random() * 0.4 + 0.15
        });
    }
    var CONNECT_DIST = 130;
    function anim() {
        ctx.clearRect(0, 0, W, H);
        for (var i = 0; i < COUNT; i++) {
            var p = particles[i];
            p.x += p.dx; p.y += p.dy;
            if (p.x < 0 || p.x > W) p.dx *= -1;
            if (p.y < 0 || p.y > H) p.dy *= -1;
            ctx.beginPath();
            ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
            ctx.fillStyle = p.color + p.alpha + ')';
            ctx.fill();
            for (var j = i + 1; j < COUNT; j++) {
                var q = particles[j];
                var dx = p.x - q.x, dy = p.y - q.y;
                var dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < CONNECT_DIST) {
                    ctx.beginPath();
                    ctx.moveTo(p.x, p.y);
                    ctx.lineTo(q.x, q.y);
                    ctx.strokeStyle = 'rgba(5,232,240,' + (1 - dist / CONNECT_DIST) * 0.2 + ')';
                    ctx.lineWidth = 0.6;
                    ctx.stroke();
                }
            }
        }
        requestAnimationFrame(anim);
    }
    anim();
})();
