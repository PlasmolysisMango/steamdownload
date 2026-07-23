<#
.SYNOPSIS
  SteamDl Windows build helper.

.USAGE
  .\build.ps1 doctor
  .\build.ps1 install-deps
  .\build.ps1 build
  .\build.ps1 run -Port 8630
  .\build.ps1 build-apk
  .\build.ps1 clean

.NOTES
  - Prefer system dotnet/java/sdkmanager when present.
  - Missing tools are installed under .tools\ without touching global system state.
  - PowerShell 5.1+ is supported; PowerShell 7+ is recommended.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'doctor', 'install-deps', 'install-dotnet', 'install-jdk', 'install-android-sdk', 'install-workload', 'restore', 'build', 'run', 'publish-server', 'build-apk', 'publish-apk', 'clean')]
    [string]$Task = 'help',

    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [int]$Port = 8630,
    [string]$Runtime = 'win-x64',
    [int]$AndroidApi = 35,
    [string]$AndroidBuildTools = '35.0.0',
    [string]$DotnetChannel = '9.0'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ToolsDir = Join-Path $Root '.tools'
$LocalDotnetDir = Join-Path $ToolsDir 'dotnet-win'
$LocalDotnet = Join-Path $LocalDotnetDir 'dotnet.exe'
$LocalJdkDir = Join-Path $ToolsDir 'jdk-win'
$AndroidSdkRoot = Join-Path $ToolsDir 'android-sdk'
$SdkManager = Join-Path $AndroidSdkRoot 'cmdline-tools\latest\bin\sdkmanager.bat'
$ServerProject = Join-Path $Root 'src\SteamDl.Server\SteamDl.Server.csproj'
$AndroidProject = Join-Path $Root 'src\SteamDl.Android\SteamDl.Android.csproj'
$Solution = Join-Path $Root 'SteamDl.sln'
$ServerOut = Join-Path $Root 'artifacts\server'
$ApkOut = Join-Path $Root 'artifacts\apk'

function Write-Section([string]$Text) {
    Write-Host "`n== $Text ==" -ForegroundColor Cyan
}

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Find-CommandPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Get-DotnetPath {
    $system = Find-CommandPath 'dotnet'
    if ($system) { return $system }
    if (Test-Path $LocalDotnet) { return $LocalDotnet }
    return $LocalDotnet
}

function Set-DotnetEnv {
    $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $ToolsDir 'nuget'
    $env:DOTNET_CLI_HOME = Join-Path $ToolsDir 'home'
    Ensure-Dir $env:NUGET_PACKAGES
    Ensure-Dir $env:DOTNET_CLI_HOME
}

function Get-JavaHome {
    if (Test-Path (Join-Path $LocalJdkDir 'bin\java.exe')) { return $LocalJdkDir }
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) { return $env:JAVA_HOME }
    $java = Find-CommandPath 'java.exe'
    if ($java) { return (Split-Path -Parent (Split-Path -Parent $java)) }
    return $LocalJdkDir
}

function Set-AndroidEnv {
    $javaHome = Get-JavaHome
    if (Test-Path (Join-Path $javaHome 'bin\java.exe')) {
        $env:JAVA_HOME = $javaHome
        $env:PATH = (Join-Path $javaHome 'bin') + [IO.Path]::PathSeparator + $env:PATH
    }
    $env:ANDROID_HOME = $AndroidSdkRoot
    $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
    $platformTools = Join-Path $AndroidSdkRoot 'platform-tools'
    if (Test-Path $platformTools) {
        $env:PATH = $platformTools + [IO.Path]::PathSeparator + $env:PATH
    }
}

function Invoke-Dotnet([string[]]$DotnetArgs) {
    Set-DotnetEnv
    $dotnet = Get-DotnetPath
    if (-not (Test-Path $dotnet) -and -not (Find-CommandPath 'dotnet')) {
        throw 'dotnet 未找到，请先运行 .\build.ps1 install-dotnet'
    }
    & $dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet 命令失败: dotnet $($DotnetArgs -join ' ')" }
}

function Download-File([string]$Url, [string]$OutFile) {
    Ensure-Dir (Split-Path -Parent $OutFile)
    Write-Host "下载: $Url"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    } catch {
        throw "下载失败: $Url`n$($_.Exception.Message)"
    }
}

