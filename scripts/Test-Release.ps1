param([string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\publish\win-x64\ClipStack.exe' }
$qaDirectory = Join-Path $projectRoot 'artifacts\qa'
$testDirectory = Join-Path $qaDirectory ('中文 路径验证\run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$copy = Join-Path $testDirectory 'ClipStack 中文版.exe'
Copy-Item -LiteralPath $Executable -Destination $copy
$savedVariables = @{}
foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','DOTNET_BUNDLE_EXTRACT_BASE_DIR')) {
    $savedVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:DOTNET_ROOT = Join-Path $testDirectory 'missing-runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $testDirectory 'native-cache'
    $coldReport = Join-Path $testDirectory 'cold-smoke.json'
    $arguments = '--smoke-test "' + $coldReport + '" --data-dir "' + (Join-Path $testDirectory '用户 数据') + '"'
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $testProcess = Start-Process -FilePath $copy -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(30000)) { Stop-Process -Id $testProcess.Id; throw '中文路径冷启动超时。' }
    $timer.Stop()
    if ($testProcess.ExitCode -ne 0) { throw '中文路径冷启动失败。' }
    $cold = Get-Content $coldReport -Raw | ConvertFrom-Json
    # Windows 自包含 singlefilehost 静态链接 CoreCLR，不会加载独立 coreclr.dll。
    $localRuntime = $cold.runtimeDirectory -and ([System.IO.Path]::GetFullPath($cold.runtimeDirectory).TrimEnd('\') -eq $testDirectory.TrimEnd('\'))
    $extractedRuntime = $cold.runtimeModulePath -and $cold.runtimeModulePath.StartsWith($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR, [System.StringComparison]::OrdinalIgnoreCase)
    $embeddedRuntime = -not $cold.runtimeModulePath -and -not $cold.frameworkDepsFile -and $localRuntime
    if (-not $cold.success -or -not ($embeddedRuntime -or $extractedRuntime)) {
        throw '运行时未从应用本身加载。'
    }
    if (-not $cold.sqliteModulePath.StartsWith($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SQLite 未从应用自己的原生缓存加载。'
    }
    $coldMilliseconds = $timer.ElapsedMilliseconds
    $timer.Restart()
    $warmProcess = Start-Process -FilePath $copy -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $warmProcess.WaitForExit(30000)) { Stop-Process -Id $warmProcess.Id; throw '重启验证超时。' }
    $timer.Stop()
    if ($warmProcess.ExitCode -ne 0) { throw '重启验证失败。' }
    $report = [ordered]@{
        success = $true
        os = $cold.os
        architecture = $cold.architecture
        unicodeAndSpacePath = $true
        freshNativeExtraction = $true
        bundledRuntimeVerified = $true
        bundledSqliteVerified = $true
        coldSmokeMilliseconds = $coldMilliseconds
        warmSmokeMilliseconds = $timer.ElapsedMilliseconds
        elapsedIncludesProcessExitAndStorageChecks = $true
        cleanMachineTest = $false
        dataDirectory = $testDirectory
    }
    $report | ConvertTo-Json | Set-Content (Join-Path $qaDirectory 'release-portability.json') -Encoding utf8
    $report
}
finally {
    foreach ($entry in $savedVariables.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
}
