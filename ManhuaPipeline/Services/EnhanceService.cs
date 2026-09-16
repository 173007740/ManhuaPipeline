using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ManhuaPipeline.Services
{
    /// <summary>
    /// 火山引擎 AI MediaKit 画质增强（大模型版）：480p/720p 视频增强到 1080p/2k。
    /// API 参考：https://docs.volcengine.com/docs/6448/2407223
    /// </summary>
    public class EnhanceService
    {
        private readonly HttpClient _http;
        public EnhanceService(HttpClient http) { _http = http; }

        public string DefaultBaseUrl => "https://mediakit.cn-beijing.volces.com";

        /// <summary>提交画质增强任务，返回增强任务 task_id。</summary>
        public async Task<string> SubmitEnhance(string sourceVideoUrl, string apiKey, string baseUrl, string resolution)
        {
            var client = _http;
            if (!await IsUrlReachable(sourceVideoUrl, client))
                throw new Exception("源视频链接不可访问（可能已过期），请先重新生成该视频再增强");

            var payload = new { video_url = sourceVideoUrl, resolution };
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/v1/tools/enhance-video-generative");
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("画质增强提交失败: " + resp.StatusCode + " " + Truncate(body, 300));

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("task_id", out var tid))
            {
                var v = tid.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            throw new Exception("画质增强提交失败: 响应中没有 task_id: " + Truncate(body, 300));
        }

        /// <summary>查询增强任务状态，返回 (status, videoUrl, error, resolution)。</summary>
        public async Task<(string Status, string? VideoUrl, string? Error, string? Resolution)> QueryEnhance(string taskId, string apiKey, string baseUrl)
        {
            var client = _http;
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/api/v1/tasks/" + Uri.EscapeDataString(taskId));
            req.Headers.Add("Authorization", "Bearer " + apiKey);

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return ("failed", null, "查询失败: " + resp.StatusCode + " " + Truncate(body, 300), null);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "running" : "running";
            string? videoUrl = null, error = null, resolution = null;
            if (status == "succeeded" || status == "completed")
            {
                if (root.TryGetProperty("result", out var result))
                {
                    if (result.TryGetProperty("video_url", out var vu)) videoUrl = vu.GetString();
                    if (result.TryGetProperty("resolution", out var res)) resolution = res.GetString();
                }
            }
            else if (status == "failed")
            {
                if (root.TryGetProperty("error", out var er))
                {
                    try { error = er.GetString(); } catch { error = er.ToString(); }
                }
                if (string.IsNullOrEmpty(error) && root.TryGetProperty("message", out var msg)) error = msg.GetString();
            }
            return (status, videoUrl, error, resolution);
        }

        private static async Task<bool> IsUrlReachable(string url, HttpClient client)
        {
            try
            {
                var head = new HttpRequestMessage(HttpMethod.Head, url);
                var resp = await client.SendAsync(head);
                if (resp.IsSuccessStatusCode) return true;
                var get = new HttpRequestMessage(HttpMethod.Get, url);
                var resp2 = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                return resp2.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static string Truncate(string? s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }
    }
}
