using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services
{
    /// <summary>
    /// 后台视频任务巡检。
    ///
    /// 频率是动态的：有页面通过 SSE 订阅时用 ActiveInterval（5 秒），
    /// 没人看时退回 IdleInterval（60 秒）。
    /// 原来"5 秒"这个频率是前端每个镜头各起一条轮询链自己查出来的，
    /// 页面一关就没人查、多开几个页面又成倍放大请求；现在查询责任收到这里统一做，
    /// 前端只负责接收推送。
    /// </summary>
    public class VideoPollingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<VideoPollingService> _logger;
        private readonly VideoEventHub _hub;

        // 自动增强扫描的节流时间戳（见 MaybeAutoEnhanceScan）
        private DateTime _lastEnhanceScan = DateTime.MinValue;

        private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

        public VideoPollingService(
            IServiceScopeFactory scopeFactory,
            ILogger<VideoPollingService> logger,
            VideoEventHub hub)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[VideoPolling] Service started");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    try
                    {
                        await PollOnce(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[VideoPolling] Poll error");
                    }
                    await Task.Delay(_hub.HasSubscribers ? ActiveInterval : IdleInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 服务停止时正常退出
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VideoPolling] Loop error, retry in 10s");
                    try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>把状态变化推给正在订阅该项目的页面（没有订阅者时是空操作）。</summary>
        private void Notify(int projectId, int promptId, string status, string? localVideoUrl = null, string? videoUrl = null, string? error = null)
        {
            try
            {
                _hub.Publish(projectId, new VideoStatusEvent(
                    promptId,
                    status,
                    localVideoUrl ?? "",
                    videoUrl ?? "",
                    error ?? ""));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VideoPolling] Publish failed for prompt {PromptId}", promptId);
            }
        }

        private async Task PollOnce(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbService>();
            var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

            var prompts = db.GetProcessingPrompts();
            if (prompts.Count == 0)
            {
                // 没有生成中的任务也照常巡检：已完成但未自动增强的视频需要补提交
                await MaybeAutoEnhanceScan(db, scope);
                return;
            }

            _logger.LogInformation("[VideoPolling] Checking {Count} tasks", prompts.Count);

            foreach (var (promptId, projectId, userId, taskId, engine) in prompts)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(taskId)) continue;

                try
                {
                    if (engine == "comfyui")
                    {
                        var comfy = scope.ServiceProvider.GetRequiredService<ComfyService>();
                        var ccfg = db.GetActiveConfig(userId, "comfyui");
                        var baseUrl = (ccfg?.ApiUrl ?? "http://127.0.0.1:8188").TrimEnd('/');
                        var (st, vidUrl, errMsg, res, tok, sd) = await comfy.QueryTaskByTaskId(taskId, baseUrl);
                        if (st == "completed")
                        {
                            string? localVideoUrl = null;
                            if (!string.IsNullOrEmpty(vidUrl))
                                localVideoUrl = await comfy.DownloadVideoToLocal(vidUrl, promptId, projectId);
                            if (string.IsNullOrEmpty(localVideoUrl))
                            {
                                var existing = db.GetPromptLight(promptId)?.LocalVideoUrl;
                                if (!string.IsNullOrEmpty(existing)) localVideoUrl = existing;
                            }
                            db.UpdatePromptVideo(promptId, vidUrl, localVideoUrl, "completed");
                            db.UpdateVideoTaskStatus(promptId, taskId, "completed", vidUrl, localVideoUrl, res, tok, sd, DateTime.Now, "success");
                            _logger.LogInformation("[VideoPolling] ComfyUI task {TaskId} completed", taskId);
                            Notify(projectId, promptId, "completed", localVideoUrl, vidUrl);
                        }
                        else if (st == "failed")
                        {
                            db.UpdatePromptStatus(promptId, "failed");
                            errMsg = VideoService.AppendRefImageInfo(errMsg, db.GetPrompt(promptId)?.ReferenceImages);
                            db.UpdateVideoTaskError(promptId, taskId, errMsg ?? "unknown error");
                            _logger.LogWarning("[VideoPolling] ComfyUI task {TaskId} failed: {Error}", taskId, errMsg);
                            Notify(projectId, promptId, "failed", error: errMsg);
                        }
                        else
                        {
                            _logger.LogInformation("[VideoPolling] ComfyUI task {TaskId} status: {Status}", taskId, st);
                        }
                        continue;
                    }

                    if (engine == "mediakit")
                    {
                        var mcfg = db.GetActiveConfig(userId, "mediakit");
                        if (mcfg == null || string.IsNullOrEmpty(mcfg.ApiKey))
                        {
                            _logger.LogWarning("[VideoPolling] No MediaKit config for user {UserId}", userId);
                            continue;
                        }
                        var enhance = scope.ServiceProvider.GetRequiredService<EnhanceService>();
                        var mbase = (mcfg.ApiUrl ?? "https://mediakit.cn-beijing.volces.com").TrimEnd('/');
                        var (mst, mvid, merr, mres) = await enhance.QueryEnhance(taskId, mcfg.ApiKey, mbase);
                        if (mst == "succeeded" || mst == "completed")
                        {
                            string? localVideoUrl = null;
                            if (!string.IsNullOrEmpty(mvid))
                            {
                                var videoSvc = scope.ServiceProvider.GetRequiredService<VideoService>();
                                localVideoUrl = await videoSvc.DownloadVideoToLocal(mvid, promptId, projectId, isEnhance: true);
                            }
                            if (string.IsNullOrEmpty(localVideoUrl))
                            {
                                var existing = db.GetPromptLight(promptId)?.LocalVideoUrl;
                                if (!string.IsNullOrEmpty(existing)) localVideoUrl = existing;
                            }
                            db.UpdatePromptVideo(promptId, mvid, localVideoUrl, "completed");
                            // 增强完成后，把增强视频地址同步到最新的视频生成任务记录上
                            db.SyncEnhanceResultToGenerationTask(promptId, mvid, localVideoUrl, mres);
                            db.UpdateVideoTaskStatus(promptId, taskId, "completed", mvid, localVideoUrl, mres, null, null, DateTime.Now, mst);
                            _logger.LogInformation("[VideoPolling] Enhance task {TaskId} completed", taskId);
                            Notify(projectId, promptId, "completed", localVideoUrl, mvid);
                        }
                        else if (mst == "failed")
                        {
                            db.UpdatePromptStatus(promptId, "failed");
                            db.UpdateVideoTaskError(promptId, taskId, merr ?? "unknown error");
                            _logger.LogWarning("[VideoPolling] Enhance task {TaskId} failed: {Error}", taskId, merr);
                            Notify(projectId, promptId, "failed", error: merr);
                        }
                        else
                        {
                            _logger.LogInformation("[VideoPolling] Enhance task {TaskId} status: {Status}", taskId, mst);
                        }
                        continue;
                    }

                    var config = db.GetActiveConfig(userId, "volcano_video");
                    if (config == null || string.IsNullOrEmpty(config.ApiKey))
                    {
                        _logger.LogWarning("[VideoPolling] No API config for user {UserId}", userId);
                        continue;
                    }

                    var apiUrl = (config.ApiUrl ?? "https://ark.cn-beijing.volces.com/api/v3").TrimEnd('/');
                    var client = http.CreateClient();

                    var req = new HttpRequestMessage(HttpMethod.Get, apiUrl + "/contents/generations/tasks/" + taskId);
                    req.Headers.Add("Authorization", "Bearer " + config.ApiKey);

                    var resp = await client.SendAsync(req, ct);
                    var json = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("[VideoPolling] API error: {Status}", resp.StatusCode);
                        continue;
                    }

                    using var doc = JsonDocument.Parse(json);
                    var status = doc.RootElement.GetProperty("status").GetString() ?? "running";
                    string? videoUrl = null;
                    string? resolution = null;
                    double? duration = null;
                    int? usageTokens = null;
                    int? seed = null;

                    if (status == "succeeded" || status == "completed")
                    {
                        if (doc.RootElement.TryGetProperty("content", out var content))
                        {
                            if (content.TryGetProperty("video_url", out var vu))
                                videoUrl = vu.GetString();
                        }
                        if (doc.RootElement.TryGetProperty("resolution", out var res))
                            resolution = res.GetString();
                        if (doc.RootElement.TryGetProperty("duration", out var dur))
                        {
                            try { duration = dur.GetDouble(); } catch { }
                        }
                        if (doc.RootElement.TryGetProperty("seed", out var sd))
                            seed = sd.GetInt32();
                        if (doc.RootElement.TryGetProperty("usage", out var usage))
                        {
                            if (usage.TryGetProperty("total_tokens", out var tt))
                                usageTokens = tt.GetInt32();
                        }

                        // 下载视频到本地
                        string? localVideoUrl = null;
                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            try
                            {
                                var videoSvc = scope.ServiceProvider.GetRequiredService<VideoService>();
                                localVideoUrl = await videoSvc.DownloadVideoToLocal(videoUrl, promptId, projectId);
                            }
                            catch (Exception dlEx)
                            {
                                _logger.LogWarning("[VideoPolling] Download failed: {Msg}", dlEx.Message);
                            }
                        }
                        if (string.IsNullOrEmpty(localVideoUrl))
                        {
                            var existing = db.GetPromptLight(promptId)?.LocalVideoUrl;
                            if (!string.IsNullOrEmpty(existing)) localVideoUrl = existing;
                        }
                        db.UpdatePromptVideo(promptId, videoUrl, localVideoUrl, "completed");
                        db.UpdateVideoTaskStatus(promptId, taskId, "completed", videoUrl, localVideoUrl, resolution, usageTokens, seed, DateTime.Now, status, duration);
                        _logger.LogInformation("[VideoPolling] Task {TaskId} completed", taskId);
                        Notify(projectId, promptId, "completed", localVideoUrl, videoUrl);

                        // 生成完成后，若配置了自动画质增强则自动提交
                        await SubmitAutoEnhance(db, scope, userId, promptId, projectId, videoUrl);
                    }
                    else if (status == "failed")
                    {
                        string? errorMsg = null;
                        if (doc.RootElement.TryGetProperty("error", out var err))
                            errorMsg = err.GetString();
                        errorMsg = VideoService.AppendRefImageInfo(errorMsg, db.GetPrompt(promptId)?.ReferenceImages);
                        db.UpdatePromptStatus(promptId, "failed");
                        db.UpdateVideoTaskError(promptId, taskId, errorMsg ?? "unknown error");
                        _logger.LogWarning("[VideoPolling] Task {TaskId} failed: {Error}", taskId, errorMsg);
                        Notify(projectId, promptId, "failed", error: errorMsg);
                    }
                    else
                    {
                        _logger.LogInformation("[VideoPolling] Task {TaskId} status: {Status}", taskId, status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VideoPolling] Error polling task {PromptId}", promptId);
                }
            }
            // 巡检：视频已完成但未自动增强的，提交增强
            await MaybeAutoEnhanceScan(db, scope);
        }

        private async Task SubmitAutoEnhance(DbService db, IServiceScope scope, int userId, int promptId, int projectId, string? videoUrl)
        {
            string? resolution = null;
            int promptDur = 11;
            try
            {
                if (string.IsNullOrEmpty(videoUrl) || !videoUrl.StartsWith("https://")) return;
                // 防重：已有进行中/已完成的增强任务则跳过
                var hasEnhance = db.GetVideoTasks(projectId)
                    .Any(t => t.PromptId == promptId && t.Engine == "mediakit"
                        && (t.Status == "enhancing" || t.Status == "completed" || t.Status == "succeeded"));
                if (hasEnhance) return;
                var mcfg = db.GetActiveConfig(userId, "mediakit");
                if (mcfg == null || !mcfg.AutoEnhance || string.IsNullOrEmpty(mcfg.ApiKey)) return;

                resolution = (mcfg.ModelName ?? "1080p").Trim();
                if (resolution != "720p" && resolution != "1080p" && resolution != "2k") resolution = "1080p";
                promptDur = db.GetPrompts(projectId).FirstOrDefault(x => x.PromptId == promptId)?.Duration ?? 11;

                var enhance = scope.ServiceProvider.GetRequiredService<EnhanceService>();
                var baseUrl = string.IsNullOrEmpty(mcfg.ApiUrl) ? enhance.DefaultBaseUrl : mcfg.ApiUrl.TrimEnd('/');
                var taskId = await enhance.SubmitEnhance(videoUrl, mcfg.ApiKey, baseUrl, resolution);
                db.SaveVideoTask(new VideoGenerationTask
                {
                    ProjectId = projectId,
                    PromptId = promptId,
                    TaskId = taskId,
                    Engine = "mediakit",
                    Status = "enhancing",
                    RequestDuration = promptDur,
                    RequestRatio = resolution,
                    CreatedAt = DateTime.Now
                });
                db.UpdatePromptStatus(promptId, "enhancing");
                _logger.LogInformation("[VideoPolling] Auto enhance submitted for prompt {PromptId}, task {TaskId}, resolution {Resolution}", promptId, taskId, resolution);
                // 让页面立刻从"完成"切到"增强中"，不用等下一轮巡检
                Notify(projectId, promptId, "enhancing", error: null, videoUrl: videoUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VideoPolling] Auto enhance failed for prompt {PromptId}: {Msg}", promptId, ex.Message);
                try
                {
                    db.SaveVideoTask(new VideoGenerationTask
                    {
                        ProjectId = projectId,
                        PromptId = promptId,
                        TaskId = "auto-enhance-failed-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"),
                        Engine = "mediakit",
                        Status = "failed",
                        RequestDuration = promptDur,
                        RequestRatio = resolution ?? "1080p",
                        ErrorMessage = "自动增强失败: " + ex.Message,
                        CreatedAt = DateTime.Now
                    });
                }
                catch { }
            }
        }

        /// <summary>
        /// 自动增强扫描的节流入口。
        ///
        /// 它不能跟着 5 秒的巡检节奏跑：待增强列表可能有几百条（实测 532 条），
        /// 每轮都要逐条查历史任务表并读配置，巡检提频 12 倍会把这部分开销一起放大。
        /// 增强本身不需要秒级响应，所以固定按 60 秒扫一次，与巡检频率解耦。
        /// </summary>
        private async Task MaybeAutoEnhanceScan(DbService db, IServiceScope scope)
        {
            var now = DateTime.Now;
            if (now - _lastEnhanceScan < TimeSpan.FromSeconds(60)) return;
            _lastEnhanceScan = now;
            await AutoEnhanceScan(db, scope);
        }

        private async Task AutoEnhanceScan(DbService db, IServiceScope scope)
        {
            try
            {
                var pending = db.GetPendingAutoEnhancePrompts();
                if (pending.Count == 0) return;
                _logger.LogInformation("[VideoPolling] Auto enhance scan: {Count} pending", pending.Count);
                foreach (var (promptId, projectId, userId, videoUrl, duration) in pending)
                {
                    await SubmitAutoEnhance(db, scope, userId, promptId, projectId, videoUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VideoPolling] Auto enhance scan error: {Msg}", ex.Message);
            }
        }

    }
}
