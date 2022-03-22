$scriptpath = (Split-Path $MyInvocation.MyCommand.Path -Parent)
$targetpath = $scriptpath + "\..\"
cd $targetpath
$result = Get-ChildItem -include *.cs,*.xaml -exclude *.g.cs,*.g.i.cs,*.Assembly*.cs -recurse
$result | % {
    $_ | Select-Object -Property 'Name', @{
        label = 'Lines'; expression = {
            ($_ | Get-Content).Length 
        } 
    } 
} | Sort-object -Property Lines -Descending
Write-Output "`nNumber of files:"
($result).Count
Write-Output "`nSum Lines:"
($result | select-string .).Count

cd $scriptpath