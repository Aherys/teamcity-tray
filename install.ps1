$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'TeamCityTray'

Get-Process -Name TeamCityTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish src/TeamCityTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$target"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Start-Process -FilePath (Join-Path $target 'TeamCityTray.exe')
