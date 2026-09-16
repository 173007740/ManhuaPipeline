#!/usr/bin/env node
/**
 * 本地识图代理
 * 作用:Codex 请求里的图片(image_url / input_image)自动用 vision.js 转成文字描述,
 *       再转发给真正的后端(deepseek 中转),让纯文本后端也能"看图"。
 * 用法:node vision_proxy.js  (默认监听 57322,转发到 127.0.0.1:57321/v1)
 */
const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");

const LISTEN_PORT = parseInt(process.env.VISION_PROXY_PORT || "57322", 10);
const UPSTREAM_BASE = process.env.VISION_PROXY_UPSTREAM || "http://127.0.0.1:57321/v1";
const VISION_JS = process.env.VISION_JS || "C:\\Users\\power\\.codex\\skills\\claude-vision-skill\\vision.js";
const DESCRIBE_PROMPT =
  "请用中文详细描述这张图片的内容，重点说明画面中的人物、动作、表情、场景、道具、文字等细节。只描述图片里实际存在的内容，不要猜测。";

function describeImage(source, isUrl) {
  const args = isUrl ? ["--url", source, DESCRIBE_PROMPT] : [source, DESCRIBE_PROMPT];
  const out = execFileSync("node", [VISION_JS, ...args], {
    encoding: "utf8",
    timeout: 90000,
    windowsHide: true,
  });
  return (out || "").trim();
}

function imageToText(source) {
  if (/^https?:\/\//i.test(source)) return describeImage(source, true);
  if (/^data:/i.test(source)) {
    const comma = source.indexOf(",");
    const meta = source.slice(5, comma);
    const b64 = source.slice(comma + 1);
    const m = /image\/([a-zA-Z0-9.+-]+)/.exec(meta);
    let ext = m ? m[1].toLowerCase() : "png";
    if (ext === "jpeg") ext = "jpg";
    const tmp = path.join(os.tmpdir(), "vp-" + Date.now() + "-" + Math.random().toString(36).slice(2) + "." + ext);
    fs.writeFileSync(tmp, Buffer.from(b64, "base64"));
    try {
      return describeImage(tmp, false);
    } finally {
      try { fs.unlinkSync(tmp); } catch {}
    }
  }
  return describeImage(source, false);
}

function convertContent(content) {
  if (!Array.isArray(content)) return content;
  const out = [];
  for (const part of content) {
    if (part && typeof part === "object" && (part.type === "input_image" || part.type === "image_url")) {
      const raw = part.image_url;
      const url = raw && typeof raw === "object" ? raw.url : raw;
      if (typeof url === "string" && url) {
        let desc;
        try {
          desc = imageToText(url);
        } catch (e) {
          desc = "(图片识别失败: " + (e.message || String(e)).slice(0, 200) + ")";
        }
        out.push({ type: "input_text", text: "[图片内容] " + desc });
        continue;
      }
    }
    out.push(part);
  }
  return out;
}

function convertPayload(body) {
  if (!body) return body;
  if (Array.isArray(body.input)) {
    body.input = body.input.map((item) => {
      if (item && typeof item === "object" && Array.isArray(item.content)) {
        return Object.assign({}, item, { content: convertContent(item.content) });
      }
      return item;
    });
  }
  if (Array.isArray(body.messages)) {
    body.messages = body.messages.map((item) => {
      if (item && typeof item === "object" && Array.isArray(item.content)) {
        return Object.assign({}, item, { content: convertContent(item.content) });
      }
      return item;
    });
  }
  return body;
}

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    let payload = null;
    const raw = Buffer.concat(chunks);
    const hasJson = /application\/json/i.test(req.headers["content-type"] || "");
    if (hasJson && raw.length) {
      try {
        payload = JSON.parse(raw.toString("utf8"));
      } catch (e) {
        res.writeHead(400, { "content-type": "application/json" });
        res.end(JSON.stringify({ error: { message: "请求体不是合法 JSON: " + e.message } }));
        return;
      }
    }
    if (payload) {
      const before = JSON.stringify(payload);
      convertPayload(payload);
      console.log("[vision_proxy] " + req.method + " " + req.url + " size=" + before.length);
    }
    const bodyOut = payload ? Buffer.from(JSON.stringify(payload), "utf8") : raw;

    const target = new URL(UPSTREAM_BASE.replace(/\/$/, "") + req.url);
    const headers = Object.assign({}, req.headers);
    headers["content-length"] = bodyOut.length;
    headers["host"] = target.host;

    const upReq = http.request(
      target,
      { method: req.method, headers: headers },
      (upRes) => {
        res.writeHead(upRes.statusCode || 502, upRes.headers);
        upRes.pipe(res);
      }
    );
    upReq.on("error", (e) => {
      res.writeHead(502, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: { message: "上游连接失败: " + e.message } }));
    });
    if (bodyOut.length) upReq.write(bodyOut);
    upReq.end();
  });
});

server.listen(LISTEN_PORT, "127.0.0.1", () => {
  console.log("[vision_proxy] listening on 127.0.0.1:" + LISTEN_PORT + " -> " + UPSTREAM_BASE);
  console.log("[vision_proxy] vision.js: " + VISION_JS);
});
