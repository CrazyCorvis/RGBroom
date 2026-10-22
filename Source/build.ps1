param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot 'RGBroom.csproj'
$taskBuildRoot = Join-Path $PSScriptRoot '.build'
$taskDebug = Join-Path $taskBuildRoot 'debug'
$taskObj = (Join-Path $taskBuildRoot 'obj') + [IO.Path]::DirectorySeparatorChar
$taskPublish = Join-Path (Split-Path $PSScriptRoot -Parent) 'App'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MAGICHOME_TEST_REPORT = Join-Path $taskBuildRoot 'test-report.txt'
$env:MAGICHOME_UI_PREVIEW = Join-Path $taskBuildRoot 'interface.png'
New-Item -ItemType Directory -Force -Path $taskBuildRoot | Out-Null
& $Dotnet build $taskProject -c Debug "-p:BaseIntermediateOutputPath=$taskObj" -o $taskDebug
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed' }
& $Dotnet (Join-Path $taskDebug 'RGBroom.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Functional checks failed. See test-report.txt' }
& $Dotnet (Join-Path $taskDebug 'RGBroom.dll') --ui-test
if ($LASTEXITCODE -ne 0) { throw 'UI checks failed. See test-report.txt' }
& $Dotnet publish $taskProject -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' "-p:BaseIntermediateOutputPath=$taskObj" -o $taskPublish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Write-Output "Ready: $taskPublish\RGBroom.exe"

