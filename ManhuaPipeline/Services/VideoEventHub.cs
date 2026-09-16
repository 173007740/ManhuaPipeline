using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace ManhuaPipeline.Services;

/// <summary>
/// 视频状态事件的订阅 / 广播中心（进程内单例）。
///
/// 为什么需要它：原来前端每个"生成中"的镜头各起一条 5 秒轮询链，打开 3 个页面就是 3 倍请求；
/// 页面一关则完全没人查，只能靠 60 秒一轮的后台巡检兜底。
/// 改成 SSE 后：页面只订阅一条长连接，由后台巡检发现状态变化后主动推给它。
///
/// 顺带解决了"没人看就不该高频查"的问题：HasSubscribers 让巡检服务在无人订阅时
/// 自动退回低频，见 VideoPollingService。
/// </summary>
public sealed class VideoEventHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subs = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>当前是否有页面正通过 SSE 订阅。巡检服务用它决定查询频率。</summary>
    public bool HasSubscribers => !_subs.IsEmpty;

    public int SubscriberCount => _subs.Count;

    private sealed class Subscriber
    {
        public int UserId { get; init; }
        public int ProjectId { get; init; }
        public Channel<string> Channel { get; init; } = default!;
    }

    /// <summary>
    /// 订阅指定项目的视频状态事件。序列里每一项都是一段可直接写入 SSE 响应的完整帧。
    /// </summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        int userId,
        int projectId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 有界队列 + 丢弃最旧：页面卡住或标签页被浏览器挂起时，不会把待发消息堆在内存里。
        // 视频状态是"最新值即全部真相"，丢掉中间态没有影响。
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _subs[id] = new Subscriber { UserId = userId, ProjectId = projectId, Channel = channel };

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(channel, heartbeatCts.Token);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            heartbeatCts.Cancel();
            _subs.TryRemove(id, out _);
            channel.Writer.TryComplete();
            // heartbeat 是靠 heartbeatCts 自行退出的，不用 await（它不持有需要释放的资源）
        }
    }

    /// <summary>
    /// 25 秒一次心跳（SSE 注释行，浏览器会忽略）。
    /// 没有它，反向代理或浏览器的空闲回收可能把长连接掐掉。
    /// </summary>
    private static async Task HeartbeatAsync(Channel<string> channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (!channel.Writer.TryWrite(": ping\n\n")) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>把某个镜头的状态变化推给正在订阅该项目的所有页面。</summary>
    public void Publish(int projectId, VideoStatusEvent evt)
    {
        if (_subs.IsEmpty) return;
        var frame = "data: " + JsonSerializer.Serialize(evt, JsonOpts) + "\n\n";
        foreach (var pair in _subs)
        {
            if (pair.Value.ProjectId != projectId) continue;
            pair.Value.Channel.Writer.TryWrite(frame);
        }
    }
}

/// <summary>一条视频状态变化事件，序列化时字段名转成 camelCase（前端按 promptId/status 读）。</summary>
public sealed record VideoStatusEvent(
    int PromptId,
    string Status,
    string LocalVideoUrl = "",
    string VideoUrl = "",
    string Error = "");
