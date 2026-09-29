<#
    把 CodexNetGuard 发布到 GitHub：建仓库 -> 推送 -> 建 Release -> 上传便携包

    用法：
      $env:GITHUB_TOKEN = 'ghp_xxx'          # 需要 repo 权限（经典 PAT）或 Fine-grained 的 Administration/Contents 写权限
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish-github.ps1 -Repo <用户名>/CodexNetGuard

      可选参数：
        -Private          建成私有仓库
        -Version 1.0.0    指定 Release 版本（默认读 exe 里的版本号）
        -SkipRelease      只推代码，不建 Release
        -Proxy http://127.0.0.1:10808   直连 GitHub 不通时走本地代理

    没有 token 时脚本仍会推代码（会用 Git 凭据管理器弹窗登录），但不会建 Release。
#>

param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Version,
    [switch]$Private,
    [switch]$SkipRelease,
    [string]$Proxy,
    [string]$Description = '一键修复「梯子 → cc-switch → Codex」三层链路：任意梯子客户端通用，免安装单文件，内置自测与一键还原。'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$topics = @('windows', 'proxy', 'v2ray', 'v2rayn', 'clash', 'mihomo', 'sing-box', 'codex', 'openai', 'portable', 'network-tools')

function Write-Step([string]$Text) { Write-Host ("== " + $Text) -ForegroundColor Cyan }
function Invoke-Git([string[]]$GitArgs) { & git.exe @GitArgs; if ($LASTEXITCODE -ne 0) { throw ("git " + ($GitArgs -join ' ') + " 失败") } }

# 统一走 UTF-8 字节发送 JSON：PowerShell 5.1 直接用字符串发中文会变问号
function Invoke-Api {
    param([string]$Method, [string]$Uri, $Object)
    $p = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($null -ne $Object) {
        $p['Body'] = [Text.Encoding]::UTF8.GetBytes(($Object | ConvertTo-Json -Depth 5))
        $p['ContentType'] = 'application/json; charset=utf-8'
    }
    if ($Proxy) { $p['Proxy'] = $Proxy }
    return Invoke-RestMethod @p
}

$repoFromStore = $false
if (-not $Token) {
    # 从 git 凭据管理器（git-credential-manager）里取已登录的 token，避免 token 出现在命令行里
    try {
        $credOut = ("protocol=https`nhost=github.com`n`n") | & git.exe credential fill 2>$null
        foreach ($line in $credOut) {
            if ($line -like 'password=*') { $Token = $line.Substring('password='.Length).Trim(); $repoFromStore = $true }
        }
    } catch { }
}

if ($Repo -notmatch '/') { $Repo = '' } # 只给了仓库名，稍后用已登录账号补全
elseif ($Repo -notmatch '^[\w.-]+/[\w.-]+$') { throw "仓库名要写成 owner/name，例如 yourname/CodexNetGuard" }
$branch = 'main'

# ---------------------------------------------------------------- 1 本地仓库
Write-Step '准备本地仓库'
if (-not (Test-Path (Join-Path $root '.git'))) {
    Invoke-Git @('init', '-b', $branch)
    Invoke-Git @('config', 'user.name', 'CodexNetGuard')
    Invoke-Git @('config', 'user.email', 'codexnetguard@users.noreply.github.com')
    Write-Host '  已 git init（提交身份用仓库级配置，没动你的全局 git 配置）'
}
    Invoke-Git @('add', '-A')
    $staged = (& git.exe diff --cached --name-only) -join ' '
if ($staged) {
        Invoke-Git @('commit', '-m', 'CodexNetGuard v1.0.0：梯子 → cc-switch → Codex 三层链路强制修复')
    Write-Host ("  已提交：" + $staged)
} else {
    Write-Host '  没有新的改动需要提交'
}
Invoke-Git @('branch', '-M', $branch)

# ---------------------------------------------------------------- 2 建远程仓库
$api = 'https://api.github.com'
$headers = @{ 'User-Agent' = 'CodexNetGuard'; 'Accept' = 'application/vnd.github+json' }
if ($Token) { $headers['Authorization'] = 'Bearer ' + $Token }
$invokeArgs = @{}
if ($Proxy) { $invokeArgs['Proxy'] = $Proxy }

