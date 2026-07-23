#!/usr/bin/env node
// SteamDl cross-platform build helper.
// No npm dependencies. Works on Windows/Linux/macOS with Node.js 18+.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import https from 'node:https';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const isWin = process.platform === 'win32';
const isLinux = process.platform === 'linux';
const isMac = process.platform === 'darwin';

const args = process.argv.slice(2);
const task = (args.find(a => !a.startsWith('--')) || 'help').toLowerCase();
const opts = parseOptions(args.filter(a => a.startsWith('--')));

const config = opts.config || process.env.CONFIG || 'Release';
const port = opts.port || process.env.PORT || '8630';
const runtime = opts.runtime || process.env.RUNTIME || (isWin ? 'win-x64' : isMac ? 'osx-x64' : 'linux-x64');
const dotnetChannel = opts.dotnetChannel || process.env.DOTNET_CHANNEL || '9.0';
const androidApi = opts.androidApi || process.env.ANDROID_API || '35';
const androidBuildTools = opts.androidBuildTools || process.env.ANDROID_BUILD_TOOLS || '35.0.0';
const nugetSource = opts.nugetSource || process.env.NUGET_SOURCE || 'https://api.nuget.org/v3/index.json';

const toolsDir = path.join(root, '.tools');
const localDotnetDir = path.join(toolsDir, isWin ? 'dotnet-win' : 'dotnet');
const localDotnet = path.join(localDotnetDir, isWin ? 'dotnet.exe' : 'dotnet');
const localJdkDir = path.join(toolsDir, isWin ? 'jdk-win' : 'jdk');
const androidSdkRoot = process.env.ANDROID_SDK_ROOT || process.env.ANDROID_HOME || path.join(toolsDir, 'android-sdk');
const sdkManager = path.join(androidSdkRoot, 'cmdline-tools', 'latest', 'bin', isWin ? 'sdkmanager.bat' : 'sdkmanager');
const serverProject = path.join(root, 'src', 'SteamDl.Server', 'SteamDl.Server.csproj');
const androidProject = path.join(root, 'src', 'SteamDl.Android', 'SteamDl.Android.csproj');
const serverOut = path.join(root, 'artifacts', 'server');
const apkOut = path.join(root, 'artifacts', 'apk');

main().catch(err => {
  console.error(`\nERROR: ${err.message}`);
  process.exit(1);
});

async function main() {
  switch (task) {
    case 'help': return help();
    case 'doctor': return doctor();
    case 'install-dotnet': return installDotnet();
    case 'install-jdk': return installJdk();
    case 'install-android-sdk': return installAndroidSdk();
    case 'install-workload': return installWorkload();
    case 'install-deps': await restoreAndroid(); console.log('依赖安装完成。可执行: node build.mjs build 或 node build.mjs build-apk'); return;
    case 'restore': return restoreServer();
    case 'build': return buildServer();
    case 'run': return runServer();
    case 'publish-server': return publishServer();
    case 'build-apk':
    case 'publish-apk': return buildApk();
    case 'clean': return clean();
    default:
      help();
      throw new Error(`未知任务: ${task}`);
  }
}

function help() {
  console.log(`SteamDl build helper\n\nUsage:\n  node build.mjs doctor\n  node build.mjs install-deps\n  node build.mjs build\n  node build.mjs run --port=8630\n  node build.mjs publish-server --config=Release --runtime=${runtime}\n  node build.mjs build-apk --config=Release --android-api=35 --android-build-tools=35.0.0\n  node build.mjs build-apk --nuget-source=https://api.nuget.org/v3/index.json\n  node build.mjs clean\n\nNotes:\n  Missing portable tools are installed under .tools/.\n  NuGet source defaults to ${nugetSource}. Override with --nuget-source=URL or NUGET_SOURCE=URL.\n  Android APK build requires .NET SDK + Android workload + JDK 17 + Android SDK.\n`);
}

