#!/usr/bin/env node
// SteamDl build helper (MAUI UI + .NET engine sidecar 架构).
// 正式构建一律通过 GitHub Actions(.github/workflows/build-apk.yml)完成,
// 本脚本只保留本地开发辅助命令,无 npm 依赖。

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const isWin = process.platform === 'win32';

const args = process.argv.slice(2);
const task = (args.find(a => !a.startsWith('--')) || 'help').toLowerCase();
const opts = parseOptions(args.filter(a => a.startsWith('--')));

const config = opts.config || process.env.CONFIG || 'Release';
const port = opts.port || process.env.PORT || '8630';
const runtime = opts.runtime || process.env.RUNTIME ||
  (isWin ? 'win-x64' : process.platform === 'darwin' ? 'osx-x64' : 'linux-x64');

const serverProject = path.join(root, 'src', 'SteamDl.Server', 'SteamDl.Server.csproj');
const mauiProject = path.join(root, 'src', 'SteamDl.Maui', 'SteamDl.Maui.csproj');
const mauiDir = path.join(root, 'src', 'SteamDl.Maui');
const engineOut = path.join(root, 'artifacts', 'engine');

main().catch(err => {
  console.error(`\nERROR: ${err.message}`);
  process.exit(1);
});

async function main() {
  switch (task) {
    case 'help': return help();
    case 'doctor': return doctor();
    case 'run': return runEngine();
    case 'publish-engine': return publishEngine();
    case 'maui-run': return mauiRun();
    case 'maui-build': return mauiBuild();
    case 'clean': return clean();
    default:
      help();
      throw new Error(`未知任务: ${task}`);
  }
}

function help() {
  console.log(`SteamDl build helper (MAUI + .NET sidecar)

正式构建(APK/桌面包)一律通过 GitHub Actions 完成:
  - push 到 develop 分支构建 Debug APK
  - push v* tag 或手动 workflow_dispatch 构建 Release 并发布

本地开发辅助命令:
  node build.mjs doctor                       检查本机工具链
  node build.mjs run --port=8630              本机启动 .NET 引擎(供 MAUI UI 连接)
  node build.mjs maui-run                     启动 MAUI UI(需要对应平台 MAUI workload)
  node build.mjs maui-build --framework=net9.0-android
                                              轻量触发 MAUI 项目构建(可能需要 workload)
  node build.mjs publish-engine --runtime=${runtime}
                                              发布引擎 sidecar 到 artifacts/engine
  node build.mjs clean                        清理构建产物
`);
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

function findOnPath(name) {
  const localCandidates = {
    dotnet: [path.join(root, '.tools', 'dotnet', isWin ? 'dotnet.exe' : 'dotnet')],
    java: [path.join(root, '.tools', 'jdk', 'bin', isWin ? 'java.exe' : 'java')],
  }[name] || [];
  for (const candidate of localCandidates) {
    if (fs.existsSync(candidate)) return candidate;
  }

  const paths = (process.env.PATH || '').split(path.delimiter);
  const names = isWin ? [name, `${name}.exe`, `${name}.bat`, `${name}.cmd`] : [name];
  for (const dir of paths) {
    for (const n of names) {
      const full = path.join(dir, n);
      if (fs.existsSync(full)) return full;
    }
  }
  return null;
}

function run(command, commandArgs = [], options = {}) {
  console.log(`> ${command} ${commandArgs.join(' ')}`);
  const useShell = isWin && /\.(bat|cmd)$/i.test(command);
  const res = spawnSync(command, commandArgs, {
    cwd: options.cwd || root,
    stdio: 'inherit',
    shell: useShell,
    env: { ...process.env, ...(options.env || {}) },
  });
  if (res.error) throw res.error;
  if (res.status !== 0) throw new Error(`命令失败(ExitCode=${res.status}): ${command}`);
}

function dotnetEnv(extra = {}) {
  return {
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    ...extra,
  };
}

function requireCommand(name, hint) {
  const found = findOnPath(name);
  if (!found) throw new Error(`未找到 ${name}。${hint || ''}`);
  return found;
}

function doctor() {
  console.log(`OS: ${os.type()} ${os.release()} ${os.arch()}`);
  for (const [name, hint] of [
    ['dotnet', '.NET 9 SDK: https://dot.net'],
    ['java', 'JDK 17(仅 Android 本地构建需要)'],
  ]) {
    const found = findOnPath(name);
    console.log(found ? `${name}: ${found}` : `${name}: 未找到 (${hint})`);
  }
  console.log('\n提示: 正式构建请推送到 GitHub 由 Actions 完成。');
}

function runEngine() {
  const dotnet = requireCommand('dotnet', '请安装 .NET 9 SDK。');
  console.log(`启动引擎: http://127.0.0.1:${port}`);
  run(dotnet, ['run', '--project', serverProject, '-c', config],
    { env: dotnetEnv({ PORT: String(port) }) });
}

function publishEngine() {
  const dotnet = requireCommand('dotnet', '请安装 .NET 9 SDK。');
  fs.rmSync(engineOut, { recursive: true, force: true });
  // linux-bionic-arm64(Android) 的 self-contained + PublishSingleFile 在 .NET SDK 里
  // 是已知缺陷(dotnet/sdk#35518),会产出缺 libhostfxr.so 的可执行文件,
  // 在设备上报 "You must install .NET"。该 RID 跳过 single-file,保留松散发布。
  const isBionic = runtime === 'linux-bionic-arm64';
  const publishArgs = [
    'publish', serverProject,
    '-c', config,
    '-r', runtime,
    '--self-contained', 'true',
    '-p:SelfContained=true',
    '-p:PublishSelfContained=true',
  ];
  if (!isBionic) {
    publishArgs.push(
      '-p:PublishSingleFile=true',
      '-p:EnableCompressionInSingleFile=true',
    );
  }
  publishArgs.push('-o', engineOut);
  run(dotnet, publishArgs, { env: dotnetEnv() });
  console.log(`引擎 sidecar 产物: ${engineOut}`);
}

function mauiRun() {
  const dotnet = requireCommand('dotnet', '请安装 .NET 9 SDK 与 MAUI workload。');
  const framework = opts.framework || process.env.TARGET_FRAMEWORK || (isWin ? 'net9.0-windows10.0.19041.0' : 'net9.0-android');
  run(dotnet, ['run', '--project', mauiProject, '-f', framework, '-c', config], { cwd: mauiDir, env: dotnetEnv() });
}

function mauiBuild() {
  const dotnet = requireCommand('dotnet', '请安装 .NET 9 SDK 与 MAUI workload。');
  const framework = opts.framework || process.env.TARGET_FRAMEWORK || 'net9.0-android';
  run(dotnet, ['build', mauiProject, '-f', framework, '-c', config], { env: dotnetEnv() });
}

function clean() {
  for (const dir of [
    path.join(root, 'artifacts'),
    path.join(root, 'src', 'SteamDl.Core', 'bin'),
    path.join(root, 'src', 'SteamDl.Core', 'obj'),
    path.join(root, 'src', 'SteamDl.Server', 'bin'),
    path.join(root, 'src', 'SteamDl.Server', 'obj'),
    path.join(root, 'src', 'SteamDl.Maui', 'bin'),
    path.join(root, 'src', 'SteamDl.Maui', 'obj'),
  ]) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  console.log('已清理构建产物');
}
