@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
set "SP=/environment/miniconda3/envs/comfyui/lib/python3.12/site-packages"
set "R=echo ---NVIDIA_DIR--- ; ls %SP%/nvidia ; echo ---NVCC_BIN--- ; find %SP%/nvidia -maxdepth 3 -name nvcc ; echo ---CUDA_NVCC_PKG--- ; ls -d %SP%/nvidia*cuda*nvcc* ; echo ---DONE---"
"%SystemRoot%\System32\OpenSSH\ssh.exe" -p 12447 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 featurize@workspace.featurize.cn "%R%" < nul
endlocal
