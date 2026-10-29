param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][long]$ReleaseId,
    [string]$Repository=$env:GITHUB_REPOSITORY
)
$ErrorActionPreference='Stop'
if(-not $env:GH_TOKEN -or $Repository -ne 'daxiaamu/NICRoute') { throw 'Expected NICRoute repository and GH_TOKEN.' }
$directory=[IO.Path]::GetFullPath($ArtifactDirectory)
$sourceRoot=Split-Path $directory -Parent
$tool=Join-Path $sourceRoot 'tools\NICRoute.UpdateTool\bin\Release\net10.0-windows\NICRoute.UpdateTool.dll'
$headers=@{Authorization='Bearer '+$env:GH_TOKEN;Accept='application/vnd.github+json';'User-Agent'='NICRoute-updates';'X-GitHub-Api-Version'='2022-11-28'}
$api="https://api.github.com/repos/$Repository"
function Api([string]$Path,[string]$Method='Get',$Body=$null) {
    $parameters=@{Uri="$api/$Path";Headers=$headers;Method=$Method;TimeoutSec=60}
    if($null -ne $Body) {$parameters.ContentType='application/json; charset=utf-8';$parameters.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12))}
    Invoke-RestMethod @parameters
}
function Content([string]$Path) {
    try {$result=Api "contents/$Path`?ref=main";[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($result.content))}
    catch {if([int]$_.Exception.Response.StatusCode -ne 404){throw};return $null}
}
$release=Api "releases/$ReleaseId"
if($release.draft -or $release.tag_name -ne $Tag) {throw 'Release must be published under its final tag before publishing update metadata.'}
$asset=$release.assets | Where-Object name -eq 'NICRoute-win-x64.zip'
if(@($asset).Count -ne 1 -or $asset.state -ne 'uploaded') {throw 'Expected exactly one Windows package.'}
$zip=Join-Path $directory 'NICRoute-win-x64.zip'
$sha=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if($asset.digest -ne 'sha256:'+$sha -or $asset.size -ne (Get-Item $zip).Length){throw 'Local package does not match the published artifact.'}
Add-Type -AssemblyName System.IO.Compression
$inspect=Join-Path $directory ('inspect-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $inspect | Out-Null
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry=$archive.GetEntry('NICRoute.dll')
    if(-not $entry -or $entry.Length -gt 16MB){throw 'Invalid application assembly.'}
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $inspect 'NICRoute.dll'))
} finally {$archive.Dispose()}
$identityJson=& dotnet $tool inspect (Join-Path $inspect 'NICRoute.dll')
if($LASTEXITCODE -ne 0){throw 'Could not read actual PE identity.'}
$identity=($identityJson -join "`n")|ConvertFrom-Json
if($identity.appId -ne 'NICRoute.Windows' -or $identity.platform -ne 'win-x64' -or $Tag -ne 'v'+$identity.versionName){throw 'Release tag and actual package identity differ.'}
$channel=if($release.prerelease){'beta'}else{'stable'}
$policies=Get-Content -LiteralPath (Join-Path $sourceRoot 'updates\policy.json') -Raw|ConvertFrom-Json
$policy=$policies.$channel
if(-not $policy -or $policy.policyRevision -isnot [ValueType] -or $policy.policyRevision -lt 1 -or
    $policy.maxForcedVersionCode -lt 0 -or $policy.maxForcedVersionCode -ge $identity.versionCode -or -not $policy.reason){throw 'Invalid checked-in update policy.'}