function Show-Help {
    @"
SteamDl Windows build helper

Usage:
  .\build.ps1 doctor
  .\build.ps1 install-deps
  .\build.ps1 build
  .\build.ps1 run [-Port 8630]
  .\build.ps1 publish-server [-Config Release] [-Runtime win-x64]
  .\build.ps1 build-apk [-Config Release] [-AndroidApi 35] [-AndroidBuildTools 35.0.0]
  .\build.ps1 clean

Notes:
  install-deps installs missing portable tools into .tools\.
  Android APK build requires Android workload + JDK 17 + Android SDK.
"@ | Write-Host
}

function Install-Dotnet {
    Ensure-Dir $ToolsDir
    $system = Find-CommandPath 'dotnet'
    if ($system) {
        Write-Host "使用系统 dotnet: $system"
        Invoke-Dotnet @('--version')
        return
    }
    if (Test-Path $LocalDotnet) {
        Write-Host "使用本地 dotnet: $LocalDotnet"
        Invoke-Dotnet @('--version')
        return
    }

    Write-Section "安装 .NET SDK $DotnetChannel 到 $LocalDotnetDir"
    $installer = Join-Path $ToolsDir 'dotnet-install.ps1'
    Download-File 'https://dot.net/v1/dotnet-install.ps1' $installer
    Unblock-File -Path $installer -ErrorAction SilentlyContinue
    & $installer -Channel $DotnetChannel -InstallDir $LocalDotnetDir
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK 安装失败' }
    Invoke-Dotnet @('--version')
}

function Install-Jdk {
    Ensure-Dir $ToolsDir
    $javaHome = Get-JavaHome
    if (Test-Path (Join-Path $javaHome 'bin\java.exe')) {
        Write-Host "使用 Java: $javaHome"
        & (Join-Path $javaHome 'bin\java.exe') -version
        return
    }

    Write-Section "安装 Temurin JDK 17 到 $LocalJdkDir"
    $zip = Join-Path $ToolsDir 'jdk17-windows-x64.zip'
    $url = 'https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse'
    Download-File $url $zip
    $tmp = Join-Path $ToolsDir 'jdk-extract'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $LocalJdkDir -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $tmp
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $rootDir = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (-not $rootDir) { throw 'JDK 解压失败，未找到根目录' }
    Move-Item $rootDir.FullName $LocalJdkDir
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    & (Join-Path $LocalJdkDir 'bin\java.exe') -version
}

function Install-AndroidSdk {
    Install-Jdk
    Ensure-Dir (Join-Path $AndroidSdkRoot 'cmdline-tools')
    if (-not (Test-Path $SdkManager)) {
        Write-Section "安装 Android cmdline-tools 到 $AndroidSdkRoot"
        $zip = Join-Path $ToolsDir 'commandlinetools-win.zip'
        $url = 'https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip'
        Download-File $url $zip
        $tmp = Join-Path $ToolsDir 'cmdline-tools-tmp'
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $AndroidSdkRoot 'cmdline-tools\latest') -Recurse -Force -ErrorAction SilentlyContinue
        Ensure-Dir $tmp
        Expand-Archive -Path $zip -DestinationPath $tmp -Force
        Move-Item (Join-Path $tmp 'cmdline-tools') (Join-Path $AndroidSdkRoot 'cmdline-tools\latest')
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    Set-AndroidEnv
    & $SdkManager --sdk_root=$AndroidSdkRoot --version
    if ($LASTEXITCODE -ne 0) { throw 'sdkmanager 不可用' }

    Write-Section "安装 Android SDK platform/build-tools"
    # 尝试接受许可证。部分环境下 sdkmanager --licenses 会返回非 0,因此不作为硬失败。
    (1..100 | ForEach-Object { 'y' }) | & $SdkManager --sdk_root=$AndroidSdkRoot --licenses | Out-Null
    & $SdkManager --sdk_root=$AndroidSdkRoot `
        'platform-tools' `
        "platforms;android-$AndroidApi" `
        "build-tools;$AndroidBuildTools" `
        'cmdline-tools;latest'
    if ($LASTEXITCODE -ne 0) { throw 'Android SDK 组件安装失败' }
}

