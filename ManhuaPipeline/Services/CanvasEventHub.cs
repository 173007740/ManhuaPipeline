using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace ManhuaPipeline.Services;

/// <summary>
/// 画布节点状态事件的订阅 / 广播中心（进程内单例）。
///
/// 与 <see cref="VideoEventHub"/> 完全同一套机制，只是订阅维度从「项目」换成「画布」：
/// 页面只开一条 SSE 长连接，后台出图队列（CanvasImageQueueService）跑完一张图就推一次，
/// 前端收到后直接改对应节点的 DOM —— 不用给每个节点起一条轮询链。
///
/// 队列：有界 64 + 丢弃最旧。节点状态是「最新值即全部真相」，页面卡住时丢中间态没有影响。
/// 心跳：25 秒一次注释行，避免反向代理 / 浏览器空闲回收掐断长连接。
/// </summary>
public sealed class CanvasEventHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subs = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>当前是否有页面正通过 SSE 订阅某块画布。</summary>
    public bool HasSubscribers => !_subs.IsEmpty;

    public int SubscriberCount => _subs.Count;

    private sealed class Subscriber
    {
        public int UserId { get; init; }
        public int BoardId { get; init; }
        public Channel<string> Channel { get; init; } = default!;
    }

    /// <summary>订阅指定画布的节点事件。序列里每一项都是一段可直接写入 SSE 响应的完整帧。</summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        int userId,
        int boardId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _subs[id] = new Subscriber { UserId = userId, BoardId = boardId, Channel = channel };

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
        }
    }

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

    /// <summary>把某个节点的状态变化推给正在订阅该画布的所有页面。</summary>
    public void Publish(int boardId, CanvasNodeEvent evt)
    {
        if (_subs.IsEmpty) return;
        var frame = "data: " + JsonSerializer.Serialize(evt, JsonOpts) + "\n\n";
        foreach (var pair in _subs)
        {
            if (pair.Value.BoardId != boardId) continue;
            pair.Value.Channel.Writer.TryWrite(frame);
        }
    }
}

/// <summary>
/// 一条画布节点事件。type 用来区分用途，前端按 type 分流：
///   node.status —— 节点状态变化（排队 / 生成中 / 完成 / 失败）
///   board.done  —— 一次批量运行跑完
/// 之所以带 type 而不是让前端靠字段猜，是为了以后加新事件（比如进度百分比）不用改前端判断逻辑。
/// </summary>
public sealed record CanvasNodeEvent(
    string Type,
    int NodeId,
    string Status = "",
    string ImageUrl = "",
    string Error = "");
