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

const defaultNugetSources = [
  'https://repo.huaweicloud.com/repository/nuget/v3/index.json',
  'https://api.nuget.org/v3/index.json',
];
const config = opts.config || process.env.CONFIG || 'Release';
const port = opts.port || process.env.PORT || '8630';
const runtime = opts.runtime || process.env.RUNTIME || (isWin ? 'win-x64' : isMac ? 'osx-x64' : 'linux-x64');
const dotnetChannel = opts.dotnetChannel || process.env.DOTNET_CHANNEL || '9.0';
const androidApi = opts.androidApi || process.env.ANDROID_API || '35';
const androidBuildTools = opts.androidBuildTools || process.env.ANDROID_BUILD_TOOLS || '35.0.0';
const nugetSources = parseList(opts.nugetSource || process.env.NUGET_SOURCE, defaultNugetSources);
const jdkUrl = opts.jdkUrl || process.env.JDK_URL || '';
const androidCmdlineToolsUrl = opts.androidCmdlineToolsUrl || process.env.ANDROID_CMDLINE_TOOLS_URL || '';
const jdkVersion = opts.jdkVersion || process.env.JDK_VERSION || '17.0.19_10';
const npmRegistry = opts.npmRegistry || process.env.NPM_REGISTRY || process.env.NPM_CONFIG_REGISTRY || 'https://registry.npmmirror.com';
const webDockerImage = opts.webDockerImage || process.env.WEB_DOCKER_IMAGE || 'docker.m.daocloud.io/library/node:22-bookworm';
const dotnetDockerImage = opts.dotnetDockerImage || process.env.DOTNET_DOCKER_IMAGE || 'm.daocloud.io/mcr.microsoft.com/dotnet/sdk:9.0';

const toolsDir = path.join(root, '.tools');
const localDotnetDir = path.join(toolsDir, isWin ? 'dotnet-win' : 'dotnet');
const localDotnet = path.join(localDotnetDir, isWin ? 'dotnet.exe' : 'dotnet');
const localJdkDir = path.join(toolsDir, isWin ? 'jdk-win' : 'jdk');
const androidSdkRoot = process.env.ANDROID_SDK_ROOT || process.env.ANDROID_HOME || path.join(toolsDir, 'android-sdk');
const sdkManager = path.join(androidSdkRoot, 'cmdline-tools', 'latest', 'bin', isWin ? 'sdkmanager.bat' : 'sdkmanager');
const serverProject = path.join(root, 'src', 'SteamDl.Server', 'SteamDl.Server.csproj');
const androidProject = path.join(root, 'src', 'SteamDl.Android', 'SteamDl.Android.csproj');
const webProject = path.join(root, 'src', 'SteamDl.Web');
const serverOut = path.join(root, 'artifacts', 'server');
const apkOut = path.join(root, 'artifacts', 'apk');
const keystore = opts.keystore || process.env.ANDROID_KEYSTORE || path.join(toolsDir, 'keystore', 'steamdl-release.keystore');
const keyAlias = opts.keyAlias || process.env.ANDROID_KEY_ALIAS || 'steamdl';
const storePass = opts.storePass || process.env.ANDROID_STORE_PASS || 'steamdl-changeit';
const keyPass = opts.keyPass || process.env.ANDROID_KEY_PASS || storePass;
const forceWebBuild = opts.forceWeb === 'true' || process.env.FORCE_WEB_BUILD === '1';
const forceRestore = opts.forceRestore === 'true' || process.env.FORCE_RESTORE === '1';
const forceApkBuild = opts.forceApk === 'true' || process.env.FORCE_APK_BUILD === '1';

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
    case 'install-deps': await installAllDeps(); return;
    case 'restore': return restoreServer();
    case 'build-web': return buildWeb();
    case 'docker-build-web': return dockerBuildWeb();
    case 'build': return buildServer();
    case 'docker-build':
    case 'docker-build-server': return dockerBuildServer();
    case 'run': return runServer();
    case 'publish-server': return publishServer();
    case 'docker-publish-server': return dockerPublishServer();
    case 'build-apk':
    case 'publish-apk': return buildApk();
    case 'docker-build-apk':
    case 'docker-publish-apk': return dockerBuildApk();
    case 'clean': return clean();
    case 'clean-artifacts': return cleanArtifacts();
    default:
      help();
      throw new Error(`未知任务: ${task}`);
  }
}

