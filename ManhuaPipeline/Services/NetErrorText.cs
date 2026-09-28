using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ManhuaPipeline.Services;

/// <summary>
/// HTTP 异常的文本展开。
///
/// 起因：.NET 的 HttpClient 在 TLS 握手失败时，外层消息永远只有一句
/// 「The SSL connection could not be established, see inner exception.」，
/// 真正原因（证书不受信 / 吊销状态查不到 / 协议版本不匹配 / 被代理或杀软拦截 / DNS 不通）
/// 全在 InnerException 里。以前只往外带 ex.Message，出图报错就只看到那句话，没法判断
/// 到底该去查中转站证书、本机代理还是系统时间。这里把内层原因和一句排查提示一起带上。
/// </summary>
public static class NetErrorText
{
    /// <summary>把异常展开成「外层 → 内层原因（+排查提示）」的一句话。</summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder(ex.Message);

        var inner = ex.InnerException;
        var depth = 0;
        while (inner != null && depth < 3)
        {
            sb.Append(" ← ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            inner = inner.InnerException;
            depth++;
        }

        var hint = Hint(ex);
        if (!string.IsNullOrEmpty(hint)) sb.Append("（").Append(hint).Append("）");
        return sb.ToString();
    }

    /// <summary>按异常类型给一句能直接照做的排查提示；认不出来时返回 null。</summary>
    public static string? Hint(Exception ex)
    {
        var all = new List<string>();
        foreach (var e in Flatten(ex)) all.Add(e.Message);
        var text = string.Join(" | ", all);

        // 证书链相关（AuthenticationException / Win32 错误都会带这些关键字）
        if (text.Contains("RevocationStatusUnknown", StringComparison.OrdinalIgnoreCase)
            || text.Contains("revocation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("吊销", StringComparison.Ordinal))
            return "证书吊销状态查不到：多半是本机取不到 CRL/OCSP（公司网络/代理），临时办法是在 appsettings.json 里加 \"Security\": { \"SkipSslValidation\": true }";

        if (text.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PartialChain", StringComparison.OrdinalIgnoreCase))
            return "证书链不受本机信任：中转站用了自签证书或缺中间证书，也可能是火绒/360/公司网关在做 HTTPS 拦截";

        if (text.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("过期", StringComparison.Ordinal))
            return "证书不在有效期内：先确认本机系统日期/时间是否正确";

        if (text.Contains("NameMismatch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CertNameMismatch", StringComparison.OrdinalIgnoreCase))
            return "证书上的域名与请求地址不一致：检查 API 地址是不是填错了";

        if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || ex is AuthenticationException
            || Has<AuthenticationException>(ex))
            return "TLS 握手失败：常见原因是代理/杀软做了 HTTPS 拦截、TLS 版本不匹配或系统时间不对";

        if (ex is SocketException se)
        {
            if (se.SocketErrorCode == SocketError.ConnectionRefused)
                return "对方端口不通：确认 API 地址和端口（HTTPS 是 443）";
            if (se.SocketErrorCode == SocketError.HostNotFound)
                return "域名解析失败：检查 DNS 或 API 地址拼写";
            return $"套接字错误 {se.SocketErrorCode}";
        }

        if (ex is TaskCanceledException or TimeoutException)
            return "请求超时：中转站排队/网络慢，稍后重试";

        return null;
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        var cur = ex;
        var guard = 0;
        while (cur != null && guard++ < 6)
        {
            yield return cur;
            cur = cur.InnerException!;
        }
    }

    private static bool Has<T>(Exception ex) where T : Exception
    {
        foreach (var e in Flatten(ex)) if (e is T) return true;
        return false;
    }
}
