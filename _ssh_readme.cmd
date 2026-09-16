@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
set "D=/home/featurize/ComfyUI/custom_nodes/comfyui-minimax-h3-turbo"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---README--- ; cat %D%/README.md ; echo ---EXAMPLE_WF--- ; ls -la %D%/example_workflows ; echo ---LORAS--- ; ls -la /home/featurize/ComfyUI/models/loras/ ; echo ---DONE---" < nul
endlocal
