$ErrorActionPreference = 'Continue'
Set-Location D:\Repos\nvidia_app
& 'C:\Program Files\dotnet\dotnet.exe' build nvidia_app.sln -c Release --nologo *> (Join-Path $env:TEMP 'full_build.log')
exit $LASTEXITCODE
