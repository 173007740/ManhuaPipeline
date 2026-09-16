function loadPrompts(){
  var statusMap={pending:"排队等待",queued:"排队等待",running:"正在生成",processing:"正在生成",succeeded:"成功",completed:"成功",failed:"失败",expired:"任务超时",null:"等待生成"};
  var statusColor={pending:"#f59e0b",queued:"#f59e0b",running:"#3b82f6",processing:"#3b82f6",succeeded:"#10b981",completed:"#10b981",failed:"#ef4444",expired:"#6b7280"};
  function fmtStatus(s){var label=statusMap[s]||s||"等待生成";var color=statusColor[s]||"#94a3b8";return "<span style='background:"+color+"22;color:"+color+";border:1px solid "+color+"44;border-radius:4px;padding:2px 8px;font-size:12px;font-weight:500;'>"+label+"</span>"}var sb="<div style='background:rgba(124,58,237,0.1);border:1px solid rgba(124,58,237,0.25);border-radius:8px;padding:12px 16px;margin-bottom:12px;display:flex;align-items:center;gap:12px;flex-wrap:wrap;'>";sb+="<span style='color:#a78bfa;font-weight:600;font-size:13px;'>⚙️ 视频参数</span>";sb+="<label style='color:#cbd5e1;font-size:13px;'>时长 <select id=vidDuration style='background:#1e293b;color:#e2e8f0;border:1px solid #475569;border-radius:4px;padding:4px 8px;font-size:13px;'><option value=5>5秒</option><option value=11 selected>11秒</option><option value=15>15秒</option><option value=20>20秒</option><option value=30>30秒</option></select></label>";sb+="<label style='color:#cbd5e1;font-size:13px;'>比例 <select id=vidRatio style='background:#1e293b;color:#e2e8f0;border:1px solid #475569;border-radius:4px;padding:4px 8px;font-size:13px;'><option value=16:9 selected>16:9</option><option value=9:16>9:16</option><option value=1:1>1:1</option></select></label>";sb+="<label style='color:#cbd5e1;font-size:13px;'><input type=checkbox id=vidWatermark style='margin-right:4px;'>水印</label>";sb+="<label style='color:#cbd5e1;font-size:13px;'><input type=checkbox id=vidAudio checked style='margin-right:4px;'>音频</label><label style='color:#cbd5e1;font-size:13px;'>分辨率 <select id=vidResolution style='background:#1e293b;color:#e2e8f0;border:1px solid #475569;border-radius:4px;padding:4px 8px;font-size:13px;'><option value='720p' selected>720p</option><option value='1080p'>1080p</option></select></label>";sb+="<button class=btn-primary id=btnBatchVideo style='margin-left:auto;font-size:13px;padding:6px 14px;background:#7c3aed;color:#fff;border:none;border-radius:6px;cursor:pointer;'><i class='fas fa-film'></i> 批量生成视频</button><a href='video-tasks.html?id='+currentProjectId+' style='font-size:12px;color:#a78bfa;text-decoration:none;margin-left:8px;'><i class='fas fa-list'></i> 任务记录</a>";sb+="</div>";$.get("/api/project/"+currentProjectId+"/prompt",function(d){window.promptData=d;if(!d||d.length===0){$("#stageResult").html("<p style='color:#94a3b8;padding:20px;'>暂无提示词数据</p>");return}var h=sb+"<table class='storyboard-table prompt-table' id='promptTable'><thead><tr><th style='width:40px;'>#</th><th style='width:200px;'>集数/单元</th><th style='width:60px;'>镜头</th><th style='width:400px;'>提示词内容</th><th style='width:60px;'>状态</th><th style='width:130px;'>操作</th><th style='width:150px;'>参考图</th><th style='width:450px;'>视频</th></tr></thead><tbody>";for(var i=0;i<d.length;i++){var p=d[i];var label="【第"+p.episodeNumber+"集】";if(p.unitName)label+=" 【单元"+p.unitName+"】";var shotLabel=p.shotLabel||(i+1);var actionBtn="";if(p.videoUrl){actionBtn="<button class='btn btn-small' onclick='genVideo(this,"+p.promptId+")' style='font-size:11px;background:#7c3aed;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-sync-alt' style='font-size:16px;'></i><span>重新生成</span></button>"}else if(p.status==="processing"||p.status==="running"||p.status==="pending"){actionBtn="<button class='btn btn-small' data-pid='"+p.promptId+"' onclick='pollVideoStatus(this,"+p.promptId+")' style='font-size:11px;background:#7c3aed;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-spinner fa-pulse' style='font-size:16px;'></i><span>生成中</span></button>"}else if(p.status==="completed"){actionBtn="<button class='btn btn-small btn-primary' onclick='genVideo(this,"+p.promptId+")' style='font-size:11px;background:#7c3aed;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-video' style='font-size:16px;'></i><span>生成视频</span></button>"}var refHtml="";var refImgs=[];try{refImgs=p.referenceImages?JSON.parse(p.referenceImages):[]}catch(e){}refHtml+="<div style=\'display:flex;flex-wrap:wrap;gap:4px;justify-content:center;\'>";for(var si=0;si<9;si++){if(si<refImgs.length&&refImgs[si]){refHtml+="<div style=\'position:relative;width:40px;height:40px;\'><img src=\'"+refImgs[si]+"\' style=\'width:40px;height:40px;object-fit:cover;border-radius:4px;cursor:pointer;\' onclick=\'window.open(\""+refImgs[si]+"\")\'><div onclick=\'clearRefSlot("+p.promptId+","+si+")\' style=\'position:absolute;top:-4px;right:-4px;width:14px;height:14px;border-radius:50%;background:#ef4444;color:#fff;font-size:10px;line-height:14px;text-align:center;cursor:pointer;\'>x</div></div>"}else{refHtml+="<label style=\'display:flex;align-items:center;justify-content:center;width:40px;height:40px;border:1px dashed rgba(255,255,255,0.2);border-radius:4px;cursor:pointer;color:rgba(255,255,255,0.3);font-size:18px;\' title=\'上传参考图\'><input type=file accept=\'image/*\' style=\'display:none\' onchange=\'uploadRefSlot(this,"+p.promptId+","+si+")\'>+</label>"}}refHtml+="</div>";var editBtn="<button class='btn btn-small' onclick='editPrompt(this,"+p.promptId+")' style='font-size:11px;background:#475569;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-pen' style='font-size:16px;'></i><span>编辑</span></button>";var deleteBtn="<button class='btn btn-small' onclick='deletePrompt(this,"+p.promptId+")' style='font-size:11px;background:#ef4444;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-trash' style='font-size:16px;'></i><span style='font-size:11px;'>删除</span></button>"var shotLabelBtn="<button class='btn btn-small' onclick=editShotLabel("+p.promptId+") style='font-size:11px;background:#f59e0b;color:#fff;border-radius:6px;padding:8px 6px;border:none;cursor:pointer;display:flex;flex-direction:column;align-items:center;gap:4px;width:100%;margin:3px 0;'><i class='fas fa-tag' style='font-size:16px;'></i><span style='font-size:11px;'>修改镜号</span></button>;;var videoHtml="";if(p.videoUrl){videoHtml="<div style='text-align:center;'><video src='"+p.videoUrl+"' controls autoplay muted style='width:100%;border-radius:6px;' loop></video></div>"}h+="<tr style='height:120px;'><td>"+(i+1)+"</td><td style='white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:200px;' title='"+label+"'>"+label+"</td><td style='white-space:nowrap;'>"+shotLabel+"</td><td style='max-width:400px;overflow:hidden;'><div class='cell-wrap'>"+escHtml(p.promptText||"")+"</div></td><td style='text-align:center;'>"+fmtStatus(p.status)+"</td><td style='width:130px;vertical-align:middle;'><div class=\"ops-col\">"+actionBtn+editBtn+deleteBtn+shotLabelBtn+"</div></td><td style='width:150px;vertical-align:middle;'>"+refHtml+"</td><td>"+videoHtml+"</td></tr>"}h+="</tbody></table>";$("#stageResult").html(h);
      setTimeout(function(){ $(".poll-trigger").each(function(){pollVideoStatus(this,$(this).data("pid"))}); }, 500);}).fail(function(){$("#stageResult").html("<p style='color:#e74c3c;padding:20px;'>加载提示词失败</p>")})}

