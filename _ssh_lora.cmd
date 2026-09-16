@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---LORADIR--- ; ls -la /home/featurize/ComfyUI/models/loras/ ; echo ---CHECK--- ; /environment/miniconda3/bin/python3 - /home/featurize/ComfyUI/models/diffusion_models/minimax_h3_ref2va_int8_convrot.safetensors /home/featurize/ComfyUI/models/diffusion_models/minimax_h3_ref2va_pruned_int8_convrot.safetensors /home/featurize/ComfyUI/models/loras/minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors" < "%~dp0_loracheck.py"
endlocal
