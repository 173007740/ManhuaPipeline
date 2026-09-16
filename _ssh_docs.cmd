@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 63542 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "echo ---TURBODIR--- ; ls -la /home/featurize/ComfyUI/custom_nodes/comfyui-minimax-h3-turbo/ ; echo ---DSROOT--- ; ls -la /home/featurize/datasets/ab7b6bc7-9a9d-4e75-b55e-1f839cc59c96/ ; echo ---DSH3--- ; ls -la /home/featurize/datasets/ab7b6bc7-9a9d-4e75-b55e-1f839cc59c96/comfyui-minimax-h3/ ; echo ---DSNONST--- ; find /home/featurize/datasets/ab7b6bc7-9a9d-4e75-b55e-1f839cc59c96/comfyui-minimax-h3/ -maxdepth 3 -type f -not -name '*.safetensors' -not -name '*.gguf' | head -60 ; echo ---GREP-ADALN--- ; grep -rn 'adaln_t_table' /home/featurize/ComfyUI/custom_nodes/ /home/featurize/ComfyUI/comfy/ /home/featurize/ComfyUI/comfy_extras/ 2>/dev/null | head -20 ; echo ---GREP-PRUNED--- ; grep -rni 'pruned' /home/featurize/ComfyUI/custom_nodes/comfyui-minimax-h3-turbo/ 2>/dev/null | head -20 ; echo ---DONE---" < nul
endlocal
