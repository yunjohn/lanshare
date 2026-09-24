<#
.SYNOPSIS
    打包 LAN Transfer 便携版（Windows x64）。

.DESCRIPTION
    1. 读取 Directory.Build.props 中的版本号
    2. 清理 dist 目录
    3. 运行全部测试（可用 -SkipTests 跳过）
    4. dotnet publish 出可运行目录
    5. 生成 zip 压缩包

    默认产出「自包含便携版」——目标机器无需安装 .NET 运行时。

.PARAMETER Configuration
    编译配置，默认 Release。

.PARAMETER Runtime
    运行时标识，默认 win-x64。

.PARAMETER SelfContained
    是否自包含。默认 $true（免装 .NET，体积较大）。
    传 $false 得到框架依赖版（体积小，但用户需自装 .NET 10 Desktop Runtime）。

.PARAMETER SkipTests
    跳过测试，仅打包。

.EXAMPLE
    pwsh -File publish.ps1

.EXAMPLE
    pwsh -File publish.ps1 -SelfContained:$false -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 让 dotnet / MSBuild 的中文输出正确解码（Windows PowerShell 5.1 默认非 UTF-8）。
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
}
catch {
    # 某些宿主（如无控制台的 CI）不支持设置，忽略即可
}

# 沙箱/CI 环境下 PROCESSOR_ARCHITECTURE 可能为空，部分 MSBuild 目标会因此退出。
if ([string]::IsNullOrEmpty($env:PROCESSOR_ARCHITECTURE)) {
    $env:PROCESSOR_ARCHITECTURE = 'AMD64'
}

$repoRoot = $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\LanTransfer.App\LanTransfer.App.csproj'
$solution = Join-Path $repoRoot 'LanTransfer.sln'
$distRoot = Join-Path $repoRoot 'dist'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Assert-LastExitCode([string]$what) {
    if ($LASTEXITCODE -ne 0) {
        throw "$what 失败（退出码 $LASTEXITCODE）。"
    }
}

# ---------------------------------------------------------------- 前置检查

Write-Step '检查环境'

if (-not (Test-Path $appProject)) {
    throw "未找到项目文件：$appProject"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '未找到 dotnet 命令。请先安装 .NET SDK 10.0.4xx 及以上版本。'
}

$sdkVersion = (& dotnet --version).Trim()
Write-Host "dotnet SDK : $sdkVersion"
Write-Host "项目       : $appProject"
Write-Host "运行时     : $Runtime"
Write-Host "自包含     : $SelfContained"
Write-Host "配置       : $Configuration"

# ---------------------------------------------------------------- 读取版本号

Write-Step '读取版本号'

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$version = '1.0.0'

if (Test-Path $propsPath) {
    [xml]$props = Get-Content -Path $propsPath -Raw
    $versionNode = $props.Project.PropertyGroup.Version
    if ($versionNode) {
        $version = ([string]$versionNode).Trim()
    }
}

Write-Host "版本       : $version"

$packageName = "LanTransfer-$version-$Runtime-portable"
$publishDir = Join-Path $distRoot $packageName
$zipPath = Join-Path $distRoot "$packageName.zip"

# ---------------------------------------------------------------- 清理

Write-Step '清理 dist 目录'

if (Test-Path $distRoot) {
    Remove-Item -Path $distRoot -Recurse -Force
    Write-Host "已删除 $distRoot"
}

New-Item -Path $distRoot -ItemType Directory -Force | Out-Null

# ---------------------------------------------------------------- 测试

if (-not $SkipTests) {
    Write-Step "运行测试（$Configuration）"
    & dotnet test $solution -c $Configuration --nologo
    Assert-LastExitCode '测试'
    Write-Host '全部测试通过。' -ForegroundColor Green
}
else {
    Write-Host '已跳过测试。' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 发布

Write-Step "发布 $Runtime（self-contained=$SelfContained）"

$publishArgs = @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $publishDir,
    '--nologo',
    "-p:SelfContained=$SelfContained",
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false'
)

& dotnet @publishArgs
Assert-LastExitCode '发布'

if (-not (Test-Path (Join-Path $publishDir 'LanTransfer.exe'))) {
    throw "发布目录中未找到 LanTransfer.exe：$publishDir"
}

# ---------------------------------------------------------------- 附带文档

Write-Step '复制文档与说明'

foreach ($doc in @('README.md')) {
    $source = Join-Path $repoRoot $doc
    if (Test-Path $source) {
        Copy-Item -Path $source -Destination $publishDir -Force
    }
}

$docsDir = Join-Path $repoRoot 'docs'
if (Test-Path $docsDir) {
    Copy-Item -Path $docsDir -Destination $publishDir -Recurse -Force
}

# 便携版说明
$portableReadme = @"
LAN Transfer $version — Windows x64 便携版
================================================

直接双击 LanTransfer.exe 运行，无需安装。

首次运行
  1. Windows 防火墙会弹窗 —— 必须勾选「专用网络」并允许访问。
     否则对端无法连接（UDP 39520 / TCP 39521）。
  2. 若误点取消，用管理员 PowerShell 执行：
       New-NetFirewallRule -DisplayName "LAN Transfer (UDP-In)" -Direction Inbound -Protocol UDP -LocalPort 39520 -Action Allow -Profile Private
       New-NetFirewallRule -DisplayName "LAN Transfer (TCP-In)" -Direction Inbound -Protocol TCP -LocalPort 39521 -Action Allow -Profile Private

数据目录
  %LOCALAPPDATA%\LanTransfer\  （数据库、配置、证书、日志）

端口
  UDP 39520  设备发现
  TCP 39521  文件传输（HTTPS）

打包信息
  运行时     : $Runtime
  自包含     : $SelfContained
  构建配置   : $Configuration
  打包时间   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

详细文档见 docs\ 目录，或项目根目录 README.md。
"@

$portableReadmePath = Join-Path $publishDir '使用说明.txt'
$portableReadme | Out-File -FilePath $portableReadmePath -Encoding UTF8

# ---------------------------------------------------------------- 压缩

Write-Step '生成 zip'

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "已生成 $zipPath"

# ---------------------------------------------------------------- 汇总

$dirSize = (Get-ChildItem -Path $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$zipSize = (Get-Item $zipPath).Length

function Format-Size([double]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N2} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:N2} KB' -f ($bytes / 1KB)) }
    return ('{0} B' -f $bytes)
}

Write-Step '打包完成'

Write-Host ''
Write-Host "  便携版目录 : $publishDir"
Write-Host "  目录大小   : $(Format-Size $dirSize)"
Write-Host "  压缩包     : $zipPath"
Write-Host "  压缩包大小 : $(Format-Size $zipSize)"
Write-Host ''
Write-Host '  运行方式   : 解压后双击 LanTransfer.exe' -ForegroundColor Green
Write-Host ''