$priorJson=Content "updates/$channel/latest.json"
if($priorJson){
    $priorPointer=$priorJson|ConvertFrom-Json
    if($priorPointer.policyRevision -gt $policy.policyRevision){throw 'Refusing policy rollback.'}
    $priorManifestJson=Content $priorPointer.manifestPath
    if(-not $priorManifestJson){throw 'Existing immutable update manifest is unavailable.'}
    $priorDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($priorManifestJson))).ToLowerInvariant()
    if($priorDigest -ne $priorPointer.manifestSha256){throw 'Existing update manifest digest is inconsistent.'}
    $priorManifest=$priorManifestJson|ConvertFrom-Json
    if($priorManifest.versionCode -gt $identity.versionCode -or $priorManifest.maxForcedVersionCode -gt $policy.maxForcedVersionCode){throw 'Refusing version or forced-boundary rollback.'}
    if($priorPointer.policyRevision -eq $policy.policyRevision){
        if($priorManifest.versionCode -eq $identity.versionCode -and $priorManifest.versionName -eq $identity.versionName -and
            $priorManifest.sha256 -eq $sha -and $priorManifest.maxForcedVersionCode -eq $policy.maxForcedVersionCode -and
            $priorManifest.changelog -eq $release.body){Write-Host 'This immutable revision already describes the same released artifact.';return}
        throw 'Increment policyRevision before changing an existing update revision.'
    }
}
$official=$asset.browser_download_url
if(-not $official.StartsWith("https://github.com/$Repository/releases/download/$Tag/",[StringComparison]::Ordinal)){throw 'Unexpected official asset URL.'}
$pool=Get-Content -LiteralPath (Join-Path $sourceRoot 'updates\download-sources.json') -Raw|ConvertFrom-Json
$repositoryParts=$Repository.Split('/')
$candidates=@($official)+@($pool.urlTemplates|ForEach-Object{$_.Replace('{officialUrl}',$official).Replace('{owner}',$repositoryParts[0]).Replace('{repo}',$repositoryParts[1]).Replace('{tag}',$Tag).Replace('{asset}','NICRoute-win-x64.zip')})
$expectedSize=[long]$asset.size
# Public, anonymous probes hash every byte. A successful HEAD or a 200 HTML page
# does not qualify a mirror. No repository token is sent to these hosts.
$probes=$candidates|Select-Object -Unique|ForEach-Object -Parallel {
    $url=$_;$expected=$using:sha;$size=$using:expectedSize;$watch=[Diagnostics.Stopwatch]::StartNew()
    $handler=[Net.Http.SocketsHttpHandler]::new();$handler.AllowAutoRedirect=$false;$handler.ConnectTimeout=[TimeSpan]::FromSeconds(8)
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
    $timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(75));$response=$null
    try {
        $current=[Uri]$url
        for($redirect=0;$redirect -lt 6;$redirect++){
            if($current.Scheme -ne 'https' -or $current.UserInfo){throw 'Non-HTTPS source or redirect.'}
            $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$current)
            try {$response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$timeout.Token).GetAwaiter().GetResult()}finally{$request.Dispose()}
            if([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400 -and $response.Headers.Location){
                $location=$response.Headers.Location;$current=if($location.IsAbsoluteUri){$location}else{[Uri]::new($current,$location)};$response.Dispose();$response=$null;continue
            }
            break
        }
        if(-not $response -or -not $response.IsSuccessStatusCode -or $current.Scheme -ne 'https'){throw 'Unavailable source.'}
        if($response.Content.Headers.ContentType.MediaType -like '*html*'){throw 'HTML response.'}
        if($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $size){throw 'Size mismatch.'}
        $stream=$response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $buffer=[byte[]]::new(65536);[long]$total=0
            while(($read=$stream.ReadAsync($buffer,0,$buffer.Length,$timeout.Token).GetAwaiter().GetResult()) -gt 0){
                $total+=$read;if($total -gt $size){throw 'Oversized source.'};$hash.AppendData($buffer,0,$read)
            }
            $actual=[Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            if($total -ne $size -or $actual -ne $expected){throw 'SHA256 mismatch.'}
        } finally {$hash.Dispose();$stream.Dispose()}
        [pscustomobject]@{url=$url;ok=$true;elapsed=$watch.ElapsedMilliseconds;error=''}
    }catch{[pscustomobject]@{url=$url;ok=$false;elapsed=$watch.ElapsedMilliseconds;error=$_.Exception.Message}}
    finally{if($response){$response.Dispose()};$timeout.Dispose();$client.Dispose()}
} -ThrottleLimit 4
$probes|ForEach-Object{Write-Host "$(([Uri]$_.url).Host): verified=$($_.ok) $($_.error)"}
if(-not ($probes|Where-Object{$_.url -eq $official -and $_.ok})){throw 'Official artifact is not anonymously downloadable with its expected hash.'}
$mirrors=@($probes|Where-Object{$_.url -ne $official -and $_.ok}|Sort-Object elapsed|Select-Object -First ([Math]::Clamp([int]$pool.maximumMirrors,0,15))|ForEach-Object url)
$published=if($release.published_at -is [DateTime]){[DateTimeOffset]$release.published_at}else{[DateTimeOffset]::Parse($release.published_at,[Globalization.CultureInfo]::InvariantCulture)}
$manifest=[ordered]@{schemaVersion=1;appId=$identity.appId;platform=$identity.platform;channel=$channel;policyRevision=[long]$policy.policyRevision;
    versionCode=[long]$identity.versionCode;versionName=$identity.versionName;maxForcedVersionCode=[long]$policy.maxForcedVersionCode;
    publishedAt=$published.ToUniversalTime().ToString('O');changelog=[string]$release.body;sha256=$sha;size=$expectedSize;urls=@($official)+$mirrors;releaseUrl=$release.html_url}
