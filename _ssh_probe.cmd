@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---GPU--- ; nvidia-smi -L ; echo ---PY--- ; which python3 ; which python ; echo ---HOME--- ; ls -d /home/featurize/ComfyUI ; echo ---NODES--- ; ls /home/featurize/ComfyUI/custom_nodes ; echo ---MODELS--- ; ls -la /home/featurize/ComfyUI/models/ ; echo ---DM--- ; ls -la /home/featurize/ComfyUI/models/diffusion_models/ 2>/dev/null ; ls -la /home/featurize/ComfyUI/models/unet/ 2>/dev/null ; echo ---DONE---" < nul
endlocal
