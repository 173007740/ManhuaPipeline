@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "/environment/miniconda3/bin/python3 - /home/featurize/ComfyUI/models/diffusion_models/minimax_h3_ref2va_int8_convrot.safetensors /home/featurize/ComfyUI/models/diffusion_models/minimax_h3_ref2va_pruned_int8_convrot.safetensors" < "%~dp0_keys.py"
endlocal
