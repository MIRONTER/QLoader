@echo off

cp -u pack.sh %~dp0..\bin\publish\Debug\
cd %~dp0..\bin\publish\Debug\

C:\Windows\Sysnative\wsl.exe "./pack.sh"

set /p Input=Enter 1 to open publish directory, any other key to exit:
if /I "%Input%"=="1" goto yes
goto no
:yes
start "" "%~dp0..\bin\publish\Debug\"
:no