// ======== generateVideo ========
function genVideo(btn,promptId){var dur=$("#vidDuration").val()||11;var ratio=$("#vidRatio").val()||"16:9";var wm=$("#vidWatermark").is(":checked");var aud=$("#vidAudio").is(":checked");var res=$("#vidResolution").val()||"720p";$(btn).prop("disabled",true).text("\u23f3 提交中...");$.ajax({url:"/api/project/"+currentProjectId+"/video/generate/"+promptId,method:"POST",contentType:"application/json",data:JSON.stringify({duration:parseInt(dur),ratio:ratio,watermark:wm,generateAudio:aud,resolution:res}),success:function(r){$(btn).text("\u23f3 生成中").prop("disabled",false);pollVideoStatus(btn,promptId)},error:function(x){$(btn).text("🎬 重试").prop("disabled",false);alert("提交失败: "+(x.responseJSON?.message||x.statusText))}})}

// ======== pollVideoStatus ========
function pollVideoStatus(btn,promptId){var statusMap={succeeded:"成功",completed:"成功",failed:"失败",processing:"正在生成",running:"正在生成",pending:"排队等待",queued:"排队等待",expired:"任务超时"};var statusColor={succeeded:"#10b981",completed:"#10b981",failed:"#ef4444",processing:"#3b82f6",running:"#3b82f6",pending:"#f59e0b",queued:"#f59e0b",expired:"#6b7280"};$(btn).html("<i class='fas fa-spinner fa-pulse' style='font-size:16px;color:#3b82f6;'></i><span style='color:#3b82f6;font-size:11px;'>查询中</span>");(function poll(){$.get("/api/project/"+currentProjectId+"/video/status/"+promptId).done(function(r){if(r.status==="succeeded"||r.status==="completed"){$(btn).replaceWith("<a href='"+r.videoUrl+"' target='_blank' class='btn btn-small' style='font-size:11px;background:#7c3aed;color:#fff;border-radius:4px;padding:4px 10px;text-decoration:none;display:inline-block;'>🎬 查看</a>");loadPrompts()}else if(r.status==="failed"){$(btn).html("<i class='fas fa-exclamation-triangle' style='font-size:16px;color:#ef4444;'></i><span style='color:#ef4444;font-size:11px;'>失败重试</span>").prop("disabled",false)}else{var stxt=statusMap[r.status]||"生成中";var sc=statusColor[r.status]||"#3b82f6";$(btn).html("<i class='fas fa-spinner fa-pulse' style='font-size:16px;color:"+sc+";'></i><span style='color:"+sc+";font-size:11px;'>"+stxt+"</span>");setTimeout(poll,5000)}}).fail(function(){$(btn).html("<i class='fas fa-exclamation-circle' style='font-size:16px;color:#ef4444;'></i><span style='color:#ef4444;font-size:11px;'>请求失败</span>").prop("disabled",false)})})()}