function help() {
  console.log(`SteamDl build helper\n\nUsage:\n  node build.mjs doctor\n  node build.mjs install-deps\n  node build.mjs build-web\n  node build.mjs docker-build-web\n  node build.mjs build\n  node build.mjs docker-build\n  node build.mjs run --port=8630\n  node build.mjs publish-server --config=Release --runtime=${runtime}\n  node build.mjs docker-publish-server --config=Release --runtime=${runtime}\n  node build.mjs build-apk --config=Release --android-api=35 --android-build-tools=35.0.0\n  node build.mjs build-apk --nuget-source=${nugetSources.join(',')}\n  node build.mjs build-apk --keystore=/path/release.keystore --key-alias=steamdl --store-pass=*** --key-pass=***\n  node build.mjs docker-build-apk --config=Release\n  node build.mjs clean\n  node build.mjs clean-artifacts\n\nDownload/build source options:\n  --nuget-source=<url[,url...]> or NUGET_SOURCE=<url[,url...]>\n  --npm-registry=${npmRegistry} or NPM_REGISTRY=${npmRegistry}\n  --jdk-url=<url> or JDK_URL=<url>\n  --android-cmdline-tools-url=<url> or ANDROID_CMDLINE_TOOLS_URL=<url>\n  --jdk-version=17.0.19_10 or JDK_VERSION=17.0.19_10\n  --web-docker-image=${webDockerImage} or WEB_DOCKER_IMAGE=${webDockerImage}\n  --dotnet-docker-image=${dotnetDockerImage} or DOTNET_DOCKER_IMAGE=${dotnetDockerImage}\n  --force-web=true or FORCE_WEB_BUILD=1\n  --force-restore=true or FORCE_RESTORE=1\n  --force-apk=true or FORCE_APK_BUILD=1\n\nNotes:\n  Missing portable tools are installed under .tools/.\n  NuGet sources default to domestic mirrors first: ${nugetSources.join(' -> ')}.\n  npm registry defaults to ${npmRegistry}.\n  JDK and Android cmdline-tools downloads try domestic mirrors first, then official URLs.\n  Release APKs are signed. If no keystore is provided, a local keystore is generated at .tools/keystore/.\n  install-deps installs/restores Web npm, .NET SDK, NuGet packages, JDK, Android SDK and Android workload.\n  Incremental builds skip fresh Web output, fresh Android restore assets and fresh APK output unless force flags are used.\n  Non-docker commands always use local toolchain. Docker is only used by explicit docker-* commands.\n  Android APK build requires .NET SDK + Android workload + JDK 17 + Android SDK.\n`);
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

function parseList(value, fallback) {
  if (!value) return [...fallback];
  return String(value)
    .split(/[;,\s]+/)
    .map(x => x.trim())
    .filter(Boolean);
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

function nugetSourceArgs() {
  return nugetSources.flatMap(source => ['--source', source]);
}

function nugetSourceShellArgs() {
  return nugetSources.map(source => `--source ${quoteShell(source)}`).join(' ');
}

function npmEnv(extra = {}) {
  return {
    NPM_CONFIG_REGISTRY: npmRegistry,
    npm_config_registry: npmRegistry,
    ...extra,
  };
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

function dotnet(commandArgs, extraEnv = {}) {
  const sdk = getDotnetSdkPath();
  if (!sdk) throw new Error('未找到可用的 .NET SDK。若系统 dotnet 只有 Runtime，请运行: node build.mjs install-dotnet');
  return run(sdk, commandArgs, { env: dotnetEnv(extraEnv) });
}

function npmCommand() {
  return executable(isWin ? 'npm.cmd' : 'npm');
}

function dockerCommand() {
  return executable('docker');
}

function newestMtime(p) {
  if (!exists(p)) return 0;
  const stat = fs.statSync(p);
  if (!stat.isDirectory()) return stat.mtimeMs;
  let newest = 0;
  for (const ent of fs.readdirSync(p, { withFileTypes: true })) {
    if (['node_modules', 'dist', 'bin', 'obj'].includes(ent.name)) continue;
    newest = Math.max(newest, newestMtime(path.join(p, ent.name)));
  }
  return newest;
}

function isWebOutputFresh() {
  const packageJson = path.join(webProject, 'package.json');
  if (!exists(packageJson)) return true;
  if (forceWebBuild) return false;
  const outputIndex = path.join(root, 'src', 'SteamDl.Core', 'wwwroot', 'index.html');
  const outputAssets = path.join(root, 'src', 'SteamDl.Core', 'wwwroot', 'assets');
  if (!exists(outputIndex) || !exists(outputAssets)) return false;
  const newestInput = Math.max(
    newestMtime(path.join(webProject, 'src')),
    newestMtime(path.join(webProject, 'index.html')),
    newestMtime(path.join(webProject, 'package.json')),
    newestMtime(path.join(webProject, 'package-lock.json')),
    newestMtime(path.join(webProject, 'tsconfig.json')),
    newestMtime(path.join(webProject, 'vite.config.ts')),
  );
  return newestMtime(outputIndex) >= newestInput;
}

function isDotnetRestoreFresh(projectFile) {
  if (forceRestore) return false;
  const projectDir = path.dirname(projectFile);
  const assets = path.join(projectDir, 'obj', 'project.assets.json');
  if (!exists(assets)) return false;
  const newestInput = Math.max(
    newestMtime(projectFile),
    newestMtime(path.join(root, 'src', 'SteamDl.Core', 'SteamDl.Core.csproj')),
    newestMtime(path.join(root, 'Directory.Packages.props')),
    newestMtime(path.join(root, 'NuGet.Config')),
    newestMtime(path.join(root, 'global.json')),
  );
  return newestMtime(assets) >= newestInput;
}

function projectTargetFramework(projectFile) {
  if (!exists(projectFile)) return '';
  const content = fs.readFileSync(projectFile, 'utf8');
  return content.match(/<TargetFramework>([^<]+)<\/TargetFramework>/)?.[1]?.trim() || '';
}

function androidApkSearchDir() {
  const targetFramework = projectTargetFramework(androidProject);
  return targetFramework
    ? path.join(root, 'src', 'SteamDl.Android', 'bin', config, targetFramework)
    : path.join(root, 'src', 'SteamDl.Android', 'bin', config);
}

function apkInputMtime() {
  return Math.max(
    newestMtime(path.join(root, 'src', 'SteamDl.Android')),
    newestMtime(path.join(root, 'src', 'SteamDl.Core')),
    config.toLowerCase() === 'release' ? newestMtime(keystore) : 0,
  );
}

function apkStampFile() {
  const targetFramework = projectTargetFramework(androidProject) || 'unknown';
  return path.join(toolsDir, 'build-cache', `apk-${config}-${targetFramework}.stamp`);
}

function apkStampValue() {
  return JSON.stringify({ config, targetFramework: projectTargetFramework(androidProject), inputMtime: apkInputMtime() });
}

function isApkOutputFresh() {
  if (forceApkBuild) return false;
  const apkFiles = findFilesShallow(androidApkSearchDir(), f => f.endsWith('.apk'));
  if (!apkFiles.length) return false;
  const stamp = apkStampFile();
  return exists(stamp) && fs.readFileSync(stamp, 'utf8') === apkStampValue();
}

function writeApkStamp() {
  const stamp = apkStampFile();
  mkdirp(path.dirname(stamp));
  fs.writeFileSync(stamp, apkStampValue());
}

function buildWeb() {
  if (!exists(path.join(webProject, 'package.json'))) return;
  if (isWebOutputFresh()) {
    section('跳过 React/Vite Web UI 构建');
    console.log('Web 产物未过期。如需强制重建，传入 --force-web=true 或 FORCE_WEB_BUILD=1。');
    return;
  }
  installWebDeps();
  run(npmCommand(), ['run', 'build'], { cwd: webProject, env: npmEnv() });
}

function installWebDeps() {
  if (!exists(path.join(webProject, 'package.json'))) return;
  section('安装/检查 React/Vite Web UI 依赖');
  const npm = npmCommand();
  if (!npm) throw new Error('构建 React/Vite 前端需要本机 npm。若要使用 Docker，请执行 docker-build-web 或 docker-build-apk。');
  if (!exists(path.join(webProject, 'node_modules'))) {
    run(npm, ['install', '--registry', npmRegistry], { cwd: webProject, env: npmEnv() });
  }
}

function dockerBuildWeb() {
  if (!exists(path.join(webProject, 'package.json'))) return;
  if (isWebOutputFresh()) {
    section('跳过 Docker React/Vite Web UI 构建');
    console.log('Web 产物未过期。如需强制重建，传入 --force-web=true 或 FORCE_WEB_BUILD=1。');
    return;
  }
  section(`Docker 构建 React/Vite Web UI (${webDockerImage})`);
  const docker = dockerCommand();
  if (!docker) throw new Error('docker-build-web 需要可用的 docker 命令。');
  const installCommand = exists(path.join(webProject, 'node_modules'))
    ? 'echo Web node_modules exists, skip npm install'
    : `npm install --registry ${quoteShell(npmRegistry)}`;
  run(docker, [
    'run', '--rm',
    '-v', `${root}:/w`,
    '-w', '/w/src/SteamDl.Web',
    webDockerImage,
    'bash', '-lc', `${installCommand} && npm run build`,
  ]);
}

async function installAllDeps() {
  installWebDeps();
  await restoreServer();
  await restoreAndroid();
  console.log('依赖安装完成：Web npm、.NET SDK、NuGet 包、JDK、Android SDK、Android workload 均已安装/还原。可执行: node build.mjs build 或 node build.mjs build-apk');
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

function jdkUrls(targetOs, arch) {
  const ext = targetOs === 'windows' ? 'zip' : 'tar.gz';
  const file = `OpenJDK17U-jdk_${arch}_${targetOs}_hotspot_${jdkVersion}.${ext}`;
  const mirror = `https://mirrors.tuna.tsinghua.edu.cn/Adoptium/17/jdk/${arch}/${targetOs}/${file}`;
  const official = targetOs === 'windows'
    ? 'https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse'
    : 'https://api.adoptium.net/v3/binary/latest/17/ga/linux/x64/jdk/hotspot/normal/eclipse';
  return uniqueUrls([jdkUrl, mirror, official]);
}

function androidCmdlineToolsUrls() {
  const file = isWin
    ? 'commandlinetools-win-11076708_latest.zip'
    : isMac
      ? 'commandlinetools-mac-11076708_latest.zip'
      : 'commandlinetools-linux-11076708_latest.zip';
  return uniqueUrls([
    androidCmdlineToolsUrl,
    `https://mirrors.tuna.tsinghua.edu.cn/android/repository/${file}`,
    `https://dl.google.com/android/repository/${file}`,
  ]);
}

function uniqueUrls(urls) {
  return urls.filter((url, index) => url && urls.indexOf(url) === index);
}

function jdkLibraryPaths(javaHome = getJavaHome()) {
  if (!isLinux) return [];
  return [path.join(javaHome, 'lib'), path.join(javaHome, 'lib', 'jli')].filter(exists);
}

function withLibraryPath(paths) {
  const existing = process.env.LD_LIBRARY_PATH || '';
  return [...paths, existing].filter(Boolean).join(path.delimiter);
}

async function installJdk() {
  mkdirp(toolsDir);
  if (hasJava()) {
    console.log(`使用 Java: ${getJavaExe()}`);
    run(getJavaExe(), ['-version'], { env: androidEnv() });
    return;
  }

  section(`安装 JDK 17 到 ${localJdkDir}`);
  if (isWin) {
    const zip = path.join(toolsDir, 'jdk17-windows-x64.zip');
    await downloadFirst(jdkUrls('windows', 'x64'), zip);
    await extractZip(zip, path.join(toolsDir, 'jdk-extract'));
    moveFirstChild(path.join(toolsDir, 'jdk-extract'), localJdkDir);
  } else if (isLinux && os.arch() === 'x64') {
    const tarball = path.join(toolsDir, 'jdk17-linux-x64.tar.gz');
    await downloadFirst(jdkUrls('linux', 'x64'), tarball);
    rmrf(localJdkDir);
    mkdirp(localJdkDir);
    run('tar', ['-xzf', tarball, '-C', localJdkDir, '--strip-components=1']);
  } else if (isMac) {
    throw new Error('macOS 请先安装 JDK 17（例如 brew install temurin17），再运行 install-deps。');
  } else {
    throw new Error('当前平台不支持自动安装 JDK，请手动安装 JDK 17。');
  }
  run(getJavaExe(), ['-version'], { env: androidEnv() });
}

function androidEnv() {
  const javaHome = getJavaHome();
  const extraPath = [path.join(javaHome, 'bin'), path.join(androidSdkRoot, 'platform-tools')].filter(exists).join(path.delimiter);
  const libPaths = jdkLibraryPaths(javaHome);
  return {
    JAVA_HOME: javaHome,
    ANDROID_HOME: androidSdkRoot,
    ANDROID_SDK_ROOT: androidSdkRoot,
    PATH: extraPath ? `${extraPath}${path.delimiter}${process.env.PATH || ''}` : process.env.PATH,
    ...(libPaths.length ? { LD_LIBRARY_PATH: withLibraryPath(libPaths) } : {}),
  };
}

async function installAndroidSdk() {
  await installJdk();
  const cmdlineToolsDir = path.join(androidSdkRoot, 'cmdline-tools');
  mkdirp(cmdlineToolsDir);
  if (!exists(sdkManager)) {
    section(`安装 Android cmdline-tools 到 ${androidSdkRoot}`);
    const url = androidCmdlineToolsUrls();
    const zip = path.join(toolsDir, `commandlinetools-${process.platform}.zip`);
    const tmp = path.join(toolsDir, 'cmdline-tools-tmp');
    await downloadFirst(url, zip);
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
  dotnet(['restore', androidProject, ...nugetSourceArgs()], androidEnv());
}

async function buildServer() {
  buildWeb();
  await restoreServer();
  dotnet(['build', serverProject, '-c', config, '--no-restore']);
}

async function runServer() {
  buildWeb();
  await restoreServer();
  console.log(`启动 http://127.0.0.1:${port}`);
  const sdk = getDotnetSdkPath();
  run(sdk, ['run', '--project', serverProject, '-c', config, '--no-restore'], { env: { ...dotnetEnv(), PORT: String(port) } });
}

async function publishServer() {
  buildWeb();
  await restoreServer();
  rmrf(serverOut);
  dotnet(['publish', serverProject, '-c', config, '-r', runtime, '--self-contained', 'false', '--no-restore', '-o', serverOut]);
  console.log(`桌面服务端产物: ${serverOut}`);
}

function dockerDotnetShell(script) {
  const docker = dockerCommand();
  if (!docker) throw new Error('Docker 构建需要可用的 docker 命令。');
  mkdirp(path.join(toolsDir, 'home'));
  mkdirp(path.join(toolsDir, 'nuget'));
  run(docker, [
    'run', '--rm',
    '-v', `${root}:/w`,
    '-w', '/w',
    '-e', 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '-e', 'NUGET_PACKAGES=/w/.tools/nuget',
    '-e', 'HOME=/w/.tools/home',
    '-e', 'DOTNET_CLI_HOME=/w/.tools/home',
    dotnetDockerImage,
    'bash', '-lc', script,
  ]);
}

function dockerBuildServer() {
  dockerBuildWeb();
  section(`Docker 构建桌面服务端 (${dotnetDockerImage}, CONFIG=${config})`);
  dockerDotnetShell(`dotnet restore /w/src/SteamDl.Server/SteamDl.Server.csproj ${nugetSourceShellArgs()} && dotnet build /w/src/SteamDl.Server/SteamDl.Server.csproj -c ${quoteShell(config)} --no-restore`);
}

function dockerPublishServer() {
  dockerBuildWeb();
  section(`Docker 发布桌面服务端 (${dotnetDockerImage}, CONFIG=${config}, RUNTIME=${runtime})`);
  rmrf(serverOut);
  dockerDotnetShell(`dotnet restore /w/src/SteamDl.Server/SteamDl.Server.csproj ${nugetSourceShellArgs()} && dotnet publish /w/src/SteamDl.Server/SteamDl.Server.csproj -c ${quoteShell(config)} -r ${quoteShell(runtime)} --self-contained false --no-restore -o /w/artifacts/server`);
  console.log(`桌面服务端产物: ${serverOut}`);
}

async function buildApk() {
  buildWeb();
  await restoreAndroid();
  rmrf(apkOut);
  mkdirp(apkOut);
  const signingArgs = config.toLowerCase() === 'release' ? ensureReleaseKeystore() : [];
  dotnet(['publish', androidProject, '-c', config, '-p:AndroidPackageFormat=apk', '--no-restore', ...signingArgs], androidEnv());
  writeApkStamp();
  copyApksToArtifacts();
}

async function dockerBuildApk() {
  dockerBuildWeb();
  section(`Docker 构建 Android APK (${dotnetDockerImage}, CONFIG=${config})`);
  const docker = dockerCommand();
  if (!docker) throw new Error('docker-build-apk 需要可用的 docker 命令。');
  if (!exists(localDotnet)) throw new Error('未找到项目本地 .NET SDK。请先运行: node build.mjs install-deps');
  if (!exists(localJdkDir)) throw new Error('未找到项目本地 JDK。请先运行: node build.mjs install-deps');
  if (!exists(androidSdkRoot)) throw new Error('未找到项目本地 Android SDK。请先运行: node build.mjs install-deps');

  const signingArgs = config.toLowerCase() === 'release' ? ensureReleaseKeystore().map(dockerMsbuildArg).join(' ') : '';
  if (isApkOutputFresh()) {
    section('跳过 Docker Android APK 发布');
    console.log('APK 产物未过期。如需强制重建，传入 --force-apk=true 或 FORCE_APK_BUILD=1。');
    copyApksToArtifacts();
    return;
  }
  const restoreCommand = isDotnetRestoreFresh(androidProject)
    ? 'echo Android restore assets fresh, skip dotnet restore'
    : `/w/.tools/dotnet/dotnet restore /w/src/SteamDl.Android/SteamDl.Android.csproj ${nugetSourceShellArgs()}`;
  run(docker, [
    'run', '--rm',
    '-v', `${root}:/w`,
    '-w', '/w',
    '-e', 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1',
    '-e', 'JAVA_HOME=/w/.tools/jdk',
    '-e', 'ANDROID_HOME=/w/.tools/android-sdk',
    '-e', 'ANDROID_SDK_ROOT=/w/.tools/android-sdk',
    '-e', 'LD_LIBRARY_PATH=/w/.tools/jdk/lib',
    '-e', 'NUGET_PACKAGES=/w/.tools/nuget',
    '-e', 'HOME=/w/.tools/home',
    '-e', 'DOTNET_CLI_HOME=/w/.tools/home',
    dotnetDockerImage,
    'bash', '-lc', `${restoreCommand} && /w/.tools/dotnet/dotnet publish /w/src/SteamDl.Android/SteamDl.Android.csproj -c ${quoteShell(config)} -p:AndroidPackageFormat=apk --no-restore ${signingArgs}`,
  ]);
  writeApkStamp();
  copyApksToArtifacts();
}

function ensureReleaseKeystore() {
  mkdirp(path.dirname(keystore));
  if (!exists(keystore)) {
    const keytool = path.join(getJavaHome(), 'bin', isWin ? 'keytool.exe' : 'keytool');
    const keytoolExe = exists(keytool) ? keytool : executable(isWin ? 'keytool.exe' : 'keytool');
    if (!keytoolExe) throw new Error('Release 签名需要 keytool。请先安装 JDK 17 或传入已有 keystore。');
    run(keytoolExe, [
      '-genkeypair',
      '-v',
      '-keystore', keystore,
      '-alias', keyAlias,
      '-keyalg', 'RSA',
      '-keysize', '2048',
      '-validity', '10000',
      '-storepass', storePass,
      '-keypass', keyPass,
      '-dname', 'CN=SteamDl, OU=SteamDl, O=SteamDl, L=Local, S=Local, C=CN',
    ]);
  }

  return [
    '-p:AndroidKeyStore=true',
    `-p:AndroidSigningKeyStore=${keystore}`,
    `-p:AndroidSigningKeyAlias=${keyAlias}`,
    `-p:AndroidSigningStorePass=${storePass}`,
    `-p:AndroidSigningKeyPass=${keyPass}`,
  ];
}

function dockerPath(p) {
  const rel = path.relative(root, p).replaceAll(path.sep, '/');
  return rel && !rel.startsWith('..') ? `/w/${rel}` : p;
}

function dockerMsbuildArg(arg) {
  if (arg.startsWith('-p:AndroidSigningKeyStore=')) {
    return quoteShell(`-p:AndroidSigningKeyStore=${dockerPath(arg.substring('-p:AndroidSigningKeyStore='.length))}`);
  }
  return quoteShell(arg);
}

function copyApksToArtifacts() {
  rmrf(apkOut);
  mkdirp(apkOut);
  const apkFiles = findFilesShallow(androidApkSearchDir(), f => f.endsWith('.apk'));
  if (!apkFiles.length) throw new Error('未找到 APK 产物');
  for (const file of apkFiles) fs.copyFileSync(file, path.join(apkOut, path.basename(file)));
  console.log(`APK 产物目录: ${apkOut}`);
  for (const file of fs.readdirSync(apkOut).filter(f => f.endsWith('.apk'))) console.log(path.join(apkOut, file));
}

function quoteShell(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function clean() {
  for (const dir of findDirs(path.join(root, 'src'), d => path.basename(d) === 'bin' || path.basename(d) === 'obj')) rmrf(dir);
  cleanArtifacts();
  console.log('已清理构建产物');
}

function cleanArtifacts() {
  rmrf(path.join(root, 'artifacts'));
  console.log('已清理 artifacts');
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
  if (hasJava()) run(getJavaExe(), ['-version'], { env: androidEnv() });
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

async function downloadFirst(urls, outFile) {
  let lastError = null;
  for (const url of urls) {
    try {
      await download(url, outFile);
      return;
    } catch (e) {
      lastError = e;
      console.warn(`下载源不可用，尝试下一个源: ${e.message}`);
    }
  }
  throw lastError || new Error(`没有可用下载源: ${outFile}`);
}

async function download(url, outFile) {
  mkdirp(path.dirname(outFile));
  console.log(`下载: ${url}`);
  fs.rmSync(outFile, { force: true });
  await new Promise((resolve, reject) => {
    const file = fs.createWriteStream(outFile);
    const headers = {
      'User-Agent': 'Wget/1.21.4',
      Accept: '*/*',
      Connection: 'close',
    };
    const request = (u, redirects = 0) => {
      https.get(u, { headers }, res => {
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

function findFilesShallow(dir, predicate) {
  if (!exists(dir)) return [];
  const out = [];
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isFile() && predicate(p)) out.push(p);
  }
  return out;
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
