<#
    CodexNetGuard 自测脚本（上市前跑一遍）

    覆盖场景：
      1  健康态：--check 返回 0
      2  系统代理指向死端口 -> 自动指回实探可用端口
      3  ProxyOverride 脏数据 -> 去重规范化
      4  假 PAC -> 清除并备份
      5  NO_PROXY 脏值 -> 规范化
      6  HTTPS_PROXY 指死端口 -> 改写/清理
      7  cc-switch 开关开着但端口没监听 -> 强制关闭
      8  客户端配置只读 -> 报告写入失败并回滚
      9  多客户端：梯子已就绪时不被后起的端口抢走
      10 SOCKS5-only 代理 -> 写成 socks=127.0.0.1:端口
      11 诱饵端口（能连但不会代理）不被选中
      12 完全无可用代理 -> 关闭系统代理，退出码 1
      13 幂等：连续修复不产生重复项/备份膨胀，最终恢复健康

    注意：第 9~12 项会临时停掉正在运行的梯子客户端，跑完会自动拉起。
#>

param(
    [switch]$SkipLadderStop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cli = Join-Path $root 'CodexNetGuard-cli.exe'
$report = Join-Path $root 'CodexNetGuard-report.txt'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$tmp = Join-Path $env:TEMP ('cng-selftest-' + (Get-Date -Format 'HHmmss'))

if (-not (Test-Path $cli)) { throw "找不到 $cli" }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$script:pass = 0
$script:fail = 0
$script:results = @()

function Assert-That {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:pass++
        Write-Host ("[PASS] " + $Name) -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host ("[FAIL] " + $Name + "  " + $Detail) -ForegroundColor Red
    }
    $script:results += [pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail }
}

function Get-ProxySetting { Get-ItemProperty -Path $regKey }

function Invoke-Guard {
    param([string[]]$ArgumentList = @('--auto', '--quiet'))
    $out = & $cli @ArgumentList 2>&1 | Out-String
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out }
}

function Get-Report { if (Test-Path $report) { Get-Content -Raw $report } else { '' } }

function Get-LadderPort {
    $r = Get-Report
    $m = [regex]::Match($r, '代理端点: 127\.0\.0\.1:(\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

# ---------------------------------------------------------------- 环境快照
$snapshot = [pscustomobject]@{
    ProxyEnable   = (Get-ProxySetting).ProxyEnable
    ProxyServer   = (Get-ProxySetting).ProxyServer
    ProxyOverride = (Get-ProxySetting).ProxyOverride
    AutoConfigURL = (Get-ProxySetting).AutoConfigURL
    NoProxy       = [Environment]::GetEnvironmentVariable('NO_PROXY', 'User')
    HttpsProxy    = [Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
    HttpProxy     = [Environment]::GetEnvironmentVariable('HTTP_PROXY', 'User')
    AllProxy      = [Environment]::GetEnvironmentVariable('ALL_PROXY', 'User')
}

Write-Host "== CodexNetGuard 自测开始 ==" -ForegroundColor Cyan
Write-Host ("临时目录: " + $tmp)

# ---------------------------------------------------------------- 12/13 用的假代理
$dummySource = @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class DummyProxy
{
    public static void Main(string[] args)
    {
        string mode = args[0];
        int port = int.Parse(args[1]);
        TcpListener l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        Console.WriteLine(mode + " on " + port);
        while (true)
        {
            TcpClient c = l.AcceptTcpClient();
            Thread t = new Thread(new ParameterizedThreadStart(delegate(object o)
            {
                TcpClient cl = (TcpClient)o;
                try
                {
                    if (mode == "http") ServeHttp(cl);
                    else if (mode == "socks") ServeSocks(cl);
                    else { Thread.Sleep(120000); cl.Close(); }
                }
                catch { try { cl.Close(); } catch { } }
            }));
            t.IsBackground = true;
            t.Start(c);
        }
    }

    private static void ServeHttp(TcpClient c)
    {
        NetworkStream ns = c.GetStream();
        string head = "";
        byte[] buf = new byte[512];
        while (head.IndexOf("\r\n\r\n") < 0)
        {
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) { c.Close(); return; }
            head += Encoding.ASCII.GetString(buf, 0, n);
        }
        string[] parts = head.Split(' ');
        int colon = parts[1].LastIndexOf(':');
        TcpClient up = new TcpClient();
        up.Connect(parts[1].Substring(0, colon), int.Parse(parts[1].Substring(colon + 1)));
        byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
        ns.Write(ok, 0, ok.Length);
        ns.Flush();
        Pump(ns, up.GetStream());
    }

    private static void ServeSocks(TcpClient c)
    {
        NetworkStream ns = c.GetStream();
        byte[] buf = new byte[512];
        ns.Read(buf, 0, 2);
        int n = buf[1];
        ns.Read(buf, 0, n);
        ns.Write(new byte[] { 5, 0 }, 0, 2);
        ns.Flush();
        ns.Read(buf, 0, 4);
        string host;
        if (buf[3] == 1) { ns.Read(buf, 0, 4); host = buf[0] + "." + buf[1] + "." + buf[2] + "." + buf[3]; }
        else if (buf[3] == 3)
        {
            ns.Read(buf, 0, 1);
            int len = buf[0];
            ns.Read(buf, 0, len);
            host = Encoding.ASCII.GetString(buf, 0, len);
        }
        else { ns.Read(buf, 0, 16); host = "::1"; }
        ns.Read(buf, 0, 2);
        int port = (buf[0] << 8) | buf[1];
        TcpClient up = new TcpClient();
        up.Connect(host, port);
        ns.Write(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
        ns.Flush();
        Pump(ns, up.GetStream());
    }

    private static void Pump(NetworkStream a, NetworkStream b)
    {
        Thread t = new Thread(new ThreadStart(delegate
        {
            try
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = a.Read(buf, 0, buf.Length)) > 0) { b.Write(buf, 0, n); b.Flush(); }
            }
            catch { }
            try { b.Close(); } catch { }
        }));
        t.IsBackground = true;
        t.Start();
        try
        {
            byte[] buf = new byte[8192];
            int n;
            while ((n = b.Read(buf, 0, buf.Length)) > 0) { a.Write(buf, 0, n); a.Flush(); }
        }
        catch { }
        try { a.Close(); } catch { }
    }
}
'@

