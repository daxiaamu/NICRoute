param([switch]$SelfContained, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    if (-not (Test-Path -LiteralPath 'engine\sing-box.exe')) { & "$PSScriptRoot\Get-Engine.ps1" }
    & "$PSScriptRoot\Get-Mudl.ps1"
    dotnet build NICRoute.slnx -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    if (-not $SkipTests) {
        dotnet run --project tests/NICRoute.Tests -c Release --no-build -- --engine "$root\engine\sing-box.exe"
        if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    }
    $contained = if ($SelfContained) { 'true' } else { 'false' }
    $output = if ($SelfContained) { 'dist\NICRoute-win-x64' } else { 'dist\NICRoute-win-x64-framework' }
    $resolvedOutput=[IO.Path]::GetFullPath((Join-Path $root $output))
    if(-not $resolvedOutput.StartsWith((Join-Path $root 'dist')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '发布目标不在项目 dist 中。' }
    if(Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
    dotnet publish src/NICRoute.App -c Release -r win-x64 --self-contained $contained -o $output -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '打包失败。' }
    $updaterOutput=Join-Path $root ('artifacts\updater-'+[Guid]::NewGuid().ToString('N'))
    dotnet publish src/NICRoute.Updater -c Release -r win-x64 --self-contained $contained -o $updaterOutput -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '更新程序打包失败。' }
    # The WPF app already contains the complete runtime. Do not copy the helper's
    # framework facades over WindowsDesktop assemblies (notably WindowsBase.dll).
    foreach($name in @('NICRoute.Updater.exe','NICRoute.Updater.dll','NICRoute.Updater.deps.json','NICRoute.Updater.runtimeconfig.json','NICRoute.Updater.pdb')) {
        $file=Join-Path $updaterOutput $name
        if(Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $resolvedOutput -Force }
    }
    & "$PSScriptRoot\Package.ps1" -Output $output
    Write-Host "可执行程序：$root\$output\NICRoute.exe"
}
finally { Pop-Location }
