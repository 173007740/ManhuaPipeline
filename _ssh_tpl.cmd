@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---TPL_MINIMAX--- ; find /environment/miniconda3/lib/python3.12/site-packages/comfyui_workflow_templates -iname '*minimax*' 2>/dev/null ; echo ---TPL_H3--- ; find /environment/miniconda3/lib/python3.12/site-packages/comfyui_workflow_templates -iname '*h3*' 2>/dev/null ; echo ---DONE---" < nul
endlocal
