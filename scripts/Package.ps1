param([string]$Output = 'dist\NICRoute-win-x64')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    if (-not (Test-Path -LiteralPath (Join-Path $Output 'NICRoute.exe'))) { throw '先构建并发布程序。' }
    Copy-Item -LiteralPath 'README.md','THIRD-PARTY-NOTICES.md','LICENSE' -Destination $Output -Force
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath ($Output + '.zip') -Force
    $sourceFiles = @('Directory.Build.props','NICRoute.slnx','README.md','THIRD-PARTY-NOTICES.md','LICENSE','.gitignore')
    $sourceFiles += @(Get-ChildItem 'src','tests','scripts','engine' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -notin '.exe','.dll' } |
        ForEach-Object { $_.FullName })
    Add-Type -AssemblyName System.IO.Compression
    $archivePath = Join-Path $root 'dist\NICRoute-0.1.0-source.zip'
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
