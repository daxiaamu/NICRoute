param([string]$Destination = (Join-Path $PSScriptRoot '..\engine'))
$ErrorActionPreference = 'Stop'
$version = '0.5.14'
$expected = 'ed47df4b630840e76dde1345fad9ddb0831ac7784b077fe520702f6774cb1a1d'
$directory = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$target = Join-Path $directory 'mudl.exe'
if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) {
    $temp = $target + '.download'
    Invoke-WebRequest -Uri "https://github.com/daxiaamu/mudl-c/releases/download/v$version/mudl-windows-x64.exe" -OutFile $temp
    if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $expected) { throw 'MUDL SHA256 校验失败。' }
    Move-Item -LiteralPath $temp -Destination $target -Force
}
$license=Join-Path $directory 'LICENSE.mudl'
if(-not (Test-Path -LiteralPath $license) -or (Get-Item -LiteralPath $license).Length -eq 0) {
    try { Invoke-WebRequest -Uri "https://raw.githubusercontent.com/daxiaamu/mudl-c/v$version/LICENSE" -OutFile $license }
    catch {
        & curl.exe --http1.1 --tlsv1.2 --tls-max 1.2 --retry 2 --retry-all-errors --fail --silent --show-error --location --output $license "https://raw.githubusercontent.com/daxiaamu/mudl-c/v$version/LICENSE"
        if($LASTEXITCODE -ne 0) { throw '无法取得 MUDL 官方许可证。' }
    }
}
[ordered]@{ version=$version; executable_sha256=$expected; source="https://github.com/daxiaamu/mudl-c/tree/v$version" } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'mudl.lock.json') -Encoding utf8
Write-Host "MUDL $version Windows x64 已获取并校验。"