if ($Token) {
    if (-not $Repo) {
        $me = Invoke-RestMethod -Uri "$api/user" -Headers $headers @invokeArgs
        $Repo = $me.login + '/CodexNetGuard'
        Write-Host ("  已登录账号：" + $me.login + "，目标仓库：" + $Repo)
    }
    Write-Step '检查远程仓库'
    $exists = $true
    try {
        Invoke-RestMethod -Uri "$api/repos/$Repo" -Headers $headers @invokeArgs | Out-Null
        Write-Host ("  已存在：" + $Repo)
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) { $exists = $false } else { throw }
    }
    if (-not $exists) {
        $body = @{ name = ($Repo -split '/')[1]; description = $Description; private = [bool]$Private; has_issues = $true; has_wiki = $false; auto_init = $false } | ConvertTo-Json
        $created = Invoke-Api 'Post' "$api/user/repos" @{ name = ($Repo -split '/')[1]; description = $Description; private = [bool]$Private; has_issues = $true; has_wiki = $false; auto_init = $false }
        Write-Host ("  已创建：" + $created.full_name + "（public=" + (-not $created.private) + "）")
        try { Invoke-Api 'Put' "$api/repos/$Repo/topics" @{ names = $topics } | Out-Null; Write-Host '  已设置 topics' } catch { Write-Host '  topics 设置失败（不影响发布）' -ForegroundColor Yellow }
    }
} else {
    Write-Host '  未提供 token：跳过自动建仓库，请确保远程仓库已经存在' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 3 推送
Write-Step '推送代码'
$remoteUrl = "https://github.com/$Repo.git"
if ($Token -and -not $repoFromStore) {
    # token 只出现在这一条命令里，不写进 .git/config
    Invoke-Git @('push', ("https://x-access-token:$Token@github.com/$Repo.git"), ("HEAD:refs/heads/" + $branch))
} else {
    # 凭据来自 git 凭据管理器：直接推送，由它负责认证
    try { Invoke-Git @('remote', 'set-url', 'origin', $remoteUrl) } catch { Invoke-Git @('remote', 'add', 'origin', $remoteUrl) }
    Invoke-Git @('push', '-u', 'origin', $branch)
}
Invoke-Git @('remote', 'set-url', 'origin', $remoteUrl)
Write-Host ("  已推送：" + $remoteUrl)

# ---------------------------------------------------------------- 4 Release
if ($SkipRelease) { Write-Step '已跳过 Release'; exit 0 }

$dist = Join-Path $root 'dist'
if (-not $Version) {
    $exe = Join-Path $root 'CodexNetGuard.exe'
    if (Test-Path $exe) { $Version = ((Get-Item $exe).VersionInfo.FileVersion -split '\.')[0..2] -join '.' }
}
if (-not $Version) { $Version = '1.0.0' }
$zip = Join-Path $dist ("CodexNetGuard-v$Version-portable.zip")
if (-not (Test-Path -LiteralPath $zip)) {
    Write-Host ("  找不到便携包 " + $zip) -ForegroundColor Yellow
    Write-Host '  先跑 tools\build-release.ps1 生成 dist 里的 zip，再重跑本脚本即可建 Release'
    exit 0
}

if (-not $Token) {
    Write-Host '  未提供 token：Release 需要手动创建，把这两个文件传上去即可：' -ForegroundColor Yellow
    Write-Host ("    " + $zip)
    Write-Host ("    " + (Join-Path $dist ("CodexNetGuard-v$Version-source.zip")))
    exit 0
}

Write-Step ("创建 Release v" + $Version)
$notes = ''
$changelog = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelog) { $notes = [IO.File]::ReadAllText($changelog) }
$release = Invoke-Api 'Post' "$api/repos/$Repo/releases" @{ tag_name = "v$Version"; name = "CodexNetGuard v$Version"; body = $notes; draft = $false; prerelease = $false }
Write-Host ("  " + $release.html_url)

Write-Step '上传便携包'
$name = Split-Path -Leaf $zip
$uploadUrl = "https://uploads.github.com/repos/$Repo/releases/" + $release.id + "/assets?name=" + [uri]::EscapeDataString($name)
Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $headers -InFile $zip -ContentType 'application/zip' @invokeArgs | Out-Null
Write-Host ("  已上传 " + $name)

Write-Step '发布完成'
Write-Host ("  仓库：" + "https://github.com/$Repo")
Write-Host ("  Release：" + $release.html_url)
Write-Host '  别忘了：Release 页面里把 zip 的 SHA256 贴到说明里，方便别人校验'