// ======== uploadRef ========

function uploadRefSlot(input,promptId,slotIdx){
  var file=input.files[0];
  if(!file)return;
  var r=new FileReader();
  r.onload=function(e){
    var dataUrl=e.target.result;
    $.get("/api/project/"+currentProjectId+"/prompt/"+promptId+"/refs",function(current){
      var arr=current.referenceImages||[];
      while(arr.length<9)arr.push(null);
      arr[slotIdx]=dataUrl;
      $.ajax({url:"/api/project/"+currentProjectId+"/video/references/"+promptId,method:"PUT",contentType:"application/json",data:JSON.stringify({referenceImages:arr}),success:function(){loadPrompts()},error:function(){alert("保存失败")}})
    }).fail(function(){
      var arr=[];
      for(var i=0;i<9;i++)arr.push(null);
      arr[slotIdx]=dataUrl;
      $.ajax({url:"/api/project/"+currentProjectId+"/video/references/"+promptId,method:"PUT",contentType:"application/json",data:JSON.stringify({referenceImages:arr}),success:function(){loadPrompts()},error:function(){alert("保存失败")}})
    });
  };
  r.readAsDataURL(file);
  input.value="";
}
function clearRefSlot(promptId,slotIdx){
  $.get("/api/project/"+currentProjectId+"/prompt/"+promptId+"/refs",function(current){
    var arr=current.referenceImages||[];
    while(arr.length<9)arr.push(null);
    arr[slotIdx]=null;
    $.ajax({url:"/api/project/"+currentProjectId+"/video/references/"+promptId,method:"PUT",contentType:"application/json",data:JSON.stringify({referenceImages:arr}),success:function(){loadPrompts()},error:function(){alert("保存失败")}})
  }).fail(function(){
    alert("获取当前参考图失败");
  });
}
function parseUnitTable(t){
  if(!t)return"";
  var lines=t.split("\n");
  var h="<table class='storyboard-table'><thead><tr><th>集</th><th>单元</th><th>时长</th><th>地点</th><th>核心动作/情绪</th><th>起始状态</th><th>结束状态</th><th>关键元素</th></tr></thead><tbody>";
  var curEp="",curUnit="",curDur="",curLoc="",curCore="",curStart="",curEnd="",curKeys="";
  function addRow(){if(curUnit||curEp){h+="<tr><td>"+escHtml(curEp)+"</td><td>"+escHtml(curUnit)+"</td><td>"+escHtml(curDur)+"</td><td>"+escHtml(curLoc)+"</td><td>"+escHtml(curCore)+"</td><td>"+escHtml(curStart)+"</td><td>"+escHtml(curEnd)+"</td><td>"+escHtml(curKeys)+"</td></tr>";curDur="";curLoc="";curCore="";curStart="";curEnd="";curKeys=""}}
  for(var i=0;i<lines.length;i++){
    var l=lines[i].trim();
    if(!l)continue;
    var em=l.match(/【第(\d+)集】/);
    if(em){addRow();curEp="第"+em[1]+"集";curUnit="";continue}
    var um=l.match(/【单元([\d.]+)】/);
    if(um){addRow();curUnit="单元"+um[1];continue}
    if(l.indexOf("---")===0||l.indexOf("##")===0)continue
    var v=l.replace(/\*\*/g,"").trim();
    if(v.indexOf("时长")===0||v.indexOf("时长")===0){curDur=v.replace(/时长[：:]/,"").trim()}
    else if(v.indexOf("地点")===0||v.indexOf("地点")===0){curLoc=v.replace(/地点[：:]/,"").trim()}
    else if(v.indexOf("核心")===0||v.indexOf("情绪")===0||v.indexOf("动作")===0){curCore=v.replace(/核心动作[／\/]情绪[：:]/,"").replace(/核心动作[：:]/,"").replace(/核心[：:]/,"").trim()}
    else if(v.indexOf("起始状态")===0||v.indexOf("起始")===0){curStart=v.replace(/起始状态[：:]/,"").replace(/起始[：:]/,"").trim()}
    else if(v.indexOf("结束状态")===0||v.indexOf("结束")===0){curEnd=v.replace(/结束状态[：:]/,"").replace(/结束[：:]/,"").trim()}
    else if(v.indexOf("关键元素")===0||v.indexOf("关键")===0){curKeys=v.replace(/关键元素[：:]/,"").replace(/关键[：:]/,"").trim()}
  }
  addRow();
  h+="</tbody></table>";
  return h
}function parseStoryboardTable(t){
  if(!t)return"";
  var lines=t.split("\n");
  var h="<table class='storyboard-table'><thead><tr><th>集</th><th>单元</th><th>镜头编号</th><th>镜头描述</th><th>构图方式</th><th>景别</th><th>镜头运动</th><th>出镜角色</th><th>对话/台词</th><th>镜头时长</th><th>起始画面</th><th>结束画面</th></tr></thead><tbody>";
  var curEp="",curUnit="",shot={};
  function addShot(){
    if(shot.number){
      h+="<tr><td>"+escHtml(curEp)+"</td><td>"+escHtml(curUnit)+"</td><td>"+escHtml(shot.number||"")+"</td><td>"+escHtml(shot.desc||"")+"</td><td>"+escHtml(shot.composition||"")+"</td><td>"+escHtml(shot.scene||"")+"</td><td>"+escHtml(shot.camera||"")+"</td><td>"+escHtml(shot.characters||"")+"</td><td>"+escHtml(shot.dialogue||"")+"</td><td>"+escHtml(shot.duration||"")+"</td><td>"+escHtml(shot.start||"")+"</td><td>"+escHtml(shot.end||"")+"</td></tr>"
    }
    shot={}
  }
  for(var i=0;i<lines.length;i++){
    var l=lines[i].trim();
    if(!l)continue;
    var em=l.match(/【第(\d+)集】/);
    if(em){addShot();curEp="第"+em[1]+"集";curUnit="";continue}
    var um=l.match(/【单元([\d.]+)】/);
    if(um){addShot();curUnit="单元"+um[1];continue}
    // Detect shot start
    var sn=l.match(/- \*\*镜头编号\*\*[：:]?\s*(.+)/i);
    if(sn){addShot();shot.number=sn[1].trim();if(!curUnit){var u=shot.number.match(/^(\d+\.\d+)/);if(u){curUnit='单元'+u[1]}}continue}
    var sd=l.match(/- \*\*镜头描述\*\*[：:]?\s*(.+)/i);
    if(sd){shot.desc=sd[1].trim();continue}
    var sc=l.match(/- \*\*构图方式\*\*[：:]?\s*(.+)/i);
    if(sc){shot.composition=sc[1].trim();continue}
    var ss=l.match(/- \*\*景别\*\*[：:]?\s*(.+)/i);
    if(ss){shot.scene=ss[1].trim();continue}
    var sm=l.match(/- \*\*镜头运动\*\*[：:]?\s*(.+)/i);
    if(sm){shot.camera=sm[1].trim();continue}
    var sch=l.match(/- \*\*出镜角色(及表情)?\*\*[：:]?\s*(.+)/i);
    if(sch){shot.characters=sch[sch.length-1].trim();continue}
    var sdi=l.match(/- \*\*对话\/台词\*\*[：:]?\s*(.+)/i);
    if(sdi){shot.dialogue=sdi[1].trim();continue}
    var sdu=l.match(/- \*\*镜头时长\*\*[：:]?\s*(.+)/i);
    if(sdu){shot.duration=sdu[1].trim();continue}
    var sst=l.match(/- \*\*起始画面\*\*[：:]?\s*(.+)/i);
    if(sst){shot.start=sst[1].trim();continue}
    var sed=l.match(/- \*\*结束画面\*\*[：:]?\s*(.+)/i);
    if(sed){shot.end=sed[1].trim();continue}
  }
  addShot();
  h+="</tbody></table>";
  return h
}function loadCoherence(){$.get("/api/project/"+currentProjectId+"/coherence",function(r){var issues=r.issues||"";var h="<div class='card' style='padding:20px;'><pre style='white-space:pre-wrap;color:#e2e8f0;font-size:13px;font-family:inherit;'>"+escHtml(issues||"暂无问题")+"</pre></div>";$("#stageResult").html(h)}).fail(function(){$("#stageResult").html("<p style='color:#e74c3c;padding:20px;'>加载衔接检查失败</p>")})}

