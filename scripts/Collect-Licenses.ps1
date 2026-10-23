param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$licenses = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$lock = Get-Content (Join-Path $projectRoot 'src\ClipStack.Core\packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add('ClipStack third-party notices')
$notices.Add('The accompanying licenses directory is part of the distribution.')
$seen = [System.Collections.Generic.HashSet[string]]::new()
foreach ($framework in $lock.dependencies.Values) {
    foreach ($entry in $framework.GetEnumerator()) {
        $id = $entry.Key
        $version = $entry.Value.resolved
        if (-not $version -or -not $seen.Add("$id/$version")) { continue }
        $packageDirectory = Join-Path $nugetRoot ($id.ToLowerInvariant() + '\' + $version)
        [xml]$nuspec = Get-Content (Join-Path $packageDirectory ($id.ToLowerInvariant() + '.nuspec')) -Raw
        $metadata = $nuspec.package.metadata
        $expression = $metadata.license.InnerText
        $notice = "$id $version`n$($metadata.copyright)`nLicense: $expression`nSource: $($metadata.repository.url)`n"
        $notices.Add($notice)
    }
}
foreach ($runtime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $directory = Get-ChildItem (Join-Path $nugetRoot $runtime) -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $directory) { throw "运行时许可目录缺失：$runtime" }
    Get-ChildItem $directory.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses ($runtime + '-' + $_.Name)) -Force
    }
}
foreach ($name in @('MIT.txt', 'Apache-2.0.txt', 'NOTICE.txt', 'DOTNET-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('docs\licenses\' + $name)) -Destination $licenses -Force
}
[System.IO.File]::WriteAllText((Join-Path $Destination 'THIRD-PARTY-NOTICES.txt'), ($notices -join "`n`n"))
