// ========== 全局工具 ==========
var API = {
  base: "",
  get: function(path) { return $.getJSON(this.base + "/api" + path); },
  post: function(path, data) { return $.ajax({ url: this.base + "/api" + path, method: "POST", contentType: "application/json", data: JSON.stringify(data) }); },
  put: function(path, data) { return $.ajax({ url: this.base + "/api" + path, method: "PUT", contentType: "application/json", data: JSON.stringify(data) }); },
  del: function(path) { return $.ajax({ url: this.base + "/api" + path, method: "DELETE" }); }
};

function escHtml(t) { return $("<span>").text(t||"").html(); }

// 检测当前页面是否在 pages/ 子目录下
var inPagesDir = window.location.pathname.indexOf("/pages/") >= 0;
function projectUrl(id) {
  return (inPagesDir ? "" : "pages/") + "project.html?id=" + id;
}

// ========== 导航栏 ==========
$(function() {
  console.log('DOM ready');
  $.get("/api/auth/me", function(user) {
    if (!user || user.userId === 0) {
      $("#navUser").hide();
      $("#navGuest").show();
      return;
    }
    $("#navGuest").hide();
    $("#navUser").show();
      var displayName = user.nickname || user.username;
      if (user.avatar) {
        $("#userName").html('<img src="' + user.avatar + '" style="width:22px;height:22px;border-radius:50%;vertical-align:middle;margin-right:4px;object-fit:cover;"> ' + displayName);
      } else {
        $("#userName").text(' ' + displayName);
      }
    // 加载作品列表（dashboard 页面）
    if ($("#projectList").length) {
      $.get("/api/project", function(projects) {
        var $grid = $("#projectList"), $empty = $("#emptyState");
        $grid.empty();
        if (!projects.length) { $grid.hide(); $empty.show(); return; }
        $grid.show(); $empty.hide();
        projects.forEach(function(p) {
          $grid.append(
            '<div class="project-card">' +
            '<div class="project-cover" style="background:#e0d6cc;font-size:48px;' + (p.coverImage ? 'background-image:url(' + p.coverImage + ');background-size:cover;background-position:center;font-size:0;' : '') + '">' + (p.coverImage ? '' : '📫') + '</div>' +
            '<div class="project-info"><h3>' + escHtml(p.title) + '</h3>' +
            '<div class="meta">阶段 ' + p.currentStage + '/10 · ' + new Date(p.updatedAt).toLocaleDateString() + '</div></div>' +
            '<div class="project-actions">' +
            '<a href="' + projectUrl(p.projectId) + '" class="btn btn-small btn-primary">进入</a>' +
            '<button class="btn btn-small btn-primary btn-edit-project" data-id="' + p.projectId + '" data-title="' + escHtml(p.title) + '">编辑</button>' +
            '<button class="btn btn-small btn-danger btn-del-project" data-id="' + p.projectId + '">删除</button></div></div>'
          );
        });
      });
    }
  });
});

$(document).on("click", "#btnLogout", function() {
    API.post("/auth/logout").done(function() { window.location.href = "/"; });
  });

$(document).on("click", ".btn-del-project", function() {
  var id = $(this).data("id");
  if (!confirm("确定删除这个项目吗？所有数据将丢失！")) return;
  API.del("/project/" + id).done(function() { window.location.reload(); });
});



// ========== 编辑项目 ==========
var editCoverFile = null;
$(document).on("click", ".btn-edit-project", function() {
  var id = $(this).data("id");
  $("#editProjectId").val(id);
  $("#editProjectTitle").val($(this).data("title"));
  $("#editCoverPreview").attr("src", "").hide();
  $("#editCoverInput").val("");
  editCoverFile = null;
  // Load current cover
  $.get("/api/project/" + id, function(p) {
    $("#editProjectDesc").val(p.description || "");
    $("#editProjectTags").val(p.tags || "");
    $("#editProjectLibCategory").val(p.libraryCategory || "");
    if (p.coverImage) {
      $("#editCoverPreview").attr("src", p.coverImage).show();
    }
  }).fail(function() { console.log("加载项目信息失败"); });
  $("#editProjectModal").css("display", "flex");
});

$("#btnCancelEditProject, #editProjectModal").click(function(e) {
  if (e.target === this) $("#editProjectModal").hide();
});

// Cover image preview
$("#editCoverInput").change(function() {
  var file = this.files[0];
  if (file) {
    editCoverFile = file;
    var reader = new FileReader();
    reader.onload = function(e) { 
      $("#editCoverPreview").attr("src", e.target.result).show(); 
    };
    reader.readAsDataURL(file);
  }
});

$("#btnRemoveCover").click(function() {
  editCoverFile = null;
  $("#editCoverPreview").attr("src", "").hide();
  $("#editCoverInput").val("");
});

// 资产库类型：随编辑弹窗一起保存。只有项目管理页的编辑弹窗带这个下拉，
// 别的页面（也引 app.js）没有该元素，此时直接跳过，绝不能发请求把已有值清空。
function saveProjectLibCategory(id, done) {
  var $sel = $("#editProjectLibCategory");
  if (!$sel.length) { done(); return; }
  $.ajax({
    url: "/api/project/" + id + "/library-category",
    method: "PUT",
    contentType: "application/json",
    data: JSON.stringify({ libraryCategory: $sel.val() || "" })
  }).done(done).fail(function(xhr) {
    alert("资产库类型保存失败: " + (xhr.responseJSON?.message || xhr.statusText));
  });
}

// Save edit
$("#btnSaveEditProject").click(function() {
  var id = $("#editProjectId").val();
  var title = $("#editProjectTitle").val().trim();
  var description = $("#editProjectDesc").val()?.trim() || "";
  var coverSrc = $("#editCoverPreview").attr("src") || "";
  var coverImage = (coverSrc === "" || coverSrc.startsWith("data:image/svg")) ? null : coverSrc;
  var finish = function() {
    $.ajax({
      url: "/api/project/" + id + "/cover",
      method: "PUT",
      contentType: "application/json",
      data: JSON.stringify({ coverImage: coverImage, title: title, description: description })
    }).done(function(r) {
      var tags = $("#editProjectTags").val().trim();
      $.ajax({
        url: "/api/project/" + id + "/tags",
        method: "PUT",
        contentType: "application/json",
        data: JSON.stringify({ tags: tags })
      }).done(function() {
        saveProjectLibCategory(id, function() {
          $("#editProjectModal").hide();
          window.location.reload();
        });
      }).fail(function(xhr) {
        alert("标签保存失败: " + (xhr.responseJSON?.message || xhr.statusText));
      });
    }).fail(function(xhr) {
      alert("保存失败: " + (xhr.responseJSON?.message || xhr.statusText));
    });
  };
  if (editCoverFile) {
    var fd = new FormData();
    fd.append("file", editCoverFile);
    $.ajax({
      url: "/api/project/" + id + "/cover-upload",
      method: "POST",
      data: fd,
      processData: false,
      contentType: false
    }).done(function(r) {
      coverImage = r.coverUrl;
      finish();
    }).fail(function(xhr) {
      alert("封面上传失败: " + (xhr.responseJSON?.message || xhr.statusText));
    });
  } else {
    finish();
  }
});