// ======== jQuery ready event handlers ========
$(function(){
  // Continue script
  $(document).on("click","#btnContinueScript",function(){var btn=$(this);btn.prop("disabled",true).text("\u23f3 处理中...");var sc=$("#scriptContent").val();$.ajax({url:"/api/project/"+currentProjectId+"/continue",method:"POST",contentType:"application/json",data:JSON.stringify({scriptContent:sc}),success:function(){alert("续写成功！");loadProject()},error:function(x){alert("续写失败: "+(x.responseJSON?.message||x.statusText));btn.prop("disabled",false).text("续写")}})});

  // Edit script
  $(document).on("click","#btnEditScript",function(){$("#scriptDisplay").hide();$("#scriptContent").show();$("#scriptContent").focus()});

  // Save script
  $(document).on("click","#btnSaveScript",function(){var sc=$("#scriptContent").val();$.ajax({url:"/api/project/"+currentProjectId+"/save",method:"POST",contentType:"application/json",data:JSON.stringify({scriptContent:sc}),success:function(){$("#scriptDisplay").html(sc.replace(/\n/g,"<br>")).show();$("#scriptContent").hide()},error:function(x){alert("保存失败: "+(x.responseJSON?.message||x.statusText))}})});

  // Cancel edit
  $(document).on("click","#btnCancelScript",function(){$("#scriptDisplay").show();$("#scriptContent").hide()});

  // Generate stage
  $(document).on("click","#btnGenerate",function(){var btn=$(this);var sn=currentStage;if(!confirm("确定生成\xab"+getStageName(sn)+"\xbb？"))return;btn.prop("disabled",true).html("<i class='fas fa-spinner fa-spin'></i> 生成中...");$.post("/api/project/"+currentProjectId+"/stage/"+sn+"/process").done(function(r){btn.html("<i class='fas fa-bolt'></i> 生成").prop("disabled",false);reloadStages()}).fail(function(x){btn.html("<i class='fas fa-bolt'></i> 生成").prop("disabled",false);alert("生成失败: "+(x.responseJSON?.message||x.statusText))})});

  // Edit stage
  $(document).on("click","#btnEditStage",function(){$("#stageResult").hide();$("#stageEditor").show();$("#stageActions").hide();$("#stageActions2").show()});

  // Save edit
  $(document).on("click","#btnSaveEdit",function(){var c=$("#stageEditor").text();$.ajax({url:"/api/project/"+currentProjectId+"/stage/"+currentStage+"/save",method:"POST",contentType:"application/json",data:JSON.stringify({content:c}),success:function(){$("#stageResult").html(c.replace(/\*\*/g,"").replace(/\n/g,"<br>")).show();$("#stageEditor").hide();$("#stageActions").show();$("#stageActions2").hide()},error:function(x){alert("保存失败: "+(x.responseJSON?.message||x.statusText))}})});

  // Cancel edit
  $(document).on("click","#btnCancelEdit",function(){$("#stageResult").show();$("#stageEditor").hide();$("#stageActions").show();$("#stageActions2").hide()});

  // Save episode count
  $(document).on("click","#btnSetEpCount",function(){var n=parseInt($("#episodeCount").val())||12;$.ajax({url:"/api/project/"+currentProjectId+"/episode-count",method:"PUT",contentType:"application/json",data:JSON.stringify({count:n}),success:function(){alert("已更新")},error:function(x){alert("更新失败: "+(x.responseJSON?.message||x.statusText))}})});

  // Script mode menu buttons
  $(document).on("click",".script-menu",function(){var mode=$(this).data("mode");$(".script-mode .btn").removeClass("btn-primary");$(this).addClass("btn-primary")});

  // Menu final
  $(document).on("click","#menuFinal",function(){showFinalResult()});
$(document).on("click","#btnBatchVideo",function(){var btn=$(this);var dur=$("#vidDuration").val()||11;var ratio=$("#vidRatio").val()||"16:9";var wm=$("#vidWatermark").is(":checked");var aud=$("#vidAudio").is(":checked");var res=$("#vidResolution").val()||"720p";btn.prop("disabled",true).html("<i class='fas fa-spinner fa-spin'></i> 提交中...");$.ajax({url:"/api/project/"+currentProjectId+"/video/batch-generate",method:"POST",contentType:"application/json",data:JSON.stringify({settings:{duration:parseInt(dur),ratio:ratio,watermark:wm,generateAudio:aud,resolution:res}}),success:function(r){alert(r.message);btn.html("<i class='fas fa-film'></i> 批量生成视频").prop("disabled",false);loadPrompts()},error:function(x){btn.html("<i class='fas fa-film'></i> 批量生成视频").prop("disabled",false);alert("批量提交失败: "+(x.responseJSON?.message||x.statusText))}})});});
</script>
<!-- Edit prompt modal -->
<div id="editModal" style="display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:1000;justify-content:center;align-items:center;">
  <div style="background:#1e293b;border-radius:12px;padding:24px;width:80%;max-width:80%;height:80%;display:flex;flex-direction:column;">
    <h3 style="color:#e2e8f0;margin:0 0 12px 0;font-size:15px;"><i class="fas fa-pen" style="margin-right:6px;"></i>编辑提示词</h3>
    <textarea id="editText" style="flex:1;min-height:200px;background:#0f172a;color:#e2e8f0;border:1px solid #334155;border-radius:8px;padding:12px;font-size:13px;font-family:monospace;resize:vertical;outline:none;"></textarea>
    <div style="margin-top:12px;display:flex;gap:8px;justify-content:flex-end;">
      <button onclick="closeEdit()" style="background:#475569;color:#fff;border:none;border-radius:6px;padding:8px 16px;cursor:pointer;font-size:13px;">取消</button>
      <button onclick="saveEdit()" style="background:#7c3aed;color:#fff;border:none;border-radius:6px;padding:8px 16px;cursor:pointer;font-size:13px;">保存</button>
    </div>
  </div>