$dummySrc = Join-Path $tmp 'DummyProxy.cs'
$dummyExe = Join-Path $tmp 'DummyProxy.exe'
Set-Content -LiteralPath $dummySrc -Value $dummySource -Encoding UTF8
& $csc /nologo /target:exe /out:$dummyExe $dummySrc | Out-Null

$stubSrc = Join-Path $tmp 'Stub.cs'
$stubExe = Join-Path $tmp 'Stub.exe'
Set-Content -LiteralPath $stubSrc -Value 'public class Stub { public static void Main() { } }' -Encoding UTF8
& $csc /nologo /target:exe /out:$stubExe $stubSrc | Out-Null

function Start-Dummy {
    param([string]$Mode, [int]$Port)
    return Start-Process -FilePath $dummyExe -ArgumentList $Mode, $Port -WindowStyle Hidden -PassThru
}

function Stop-AllDummy {
    Get-Process -Name DummyProxy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

$dummyProcs = @()

try {
    # ------------------------------------------------------------ 1 健康态
    $r = Invoke-Guard @('--auto', '--quiet')
    $ladderPort = Get-LadderPort
    Assert-That '1. 健康态修复返回 0' ($r.Exit -eq 0) ("exit=" + $r.Exit)
    Assert-That '1b. 能识别到可用代理端点' ($ladderPort -gt 0) (Get-Report)

    if ($ladderPort -gt 0) {
        # -------------------------------------------------------- 2 死端口
        Set-ItemProperty -Path $regKey -Name ProxyServer -Value '127.0.0.1:15721'
        Set-ItemProperty -Path $regKey -Name ProxyEnable -Value 1 -Type DWord
        $r = Invoke-Guard @('--auto', '--quiet')
        $p = Get-ProxySetting
        Assert-That '2. 死端口被指回可用端口' (($p.ProxyEnable -eq 1) -and ($p.ProxyServer -match "127\.0\.0\.1:$ladderPort")) $p.ProxyServer

        # -------------------------------------------------------- 3 脏 ProxyOverride
        Set-ItemProperty -Path $regKey -Name ProxyOverride -Value ';;localhost.*;;localhost.*;;foo;;'
        $r = Invoke-Guard @('--auto', '--quiet')
        $ov = (Get-ProxySetting).ProxyOverride
        $items = $ov -split ';' | Where-Object { $_ -ne '' }
        $dup = ($items | Group-Object | Where-Object Count -gt 1).Count
        Assert-That '3. ProxyOverride 去重且无空项' (($dup -eq 0) -and ($ov -notmatch ';;')) $ov

        # -------------------------------------------------------- 4 假 PAC
        Set-ItemProperty -Path $regKey -Name AutoConfigURL -Value 'http://127.0.0.1:9/dead.pac'
        $r = Invoke-Guard @('--auto', '--quiet')
        Assert-That '4. 假 PAC 被清除' ([string]::IsNullOrEmpty((Get-ProxySetting).AutoConfigURL)) ((Get-ProxySetting).AutoConfigURL)

        $restore = Invoke-Guard @('--restore', '--quiet')
        Assert-That '4b. --restore 能把 PAC 还原回来' ((Get-ProxySetting).AutoConfigURL -eq 'http://127.0.0.1:9/dead.pac') ((Get-ProxySetting).AutoConfigURL)
        Remove-ItemProperty -Path $regKey -Name AutoConfigURL -ErrorAction SilentlyContinue

        # -------------------------------------------------------- 5/6 环境变量
        [Environment]::SetEnvironmentVariable('NO_PROXY', 'foo,bar', 'User')
        [Environment]::SetEnvironmentVariable('HTTPS_PROXY', 'http://127.0.0.1:15721', 'User')
        $r = Invoke-Guard @('--auto', '--quiet')
        $np = [Environment]::GetEnvironmentVariable('NO_PROXY', 'User')
        $hp = [Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
        Assert-That '5. NO_PROXY 被规范化' ($np -match 'localhost' -and $np -match '127\.0\.0\.1') $np
        # 梯子此刻可用 -> 必须改写；梯子此刻抖动（无可用端点）-> 按设计清空死值
        $hasEndpoint = (Get-Report) -match '\[OK\] \[梯子\] 代理端点'
        if ($hasEndpoint) {
            Assert-That '6. HTTPS_PROXY 死端口被改写到可用端口' ($hp -match "127\.0\.0\.1:$ladderPort") $hp
        } else {
            Assert-That '6. 无可用端点时清空死端口 HTTPS_PROXY（梯子当前抖动）' ([string]::IsNullOrEmpty($hp)) $hp
        }
    } else {
        Assert-That '2~6. 需要先有可用梯子，已跳过' $false '未识别到代理端点'
    }

    # ------------------------------------------------------------ 7 cc-switch
    $cc = Join-Path $env:USERPROFILE '.cc-switch\settings.json'
    $ccExist = Test-Path $cc
    if ($ccExist) {
        $ccOrig = Get-Content -Raw $cc
        $cc2 = $ccOrig -replace '"enableLocalProxy":\s*false', '"enableLocalProxy": true'
        Set-Content -LiteralPath $cc -Value $cc2 -Encoding UTF8 -NoNewline
        $r = Invoke-Guard @('--auto', '--quiet')
        $ccText = Get-Content -Raw $cc
        Assert-That '7. cc-switch 开关被强制关闭' ($ccText -match '"enableLocalProxy":\s*false') '仍为 true'
    } else {
        Assert-That '7. cc-switch 未安装，跳过' $true ''
    }

    # ------------------------------------------------------------ 8 只读客户端配置
    $roDir = Join-Path $tmp 'rotest'
    New-Item -ItemType Directory -Force -Path (Join-Path $roDir 'guiConfigs') | Out-Null
    Copy-Item $stubExe (Join-Path $roDir 'rotest.exe') -Force
    $roFile = Join-Path $roDir 'guiConfigs\guiNConfig.json'
    Set-Content -LiteralPath $roFile -Value '{"SystemProxyItem":{"SysProxyType":0}}' -Encoding UTF8
    Set-ItemProperty -LiteralPath $roFile -Name IsReadOnly -Value $true
    $iniPath = Join-Path $root 'CodexNetGuard.ini'
    $iniOrig = Get-Content -Raw $iniPath
    Set-Content -LiteralPath $iniPath -Value ("clientPath=" + (Join-Path $roDir 'rotest.exe') + "`r`n") -Encoding UTF8 -NoNewline
    $r = Invoke-Guard @('--auto', '--quiet')
    $text = Get-Report
    Assert-That '8. 只读配置被报告并回滚' (($text -match '写入失败') -and ($text -match '回滚')) $text
    Set-ItemProperty -LiteralPath $roFile -Name IsReadOnly -Value $false
    Set-Content -LiteralPath $iniPath -Value $iniOrig -Encoding UTF8 -NoNewline

    # ------------------------------------------------------------ 9 多客户端优先级
    $dummyProcs += Start-Dummy 'http' 7897
    Start-Sleep -Seconds 2
    $r = Invoke-Guard @('--auto', '--quiet')
    $port = Get-LadderPort
    Assert-That '9. 梯子已就绪时不被后起的端口抢走' (($port -gt 0) -and ($port -ne 7897)) ("selected=" + $port)

    if (-not $SkipLadderStop) {
        $ladderExe = [regex]::Match($iniOrig, 'clientPath=(.+)').Groups[1].Value
        if ($ladderExe -eq '') { $ladderExe = [regex]::Match($iniOrig, 'v2rayNPath=(.+)').Groups[1].Value }
        $ladderExe = $ladderExe.Trim()

        Get-Process -Name v2rayN, xray, clash, mihomo, sing-box, verge-mihomo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        # -------------------------------------------------------- 10 SOCKS5-only
        Stop-AllDummy
        $dummyProcs = @()
        $dummyProcs += Start-Dummy 'socks' 2080
        $dummyProcs += Start-Dummy 'decoy' 1080
        Start-Sleep -Seconds 2
        $r = Invoke-Guard @('--auto', '--quiet')
        $p = Get-ProxySetting
        Assert-That '10. SOCKS5-only 写成 socks=' ($p.ProxyServer -eq 'socks=127.0.0.1:2080') $p.ProxyServer

        # -------------------------------------------------------- 11 诱饵不被选中
        Assert-That '11. 诱饵端口（1080）未被选中' ($p.ProxyServer -notmatch '1080') $p.ProxyServer

        # -------------------------------------------------------- 12 全不可用
        Stop-AllDummy
        $dummyProcs = @()
        $dummyProcs += Start-Dummy 'decoy' 1080
        # 把 clientPath 指向空壳程序：否则工具会自动把真梯子拉起来（那是设计行为，不是失败）
        Set-Content -LiteralPath $iniPath -Value ("clientPath=" + $stubExe + "`r`n") -Encoding UTF8 -NoNewline
        Start-Sleep -Seconds 2
        $r = Invoke-Guard @('--auto', '--quiet')
        $p = Get-ProxySetting
        Assert-That '12. 无可用代理时关闭系统代理且退出码 1' (($p.ProxyEnable -eq 0) -and ($r.Exit -eq 1)) ("enable=" + $p.ProxyEnable + " exit=" + $r.Exit)
        Set-Content -LiteralPath $iniPath -Value $iniOrig -Encoding UTF8 -NoNewline

        Stop-AllDummy
        $dummyProcs = @()
        if ($ladderExe -ne '' -and (Test-Path $ladderExe)) {
            Start-Process -FilePath $ladderExe -WorkingDirectory (Split-Path -Parent $ladderExe) | Out-Null
            Start-Sleep -Seconds 15
        }
    }

    # ------------------------------------------------------------ 13 幂等
    $beforeBackups = (Get-ChildItem $root -Filter '*.bak-CodexNetGuard-*' -ErrorAction SilentlyContinue).Count
    $r1 = Invoke-Guard @('--auto', '--quiet')
    $ov1 = (Get-ProxySetting).ProxyOverride
    $r2 = Invoke-Guard @('--auto', '--quiet')
    $r3 = Invoke-Guard @('--auto', '--quiet')
    $ov3 = (Get-ProxySetting).ProxyOverride
    $afterBackups = (Get-ChildItem $root -Filter '*.bak-CodexNetGuard-*' -ErrorAction SilentlyContinue).Count
    Assert-That '13. 连续修复幂等（无重复项、无备份膨胀）' (($ov1 -eq $ov3) -and ($beforeBackups -eq $afterBackups)) ("ov1=$ov1" + " ov3=$ov3")
    Assert-That '13b. 最终状态健康' ($r3.Exit -eq 0) ("exit=" + $r3.Exit)
}
finally {
    # ---------------------------------------------------------------- 还原
    Stop-AllDummy
    Set-ItemProperty -Path $regKey -Name ProxyServer -Value $snapshot.ProxyServer
    Set-ItemProperty -Path $regKey -Name ProxyEnable -Value $snapshot.ProxyEnable -Type DWord
    Set-ItemProperty -Path $regKey -Name ProxyOverride -Value $snapshot.ProxyOverride
    if ([string]::IsNullOrEmpty($snapshot.AutoConfigURL)) {
        Remove-ItemProperty -Path $regKey -Name AutoConfigURL -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -Path $regKey -Name AutoConfigURL -Value $snapshot.AutoConfigURL
    }
    [Environment]::SetEnvironmentVariable('NO_PROXY', $snapshot.NoProxy, 'User')
    [Environment]::SetEnvironmentVariable('HTTPS_PROXY', $snapshot.HttpsProxy, 'User')
    [Environment]::SetEnvironmentVariable('HTTP_PROXY', $snapshot.HttpProxy, 'User')
    [Environment]::SetEnvironmentVariable('ALL_PROXY', $snapshot.AllProxy, 'User')

    $final = Invoke-Guard @('--auto', '--quiet')
    Write-Host ''
    Write-Host ("== 结果：通过 " + $script:pass + " / 失败 " + $script:fail + "，收尾修复退出码 " + $final.Exit + " ==") -ForegroundColor Cyan
    Get-Report | Select-String -Pattern '链路|结果:' | ForEach-Object { Write-Host $_.Line }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
