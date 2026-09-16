@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 12447 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---OUT_VIDEO--- ; ls -lat /home/featurize/ComfyUI/output/video 2>/dev/null | head -20 ; echo ---OUT_COUNT--- ; ls /home/featurize/ComfyUI/output/video 2>/dev/null | wc -l ; echo ---OUT_TOP--- ; ls -lat /home/featurize/ComfyUI/output 2>/dev/null | head -10 ; echo ---LORAS--- ; ls -la /home/featurize/ComfyUI/models/loras/ ; echo ---MODELS--- ; ls -la /home/featurize/ComfyUI/models/diffusion_models/ 2>/dev/null ; ls -la /home/featurize/ComfyUI/models/unet/ 2>/dev/null ; echo ---NODES--- ; ls /home/featurize/ComfyUI/custom_nodes ; echo ---DONE---" < nul
endlocal
