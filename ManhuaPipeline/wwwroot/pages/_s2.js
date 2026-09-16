
var editingPromptId=0;
function editShotLabel(pid){var curP="";for(var i=0;i<window.promptData&&window.promptData.length;i++){if(window.promptData[i].promptId==pid){curP=window.promptData[i].shotLabel||"";break}}var newLabel=prompt("请输入新的镜号：",curP);if(newLabel!==null&&newLabel.trim()!=="")$.ajax({url:"/api/project/"+currentProjectId+"/prompt/"+pid+"/shotlabel",method:"PUT",contentType:"application/json",data:JSON.stringify({shotLabel:newLabel.trim()}),success:function(){loadPrompts()},error:function(x){alert("修改失败: "+(x.responseJSON?.message||x.statusText))}})}
function editPrompt(btn,pid){editingPromptId=pid;var txt=$(btn).closest("tr").find("td").eq(3).text();$("#editText").val(txt);$("#editModal").css("display","flex")}
function closeEdit(){$("#editModal").hide()}
function deletePrompt(btn,pid){if(!confirm('确定删除该提示词？'))return;$.ajax({url:'/api/project/'+currentProjectId+'/prompt/'+pid,method:'DELETE',success:function(){loadPrompts()},error:function(x){alert('删除失败: '+(x.responseJSON?.message||x.statusText))}})}
function saveEdit(){var txt=$("#editText").val();$.ajax({url:"/api/project/"+currentProjectId+"/prompt/"+editingPromptId,method:"PUT",contentType:"application/json",data:JSON.stringify({promptText:txt}),success:function(){closeEdit();loadPrompts()},error:function(x){alert("保存失败: "+(x.responseJSON?.message||x.statusText))}})}
