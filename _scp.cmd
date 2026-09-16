@echo off
setlocal enableextensions
set "SSH_ASKPASS=%USERPROFILE%\.ssh\featurize_askpass.cmd"
set "SSH_ASKPASS_REQUIRE=force"
"%SystemRoot%\System32\OpenSSH\scp.exe" -P 53862 -o StrictHostKeyChecking=no -o PubkeyAuthentication=no -o PreferredAuthentications=password -o NumberOfPasswordPrompts=1 -o ConnectTimeout=15 %* < nul
endlocal
