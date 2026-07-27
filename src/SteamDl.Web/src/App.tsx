import { useEffect, useMemo, useRef, useState, type TouchEvent } from 'react';

type Job = {
  job_id: string;
  kind: string;
  id: string;
  name?: string;
  title?: string;
  username?: string;
  anonymous: boolean;
  os: string;
  depot?: string;
  output_dir: string;
  state: string;
  prompt?: string;
  prompt_secret?: boolean;
  percent: number;
  progress_text?: string;
  error?: string;
  downloaded?: boolean;
  log?: string;
  updated_at: string;
};

type Settings = {
  default_download_dir: string;
  default_platform_os: string;
  max_downloads: number;
  auto_resume: boolean;
};

type Config = { can_pick_directory: boolean; can_open_output: boolean; download_dir: string };
type LibraryGame = { app_id: string; name: string; header_image?: string; install_dir?: string; installdir?: string; size_bytes?: number; size_text?: string; is_downloaded?: boolean; downloaded_at?: string };
type AccountDetail = { username: string; logged_in: boolean; remember_password: boolean; has_saved_password: boolean; last_used_at?: string };
type LoginState = { username?: string; state: string; prompt?: string; prompt_secret?: boolean; error?: string; log?: string; remember_password?: boolean };
type DownloadSeed = { kind: string; id: string; name?: string; install_dir?: string; installdir?: string; size_bytes?: number; size_text?: string } | null;
type Tab = 'accounts' | 'download' | 'library' | 'jobs' | 'settings';

function deleteDebug(message: string, data?: unknown, error = false) {
  const payload = data === undefined ? '' : data;
  if (error) console.error(`[SteamDl][delete-task] ${message}`, payload);
  else console.log(`[SteamDl][delete-task] ${message}`, payload);
}

