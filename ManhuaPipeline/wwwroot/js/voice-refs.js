/* 角色音色参考（提示词里的音色编号）
 *
 * 给项目的角色绑一段参考音频，生成提示词时该角色开口会带上音色编号——
 * SD 与 H3 都认，编号同源，由 VoiceRefResolver 保证。
 * 写法按引擎分：H3 写 <Audio N>（落在说话句里），SD 写 @音频N
 * （内联在【时间轴分镜】的台词行里——七段式栏位是写死的，不能为音色新增段落）。
 * 提交时两边都真的带音频：H3 进 ComfyUI 的 ref_audios 槽位，
 * SD 进火山方舟 content 里的 role=reference_audio，顺序都是这个编号——编错就是张冠李戴。
 * 段数上限：H3 与 SD 2.0 最多 3 段（≤15s），SD 2.5 最多 10 段（≤30s）。
 * 另：SD 2.0 不允许「只有音频没有图」，那种镜头提交时会自动不带音色。
 *
 * 这个面板原来只长在 project.html 里，新页面（skill-studio）没有入口，
 * 于是在新页面绑了音色还得跑回旧页面去配。抽出来两个页面共用：
 * 项目 id 由调用方传入，不读任何页面自己的全局变量。
 *
 * 弹窗用的是全站那套 .modal-overlay / .modal（dashboard.html「新建漫剧」同一颗：
 * 切角卡片、外发光、右上角 ×、底部按钮右对齐），不再自带一套内联样式。
 * 宿主页面若没覆写过 .modal，会落到 admin-theme.css 的基础款（圆角卡片），同样是站内风格。
 *
 * 用法：VoiceRefs.open(projectId)
 */