</div>
<script>
var editingPromptId=0;
function editShotLabel(pid){var curP="";for(var i=0;i<window.promptData&&window.promptData.length;i++){if(window.promptData[i].promptId==pid){curP=window.promptData[i].shotLabel||"";break}}var newLabel=prompt("请输入新的镜号：",curP);if(newLabel!==null&&newLabel.trim()!=="")$.ajax({url:"/api/project/"+currentProjectId+"/prompt/"+pid+"/shotlabel",method:"PUT",contentType:"application/json",data:JSON.stringify({shotLabel:newLabel.trim()}),success:function(){loadPrompts()},error:function(x){alert("修改失败: "+(x.responseJSON?.message||x.statusText))}})}
function editPrompt(btn,pid){editingPromptId=pid;var txt=$(btn).closest("tr").find("td").eq(3).text();$("#editText").val(txt);$("#editModal").css("display","flex")}
function closeEdit(){$("#editModal").hide()}
function deletePrompt(btn,pid){if(!confirm('确定删除该提示词？'))return;$.ajax({url:'/api/project/'+currentProjectId+'/prompt/'+pid,method:'DELETE',success:function(){loadPrompts()},error:function(x){alert('删除失败: '+(x.responseJSON?.message||x.statusText))}})}
function saveEdit(){var txt=$("#editText").val();$.ajax({url:"/api/project/"+currentProjectId+"/prompt/"+editingPromptId,method:"PUT",contentType:"application/json",data:JSON.stringify({promptText:txt}),success:function(){closeEdit();loadPrompts()},error:function(x){alert("保存失败: "+(x.responseJSON?.message||x.statusText))}})}
</script>
</body>
</html>
