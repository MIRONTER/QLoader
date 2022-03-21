@echo off

SET /A mirrornumber=%RANDOM% * 20 / 32768 + 1

cd %~dp0

echo Using mirror FFA-%mirrornumber%

echo Updating thumbnails
..\tools\windows\rclone\FFA.exe copy --retries 1 --progress "FFA-%mirrornumber%:/Quest Games/.meta/thumbnails/" ../Resources/thumbnails/

echo Done!
