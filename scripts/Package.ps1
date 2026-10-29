param([string]$Output = 'dist\NICRoute-win-x64')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    if (-not (Test-Path -LiteralPath (Join-Path $Output 'NICRoute.exe'))) { throw '先构建并发布程序。' }
    Copy-Item -LiteralPath 'README.md','THIRD-PARTY-NOTICES.md','LICENSE' -Destination $Output -Force
    dotnet run --project tools/NICRoute.UpdateTool -c Release --no-build -- index ([IO.Path]::GetFullPath($Output))
    if($LASTEXITCODE -ne 0) { throw '安装包文件清单生成失败。' }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath ($Output + '.zip') -Force
    $sourceFiles = @('Directory.Build.props','NICRoute.slnx','README.md','THIRD-PARTY-NOTICES.md','LICENSE','.gitignore')
    $sourceRoots=@('src','tests','scripts','engine','tools','.github','updates','docs') | Where-Object { Test-Path -LiteralPath $_ }
    $sourceFiles += @(Get-ChildItem $sourceRoots -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -notin '.exe','.dll' } |
        ForEach-Object { $_.FullName })
    Add-Type -AssemblyName System.IO.Compression
    [xml]$project=Get-Content -LiteralPath 'src\NICRoute.App\NICRoute.App.csproj' -Raw
    $version=[string]($project.Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1)
    $archivePath = Join-Path $root "dist\NICRoute-$version-source.zip"
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $sourceFiles) {
            $absolute = [IO.Path]::GetFullPath($file)
            $entry = [IO.Path]::GetRelativePath($root,$absolute).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$absolute,$entry) | Out-Null
        }
    }
    finally { $zip.Dispose(); $stream.Dispose() }
    Write-Host "运行包：$Output.zip"
    Write-Host "源码包：$archivePath"
}
finally { Pop-Location }
