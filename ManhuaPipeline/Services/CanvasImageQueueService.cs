using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 画布出图后台队列：并发执行 CanvasTasks 里 status='queued' 的任务。
///
/// 与 <see cref="AssetImageQueueService"/> 完全同一套思路：
///   · 出图不在 HTTP 请求里同步等 —— 页面关掉、刷新、换设备都会跑完，回来由 SSE 收到结果。
///   · 并发度可配：CanvasQueue:MaxConcurrency（默认 3，钳制 1~8），中转限流时调回 1。
///   · 并发安全靠 TryMarkCanvasTaskRunning 的乐观更新（WHERE Status='queued'），
///     同一条任务只可能被一个 worker 抢到。
///   · 启动时回收上次进程退出时卡在 running 的任务。
/// </summary>
public class CanvasImageQueueService : BackgroundService
{
    public const int DefaultMaxConcurrency = 3;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CanvasImageQueueService> _logger;
    private readonly int _maxConcurrency;

    public CanvasImageQueueService(
        IServiceScopeFactory scopeFactory, ILogger<CanvasImageQueueService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var configured = config.GetValue<int?>("CanvasQueue:MaxConcurrency") ?? DefaultMaxConcurrency;
        _maxConcurrency = Math.Clamp(configured, 1, 8);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope0 = _scopeFactory.CreateScope();
            var db0 = scope0.ServiceProvider.GetRequiredService<DbService>();
            var recovered = db0.ResetStuckCanvasTasks();
            if (recovered > 0)
                _logger.LogInformation("[CanvasQueue] 回收上次中断的出图任务 {Count} 条", recovered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CanvasQueue] 回收中断任务失败（忽略，继续跑队列）");
        }

        _logger.LogInformation("[CanvasQueue] Service started（并发 {Concurrency} 张）", _maxConcurrency);

        var workers = new Task[_maxConcurrency];
        for (var i = 0; i < _maxConcurrency; i++)
            workers[i] = WorkerLoopAsync(stoppingToken);

        await Task.WhenAll(workers);

        _logger.LogInformation("[CanvasQueue] Service stopped");
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await RunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CanvasQueue] 执行出图任务出错");
                didWork = true; // 出错也立刻看下一条，避免卡在同一条上
            }

            if (didWork) continue;

            try
            {
                await Task.Delay(IdleDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbService>();

        var next = db.GetQueuedCanvasTasks(1).FirstOrDefault();
        if (next == null) return false;

        // 抢任务：抢不到说明别的 worker 已经处理，直接看下一条
        if (!db.TryMarkCanvasTaskRunning(next.TaskId)) return true;

        _logger.LogInformation("[CanvasQueue] 开始任务 {TaskId}（画布 {BoardId} 节点 {NodeId}）",
            next.TaskId, next.BoardId, next.NodeId);

        try
        {
            var runner = scope.ServiceProvider.GetRequiredService<CanvasImageRunner>();
            var outcome = await runner.RunAsync(next, ct);

            if (outcome.Ok)
                _logger.LogInformation("[CanvasQueue] 任务 {TaskId} 完成 → {Url}", next.TaskId, outcome.ImageUrl);
            else
                _logger.LogWarning("[CanvasQueue] 任务 {TaskId} 失败：{Error}", next.TaskId, outcome.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停机：放回队列，下次启动接着跑
            try { db.ReleaseCanvasTask(next.TaskId); } catch { }
        }
        catch (Exception ex)
        {
            db.FailCanvasTask(next.TaskId, ex.Message);
            _logger.LogError(ex, "[CanvasQueue] 任务 {TaskId} 异常", next.TaskId);
        }

        return true;
    }
}
