@echo off

cp -u pack.sh %~dp0..\bin\publish\Release\
cd %~dp0..\bin\publish\Release\

C:\Windows\Sysnative\wsl.exe "./pack.sh"

set /p Input=Enter 1 to open publish directory, any other key to exit:
if /I "%Input%"=="1" goto yes
goto no
:yes
start "" "%~dp0..\bin\publish\Release\"
:no