(function () {
    function escHtml(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c];
        });
    }

    // 上传/删除后列表会整体重建，提示语得先存下来，重建完再贴回去
    var pendingMsg = null;
    var pid = 0;

    function open(projectId) {
        pid = parseInt(projectId, 10) || 0;
        if (pid <= 0) { alert("先在页面上选好项目，再配音色"); return; }

        $("#voiceRefsModal").remove();
        var h = '<div id="voiceRefsModal" class="modal-overlay" style="display:flex;">';
        h += '<div class="modal" style="width:min(680px,96vw);">';
        h += '<header>';
        h += '<h3>角色音色参考</h3>';
        h += '<button class="modal-close" id="voiceRefsCloseBtn" title="关闭">&times;</button>';
        h += '</header>';
        h += '<p style="color:#8da6ad;font-size:12px;line-height:1.8;margin:10px 0 0;">给角色绑定一段参考音频后，生成提示词时该角色说话会参考它的<b style="color:#05e8f0;">音色与说话方式</b>（只借音色，不会复制音频里的原话与音乐）。<b>SD 与 H3 都生效</b>：H3 写成 &lt;Audio N&gt;，SD 写成 @音频N（标在【时间轴分镜】的台词行里），编号同源。建议用单一说话人的干净人声、2~15 秒；一个镜头最多自动带 3 段（SD 2.5 可到 10 段），按该镜出场角色匹配。</p>';
        h += '<p style="color:#f0b55d;background:rgba(240,181,93,0.08);border-left:3px solid #f0b55d;border-radius:4px;padding:8px 10px;font-size:12px;line-height:1.8;margin:10px 0 0;">用法顺序：<b>①</b> 在这里绑定音色 → <b>②</b> 重新生成该镜头的提示词（音色只有这一步才会写进提示词，SD / H3 都一样）→ <b>③</b> 再生成视频。已经生成过提示词的镜头必须重新生成一次，否则不会带音色。</p>';
        h += '<p style="color:#8da6ad;font-size:12px;line-height:1.8;margin:10px 0 0;">角色名要和分镜「角色」字段对得上（分镜里写成 @CHR-杨彦刚 的，这里填「杨彦刚」）——对不上就关联不到镜头。</p>';
        h += '<div id="voiceRefsBody" style="color:#8da6ad;font-size:13px;margin-top:18px;">加载中…</div>';
        h += '<div class="modal-actions"><button class="btn" id="voiceRefsCancelBtn">关闭</button></div>';
        h += '</div></div>';
        $("body").append(h);
        $("#voiceRefsCloseBtn,#voiceRefsCancelBtn").on("click", close);
        $("#voiceRefsModal").on("click", function (e) { if (e.target === this) close(); });
        load();
    }

    function close() { $("#voiceRefsModal").remove(); }

    function load() {
        $.get("/api/project/" + pid + "/video/voices", function (d) {
            var voices = (d && d.voices) || [];
            var chars = (d && d.characters) || [];
            var h = "";
            if (!voices.length) {
                h += '<div style="padding:10px 12px;background:rgba(148,163,184,0.08);border-radius:4px;">还没有为任何角色绑定音色参考音频。</div>';
            } else {
                h += '<table>';
                h += '<thead><tr><th>角色</th><th>参考音频</th><th style="width:150px;">操作</th></tr></thead><tbody>';
                for (var i = 0; i < voices.length; i++) {
                    var v = voices[i];
                    h += '<tr>';
                    h += '<td style="font-weight:600;">' + escHtml(v.characterName || "") + '</td>';
                    h += '<td><audio controls preload="none" src="' + escHtml(v.audioUrl || "") + '" style="height:32px;max-width:240px;vertical-align:middle;"></audio></td>';
                    h += '<td style="white-space:nowrap;"><button class="btn btn-sm voice-replace-btn" data-name="' + escHtml(v.characterName || "") + '">替换</button> '
                        + '<button class="btn btn-sm btn-danger voice-del-btn" data-voice-id="' + v.voiceId + '" data-name="' + escHtml(v.characterName || "") + '">删除</button></td>';
                    h += '</tr>';
                }
                h += '</tbody></table>';
            }
            h += '<div style="border-top:1px solid #183033;padding-top:16px;margin-top:16px;">';
            h += '<div style="color:#e4fbff;font-size:13px;font-weight:600;">绑定 / 替换音色</div>';
            h += '<label>角色名（可下拉选择，自动取自项目角色资产）</label>';
            h += '<input id="voiceCharacterInput" list="voiceCharacterList" placeholder="输入或选择角色名">';
            h += '<datalist id="voiceCharacterList">';
            for (var j = 0; j < chars.length; j++) h += '<option value="' + escHtml(chars[j]) + '"></option>';
            h += '</datalist>';
            h += '<label>音频文件（2~15 秒的干净人声）</label>';
            h += '<input type="file" id="voiceFileInput" accept="audio/*,.wav,.mp3,.m4a,.flac,.ogg" style="color:#8da6ad;">';
            h += '<div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:16px;">';
            h += '<button class="btn btn-primary" id="voiceUploadBtn">上传绑定</button>';
            h += '<button class="btn" id="voicePickLibBtn" title="从音色库里选一个已保存的音色直接绑定给该角色，不用每次重新上传">从音色库选择</button>';
            h += '<a href="voice-library.html" target="_blank" class="btn" style="text-decoration:none;" title="打开音色库页面管理音色">音色库</a>';
            h += '</div>';
            h += '<div id="voiceUploadMsg" style="font-size:12px;margin-top:10px;"></div>';
            h += '</div>';
            $("#voiceRefsBody").html(h);
            $("#voiceRefsBody .voice-del-btn").on("click", function () {
                remove(parseInt($(this).attr("data-voice-id"), 10), $(this).attr("data-name") || "");
            });
            $("#voiceRefsBody .voice-replace-btn").on("click", function () {
                $("#voiceCharacterInput").val($(this).attr("data-name") || "");
                $("#voiceFileInput").trigger("click");
            });
            $("#voiceUploadBtn").on("click", upload);
            $("#voicePickLibBtn").on("click", openLibraryPicker);
            if (pendingMsg) { $("#voiceUploadMsg").html(pendingMsg); pendingMsg = null; }
        }).fail(function (xhr) {
            var msg = "读取音色参考失败";
            try { msg = xhr.responseJSON.message || msg; } catch (e) { }
            $("#voiceRefsBody").html('<div style="color:#ff3852;">' + escHtml(msg) + '</div>');
        });
    }

    function upload() {
        var name = ($("#voiceCharacterInput").val() || "").trim();
        var input = $("#voiceFileInput")[0];
        if (!name) { $("#voiceUploadMsg").html('<span style="color:#f0b55d;">请先填写角色名</span>'); return; }
        if (!input || !input.files || !input.files.length) { $("#voiceUploadMsg").html('<span style="color:#f0b55d;">请选择音频文件</span>'); return; }
        var fd = new FormData();
        fd.append("characterName", name);
        fd.append("file", input.files[0]);
        var b = $("#voiceUploadBtn");
        b.prop("disabled", true).text("上传中…");
        $("#voiceUploadMsg").html('<span style="color:#8da6ad;">正在上传…</span>');
        $.ajax({ url: "/api/project/" + pid + "/video/voices", type: "POST", data: fd, processData: false, contentType: false })
            .done(function () {
                pendingMsg = '<span style="color:#6af7ff;">已绑定「' + escHtml(name) + '」。<b>下一步：</b>重新生成该镜头的 H3 提示词，音色才会写进去。</span>';
                load();
            })
            .fail(function (xhr) {
                var msg = "上传失败";
                try { msg = xhr.responseJSON.message || msg; } catch (e) { }
                $("#voiceUploadMsg").html('<span style="color:#ff3852;">' + escHtml(msg) + '</span>');
            })
            .always(function () { b.prop("disabled", false).text("上传绑定"); });
    }

    function openLibraryPicker() {
        var name = ($("#voiceCharacterInput").val() || "").trim();
        if (!name) {
            $("#voiceUploadMsg").html('<span style="color:#f0b55d;">先填写或选择角色名，再从音色库里挑音色</span>');
            return;
        }
        var mask = $('<div class="modal-overlay" style="display:flex;">'
            + '<div class="modal" style="width:min(620px,96vw);">'
            + '<header><h3><span class="material-icons" style="font-size:inherit;vertical-align:middle">music_note</span> 为「' + escHtml(name) + '」选择音色</h3>'
            + '<button class="modal-close" id="voiceLibClose" title="关闭">&times;</button></header>'
            + '<div style="display:flex;gap:10px;align-items:center;margin:12px 0;">'
            + '<label style="margin:0;">分类</label>'
            + '<select id="voiceLibCat" style="width:auto;margin:0;padding:8px 10px;">'
            + '<option value="">全部</option><option value="动漫">动漫</option><option value="游戏">游戏</option>'
            + '<option value="写实">写实</option><option value="仙侠">仙侠</option></select>'
            + '<span id="voiceLibCatTip" style="font-size:11px;color:#5c7474;"></span></div>'
            + '<input id="voiceLibSearch" placeholder="搜索音色名 / 标签 / 备注">'
            + '<div id="voiceLibList" style="color:#8da6ad;font-size:13px;overflow:auto;max-height:46vh;margin-top:12px;">加载中…</div>'
            + '</div></div>');
        $("body").append(mask);
        mask.on("click", function (e) { if (e.target === this) mask.remove(); });
        mask.find("#voiceLibClose").on("click", function () { mask.remove(); });
        var timer = null;
        mask.find("#voiceLibSearch").on("input", function () {
            clearTimeout(timer);
            timer = setTimeout(function () { renderLibList(name, mask); }, 250);
        });
        mask.find("#voiceLibCat").on("change", function () { renderLibList(name, mask); });
        // 默认按当前项目的资产库类型过滤，省得每次手动切
        $.get("/api/project/" + pid, function (p) {
            if (p && p.libraryCategory) {
                mask.find("#voiceLibCat").val(p.libraryCategory);
                mask.find("#voiceLibCatTip").text("已按本项目类型「" + p.libraryCategory + "」筛选");
            }
        }).always(function () { renderLibList(name, mask); });
    }

    function renderLibList(name, mask) {
        var q = mask.find("#voiceLibSearch").val() || "";
        var cat = mask.find("#voiceLibCat").val() || "";
        $.get("/api/voice-library", { search: q, category: cat }, function (d) {
            var items = (d && d.items) || [];
            var box = mask.find("#voiceLibList").empty();
            if (!items.length) {
                box.html('<div style="padding:16px;text-align:center;">没有匹配的音色。<a href="voice-library.html" target="_blank" style="color:#05e8f0;">去音色库添加 →</a></div>');
                return;
            }
            for (var i = 0; i < items.length; i++) {
                var v = items[i];
                var row = $('<div style="display:flex;align-items:center;gap:10px;padding:10px 0;border-bottom:1px solid #183033;"></div>');
                row.append('<div style="flex:1;min-width:0;">'
                    + '<div style="color:#e4fbff;font-size:13px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">'
                    + escHtml(v.name)
                    + (v.category ? ' <span style="font-size:11px;color:#05e8f0;">' + escHtml(v.category) + '</span>' : '')
                    + (v.tag ? ' <span style="font-size:11px;color:#6af7ff;">' + escHtml(v.tag) + '</span>' : '') + '</div>'
                    + (v.note ? '<div style="font-size:11px;color:#8da6ad;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + escHtml(v.note) + '</div>' : '')
                    + '<audio controls preload="none" src="' + escHtml(v.audioUrl) + '" style="height:28px;max-width:220px;margin-top:4px;"></audio></div>');
                row.append('<button class="btn btn-primary btn-sm btn-lib-pick" data-id="' + v.id + '" data-vname="' + escHtml(v.name) + '">用这个</button>');
                box.append(row);
            }
            box.find(".btn-lib-pick").on("click", function () {
                bindFromLibrary(name, parseInt($(this).attr("data-id"), 10), $(this).attr("data-vname") || "", mask);
            });
        }).fail(function () {
            mask.find("#voiceLibList").html('<div style="color:#ff3852;">读取音色库失败</div>');
        });
    }

    // 绑定时后端会复制一份音频到项目音色目录，所以以后删库里的音色不影响这里
    function bindFromLibrary(name, libraryId, voiceName, mask) {
        $.ajax({
            url: "/api/project/" + pid + "/video/voices/from-library",
            type: "POST", contentType: "application/json",
            data: JSON.stringify({ characterName: name, libraryId: libraryId })
        }).done(function () {
            mask.remove();
            pendingMsg = '<span style="color:#6af7ff;">已从音色库把「' + escHtml(voiceName) + '」绑定给 ' + escHtml(name)
                + '。<b>下一步：</b>重新生成该镜头的 H3 提示词，音色才会写进去。</span>';
            load();
        }).fail(function (xhr) {
            var msg = "绑定失败";
            try { msg = xhr.responseJSON.message || msg; } catch (e) { }
            alert(msg);
        });
    }

    function remove(voiceId, name) {
        if (!confirm("确定删除角色「" + name + "」的音色参考音频？")) return;
        $.ajax({ url: "/api/project/" + pid + "/video/voices/" + voiceId, type: "DELETE" })
            .done(function () {
                pendingMsg = '<span style="color:#8da6ad;">已删除「' + escHtml(name) + '」的音色参考。</span>';
                load();
            })
            .fail(function (xhr) {
                var msg = "删除失败";
                try { msg = xhr.responseJSON.message || msg; } catch (e) { }
                alert(msg);
            });
    }

    window.VoiceRefs = { open: open };
})();
