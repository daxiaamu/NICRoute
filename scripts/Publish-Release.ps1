param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$TargetCommit,
    [string]$Repository = $env:GITHUB_REPOSITORY
)
$ErrorActionPreference = 'Stop'
if (-not $env:GH_TOKEN -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Set GH_TOKEN and a valid repository before publishing.'
}
$directory = [IO.Path]::GetFullPath($ArtifactDirectory)
$version = $Tag.Substring(1)
$names = @('NICRoute-win-x64.zip',"NICRoute-$version-source.zip",'SHA256SUMS.txt')
$checksums = foreach ($name in $names[0..1]) {
    $hash = Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-Path $directory 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$api = "https://api.github.com/repos/$Repository"
$headers = @{ Authorization = 'Bearer ' + $env:GH_TOKEN; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NICRoute-release' }
function Get-Optional([string]$Uri) {
    try { Invoke-RestMethod -Uri $Uri -Headers $headers -TimeoutSec 60 }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }; return $null }
}
$tagRef = Get-Optional "$api/git/ref/tags/$Tag"
if ($tagRef) {
    $object = $tagRef.object
    while ($object.type -eq 'tag') { $object = (Invoke-RestMethod -Uri "$api/git/tags/$($object.sha)" -Headers $headers).object }
    if ($object.sha -ne $TargetCommit) { throw 'Existing tag points to another commit; refusing to move it.' }
}
$release = Get-Optional "$api/releases/tags/$Tag"
if (-not $release) {
    $release = @(Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers) | Where-Object tag_name -eq $Tag | Select-Object -First 1
}
$notes = @"
NICRoute 首个版本：为 Windows 应用设置不同网卡、全局默认出口和回退规则。

### 功能
- 按名称选择 Windows 应用（UWP / Microsoft Store / MSIX），也支持桌面程序 exe。
- 每应用首选网卡；不可用时选择不联网、全局默认或另一张网卡。回退目标也不可用则不联网，首选恢复后自动切回。
- 每 5 秒检测网卡，可选 TCP 连通性检测；支持配置导入/导出、托盘与连接日志。
- 基于 sing-box TUN 处理 IPv4 / IPv6 的 TCP、UDP 流量。

### 下载与使用
- **NICRoute-win-x64.zip**：Windows 10 / 11 x64 独立运行包，包含 .NET 10.0.14 和 sing-box 1.14.4，无需另装 .NET。
- **NICRoute-$version-source.zip**：NICRoute 源码、测试与构建脚本。
- **SHA256SUMS.txt**：上述两个压缩包的 SHA256。

解压整个运行包，运行 NICRoute.exe，添加规则并保存后启动分流。启动需要管理员权限，请保留 engine 目录。

### 验证及限制
- 本地已通过 85 项断言，包括实际引擎的进程识别、TCP / UDP 阻断和恢复、网卡绑定源 IP 及正常启停检查；发布前另经 GitHub Actions 构建与引擎集成测试。
- 测试未启用系统级 TUN。真实多网卡拔插、Windows 应用联网，以及 TUN 吞吐、延迟仍需实机验收。
- Windows 应用按包身份保存、以版本无关的包目录匹配；共享宿主、包外服务和代理代发流量不能保证归属于原应用。
- Windows 共享 DNS 服务使用全局出口。已有连接建议重启目标应用后验证。
- 不联网是运行期间的分流策略，不是持久防泄漏防火墙；停止、崩溃、重新应用期间不保证断网隔离。重新应用会短暂恢复系统路由。
- 本版本没有限速，也没有与 Proxifier 对比的性能基准结果。

[使用说明](https://github.com/$Repository/blob/$Tag/README.md)。对应应用提交：$TargetCommit。

源码采用 GPL-3.0-or-later，第三方许可随包提供。sing-box 完整上游源码：[v1.14.4](https://github.com/SagerNet/sing-box/archive/refs/tags/v1.14.4.tar.gz)，依赖见其中 go.mod / go.sum。NICRoute 与 SagerNet 无隶属或背书关系。
"@
if (-not $release) {
    $body = @{ tag_name=$Tag; target_commitish=$TargetCommit; name="NICRoute $Tag"; body=$notes; draft=$true; prerelease=$false } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
}
if ($release.draft) {
    $body = @{ body=$notes } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
}
foreach ($name in $names) {
    $file = Get-Item -LiteralPath (Join-Path $directory $name)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $existing = $release.assets | Where-Object name -eq $name
    if ($existing) {
        if ($existing.state -eq 'uploaded' -and $existing.digest -eq $digest -and $existing.size -eq $file.Length) { continue }
        if ($release.draft -and $existing.state -eq 'starter') {
            Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($existing.id)" -Headers $headers | Out-Null
        }
        else { throw "Refusing to replace a different existing asset: $name" }
    }
    $url = $release.upload_url.Split('{')[0] + '?name=' + [Uri]::EscapeDataString($name)
    $type = if ($name.EndsWith('.zip')) { 'application/zip' } else { 'text/plain' }
    Write-Host "Uploading $name ($($file.Length) bytes)"
    $asset = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType $type -InFile $file.FullName -TimeoutSec 600
    if ($asset.state -ne 'uploaded' -or $asset.digest -ne $digest -or $asset.size -ne $file.Length) { throw "Asset verification failed: $name" }
}
$release = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers
foreach ($name in $names) {
    $file = Get-Item -LiteralPath (Join-Path $directory $name)
    $asset = $release.assets | Where-Object name -eq $name
    $expected = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.state -ne 'uploaded' -or $asset.digest -ne $expected -or $asset.size -ne $file.Length) { throw "Release asset missing or incorrect: $name" }
}
if ($release.draft) {
    $body = @{ draft=$false; prerelease=$false; make_latest='true' } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
}
if ($release.draft) { throw 'Release was not published.' }
Write-Host "Published $($release.html_url)"
if ($env:GITHUB_STEP_SUMMARY) {
    "Published: $($release.html_url)`n`n" + ($checksums -join "`n") | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
