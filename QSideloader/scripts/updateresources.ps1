$currentpath = Get-Location
$mypath = $MyInvocation.MyCommand.Path
$mypath = Split-Path $mypath -Parent
Set-Location $mypath

$mirrornumber = ( Get-Random -Minimum 97 -Maximum 99 ).ToString('00')

Write-Output "Using mirror FFA-$mirrornumber"

Write-Output "Updating thumbnails"
..\tools\windows\rclone\FFA.exe sync --retries 1 --progress "FFA-${mirrornumber}:/Quest Games/.meta/thumbnails/" ../Resources/thumbnails/

Write-Output "Updating videos"
..\tools\windows\rclone\FFA.exe sync --retries 1 --progress "FFA-${mirrornumber}:/Quest Games/.meta/videos/" ../Resources/videos/

Write-Output "Done!"

Set-Location $currentpath