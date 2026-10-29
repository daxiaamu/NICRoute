param([string]$Destination = (Join-Path $PSScriptRoot '..\engine'))
$ErrorActionPreference = 'Stop'
$version = '1.14.4'
$archiveHash = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
$url = "https://github.com/SagerNet/sing-box/releases/download/v$version/sing-box-$version-windows-amd64.zip"
$destinationPath = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
$tempPath = Join-Path ([IO.Path]::GetTempPath()) ('NICRoute-engine-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempPath | Out-Null
try {
    $archive = Join-Path $tempPath 'engine.zip'
    Invoke-WebRequest -Uri $url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw '引擎下载 SHA256 不匹配。' }
    Expand-Archive -LiteralPath $archive -DestinationPath $tempPath
    $source = Join-Path $tempPath "sing-box-$version-windows-amd64"
    Copy-Item -LiteralPath (Join-Path $source 'sing-box.exe') -Destination $destinationPath -Force
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $destinationPath 'LICENSE.sing-box') -Force
    # libcronet.dll is for Naive proxy only; NICRoute uses direct/SOCKS and does not load it.
    $binaryHash = (Get-FileHash -LiteralPath (Join-Path $destinationPath 'sing-box.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ version = $version; archive_url = $url; archive_sha256 = $archiveHash; executable_sha256 = $binaryHash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationPath 'engine.lock.json') -Encoding utf8
    Write-Host "已下载并校验 sing-box $version (Windows x64)。"
}
finally {
    # Validate absolute target before any recursive cleanup, and keep cleanup in this shell.
    $resolvedTemp = [IO.Path]::GetFullPath($tempPath)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedTemp -Leaf).StartsWith('NICRoute-engine-')) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
