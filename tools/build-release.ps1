<#
    CodexNetGuard 发布打包脚本

    流程：编译两个 exe -> --check 门禁 -> （默认）跑完整自测 -> 生成两个 zip

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1 -SkipTests
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1 -SkipBuild -SkipTests

    产物（dist 目录）：
      CodexNetGuard-v<版本>-portable.zip   用户版：两个 exe + README + CHANGELOG + SHA256SUMS
      CodexNetGuard-v<版本>-source.zip     源码版：src / tests / tools / README / CHANGELOG / 旧版脚本
#>

param(
    [switch]$SkipTests,
    [switch]$SkipBuild,
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $root 'src\CodexNetGuard.cs'
$gui = Join-Path $root 'CodexNetGuard.exe'
$cli = Join-Path $root 'CodexNetGuard-cli.exe'
$dist = Join-Path $root $OutDir
$stage = Join-Path $dist '_stage'

function Write-Step([string]$Text) { Write-Host ("== " + $Text) -ForegroundColor Cyan }

# 先压到临时文件名再替换：这样目标 zip 被别的程序占用（或中途打断）也不会让旧包消失
function New-PackageZip {
    param([string]$SourceDir, [string]$Destination)
    $tmp = $Destination + '.tmp.zip'
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    Compress-Archive -Path $SourceDir -DestinationPath $tmp -CompressionLevel Optimal
    try {
        Move-Item -LiteralPath $tmp -Destination $Destination -Force
        return $Destination
    } catch {
        $alt = $Destination -replace '\.zip$', '-new.zip'
        if (Test-Path -LiteralPath $alt) { Remove-Item -LiteralPath $alt -Force }
        Move-Item -LiteralPath $tmp -Destination $alt -Force
        Write-Host ("  ! " + (Split-Path -Leaf $Destination) + " 被其它程序占用，已改存为 " + (Split-Path -Leaf $alt)) -ForegroundColor Yellow
        return $alt
    }
}

# ---------------------------------------------------------------- 编译
if (-not $SkipBuild) {
    Write-Step '编译'
    if (-not (Test-Path $csc)) { throw "找不到 csc：$csc" }
    $o1 = & $csc /nologo /warn:4 /target:winexe /out:$gui /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll $src 2>&1
    if ($LASTEXITCODE -ne 0) { throw "GUI 编译失败：`n$o1" }
    $o2 = & $csc /nologo /warn:4 /target:exe /out:$cli /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll $src 2>&1
    if ($LASTEXITCODE -ne 0) { throw "CLI 编译失败：`n$o2" }
    if ($o1 -or $o2) { Write-Host ("编译告警：`n" + ($o1 + $o2)) -ForegroundColor Yellow }
}

$version = (Get-Item $gui).VersionInfo.FileVersion
$version = ($version -split '\.')[0..2] -join '.'
Write-Step ("版本 " + $version)

# ---------------------------------------------------------------- 快速门禁
Write-Step '运行 --check 门禁'
& $cli --check --quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $root 'CodexNetGuard-report.txt') | Select-String -Pattern 'FAIL|结果:' | ForEach-Object { Write-Host $_.Line }
    throw "健康检查未通过（退出码 $LASTEXITCODE），已中止打包"
}
Write-Host '检查通过'

# ---------------------------------------------------------------- 完整自测
$testSummary = '未运行（-SkipTests）'
if (-not $SkipTests) {
    Write-Step '运行完整自测（会临时停一次梯子，约 6 分钟）'
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\run-selftest.ps1') 2>&1
    $log | Select-String -Pattern '\[PASS\]|\[FAIL\]|== 结果' | ForEach-Object { Write-Host $_.Line }
    $line = ($log | Select-String -Pattern '== 结果').Line
    if (-not $line) { throw '自测没有输出结果行，视为失败' }
    $testSummary = $line
    if ($line -match '失败\s+([1-9]\d*)') { throw ("自测存在失败：" + $line) }
}

# ---------------------------------------------------------------- 整理产物
Write-Step '整理打包目录'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$pkgUser = Join-Path $stage 'CodexNetGuard'
$pkgSrc = Join-Path $stage ("CodexNetGuard-v$version-source")
New-Item -ItemType Directory -Force -Path $pkgUser, $pkgSrc | Out-Null

# 用户版
Copy-Item $gui, $cli (Join-Path $pkgUser '.') -Force
Copy-Item (Join-Path $root 'README.md') $pkgUser -Force
Copy-Item (Join-Path $root 'CHANGELOG.md') $pkgUser -Force
$sums = @()
foreach ($f in @($gui, $cli)) {
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
    $sums += ($h + '  ' + (Split-Path -Leaf $f))
}
Set-Content -LiteralPath (Join-Path $pkgUser 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

# 源码版
Copy-Item (Join-Path $root 'src') $pkgSrc -Recurse -Force
Copy-Item (Join-Path $root 'tests') $pkgSrc -Recurse -Force
Copy-Item (Join-Path $root 'tools') $pkgSrc -Recurse -Force
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') $pkgSrc -Force
foreach ($extra in @('CodexNetGuard.ps1', 'Codex CLI(带网络守护).cmd', 'config.json')) {
    $p = Join-Path $root $extra
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p $pkgSrc -Force }
}

Get-ChildItem -Recurse -File $stage | Where-Object { $_.Name -like '*.bak-*' -or $_.Name -eq 'CodexNetGuard.ini' } | Remove-Item -Force

# ---------------------------------------------------------------- 压缩
Write-Step '生成 zip'
$zipUser = Join-Path $dist ("CodexNetGuard-v$version-portable.zip")
$zipSrc = Join-Path $dist ("CodexNetGuard-v$version-source.zip")
try {
    $madeUser = New-PackageZip $pkgUser $zipUser
    $madeSrc = New-PackageZip $pkgSrc $zipSrc
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

# ---------------------------------------------------------------- 汇总
Write-Step '打包完成'
foreach ($z in @($madeUser, $madeSrc)) {
    $i = Get-Item -LiteralPath $z
    Write-Host ("  " + $i.Name + "  " + [math]::Round($i.Length / 1KB, 1) + " KB")
}
Write-Host ("  自测：" + $testSummary)
Write-Host '  SHA256：'
$sums | ForEach-Object { Write-Host ("    " + $_) }
Write-Host ("  zip 校验：")
foreach ($z in @($madeUser, $madeSrc)) {
    Write-Host ("    " + (Get-FileHash -LiteralPath $z -Algorithm SHA256).Hash + "  " + (Split-Path -Leaf $z))
}
