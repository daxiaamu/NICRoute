param([string]$PackageDirectory='dist\NICRoute-win-x64')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    $fixtures=Join-Path $root 'artifacts\update-fixtures'
    foreach($variant in @(@{name='old';code=1;version='0.1.0';fail='false'},@{name='new';code=2;version='0.2.0';fail='false'},@{name='bad';code=2;version='0.2.0';fail='true'})) {
        dotnet publish tests/UpdateFixture -c Release -r win-x64 --self-contained true -o (Join-Path $fixtures $variant.name) "-p:FixtureVersionCode=$($variant.code)" "-p:Version=$($variant.version)" "-p:FailStartup=$($variant.fail)" -p:NuGetAudit=false
        if($LASTEXITCODE -ne 0) {throw 'Updater fixture build failed.'}
    }
    dotnet run --project tests/NICRoute.Tests -c Release --no-build -- --updater-e2e ([IO.Path]::GetFullPath($PackageDirectory)) --fixtures $fixtures
    if($LASTEXITCODE -ne 0) {throw 'Updater end-to-end verification failed.'}
}
finally {Pop-Location}