function Install-Workload {
    Install-Dotnet
    Write-Section '安装/还原 .NET Android workload'
    Invoke-Dotnet @('workload', 'restore', $AndroidProject)
}

function Restore-Server {
    Install-Dotnet
    Invoke-Dotnet @('restore', $ServerProject)
}

function Restore-Android {
    Install-Workload
    Install-AndroidSdk
    Set-AndroidEnv
    Invoke-Dotnet @('restore', $AndroidProject)
}

function Build-Server {
    Restore-Server
    Invoke-Dotnet @('build', $ServerProject, '-c', $Config, '--no-restore')
}

function Run-Server {
    Restore-Server
    $env:PORT = [string]$Port
    Write-Host "启动 http://127.0.0.1:$Port"
    Invoke-Dotnet @('run', '--project', $ServerProject, '-c', $Config, '--no-restore')
}

function Publish-Server {
    Restore-Server
    Remove-Item $ServerOut -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Dotnet @('publish', $ServerProject, '-c', $Config, '-r', $Runtime, '--self-contained', 'false', '-o', $ServerOut)
    Write-Host "桌面服务端产物: $ServerOut"
}

function Build-Apk {
    Restore-Android
    Remove-Item $ApkOut -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $ApkOut
    Set-AndroidEnv
    Invoke-Dotnet @('publish', $AndroidProject, '-c', $Config, '-p:AndroidPackageFormat=apk')
    Get-ChildItem (Join-Path $Root "src\SteamDl.Android\bin\$Config") -Recurse -Filter '*.apk' |
        Copy-Item -Destination $ApkOut -Force
    Write-Host "APK 产物目录: $ApkOut"
    Get-ChildItem $ApkOut -Filter '*.apk' | ForEach-Object { Write-Host $_.FullName }
}

function Clean-Project {
    Get-ChildItem (Join-Path $Root 'src') -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Root 'artifacts') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '已清理构建产物'
}

function Doctor {
    Write-Section 'System'
    Write-Host "OS: $([System.Environment]::OSVersion.VersionString)"
    Write-Host "Arch: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
    Write-Host "CPU: $([Environment]::ProcessorCount)"
    try {
        $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).TrimEnd('\').TrimEnd(':'))
        if ($drive) { Write-Host "Disk free: $([math]::Round($drive.Free / 1GB, 2)) GiB" }
    } catch { }

    Write-Section '.NET'
    $dotnet = Get-DotnetPath
    if ((Test-Path $dotnet) -or (Find-CommandPath 'dotnet')) { Invoke-Dotnet @('--info') } else { Write-Host 'dotnet 未找到: 运行 .\build.ps1 install-dotnet' }

    Write-Section 'Java'
    $javaHome = Get-JavaHome
    if (Test-Path (Join-Path $javaHome 'bin\java.exe')) { & (Join-Path $javaHome 'bin\java.exe') -version } else { Write-Host 'java 未找到: 运行 .\build.ps1 install-jdk' }

    Write-Section 'Android SDK'
    Write-Host "ANDROID_SDK_ROOT=$AndroidSdkRoot"
    if (Test-Path $SdkManager) { Set-AndroidEnv; & $SdkManager --version } else { Write-Host 'sdkmanager 未找到: 运行 .\build.ps1 install-android-sdk' }
}

Push-Location $Root
try {
    switch ($Task) {
        'help' { Show-Help }
        'doctor' { Doctor }
        'install-dotnet' { Install-Dotnet }
        'install-jdk' { Install-Jdk }
        'install-android-sdk' { Install-AndroidSdk }
        'install-workload' { Install-Workload }
        'install-deps' { Restore-Android; Write-Host '依赖安装完成。可执行: .\build.ps1 build 或 .\build.ps1 build-apk' }
        'restore' { Restore-Server }
        'build' { Build-Server }
        'run' { Run-Server }
        'publish-server' { Publish-Server }
        'build-apk' { Build-Apk }
        'publish-apk' { Build-Apk }
        'clean' { Clean-Project }
        default { Show-Help }
    }
}
finally {
    Pop-Location
}
