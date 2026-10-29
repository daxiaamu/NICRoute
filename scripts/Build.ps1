param([switch]$SelfContained, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    if (-not (Test-Path -LiteralPath 'engine\sing-box.exe')) { & "$PSScriptRoot\Get-Engine.ps1" }
    dotnet build NICRoute.slnx -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    if (-not $SkipTests) {
        dotnet run --project tests/NICRoute.Tests -c Release --no-build -- --engine "$root\engine\sing-box.exe"
        if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    }
    $contained = if ($SelfContained) { 'true' } else { 'false' }
    $output = if ($SelfContained) { 'dist\NICRoute-win-x64' } else { 'dist\NICRoute-win-x64-framework' }
    dotnet publish src/NICRoute.App -c Release -r win-x64 --self-contained $contained -o $output -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '打包失败。' }
    & "$PSScriptRoot\Package.ps1" -Output $output
    Write-Host "可执行程序：$root\$output\NICRoute.exe"
}
finally { Pop-Location }
