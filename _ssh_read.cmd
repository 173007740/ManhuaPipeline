@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "R=/home/featurize/ComfyUI/custom_nodes/comfyui-minimax-h3-turbo/README.md ; echo ---R1--- ; sed -n '35,75p' $R ; echo ---R2--- ; sed -n '75,140p' $R ; echo ---R3--- ; sed -n '140,240p' $R ; echo ---DONE---" < nul
endlocal