function parseOptions(optionArgs) {
  const out = {};
  for (const arg of optionArgs) {
    const raw = arg.replace(/^--/, '');
    const [key, ...rest] = raw.split('=');
    const value = rest.length ? rest.join('=') : 'true';
    out[key.replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = value;
  }
  return out;
}

function section(title) {
  console.log(`\n== ${title} ==`);
}

function mkdirp(p) {
  fs.mkdirSync(p, { recursive: true });
}

function exists(p) {
  return fs.existsSync(p);
}

function executable(name) {
  const found = findOnPath(name);
  return found || null;
}

function findOnPath(name) {
  const paths = (process.env.PATH || '').split(path.delimiter);
  const names = isWin && !name.toLowerCase().endsWith('.exe') && !name.toLowerCase().endsWith('.bat')
    ? [name, `${name}.exe`, `${name}.bat`, `${name}.cmd`]
    : [name];
  for (const dir of paths) {
    for (const n of names) {
      const full = path.join(dir, n);
      if (exists(full)) return full;
    }
  }
  return null;
}

function run(command, commandArgs = [], options = {}) {
  console.log(`> ${command} ${commandArgs.map(quoteArg).join(' ')}`);
  const useShell = isWin && /\.(bat|cmd)$/i.test(command);
  const res = spawnSync(command, commandArgs, {
    cwd: options.cwd || root,
    stdio: options.stdio || 'inherit',
    input: options.input,
    shell: useShell,
    env: { ...process.env, ...(options.env || {}) },
  });
  if (res.error) throw res.error;
  if (res.status !== 0) throw new Error(`命令失败(ExitCode=${res.status}): ${command} ${commandArgs.join(' ')}`);
  return res;
}

function runCapture(command, commandArgs = [], options = {}) {
  const useShell = isWin && /\.(bat|cmd)$/i.test(command);
  const res = spawnSync(command, commandArgs, {
    cwd: options.cwd || root,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: useShell,
    env: { ...process.env, ...(options.env || {}) },
  });
  return { code: res.status ?? 1, stdout: res.stdout || '', stderr: res.stderr || '', error: res.error };
}

function quoteArg(arg) {
  return /\s/.test(arg) ? JSON.stringify(arg) : arg;
}

function dotnetEnv(extra = {}) {
  mkdirp(path.join(toolsDir, 'nuget'));
  mkdirp(path.join(toolsDir, 'home'));
  mkdirp(path.join(toolsDir, 'tmp'));
  return {
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    NUGET_PACKAGES: path.join(toolsDir, 'nuget'),
    DOTNET_CLI_HOME: path.join(toolsDir, 'home'),
    TMPDIR: path.join(toolsDir, 'tmp'),
    TEMP: path.join(toolsDir, 'tmp'),
    TMP: path.join(toolsDir, 'tmp'),
    ...extra,
  };
}

function getDotnetPathForDisplay() {
  if (exists(localDotnet)) return localDotnet;
  return executable('dotnet') || localDotnet;
}

function hasDotnetSdk(dotnetPath) {
  if (!dotnetPath || !exists(dotnetPath)) return false;
  const res = runCapture(dotnetPath, ['--list-sdks'], { env: dotnetEnv() });
  return res.code === 0 && res.stdout.trim().length > 0;
}

function getDotnetSdkPath() {
  if (exists(localDotnet) && hasDotnetSdk(localDotnet)) return localDotnet;
  const system = executable('dotnet');
  if (system && hasDotnetSdk(system)) return system;
  return null;
}

function dotnet(commandArgs) {
  const sdk = getDotnetSdkPath();
  if (!sdk) throw new Error('未找到可用的 .NET SDK。若系统 dotnet 只有 Runtime，请运行: node build.mjs install-dotnet');
  return run(sdk, commandArgs, { env: dotnetEnv() });
}

function nugetSourceArgs() {
  return ['--source', nugetSource];
}

async function installDotnet() {
  mkdirp(toolsDir);
  const system = executable('dotnet');
  if (system && hasDotnetSdk(system)) {
    console.log(`使用系统 dotnet SDK: ${system}`);
    dotnet(['--version']);
    return;
  }
  if (system) console.warn(`检测到系统 dotnet 但没有可用 SDK，将安装项目本地 .NET SDK: ${system}`);
  if (exists(localDotnet) && hasDotnetSdk(localDotnet)) {
    console.log(`使用本地 dotnet SDK: ${localDotnet}`);
    dotnet(['--version']);
    return;
  }

  section(`安装 .NET SDK ${dotnetChannel} 到 ${localDotnetDir}`);
  if (isWin) {
    const installer = path.join(toolsDir, 'dotnet-install.ps1');
    await download('https://dot.net/v1/dotnet-install.ps1', installer);
    const ps = executable('powershell') || executable('pwsh');
    if (!ps) throw new Error('安装 .NET SDK 需要 powershell 或 pwsh。Windows 默认应自带 powershell。');
    run(ps, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', installer, '-Channel', dotnetChannel, '-InstallDir', localDotnetDir]);
  } else {
    const installer = path.join(toolsDir, 'dotnet-install.sh');
    await download('https://dot.net/v1/dotnet-install.sh', installer);
    fs.chmodSync(installer, 0o755);
    run('bash', [installer, '--channel', dotnetChannel, '--install-dir', localDotnetDir], { env: dotnetEnv() });
  }
  if (!hasDotnetSdk(localDotnet)) throw new Error(`.NET SDK 安装后仍不可用: ${localDotnet}`);
  dotnet(['--version']);
}

function getJavaExe() {
  const local = path.join(localJdkDir, 'bin', isWin ? 'java.exe' : 'java');
  if (exists(local)) return local;
  return executable('java') || local;
}

function getJavaHome() {
  const localJava = path.join(localJdkDir, 'bin', isWin ? 'java.exe' : 'java');
  if (exists(localJava)) return localJdkDir;
  if (process.env.JAVA_HOME) {
    const java = path.join(process.env.JAVA_HOME, 'bin', isWin ? 'java.exe' : 'java');
    if (exists(java)) return process.env.JAVA_HOME;
  }
  const java = executable('java');
  if (java) return path.dirname(path.dirname(java));
  return localJdkDir;
}

function hasJava() {
  const java = getJavaExe();
  return exists(java);
}

async function installJdk() {
  mkdirp(toolsDir);
  if (hasJava()) {
    console.log(`使用 Java: ${getJavaExe()}`);
    run(getJavaExe(), ['-version']);
    return;
  }

  section(`安装 JDK 17 到 ${localJdkDir}`);
  if (isWin) {
    const zip = path.join(toolsDir, 'jdk17-windows-x64.zip');
    await download('https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse', zip);
    await extractZip(zip, path.join(toolsDir, 'jdk-extract'));
    moveFirstChild(path.join(toolsDir, 'jdk-extract'), localJdkDir);
  } else if (isLinux && os.arch() === 'x64') {
    const tarball = path.join(toolsDir, 'jdk17-linux-x64.tar.gz');
    await download('https://api.adoptium.net/v3/binary/latest/17/ga/linux/x64/jdk/hotspot/normal/eclipse', tarball);
    rmrf(localJdkDir);
    mkdirp(localJdkDir);
    run('tar', ['-xzf', tarball, '-C', localJdkDir, '--strip-components=1']);
  } else if (isMac) {
    throw new Error('macOS 请先安装 JDK 17（例如 brew install temurin17），再运行 install-deps。');
  } else {
    throw new Error('当前平台不支持自动安装 JDK，请手动安装 JDK 17。');
  }
  run(getJavaExe(), ['-version']);
}

function androidEnv() {
  const javaHome = getJavaHome();
  const extraPath = [path.join(javaHome, 'bin'), path.join(androidSdkRoot, 'platform-tools')].filter(exists).join(path.delimiter);
  return {
    JAVA_HOME: javaHome,
    ANDROID_HOME: androidSdkRoot,
    ANDROID_SDK_ROOT: androidSdkRoot,
    PATH: extraPath ? `${extraPath}${path.delimiter}${process.env.PATH || ''}` : process.env.PATH,
  };
}

async function installAndroidSdk() {
  await installJdk();
  const cmdlineToolsDir = path.join(androidSdkRoot, 'cmdline-tools');
  mkdirp(cmdlineToolsDir);
  if (!exists(sdkManager)) {
    section(`安装 Android cmdline-tools 到 ${androidSdkRoot}`);
    const url = isWin
      ? 'https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip'
      : isMac
        ? 'https://dl.google.com/android/repository/commandlinetools-mac-11076708_latest.zip'
        : 'https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip';
    const zip = path.join(toolsDir, `commandlinetools-${process.platform}.zip`);
    const tmp = path.join(toolsDir, 'cmdline-tools-tmp');
    await download(url, zip);
    await extractZip(zip, tmp);
    rmrf(path.join(cmdlineToolsDir, 'latest'));
    fs.renameSync(path.join(tmp, 'cmdline-tools'), path.join(cmdlineToolsDir, 'latest'));
    rmrf(tmp);
  }

  run(sdkManager, ['--sdk_root=' + androidSdkRoot, '--version'], { env: androidEnv() });
  section('安装 Android SDK platform/build-tools');
  const licenseInput = Array(100).fill('y').join(os.EOL) + os.EOL;
  const lic = spawnSync(sdkManager, ['--sdk_root=' + androidSdkRoot, '--licenses'], {
    cwd: root,
    input: licenseInput,
    encoding: 'utf8',
    stdio: ['pipe', 'inherit', 'inherit'],
    shell: isWin && /\.(bat|cmd)$/i.test(sdkManager),
    env: { ...process.env, ...androidEnv() },
  });
  if (lic.error) console.warn(`许可证接受步骤失败: ${lic.error.message}`);

  run(sdkManager, [
    '--sdk_root=' + androidSdkRoot,
    'platform-tools',
    `platforms;android-${androidApi}`,
    `build-tools;${androidBuildTools}`,
    'cmdline-tools;latest',
  ], { env: androidEnv() });
}

async function installWorkload() {
  await installDotnet();
  section('安装/还原 .NET Android workload');
  try {
    dotnet(['workload', 'restore', androidProject, ...nugetSourceArgs()]);
  } catch (e) {
    console.warn('dotnet workload restore 失败，尝试 fallback: dotnet workload install android');
    console.warn(e.message);
    dotnet(['workload', 'install', 'android', ...nugetSourceArgs()]);
  }
}

async function restoreServer() {
  await installDotnet();
  dotnet(['restore', serverProject, ...nugetSourceArgs()]);
}

async function restoreAndroid() {
  await installWorkload();
  await installAndroidSdk();
  dotnet(['restore', androidProject, ...nugetSourceArgs()]);
}

async function buildServer() {
  await restoreServer();
  dotnet(['build', serverProject, '-c', config, '--no-restore']);
}

async function runServer() {
  await restoreServer();
  console.log(`启动 http://127.0.0.1:${port}`);
  const sdk = getDotnetSdkPath();
  run(sdk, ['run', '--project', serverProject, '-c', config, '--no-restore'], { env: { ...dotnetEnv(), PORT: String(port) } });
}

async function publishServer() {
  await restoreServer();
  rmrf(serverOut);
  dotnet(['publish', serverProject, '-c', config, '-r', runtime, '--self-contained', 'false', '-o', serverOut]);
  console.log(`桌面服务端产物: ${serverOut}`);
}

async function buildApk() {
  await restoreAndroid();
  rmrf(apkOut);
  mkdirp(apkOut);
  dotnet(['publish', androidProject, '-c', config, '-p:AndroidPackageFormat=apk']);
  const apkFiles = findFiles(path.join(root, 'src', 'SteamDl.Android', 'bin', config), f => f.endsWith('.apk'));
  if (!apkFiles.length) throw new Error('未找到 APK 产物');
  for (const file of apkFiles) fs.copyFileSync(file, path.join(apkOut, path.basename(file)));
  console.log(`APK 产物目录: ${apkOut}`);
  for (const file of fs.readdirSync(apkOut).filter(f => f.endsWith('.apk'))) console.log(path.join(apkOut, file));
}

function clean() {
  for (const dir of findDirs(path.join(root, 'src'), d => path.basename(d) === 'bin' || path.basename(d) === 'obj')) rmrf(dir);
  rmrf(path.join(root, 'artifacts'));
  console.log('已清理构建产物');
}

function doctor() {
  section('System');
  console.log(`OS: ${os.type()} ${os.release()} ${os.arch()}`);
  console.log(`CPU: ${os.cpus().length}`);
  console.log(`Free memory: ${(os.freemem() / 1024 / 1024 / 1024).toFixed(2)} GiB`);

  section('.NET');
  showDotnetCandidate('本地 dotnet', localDotnet);
  showDotnetCandidate('系统 dotnet', executable('dotnet'));
  const sdk = getDotnetSdkPath();
  if (sdk) console.log(`将用于构建的 dotnet SDK: ${sdk}`);
  else console.warn('未找到可用于构建的 .NET SDK。请运行: node build.mjs install-dotnet');

  section('Java');
  if (hasJava()) run(getJavaExe(), ['-version']);
  else console.warn('java 未找到: 运行 node build.mjs install-jdk');

  section('Android SDK');
  console.log(`ANDROID_SDK_ROOT=${androidSdkRoot}`);
  if (exists(sdkManager)) run(sdkManager, ['--sdk_root=' + androidSdkRoot, '--version'], { env: androidEnv() });
  else console.warn('sdkmanager 未找到: 运行 node build.mjs install-android-sdk');
}

function showDotnetCandidate(label, dotnetPath) {
  if (!dotnetPath || !exists(dotnetPath)) {
    console.log(`${label}: 未找到`);
    return;
  }
  console.log(`${label}: ${dotnetPath}`);
  const info = runCapture(dotnetPath, ['--info'], { env: dotnetEnv() });
  if (info.stdout.trim()) console.log(info.stdout.trim());
  const sdks = runCapture(dotnetPath, ['--list-sdks'], { env: dotnetEnv() });
  if (sdks.code === 0 && sdks.stdout.trim()) console.log(`${label} SDK:\n${sdks.stdout.trim()}`);
  else console.warn(`${label} SDK: 未找到（通常表示只安装了 .NET Runtime，不能构建项目）`);
}

async function download(url, outFile) {
  mkdirp(path.dirname(outFile));
  console.log(`下载: ${url}`);
  await new Promise((resolve, reject) => {
    const file = fs.createWriteStream(outFile);
    const request = (u, redirects = 0) => {
      https.get(u, res => {
        if ([301, 302, 303, 307, 308].includes(res.statusCode) && res.headers.location) {
          res.resume();
          if (redirects > 8) reject(new Error('重定向过多'));
          else request(new URL(res.headers.location, u).toString(), redirects + 1);
          return;
        }
        if (res.statusCode !== 200) {
          res.resume();
          reject(new Error(`下载失败 HTTP ${res.statusCode}: ${u}`));
          return;
        }
        res.pipe(file);
        file.on('finish', () => file.close(resolve));
      }).on('error', reject);
    };
    request(url);
  });
}

async function extractZip(zipFile, destDir) {
  rmrf(destDir);
  mkdirp(destDir);
  if (isWin) {
    const ps = executable('powershell') || executable('pwsh');
    if (!ps) throw new Error('解压 zip 需要 powershell/pwsh');
    run(ps, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', `Expand-Archive -LiteralPath ${JSON.stringify(zipFile)} -DestinationPath ${JSON.stringify(destDir)} -Force`]);
  } else {
    const unzip = executable('unzip');
    if (!unzip) throw new Error('解压 zip 需要 unzip，请先安装 unzip');
    run(unzip, ['-q', zipFile, '-d', destDir]);
  }
}

function moveFirstChild(fromDir, toDir) {
  rmrf(toDir);
  const children = fs.readdirSync(fromDir).map(n => path.join(fromDir, n));
  const firstDir = children.find(p => fs.statSync(p).isDirectory());
  if (!firstDir) throw new Error(`未找到解压目录: ${fromDir}`);
  fs.renameSync(firstDir, toDir);
  rmrf(fromDir);
}

function rmrf(p) {
  fs.rmSync(p, { recursive: true, force: true });
}

function findFiles(dir, predicate) {
  if (!exists(dir)) return [];
  const out = [];
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) out.push(...findFiles(p, predicate));
    else if (predicate(p)) out.push(p);
  }
  return out;
}

function findDirs(dir, predicate) {
  if (!exists(dir)) return [];
  const out = [];
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) {
      if (predicate(p)) out.push(p);
      else out.push(...findDirs(p, predicate));
    }
  }
  return out;
}