async function api<T>(path: string, body?: unknown, method?: string): Promise<T> {
  const options: RequestInit = body === undefined
    ? {}
    : { method: method ?? 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
  if (method && body === undefined) options.method = method;
  const res = await fetch(path, options);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

const stateText: Record<string, string> = {
  idle: '空闲', queued: '排队中', starting: '启动中', running: '下载中', paused: '已暂停',
  waiting_input: '等待输入', interrupted: '已中断', done: '完成', error: '出错', cancelled: '已取消',
};

function formatBytes(bytes?: number) {
  const value = Number(bytes || 0);
  if (!Number.isFinite(value) || value <= 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let next = value;
  let unit = 0;
  while (next >= 1024 && unit < units.length - 1) {
    next /= 1024;
    unit++;
  }
  return `${next.toFixed(next >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

const tabs: [Tab, string][] = [['accounts', '账号'], ['download', '下载'], ['library', '游戏库'], ['jobs', '任务'], ['settings', '设置']];

export function App() {
  const [tab, setTab] = useState<Tab>(() => localStorage.getItem('steamdl.account') ? 'download' : 'accounts');
  const [jobs, setJobs] = useState<Job[]>([]);
  const [activeJob, setActiveJob] = useState<Job | null>(null);
  const [accounts, setAccounts] = useState<string[]>([]);
  const [accountDetails, setAccountDetails] = useState<AccountDetail[]>([]);
  const [loginState, setLoginState] = useState<LoginState>({ state: 'idle' });
  const [selectedAccount, setSelectedAccountState] = useState(() => localStorage.getItem('steamdl.account') || '');
  const [downloadSeed, setDownloadSeed] = useState<DownloadSeed>(null);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [config, setConfig] = useState<Config | null>(null);
  const [toast, setToast] = useState('');
  const [serviceOnline, setServiceOnline] = useState(true);
  const touchStart = useRef<{ x: number; y: number } | null>(null);
  const autoLibrarySyncRef = useRef('');

  function setSelectedAccount(username: string) {
    const next = username.trim();
    setSelectedAccountState(next);
    if (next) localStorage.setItem('steamdl.account', next);
    else localStorage.removeItem('steamdl.account');
  }

  async function refresh() {
    const [jobsRes, accountsRes, settingsRes, configRes] = await Promise.all([
      api<{ jobs: Job[] }>('/api/jobs'),
      api<{ accounts: string[]; account_details?: AccountDetail[]; login?: LoginState }>('/api/accounts').catch(() => ({ accounts: [], account_details: [], login: { state: 'idle' } })),
      api<Settings>('/api/settings'),
      api<Config>('/api/config').catch(() => null),
    ]);
    setJobs(jobsRes.jobs);
    setAccounts(accountsRes.accounts);
    setAccountDetails(accountsRes.account_details || []);
    setLoginState(accountsRes.login || { state: 'idle' });
    if (accountsRes.login?.state === 'done' && accountsRes.login.username && accountsRes.accounts.includes(accountsRes.login.username)) {
      setSelectedAccount(accountsRes.login.username);
    }
    setSettings(settingsRes);
    if (configRes) setConfig(configRes);
    setServiceOnline(true);
    if (activeJob) {
      const detail = await api<Job>(`/api/jobs/${activeJob.job_id}`).catch(() => null);
      if (detail) setActiveJob(detail);
    } else if (jobsRes.jobs.length > 0) {
      const running = jobsRes.jobs.find(j => ['queued', 'starting', 'running', 'waiting_input'].includes(j.state));
      if (running) setActiveJob(await api<Job>(`/api/jobs/${running.job_id}`));
    }
  }

  useEffect(() => {
    const runRefresh = () => refresh().catch(e => { setServiceOnline(false); setToast(e.message); });
    runRefresh();
    const timer = setInterval(() => refresh().catch(() => setServiceOnline(false)), 1000);
    return () => clearInterval(timer);
  }, [activeJob?.job_id]);

  useEffect(() => {
    if (loginState.state === 'done') setToast('');
  }, [loginState.state]);

  useEffect(() => {
    if (!selectedAccount || !accounts.includes(selectedAccount)) { autoLibrarySyncRef.current = ''; return; }
    const key = selectedAccount;
    if (autoLibrarySyncRef.current === key) return;
    autoLibrarySyncRef.current = key;
    api('/api/library/sync', { username: selectedAccount, mode: 'incremental' }).catch(() => {});
  }, [selectedAccount, accounts.join('|')]);

  useEffect(() => {
    const updateNetwork = () => setServiceOnline(navigator.onLine);
    window.addEventListener('online', updateNetwork);
    window.addEventListener('offline', updateNetwork);
    updateNetwork();
    return () => {
      window.removeEventListener('online', updateNetwork);
      window.removeEventListener('offline', updateNetwork);
    };
  }, []);

  const running = useMemo(() => jobs.some(j => ['queued', 'starting', 'running', 'waiting_input'].includes(j.state)), [jobs]);
  const locked = !selectedAccount || !accounts.includes(selectedAccount);

  async function manualRefresh() {
    try {
      await refresh();
      setToast('已刷新');
    } catch (e: any) {
      setServiceOnline(false);
      setToast(e.message);
    }
  }

  function openTab(next: Tab) {
    if (locked && next !== 'accounts' && next !== 'jobs' && next !== 'settings') {
      setToast('请先在账号页面完成登录');
      setTab('accounts');
      return;
    }
    setTab(next);
  }

  function openDownload(seed: DownloadSeed) {
    setDownloadSeed(seed);
    openTab('download');
  }

  function onTouchStart(e: TouchEvent) {
    const touch = e.touches[0];
    touchStart.current = { x: touch.clientX, y: touch.clientY };
  }

  function onTouchEnd(e: TouchEvent) {
    const start = touchStart.current;
    touchStart.current = null;
    if (!start) return;
    const touch = e.changedTouches[0];
    const dx = touch.clientX - start.x;
    const dy = touch.clientY - start.y;
    if (Math.abs(dx) < 70 || Math.abs(dx) < Math.abs(dy) * 1.4) return;
    const index = tabs.findIndex(([key]) => key === tab);
    const nextIndex = dx < 0 ? Math.min(tabs.length - 1, index + 1) : Math.max(0, index - 1);
    if (nextIndex !== index) openTab(tabs[nextIndex][0]);
  }

  return <div className="app">
    <aside className="sidebar">
      <div className="brand">
        <div className="logo">SD</div>
        <div>
          <h1>SteamDl</h1>
          <p>{selectedAccount && accounts.includes(selectedAccount) ? `当前账号 ${selectedAccount}` : '请先登录账号'}</p>
        </div>
      </div>
      <Nav tab={tab} setTab={openTab} />
    </aside>

    <main className="main" onTouchStart={onTouchStart} onTouchEnd={onTouchEnd}>
      {toast && <div className="toast" onClick={() => setToast('')}>{toast}</div>}
      <div hidden={tab !== 'accounts'}><AccountsPage accounts={accounts} accountDetails={accountDetails} loginState={loginState} selectedAccount={selectedAccount} setSelectedAccount={setSelectedAccount} refresh={refresh} setToast={setToast} /></div>
      <div hidden={tab !== 'download'}>{locked ? <LoginGate setTab={openTab} /> : <DownloadPage settings={settings} config={config} selectedAccount={selectedAccount} seed={downloadSeed} clearSeed={() => setDownloadSeed(null)} setToast={setToast} refresh={refresh} setActiveJob={setActiveJob} setTab={openTab} />}</div>
      <div hidden={tab !== 'library'}>{locked ? <LoginGate setTab={openTab} /> : <LibraryPage selectedAccount={selectedAccount} jobs={jobs} setToast={setToast} openDownload={openDownload} />}</div>
      <div hidden={tab !== 'jobs'}><JobsPage jobs={jobs} activeJob={activeJob} setActiveJob={setActiveJob} refresh={refresh} setToast={setToast} /></div>
      <div hidden={tab !== 'settings'}><SettingsPage settings={settings} config={config} setSettings={setSettings} setToast={setToast} loginState={loginState} activeJob={activeJob} jobs={jobs} setActiveJob={setActiveJob} refresh={refresh} /></div>
    </main>
  </div>;
}

function Nav({ tab, setTab }: { tab: Tab; setTab: (tab: Tab) => void }) {
  return <nav>{tabs.map(([key, label]) => <button key={key} className={tab === key ? 'active' : ''} onClick={() => setTab(key)}>{label}</button>)}</nav>;
}

function LoginGate({ setTab }: { setTab: (tab: Tab) => void }) {
  return <div className="card gate">
    <h3>需要先登录账号</h3>
    <p className="muted">SteamDl 会先完成账号登录并保存 refresh token，再允许解析链接、访问游戏库和创建下载任务。设置页面可随时打开。</p>
    <button className="primary" onClick={() => setTab('accounts')}>去账号页面</button>
  </div>;
}

function DownloadPage({ settings, config, selectedAccount, seed, clearSeed, setToast, refresh, setActiveJob, setTab }: { settings: Settings | null; config: Config | null; selectedAccount: string; seed: DownloadSeed; clearSeed: () => void; setToast: (s: string) => void; refresh: () => Promise<void>; setActiveJob: (job: Job | null) => void; setTab: (tab: Tab) => void }) {
  const [url, setUrl] = useState('');
  const [parsed, setParsed] = useState<{ kind: string; id: string } | null>(seed ? { kind: seed.kind, id: seed.id } : null);
  const [appInfo, setAppInfo] = useState<any>(null);
  const [os, setOs] = useState(settings?.default_platform_os ?? 'windows');
  const [depot, setDepot] = useState('');
  const [outputDir, setOutputDir] = useState(settings?.default_download_dir ?? '');

  useEffect(() => {
    if (settings) {
      setOs(settings.default_platform_os);
      setOutputDir(settings.default_download_dir);
    }
  }, [settings?.default_download_dir, settings?.default_platform_os]);

  useEffect(() => {
    if (!seed) return;
    const seedInstallDir = seed.installdir || seed.install_dir || '';
    setParsed({ kind: seed.kind, id: seed.id });
    setAppInfo(seed.name ? { name: seed.name, installdir: seedInstallDir, install_dir: seedInstallDir, size_bytes: seed.size_bytes, size_text: seed.size_text } : null);
    if (seed.kind === 'app') {
      api<any>(`/api/appinfo/${seed.id}`).then(info => setAppInfo({
        ...info,
        name: info?.name || seed.name,
        installdir: info?.installdir || info?.install_dir || seedInstallDir,
        install_dir: info?.install_dir || info?.installdir || seedInstallDir,
        size_bytes: Number(info?.size_bytes || seed.size_bytes || 0),
      })).catch(() => {});
    }
    clearSeed();
  }, [seed?.id]);

  async function parse() {
    try {
      const next = await api<{ kind: string; id: string }>('/api/parse', { url });
      setParsed(next);
      setAppInfo(null);
      if (next.kind === 'app') api<any>(`/api/appinfo/${next.id}`).then(info => setAppInfo({ ...info, size_bytes: Number(info?.size_bytes || 0) })).catch(() => {});
    } catch (e: any) { setToast(e.message); }
  }

  async function pickDirectory() {
    if (!config?.can_pick_directory) {
      const picked = window.prompt('当前浏览器无法读取本机目录路径，请手动输入保存目录：', outputDir || settings?.default_download_dir || '/storage/emulated/0/Download/steamdl');
      if (picked?.trim()) {
        setOutputDir(picked.trim());
        setToast('已填写保存目录');
      }
      return;
    }
    try {
      const res = await api<{ path?: string; download_dir?: string }>('/api/pick-directory', {});
      const picked = res.path || res.download_dir || '';
      if (picked) {
        setOutputDir(picked);
        setToast('已选择保存目录');
      }
    } catch (e: any) { setToast(e.message); }
  }

  async function start() {
    if (!parsed) return;
    try {
      const installDir = appInfo?.install_dir || appInfo?.installdir || appInfo?.name;
      const res = await api<{ job: Job }>('/api/jobs', { kind: parsed.kind, id: parsed.id, username: selectedAccount, anonymous: false, os, depot, output_dir: outputDir, install_dir: installDir, installdir: installDir, name: appInfo?.name });
      setActiveJob(res.job);
      setToast(`任务已创建: ${res.job.job_id.slice(0, 8)}`);
      await refresh();
      setTab('jobs');
    } catch (e: any) { setToast(e.message); }
  }

  return <section className="grid two">
    <div className="card span2">
      <h3>解析链接下载</h3>
      <p className="muted">当前账号：{selectedAccount}。也可以从游戏库选择游戏后自动跳转到这里。</p>
      <div className="row">
        <input value={url} onChange={e => setUrl(e.target.value)} placeholder="https://store.steampowered.com/app/730/... 或 AppID" />
        <button onClick={parse}>解析到下载页</button>
      </div>
    </div>
    <div className="card cover">
      {appInfo?.header_image ? <img src={appInfo.header_image} /> : <div className="emptyCover">Steam</div>}
      <h3>{appInfo?.name || (parsed ? `${parsed.kind} ${parsed.id}` : '等待选择游戏')}</h3>
      <p>{parsed ? `类型 ${parsed.kind} · ID ${parsed.id}${appInfo?.size_bytes ? ` · 待下载大小 ${formatBytes(appInfo.size_bytes)}` : ''}` : '从游戏库选择，或解析链接后创建下载任务'}</p>
    </div>
    <div className="card">
      <h3>下载选项</h3>
      <label>目标平台
        <select value={os} onChange={e => setOs(e.target.value)}>
          <option value="windows">Windows</option><option value="linux">Linux</option><option value="any">全部平台</option>
        </select>
      </label>
      <label>Depot ID（可选）<input value={depot} onChange={e => setDepot(e.target.value)} placeholder="例如 731" /></label>
      <label>保存目录
        <div className="row inlineRow"><input value={outputDir} onChange={e => setOutputDir(e.target.value)} placeholder={settings?.default_download_dir || '/storage/emulated/0/Download/steamdl'} /><button type="button" onClick={pickDirectory}>{config?.can_pick_directory ? '选择' : '填写'}</button></div>
      </label>
      <p className="muted">实际下载会自动在该目录下创建游戏目录（优先使用 Steam 游戏安装目录名）。</p>
      <button className="primary block" disabled={!parsed} onClick={start}>使用 {selectedAccount} 开始下载</button>
    </div>
  </section>;
}

function displayJobTitle(job: Job) {
  return job.title || job.name || `${job.kind} ${job.id}`;
}

function JobsPage({ jobs, activeJob, setActiveJob, refresh, setToast }: { jobs: Job[]; activeJob: Job | null; setActiveJob: (job: Job | null) => void; refresh: () => Promise<void>; setToast: (s: string) => void }) {
  const [jobMenu, setJobMenu] = useState<{ job: Job; x: number; y: number } | null>(null);
  const longPressTimer = useRef<number | null>(null);
  const longPressTriggered = useRef(false);
  async function load(job: Job) { setActiveJob(await api<Job>(`/api/jobs/${job.job_id}`)); }
  async function action(job: Job, name: 'cancel' | 'retry' | 'pause' | 'resume') {
    if (name === 'cancel' && !window.confirm('确定要取消当前下载任务吗？')) return;
    try { await api(`/api/jobs/${job.job_id}/${name}`, {}); await refresh(); if (activeJob?.job_id === job.job_id) await load(job); }
    catch (e: any) { setToast(e.message); }
  }
  function clearLongPress() {
    if (longPressTimer.current !== null) window.clearTimeout(longPressTimer.current);
    longPressTimer.current = null;
  }
  function openJobMenu(job: Job, x: number, y: number) {
    deleteDebug('打开单任务菜单', { job_id: job.job_id, title: displayJobTitle(job), x, y });
    setJobMenu({ job, x, y });
  }
  async function deleteJob(job: Job) {
    deleteDebug('点击菜单删除任务', { job_id: job.job_id, title: displayJobTitle(job) });
    setJobMenu(null);
    if (!window.confirm(`确定要删除任务“${displayJobTitle(job)}”吗？`)) {
      deleteDebug('用户取消删除任务确认', { job_id: job.job_id });
      return;
    }
    const deleteFiles = window.confirm('是否同时删除该任务已下载的文件？选择“取消”将只删除任务记录。');
    deleteDebug('准备发送删除任务请求', { job_id: job.job_id, delete_files: deleteFiles, url: `/api/jobs/${job.job_id}` });
    try {
      await api(`/api/jobs/${job.job_id}`, { delete_files: deleteFiles }, 'DELETE');
      deleteDebug('删除任务请求成功', { job_id: job.job_id, delete_files: deleteFiles });
      if (activeJob?.job_id === job.job_id) setActiveJob(null);
      await refresh();
      deleteDebug('删除任务后刷新完成', { job_id: job.job_id });
      setToast(deleteFiles ? '任务和文件已删除' : '任务记录已删除');
    } catch (e: any) {
      deleteDebug('删除任务请求失败', { job_id: job.job_id, message: e?.message || String(e) }, true);
      setToast(e.message);
    }
  }
  async function deleteAllJobs() {
    deleteDebug('点击删除全部任务', { count: jobs.length });
    if (jobs.length === 0) {
      deleteDebug('删除全部任务被忽略：任务列表为空');
      return;
    }
    if (!window.confirm(`确定要删除全部 ${jobs.length} 个任务记录吗？`)) {
      deleteDebug('用户取消删除全部任务确认', { count: jobs.length });
      return;
    }
    const deleteFiles = window.confirm('是否同时删除这些任务对应的下载文件？选择“取消”将只删除任务记录。');
    deleteDebug('准备发送删除全部任务请求', { count: jobs.length, delete_files: deleteFiles, url: '/api/jobs' });
    try {
      await api('/api/jobs', { delete_files: deleteFiles }, 'DELETE');
      deleteDebug('删除全部任务请求成功', { delete_files: deleteFiles });
      setActiveJob(null);
      await refresh();
      deleteDebug('删除全部任务后刷新完成');
      setToast(deleteFiles ? '全部任务和文件已删除' : '全部任务记录已删除');
    } catch (e: any) {
      deleteDebug('删除全部任务请求失败', { message: e?.message || String(e) }, true);
      setToast(e.message);
    }
  }
  async function sendInput() {
    if (!activeJob) return;
    const input = document.querySelector<HTMLInputElement>('#jobInput')?.value || '';
    await api(`/api/jobs/${activeJob.job_id}/input`, { answer: input });
    await load(activeJob);
  }
  return <section className="grid two jobsPage">
    <div className="card">
      <div className="detailHead"><h3>任务历史</h3><button className="danger" disabled={jobs.length === 0} onClick={deleteAllJobs}>删除全部</button></div>
      <p className="muted hintText">右键或长按任务可删除单个任务。</p>
      <div className="jobList">{jobs.map(job => <button className="jobItem" key={job.job_id} onClick={e => {
        if (longPressTriggered.current) {
          e.preventDefault();
          longPressTriggered.current = false;
          return;
        }
        load(job);
      }} onContextMenu={e => {
        e.preventDefault();
        clearLongPress();
        openJobMenu(job, e.clientX, e.clientY);
      }} onPointerDown={e => {
        if (e.pointerType === 'mouse') return;
        clearLongPress();
        longPressTriggered.current = false;
        const x = e.clientX;
        const y = e.clientY;
        longPressTimer.current = window.setTimeout(() => {
          longPressTriggered.current = true;
          openJobMenu(job, x, y);
        }, 560);
      }} onPointerUp={clearLongPress} onPointerCancel={clearLongPress} onPointerLeave={clearLongPress}>
        <span>{displayJobTitle(job)}</span><small>{stateText[job.state] || job.state} · {Math.round(job.percent || 0)}%</small>
      </button>)}</div>
      {jobMenu && <div className="contextBackdrop" onClick={() => setJobMenu(null)} onContextMenu={e => e.preventDefault()}>
        <div className="contextMenu" style={{ left: `min(${jobMenu.x}px, calc(100vw - 220px))`, top: `min(${jobMenu.y}px, calc(100vh - 150px))` }} onClick={e => e.stopPropagation()}>
          <p>{displayJobTitle(jobMenu.job)}</p>
          <button className="danger block" onClick={() => deleteJob(jobMenu.job)}>删除任务</button>
          <button className="ghost block" onClick={() => setJobMenu(null)}>取消</button>
        </div>
      </div>}
    </div>
    <div className="card detail">
      {!activeJob ? <p className="muted">选择一个任务查看详情</p> : <>
        <div className="detailHead"><h3>{displayJobTitle(activeJob)}</h3><span className={`pill ${activeJob.state}`}>{stateText[activeJob.state] || activeJob.state}</span></div>
        <div className="bar"><i style={{ width: `${activeJob.percent || 0}%` }} /></div>
        <p className="muted">{activeJob.progress_text || activeJob.error || activeJob.output_dir}</p>
        {activeJob.state === 'waiting_input' && <div className="prompt"><p>{activeJob.prompt}</p><div className="row"><input id="jobInput" type={activeJob.prompt_secret ? 'password' : 'text'} /><button onClick={sendInput}>提交</button></div></div>}
        <p className="muted">详细日志请到“设置 - 日志”查看。</p>
        <div className="actions">
          {['queued', 'starting', 'running', 'waiting_input'].includes(activeJob.state) && <button onClick={() => action(activeJob, 'pause')}>暂停</button>}
          {activeJob.state === 'paused' && <button className="primary" onClick={() => action(activeJob, 'resume')}>继续</button>}
          <button className="danger" onClick={() => action(activeJob, 'cancel')}>取消</button>
          <button onClick={() => action(activeJob, 'retry')}>重试</button>
        </div>
      </>}
    </div>
  </section>;
}

function LibraryPage({ selectedAccount, jobs, setToast, openDownload }: { selectedAccount: string; jobs: Job[]; setToast: (s: string) => void; openDownload: (seed: DownloadSeed) => void }) {
  const [games, setGames] = useState<LibraryGame[]>([]);
  const [message, setMessage] = useState('');
  const [query, setQuery] = useState('');
  const [syncing, setSyncing] = useState(false);
  const [progress, setProgress] = useState({ scanned: 0, total: 0, percent: 0, state: 'idle', mode: 'full' });
  const [sortBy, setSortBy] = useState<'name' | 'app_id'>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [downloadFilter, setDownloadFilter] = useState<'all' | 'downloaded' | 'undownloaded'>('all');
  const [filterOpen, setFilterOpen] = useState(false);
  const jobByAppId = useMemo(() => {
    const map = new Map<string, Job>();
    for (const job of jobs) {
      if (job.kind !== 'app') continue;
      if (job.username && job.username !== selectedAccount) continue;
      const current = map.get(job.id);
      const currentTime = current ? Date.parse(current.updated_at || '') || 0 : 0;
      const nextTime = Date.parse(job.updated_at || '') || 0;
      if (!current || nextTime >= currentTime) map.set(job.id, job);
    }
    return map;
  }, [jobs, selectedAccount]);
  const visibleGames = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = games.filter(g => {
      if (q && !g.app_id.includes(q) && !g.name.toLowerCase().includes(q)) return false;
      const job = jobByAppId.get(g.app_id);
      const downloaded = !!(g.is_downloaded || job?.downloaded || job?.state === 'done');
      if (downloadFilter === 'downloaded') return downloaded;
      if (downloadFilter === 'undownloaded') return !downloaded;
      return true;
    });
    return [...filtered].sort((a, b) => {
      const value = sortBy === 'app_id' ? Number(a.app_id) - Number(b.app_id) : a.name.localeCompare(b.name, 'zh-Hans');
      return sortDir === 'asc' ? value : -value;
    });
  }, [games, query, sortBy, sortDir, downloadFilter, jobByAppId]);
  function applyLibraryStatus(res: any) {
    const items = Array.isArray(res.items) ? res.items : [];
    const nextGames = items.map((x: any) => ({ app_id: String(x.app_id || x.id), name: x.name || `App ${x.app_id || x.id}`, header_image: x.header_image, install_dir: x.install_dir || x.installdir, installdir: x.installdir || x.install_dir, size_bytes: Number(x.size_bytes || 0), size_text: x.size_text, is_downloaded: !!x.is_downloaded, downloaded_at: x.downloaded_at || '' }));
    setGames(nextGames);
    setMessage(res.message || `已同步 ${nextGames.length} 个游戏`);
    setProgress({ scanned: Number(res.scanned_app_count || 0), total: Number(res.app_count || 0), percent: Number(res.progress || 0), state: res.state || 'idle', mode: res.sync_mode || 'full' });
    setSyncing(res.state === 'running');
  }

  async function pollStatus() {
    let res = await api<any>(`/api/library/status?username=${encodeURIComponent(selectedAccount)}`);
    applyLibraryStatus(res);
    while (res.state === 'running') {
      await new Promise(resolve => setTimeout(resolve, 700));
      res = await api<any>(`/api/library/status?username=${encodeURIComponent(selectedAccount)}`);
      applyLibraryStatus(res);
    }
    return res;
  }

  useEffect(() => {
    let cancelled = false;
    async function restore() {
      try {
        let res = await api<any>(`/api/library/status?username=${encodeURIComponent(selectedAccount)}`);
        if (cancelled) return;
        applyLibraryStatus(res);
        while (!cancelled && res.state === 'running') {
          await new Promise(resolve => setTimeout(resolve, 700));
          res = await api<any>(`/api/library/status?username=${encodeURIComponent(selectedAccount)}`);
          if (!cancelled) applyLibraryStatus(res);
        }
      } catch {
        if (!cancelled) setSyncing(false);
      }
    }
    restore();
    return () => { cancelled = true; };
  }, [selectedAccount]);

  async function sync(mode: 'full' | 'incremental') {
    setSyncing(true);
    setQuery('');
    try {
      const res = await api<any>('/api/library/sync', { username: selectedAccount, mode, force_full_sync: mode === 'full' });
      applyLibraryStatus(res);
      const finalStatus = await pollStatus();
      if (finalStatus.state === 'error') setToast(finalStatus.message || '游戏库同步失败');
    } catch (e: any) { setToast(e.message); }
    finally { setSyncing(false); }
  }
  const modeText = progress.mode === 'incremental' ? '增量同步' : '全量同步';
  const emptyLibraryText = query.trim()
    ? '当前搜索没有匹配的游戏。清空搜索框可查看已同步的全部游戏。'
    : downloadFilter === 'downloaded'
      ? '当前没有匹配的已下载游戏。'
      : downloadFilter === 'undownloaded'
        ? '当前没有匹配的未下载游戏。'
        : '当前筛选没有匹配的游戏。';
  return <section className="grid two">
    <div className="card span2">
      <h3>游戏库</h3>
      <p className="muted">当前账号：{selectedAccount}。首次增量同步会自动按全量执行；后续增量同步仅检查新增候选应用。</p>
      <div className="row"><button disabled={syncing} onClick={() => sync('incremental')}>{syncing ? '同步中…' : '增量同步'}</button><button disabled={syncing} onClick={() => sync('full')}>全量同步</button></div>
      <div className="libraryToolbar">
        <div className="searchWithFilter"><input className="search" value={query} onChange={e => setQuery(e.target.value)} placeholder="搜索游戏名或 AppID" /><button className={`iconButton filterToggle ${filterOpen ? 'active' : ''}`} title="筛选和排序" aria-label="筛选和排序" onClick={() => setFilterOpen(v => !v)}><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h16M7 12h10M10 18h4" /></svg></button></div>
        {filterOpen && <div className="filterPanel">
          <label>下载状态<select value={downloadFilter} onChange={e => setDownloadFilter(e.target.value as 'all' | 'downloaded' | 'undownloaded')}><option value="all">全部游戏</option><option value="downloaded">已下载</option><option value="undownloaded">未下载</option></select></label>
          <label>排序方式<select value={sortBy} onChange={e => setSortBy(e.target.value as 'name' | 'app_id')}><option value="name">按名称排序</option><option value="app_id">按 AppID 排序</option></select></label>
          <label>排序方向<select value={sortDir} onChange={e => setSortDir(e.target.value as 'asc' | 'desc')}><option value="asc">升序</option><option value="desc">降序</option></select></label>
          <label>展示方式<select value={viewMode} onChange={e => setViewMode(e.target.value as 'grid' | 'list')}><option value="grid">大图显示</option><option value="list">列表显示</option></select></label>
        </div>}
      </div>
      {message && <p className="muted">{message}</p>}
      {(syncing || progress.total > 0) && <>
        <div className="bar" title="已检查候选应用 / 候选应用总数"><i style={{ width: `${progress.percent || 0}%` }} /></div>
        <p className="muted">{modeText}进度：已检查候选应用 {progress.scanned}/{progress.total || '?'} · 已显示 {games.length} 个游戏</p>
      </>}
    </div>
    {games.length === 0 ? <div className="card span2"><p className="muted">{message || '尚未同步游戏库。点击“增量同步”会在首次自动全量读取当前账号拥有的游戏。'}</p></div> : visibleGames.length === 0 ? <div className="card span2"><p className="muted">{emptyLibraryText}</p></div> : <div className={`libraryResults span2 ${viewMode === 'list' ? 'list' : 'grid'}`}>{visibleGames.map(game => {
      const job = jobByAppId.get(game.app_id);
      const downloading = !!job && ['queued', 'starting', 'running', 'waiting_input', 'paused'].includes(job.state);
      const downloaded = game.is_downloaded || job?.downloaded || job?.state === 'done';
      return <div className={`card game ${downloaded ? 'downloaded' : ''}`} key={game.app_id}>
        {game.header_image && <img src={game.header_image} />}
        <div className="gameText"><div className="gameTitle"><h3>{game.name}</h3>{downloaded && <span className="pill done">已下载</span>}</div><p className="muted">AppID {game.app_id}{game.size_bytes ? ` · 大小 ${formatBytes(game.size_bytes)}` : ''}</p></div>
        {job && <div className="libraryJobStatus"><div className="bar"><i style={{ width: `${job.percent || 0}%` }} /></div><p className="muted">{stateText[job.state] || job.state} · {Math.round(job.percent || 0)}%{job.progress_text ? ` · ${job.progress_text}` : ''}</p></div>}
        <button className="primary block" disabled={downloading} onClick={() => openDownload({ kind: 'app', id: game.app_id, name: game.name, install_dir: game.install_dir || game.installdir, installdir: game.installdir || game.install_dir, size_bytes: game.size_bytes, size_text: game.size_text })}>{downloading ? (job?.state === 'paused' ? '已暂停' : '下载中…') : downloaded ? '重新下载' : '选择并下载'}</button>
      </div>;
    })}</div>}
  </section>;
}

function AccountsPage({ accounts, accountDetails, loginState, selectedAccount, setSelectedAccount, refresh, setToast }: { accounts: string[]; accountDetails: AccountDetail[]; loginState: LoginState; selectedAccount: string; setSelectedAccount: (s: string) => void; refresh: () => Promise<void>; setToast: (s: string) => void }) {
  const [draft, setDraft] = useState(selectedAccount);
  const [password, setPassword] = useState('');
  const [rememberPassword, setRememberPassword] = useState(false);
  const [answer, setAnswer] = useState('');
  const detailMap = useMemo(() => new Map(accountDetails.map(a => [a.username.toLowerCase(), a])), [accountDetails]);
  const visibleAccounts = useMemo(() => {
    const names = new Set<string>();
    for (const account of accountDetails) names.add(account.username);
    for (const account of accounts) names.add(account);
    return Array.from(names).sort((a, b) => a.localeCompare(b));
  }, [accounts, accountDetails]);
  useEffect(() => {
    if (loginState.state !== 'waiting_input') setAnswer('');
  }, [loginState.state, loginState.prompt]);

  async function logout(username: string) {
    try {
      await api('/api/accounts/logout', { username });
      if (selectedAccount === username) setSelectedAccount('');
      await refresh();
    } catch (e: any) { setToast(e.message); }
  }
  async function login() {
    const username = draft.trim();
    if (!username) { setToast('请输入 Steam 用户名'); return; }
    const detail = detailMap.get(username.toLowerCase());
    if (!accounts.includes(username) && !detail?.has_saved_password && !password) { setToast('首次登录该账号需要输入密码'); return; }
    try {
      await api('/api/accounts/login', { username, password, remember_password: rememberPassword });
      setToast('登录已启动，请根据提示完成 Guard/2FA');
      setPassword('');
      await refresh();
    } catch (e: any) { setToast(e.message); }
  }
  async function relogin(username: string) {
    try {
      await api('/api/accounts/relogin', { username });
      setToast('重新登录已启动，请根据提示完成可能需要的 Guard/2FA');
      await refresh();
    } catch (e: any) { setToast(e.message); }
  }
  async function submitGuard() {
    try {
      await api('/api/accounts/login/input', { answer });
      setAnswer('');
      await refresh();
    } catch (e: any) { setToast(e.message); }
  }
  return <section className="grid two">
    <div className="card">
      <h3>新增并登录账号</h3>
      <p className="muted">登录成功后会保存 refresh token。勾选记住密码后，token 失效时可一键或自动重新登录；密码仅本地加密保存。</p>
      <label>Steam 用户名<input value={draft} onChange={e => setDraft(e.target.value)} placeholder="输入 Steam 用户名" /></label>
      <label>密码<input value={password} type="password" onChange={e => setPassword(e.target.value)} placeholder="用于本次登录；勾选后加密保存" /></label>
      <label className="check"><input type="checkbox" checked={rememberPassword} onChange={e => setRememberPassword(e.target.checked)} />记住密码，用于 token 失效后重新登录</label>
      <button className="primary block" disabled={loginState.state === 'running' || loginState.state === 'waiting_input'} onClick={login}>登录并保存授权</button>
      {(loginState.state === 'running' || loginState.state === 'waiting_input') && <p className="pill running">登录中：{loginState.username || draft}</p>}
      {loginState.state === 'waiting_input' && <div className="prompt"><p>{loginState.prompt || '请输入 Steam Guard / 2FA 验证码'}</p><div className="row"><input value={answer} type={loginState.prompt_secret ? 'password' : 'text'} onChange={e => setAnswer(e.target.value)} placeholder="Steam Guard / 2FA" /><button onClick={submitGuard}>提交验证</button></div></div>}
      {loginState.state === 'error' && <p className="muted">登录失败：{loginState.error}</p>}
      {loginState.log && <p className="muted">登录日志已移到“设置 - 日志”。</p>}
    </div>
    <div className="card">
      <h3>已保存账号</h3>
      {selectedAccount && accounts.includes(selectedAccount) && <p className="pill running">当前账号：{selectedAccount}</p>}
      {visibleAccounts.length === 0 ? <p className="muted">暂无已保存账号。请先在左侧完成登录和 Guard/2FA。</p> : visibleAccounts.map(a => {
        const detail = detailMap.get(a.toLowerCase());
        const loggedIn = accounts.includes(a) || !!detail?.logged_in;
        return <div className="account" key={a}>
          <div className="accountMain">
            <button className={selectedAccount === a ? 'activeAccount' : ''} disabled={!loggedIn} onClick={() => loggedIn && setSelectedAccount(a)}>{a}</button>
            <span className="muted">{loggedIn ? '已登录' : '需重登'} · {detail?.has_saved_password ? '已记住密码' : '未保存密码'}</span>
          </div>
          <div className="accountActions">
            {detail?.has_saved_password && <button onClick={() => relogin(a)}>重新登录</button>}
            <button className="danger" onClick={() => logout(a)}>退出登录</button>
          </div>
        </div>;
      })}
    </div>
  </section>;
}

function SettingsPage({ settings, config, setSettings, setToast, loginState, activeJob, jobs, setActiveJob, refresh }: { settings: Settings | null; config: Config | null; setSettings: (s: Settings) => void; setToast: (s: string) => void; loginState: LoginState; activeJob: Job | null; jobs: Job[]; setActiveJob: (job: Job | null) => void; refresh: () => Promise<void> }) {
  const [draft, setDraft] = useState<Settings | null>(settings);
  const [dirty, setDirty] = useState(false);
  useEffect(() => { if (!dirty) setDraft(settings); }, [settings?.default_download_dir, settings?.default_platform_os, settings?.max_downloads, settings?.auto_resume, dirty]);
  if (!draft) return <div className="card">加载设置中…</div>;
  function update(next: Settings) { setDraft(next); setDirty(true); }
  async function save() {
    try {
      const saved = await api<Settings>('/api/settings', draft);
      setSettings(saved);
      setDraft(saved);
      setDirty(false);
      setToast('设置已保存');
    } catch (e: any) { setToast(e.message); }
  }
  async function pickDirectory() {
    if (!config?.can_pick_directory) {
      const picked = window.prompt('当前浏览器无法读取本机目录路径，请手动输入保存目录：', draft.default_download_dir || '/storage/emulated/0/Download/steamdl');
      if (picked?.trim()) update({ ...draft, default_download_dir: picked.trim() });
      return;
    }
    try {
      const res = await api<{ path?: string; download_dir?: string }>('/api/pick-directory', {});
      const picked = res.path || res.download_dir || '';
      if (picked) update({ ...draft, default_download_dir: picked });
    } catch (e: any) { setToast(e.message); }
  }
  async function loadJobLog(jobId: string) {
    const job = await api<Job>(`/api/jobs/${jobId}`);
    setActiveJob(job);
  }
  const logText = activeJob?.log || loginState.log || '';
  return <section className="grid two">
    <div className="card form">
      <h3>设置</h3>
      <label>默认下载目录
        <div className="row inlineRow"><input value={draft.default_download_dir} onChange={e => update({ ...draft, default_download_dir: e.target.value })} placeholder="/storage/emulated/0/Download/steamdl" /><button type="button" onClick={pickDirectory}>{config?.can_pick_directory ? '选择' : '填写'}</button></div>
      </label>
      <label>默认平台<select value={draft.default_platform_os} onChange={e => update({ ...draft, default_platform_os: e.target.value })}><option value="windows">Windows</option><option value="linux">Linux</option><option value="any">全部平台</option></select></label>
      <label>最大下载线程<input type="number" min={1} value={draft.max_downloads} onChange={e => update({ ...draft, max_downloads: Number(e.target.value) })} /></label>
      <label className="check"><input type="checkbox" checked={draft.auto_resume} onChange={e => update({ ...draft, auto_resume: e.target.checked })} />服务重启后自动恢复未完成任务</label>
      <button onClick={save}>保存设置</button>
    </div>
    <div className="card detail">
      <div className="detailHead"><h3>日志</h3><button className="ghost" onClick={() => refresh().catch(e => setToast(e.message))}>刷新</button></div>
      {jobs.length > 0 && <label>选择任务日志<select value={activeJob?.job_id || ''} onChange={e => e.target.value && loadJobLog(e.target.value).catch(err => setToast(err.message))}><option value="">登录日志 / 当前任务</option>{jobs.map(job => <option key={job.job_id} value={job.job_id}>{displayJobTitle(job)} · {stateText[job.state] || job.state}</option>)}</select></label>}
      {activeJob && <p className="muted">当前任务：{displayJobTitle(activeJob)} · {stateText[activeJob.state] || activeJob.state}</p>}
      {!activeJob && loginState.state !== 'idle' && <p className="muted">当前登录流程：{stateText[loginState.state] || loginState.state}</p>}
      <pre className="expanded">{logText || '暂无日志'}</pre>
    </div>
  </section>;
}

function title(tab: Tab) {
  return ({ accounts: '账号', download: '下载', library: '游戏库', jobs: '任务', settings: '设置' } as Record<Tab, string>)[tab];
}