$manifestJson=$manifest|ConvertTo-Json -Depth 8
$manifestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifestJson))).ToLowerInvariant()
$manifestPath="updates/$channel/manifests/$($policy.policyRevision)-$manifestHash.json"
$pointer=[ordered]@{schemaVersion=1;channel=$channel;policyRevision=[long]$policy.policyRevision;manifestPath=$manifestPath;manifestSha256=$manifestHash;expiresAt=$published.AddYears(1).ToUniversalTime().ToString('O')}
$pointerJson=$pointer|ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $inspect 'manifest.json'),$manifestJson,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $inspect 'latest.json'),$pointerJson,[Text.UTF8Encoding]::new($false))
& dotnet $tool validate-feed $inspect
if($LASTEXITCODE -ne 0){throw 'Client schema validation rejected generated update metadata.'}
for($attempt=0;$attempt -lt 3;$attempt++){
    $head=Api 'git/ref/heads/main'
    $oldJson=Content "updates/$channel/latest.json"
    if($oldJson){
        $old=$oldJson|ConvertFrom-Json
        if($old.policyRevision -gt $policy.policyRevision){throw 'Refusing policy rollback.'}
        if($old.policyRevision -eq $policy.policyRevision){
            if($old.manifestSha256 -eq $manifestHash){Write-Host 'Identical update metadata is already published.';return}
            throw 'A policy revision is immutable. Increment the checked-in revision before publishing changed metadata.'
        }
        $previousJson=Content $old.manifestPath
        if(-not $previousJson){throw 'Previous manifest unavailable; refusing to overwrite policy.'}
        $previous=$previousJson|ConvertFrom-Json
        if($previous.versionCode -gt $identity.versionCode -or $previous.maxForcedVersionCode -gt $policy.maxForcedVersionCode){throw 'Refusing version or forced-boundary rollback.'}
    }
    $commit=Api "git/commits/$($head.object.sha)"
    $tree=Api 'git/trees' 'Post' @{base_tree=$commit.tree.sha;tree=@(
        @{path=$manifestPath;mode='100644';type='blob';content=$manifestJson},
        @{path="updates/$channel/latest.json";mode='100644';type='blob';content=$pointerJson})}
    $next=Api 'git/commits' 'Post' @{message="chore: publish $channel update $Tag (revision $($policy.policyRevision))";tree=$tree.sha;parents=@($head.object.sha)}
    try {Api 'git/refs/heads/main' 'Patch' @{sha=$next.sha;force=$false}|Out-Null;Write-Host "Update feed published: $manifestPath";return}
    catch {if([int]$_.Exception.Response.StatusCode -ne 422 -or $attempt -eq 2){throw}}
}
throw 'Could not advance the update pointer safely.'
