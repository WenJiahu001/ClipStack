param(
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    & $dotnet run --project tests\ClipStack.Tests\ClipStack.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '核心测试失败，已停止发布。' }
    $executable = Join-Path $publishDirectory 'ClipStack.exe'
    # 用户授权：只停止这个固定目录中的程序，避免影响其他副本。
    foreach ($runningApp in @(Get-Process -Name ClipStack -ErrorAction SilentlyContinue)) {
        if ([string]::Equals($runningApp.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $runningApp.Id -Force -ErrorAction Stop
            $runningApp.WaitForExit()
            Write-Output "已停止旧程序：$executable"
        }
    }
    & $dotnet publish src\ClipStack.App\ClipStack.App.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
        -p:DebugType=embedded -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw '单文件发布失败。' }
    & (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -Destination $publishDirectory
    if ($CertificateThumbprint) {
        $signTool = (Get-Command signtool.exe -ErrorAction Stop).Source
        & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $executable
        if ($LASTEXITCODE -ne 0) { throw '签名失败，已停止发布。' }
        & $signTool verify /pa $executable
        if ($LASTEXITCODE -ne 0) { throw '签名校验失败。' }
    }
    $qaDirectory = Join-Path $projectRoot 'artifacts\qa'
    New-Item -ItemType Directory -Path $qaDirectory -Force | Out-Null
    $report = Join-Path $qaDirectory 'single-file-smoke.json'
    # 模拟机器没有可发现的全局运行时；是否真正无运行时还须在干净虚拟机验收。
    $oldRoot = $env:DOTNET_ROOT
    $oldRootX64 = $env:DOTNET_ROOT_X64
    try {
        $env:DOTNET_ROOT = Join-Path $qaDirectory 'no-runtime'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $smokeArguments = '--smoke-test "' + $report + '" --data-dir "' + (Join-Path $qaDirectory 'smoke-data') + '"'
        $smokeProcess = Start-Process -FilePath $executable -ArgumentList $smokeArguments -WindowStyle Hidden -PassThru
        if (-not $smokeProcess.WaitForExit(30000)) {
            Stop-Process -Id $smokeProcess.Id -ErrorAction SilentlyContinue
            throw '单文件启动检查超时。'
        }
        if ($smokeProcess.ExitCode -ne 0 -or -not (Test-Path $report)) { throw '单文件启动检查失败。' }
        if (-not (Get-Content $report -Raw | ConvertFrom-Json).success) { throw '单文件数据读写验证失败。' }
    }
    finally { $env:DOTNET_ROOT = $oldRoot; $env:DOTNET_ROOT_X64 = $oldRootX64 }
    $panelReport = Join-Path $qaDirectory 'panel-checks.json'
    $panelArguments = '--panel-test "' + $panelReport + '" --data-dir "' + (Join-Path $qaDirectory ('panel-' + [Guid]::NewGuid().ToString('N'))) + '"'
    $panelProcess = Start-Process -FilePath $executable -ArgumentList $panelArguments -WindowStyle Hidden -PassThru
    if (-not $panelProcess.WaitForExit(45000)) {
        Stop-Process -Id $panelProcess.Id -ErrorAction SilentlyContinue
        throw '窗口交互检查超时。'
    }
    if ($panelProcess.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $panelReport -Raw | ConvertFrom-Json).success) {
        throw '窗口交互检查失败。'
    }
    Copy-Item -LiteralPath (Join-Path $qaDirectory 'floating-panel.png') -Destination (Join-Path $publishDirectory '悬浮效果.png') -Force
    Copy-Item -LiteralPath (Join-Path $qaDirectory 'inline-note.png') -Destination (Join-Path $publishDirectory '行内备注.png') -Force
    Copy-Item -LiteralPath $panelReport -Destination (Join-Path $publishDirectory '窗口检查.json') -Force
    Copy-Item -LiteralPath $report -Destination (Join-Path $publishDirectory '启动检查.json') -Force
    $performanceReport = Join-Path $qaDirectory 'performance-checks.json'
    $performanceArguments = '--performance-test "' + $performanceReport + '" --data-dir "' + (Join-Path $qaDirectory ('performance-' + [Guid]::NewGuid().ToString('N'))) + '"'
    $performanceProcess = Start-Process -FilePath $executable -ArgumentList $performanceArguments -WindowStyle Hidden -PassThru
    if (-not $performanceProcess.WaitForExit(45000)) {
        Stop-Process -Id $performanceProcess.Id -ErrorAction SilentlyContinue
        throw '性能回归检查超时。'
    }
    if ($performanceProcess.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $performanceReport -Raw | ConvertFrom-Json).success) {
        throw '性能回归检查失败。'
    }
    Copy-Item -LiteralPath $performanceReport -Destination (Join-Path $publishDirectory '性能回归检查.json') -Force
    $previewData = Join-Path $qaDirectory ('preview-' + [Guid]::NewGuid().ToString('N'))
    foreach ($preview in @(
        @{ Mode = 'preview'; Image = '主界面.png'; Density = 0 },
        @{ Mode = 'preview-settings'; Image = '设置界面.png'; Density = 0 },
        @{ Mode = 'preview'; Image = '主界面-紧凑.png'; Density = 1 },
        @{ Mode = 'preview'; Image = '主界面-宽松.png'; Density = 2 }
    )) {
        $densityData = Join-Path $previewData $preview.Density
        New-Item -ItemType Directory -Path $densityData -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $densityData 'settings.json'), (@{ MainWindowDensity = $preview.Density } | ConvertTo-Json))
        $previewArguments = '--' + $preview.Mode + ' "' + (Join-Path $publishDirectory $preview.Image) + '" --data-dir "' + $densityData + '"'
        $previewProcess = Start-Process -FilePath $executable -ArgumentList $previewArguments -WindowStyle Hidden -PassThru
        if (-not $previewProcess.WaitForExit(20000)) {
            Stop-Process -Id $previewProcess.Id -ErrorAction SilentlyContinue
            throw '界面预览超时。'
        }
        if ($previewProcess.ExitCode -ne 0) { throw '界面预览失败。' }
    }
    $checksum = (Get-FileHash $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory 'SHA256SUMS.txt'), "$checksum  ClipStack.exe`n")
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\使用说明.md') -Destination $publishDirectory -Force
    $releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    Copy-Item -LiteralPath (Join-Path $projectRoot "docs\releases\v$releaseVersion.md") -Destination (Join-Path $publishDirectory '发布说明.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\第三方组件.md') -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\验收记录.md') -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\架构说明.md') -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\性能优化报告.md') -Destination $publishDirectory -Force
    foreach ($benchmark in @('performance-before.json', 'performance-after.json')) {
        $benchmarkPath = Join-Path $qaDirectory $benchmark
        if (Test-Path -LiteralPath $benchmarkPath) { Copy-Item -LiteralPath $benchmarkPath -Destination $publishDirectory -Force }
    }
    $notesSql = Get-Content -LiteralPath (Join-Path $projectRoot 'src\ClipStack.Core\Migrations\002_favorite_notes.sql') -Raw
    $sortSql = Get-Content -LiteralPath (Join-Path $projectRoot 'src\ClipStack.Core\Migrations\003_sort_order.sql') -Raw
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory '数据库升级.sql'), "-- 仅适用于 v1 → v3。程序会自动完整备份并升级，无需手动执行；不可重复执行。`nBEGIN IMMEDIATE;`n$notesSql`n$sortSql`nCOMMIT;`n")
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory '数据库升级-v2到v3.sql'), "-- 仅适用于 v2 → v3。程序会自动完整备份并升级，无需手动执行；不可重复执行。`nBEGIN IMMEDIATE;`n$sortSql`nCOMMIT;`n")
    $archive = Join-Path $publishDirectory 'ClipStack-win-x64.zip'
    # 公开下载包使用明确清单，避免混入本机诊断路径、旧报告或用户临时文件。
    $archiveFiles = @('ClipStack.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'licenses',
        '使用说明.md', '发布说明.md', '第三方组件.md', 'SHA256SUMS.txt') | ForEach-Object { Join-Path $publishDirectory $_ }
    Compress-Archive -LiteralPath $archiveFiles -DestinationPath $archive -Force
    Write-Output "发布完成：$executable"
    Write-Output "完整发布包：$archive"
    Write-Output "SHA256：$checksum"
    if (-not $CertificateThumbprint) { Write-Warning '当前构建未签名，仅供内测；正式公开发布需提供受信任的签名证书。' }
}
finally { Pop-Location }
