using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 资产出图后台队列：并发执行 AssetImageTasks 里 status='queued' 的任务。
///
/// 为什么要有它：出图原来在 HTTP 请求里同步等中转接口，页面一关/一刷新连接断开，
/// ASP.NET Core 的 RequestAborted 就会把这次出图取消（图白出、资产卡不回填）。
/// 改成后台执行后与浏览器无关，关页面/刷新/换设备都会跑完，前端回来查任务状态即可。
///
/// 并发度：配置 AssetImageQueue:MaxConcurrency（默认 3，钳制在 1~8），调 1 即回到串行。
/// 并发安全：取任务靠 TryMarkAssetImageTaskRunning 的乐观更新抢占
/// （UPDATE ... WHERE TaskId=@id AND Status='queued'），同一条任务只可能被一个 worker 抢到，
/// 所以起多个 worker 同时轮询不会重复出图。中转接口若限流，把并发调回 1 即可。
/// </summary>
public class AssetImageQueueService : BackgroundService
{
    /// <summary>默认并发度（同时出图的张数）。</summary>
    public const int DefaultMaxConcurrency = 3;

    /// <summary>没活时多久看一次队列。</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssetImageQueueService> _logger;
    private readonly int _maxConcurrency;

    public AssetImageQueueService(
        IServiceScopeFactory scopeFactory, ILogger<AssetImageQueueService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var configured = config.GetValue<int?>("AssetImageQueue:MaxConcurrency") ?? DefaultMaxConcurrency;
        _maxConcurrency = Math.Clamp(configured, 1, 8);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 上次进程退出时正在跑的任务没人收尾，启动时放回队列
        try
        {
            using var scope0 = _scopeFactory.CreateScope();
            var db0 = scope0.ServiceProvider.GetRequiredService<DbService>();
            var recovered = db0.ResetStuckAssetImageTasks();
            if (recovered > 0) _logger.LogInformation("[AssetImageQueue] 回收上次中断的出图任务 {Count} 条", recovered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AssetImageQueue] 回收中断任务失败（忽略，继续跑队列）");
        }

        _logger.LogInformation("[AssetImageQueue] Service started（并发 {Concurrency} 张）", _maxConcurrency);

        // 起 N 个 worker 并行取任务。同一条任务只会被一个 worker 抢到（乐观更新），不会重复出图。
        var workers = new Task[_maxConcurrency];
        for (var i = 0; i < _maxConcurrency; i++)
            workers[i] = WorkerLoopAsync(stoppingToken);

        await Task.WhenAll(workers);

        _logger.LogInformation("[AssetImageQueue] Service stopped");
    }

    /// <summary>单个 worker 的循环：取一条跑一条，没活就歇一会。多个 worker 共享同一个任务队列。</summary>
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
                _logger.LogError(ex, "[AssetImageQueue] 执行出图任务出错");
                didWork = true; // 出错也立刻看下一条，避免卡在同一条上
            }

            if (didWork) continue;

            // 空闲时错开唤醒，避免 N 个 worker 同时打数据库
            try { await Task.Delay(IdleDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>取一条任务执行；没有任务返回 false。</summary>
    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbService>();

        var next = db.GetQueuedAssetImageTasks(1).FirstOrDefault();
        if (next == null) return false;

        // 抢任务：抢不到说明别的地方已经处理，直接看下一条
        if (!db.TryMarkAssetImageTaskRunning(next.TaskId)) return true;

        _logger.LogInformation("[AssetImageQueue] 开始任务 {TaskId} {Category}#{AssetId}（{Name}）",
            next.TaskId, next.Category, next.AssetId, next.AssetName ?? "");

        try
        {
            var runner = scope.ServiceProvider.GetRequiredService<AssetImageRunner>();
            var outcome = await runner.GenerateForTaskAsync(next, ct);

            if (outcome.Ok)
            {
                db.CompleteAssetImageTask(next.TaskId, outcome.ImageUrl ?? "", outcome.LibraryAssetId, outcome.UsedPrompt);
                _logger.LogInformation("[AssetImageQueue] 任务 {TaskId} 完成 → {Url}", next.TaskId, outcome.ImageUrl);
            }
            else
            {
                db.FailAssetImageTask(next.TaskId, outcome.Error);
                _logger.LogWarning("[AssetImageQueue] 任务 {TaskId} 失败：{Error}", next.TaskId, outcome.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停机：把任务放回队列，下次启动接着跑（running → queued，不能用 Retry，
            // 那个只接受 failed/completed，否则任务会永久卡在 running）
            try { db.ReleaseAssetImageTask(next.TaskId); } catch { }
        }
        catch (Exception ex)
        {
            db.FailAssetImageTask(next.TaskId, ex.Message);
            _logger.LogError(ex, "[AssetImageQueue] 任务 {TaskId} 异常", next.TaskId);
        }

        return true;
    }
}
