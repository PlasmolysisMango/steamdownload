import { useEffect, useMemo, useRef, useState, type TouchEvent } from 'react';

type Job = {
  job_id: string;
  kind: string;
  id: string;
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
type LibraryGame = { app_id: string; name: string; header_image?: string; install_dir?: string; installdir?: string };
type AccountDetail = { username: string; logged_in: boolean; remember_password: boolean; has_saved_password: boolean; last_used_at?: string };
type LoginState = { username?: string; state: string; prompt?: string; prompt_secret?: boolean; error?: string; log?: string; remember_password?: boolean };
type DownloadSeed = { kind: string; id: string; name?: string; install_dir?: string; installdir?: string } | null;
type Tab = 'accounts' | 'download' | 'library' | 'jobs' | 'settings';

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
  idle: '空闲', queued: '排队中', starting: '启动中', running: '下载中',
  waiting_input: '等待输入', interrupted: '已中断', done: '完成', error: '出错', cancelled: '已取消',
};

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
      {tab === 'accounts' && <AccountsPage accounts={accounts} accountDetails={accountDetails} loginState={loginState} selectedAccount={selectedAccount} setSelectedAccount={setSelectedAccount} refresh={refresh} setToast={setToast} />}
      {tab === 'download' && (locked ? <LoginGate setTab={openTab} /> : <DownloadPage settings={settings} config={config} selectedAccount={selectedAccount} seed={downloadSeed} clearSeed={() => setDownloadSeed(null)} setToast={setToast} refresh={refresh} setActiveJob={setActiveJob} setTab={openTab} />)}
      {tab === 'library' && (locked ? <LoginGate setTab={openTab} /> : <LibraryPage selectedAccount={selectedAccount} setToast={setToast} openDownload={openDownload} />)}
      {tab === 'jobs' && <JobsPage jobs={jobs} activeJob={activeJob} setActiveJob={setActiveJob} refresh={refresh} setToast={setToast} />}
      {tab === 'settings' && <SettingsPage settings={settings} config={config} setSettings={setSettings} setToast={setToast} loginState={loginState} activeJob={activeJob} jobs={jobs} setActiveJob={setActiveJob} refresh={refresh} />}
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
    setParsed({ kind: seed.kind, id: seed.id });
    setAppInfo(seed.name ? { name: seed.name, installdir: seed.installdir || seed.install_dir, install_dir: seed.install_dir || seed.installdir } : null);
    if (seed.kind === 'app') api(`/api/appinfo/${seed.id}`).then(setAppInfo).catch(() => {});
    clearSeed();
  }, [seed?.id]);

  async function parse() {
    try {
      const next = await api<{ kind: string; id: string }>('/api/parse', { url });
      setParsed(next);
      setAppInfo(null);
      if (next.kind === 'app') api(`/api/appinfo/${next.id}`).then(setAppInfo).catch(() => {});
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
      const res = await api<{ job: Job }>('/api/jobs', { kind: parsed.kind, id: parsed.id, username: selectedAccount, anonymous: false, os, depot, output_dir: outputDir, install_dir: appInfo?.installdir || appInfo?.install_dir, name: appInfo?.name });
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
      <p>{parsed ? `类型 ${parsed.kind} · ID ${parsed.id}` : '从游戏库选择，或解析链接后创建下载任务'}</p>
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

function JobsPage({ jobs, activeJob, setActiveJob, refresh, setToast }: { jobs: Job[]; activeJob: Job | null; setActiveJob: (job: Job | null) => void; refresh: () => Promise<void>; setToast: (s: string) => void }) {
  async function load(job: Job) { setActiveJob(await api<Job>(`/api/jobs/${job.job_id}`)); }
  async function action(job: Job, name: 'cancel' | 'retry') {
    if (name === 'cancel' && !window.confirm('确定要取消当前下载任务吗？')) return;
    try { await api(`/api/jobs/${job.job_id}/${name}`, {}); await refresh(); }
    catch (e: any) { setToast(e.message); }
  }
  async function sendInput() {
    if (!activeJob) return;
    const input = document.querySelector<HTMLInputElement>('#jobInput')?.value || '';
    await api(`/api/jobs/${activeJob.job_id}/input`, { answer: input });
    await load(activeJob);
  }
  return <section className="grid two">
    <div className="card">
      <h3>任务历史</h3>
      <div className="jobList">{jobs.map(job => <button key={job.job_id} className="jobItem" onClick={() => load(job)}>
        <span>{job.kind} {job.id}</span><small>{stateText[job.state] || job.state} · {Math.round(job.percent || 0)}%</small>
      </button>)}</div>
    </div>
    <div className="card detail">
      {!activeJob ? <p className="muted">选择一个任务查看详情</p> : <>
        <div className="detailHead"><h3>{activeJob.kind} {activeJob.id}</h3><span className={`pill ${activeJob.state}`}>{stateText[activeJob.state] || activeJob.state}</span></div>
        <div className="bar"><i style={{ width: `${activeJob.percent || 0}%` }} /></div>
        <p className="muted">{activeJob.progress_text || activeJob.error || activeJob.output_dir}</p>
        {activeJob.state === 'waiting_input' && <div className="prompt"><p>{activeJob.prompt}</p><div className="row"><input id="jobInput" type={activeJob.prompt_secret ? 'password' : 'text'} /><button onClick={sendInput}>提交</button></div></div>}
        <p className="muted">详细日志请到“设置 - 日志”查看。</p>
        <div className="actions"><button className="danger" onClick={() => action(activeJob, 'cancel')}>取消</button><button onClick={() => action(activeJob, 'retry')}>重试/继续</button></div>
      </>}
    </div>
  </section>;
}

function LibraryPage({ selectedAccount, setToast, openDownload }: { selectedAccount: string; setToast: (s: string) => void; openDownload: (seed: DownloadSeed) => void }) {
  const [games, setGames] = useState<LibraryGame[]>([]);
  const [message, setMessage] = useState('');
  const [manualId, setManualId] = useState('');
  const [query, setQuery] = useState('');
  const [syncing, setSyncing] = useState(false);
  const visibleGames = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return games;
    return games.filter(g => g.app_id.includes(q) || g.name.toLowerCase().includes(q));
  }, [games, query]);
  async function sync() {
    setSyncing(true);
    try {
      const res = await api<any>(`/api/library?username=${encodeURIComponent(selectedAccount)}`);
      const items = Array.isArray(res.items) ? res.items : [];
      const nextGames = items.map((x: any) => ({ app_id: String(x.app_id || x.id), name: x.name || `App ${x.app_id || x.id}`, header_image: x.header_image, install_dir: x.install_dir || x.installdir, installdir: x.installdir || x.install_dir }));
      setGames(nextGames);
      setQuery('');
      setMessage(res.message || `库同步完成，共 ${nextGames.length} 个应用`);
    } catch (e: any) { setToast(e.message); }
    finally { setSyncing(false); }
  }
  return <section className="grid two">
    <div className="card span2">
      <h3>游戏库</h3>
      <p className="muted">当前账号：{selectedAccount}。同步后可直接选择游戏下载，也可以输入 AppID 快速跳转。</p>
      <div className="row"><button disabled={syncing} onClick={sync}>{syncing ? '同步中…' : '同步库'}</button><input value={manualId} onChange={e => setManualId(e.target.value)} placeholder="输入 AppID，例如 730" /><button disabled={!manualId.trim()} onClick={() => openDownload({ kind: 'app', id: manualId.trim() })}>下载此 AppID</button></div>
      {games.length > 0 && <input className="search" value={query} onChange={e => setQuery(e.target.value)} placeholder="搜索游戏名或 AppID" />}
      {message && <p className="muted">{message}</p>}
    </div>
    {games.length === 0 ? <div className="card span2"><p className="muted">{message || '尚未同步游戏库。点击“同步库”读取当前账号拥有的游戏，或直接输入 AppID 下载。'}</p></div> : visibleGames.length === 0 ? <div className="card span2"><p className="muted">当前搜索没有匹配的游戏。清空搜索框可查看已同步的全部游戏。</p></div> : visibleGames.map(game => <div className="card game" key={game.app_id}>
      {game.header_image && <img src={game.header_image} />}
      <h3>{game.name}</h3><p className="muted">AppID {game.app_id}</p>
      <button className="primary block" onClick={() => openDownload({ kind: 'app', id: game.app_id, name: game.name, install_dir: game.install_dir || game.installdir })}>选择并下载</button>
    </div>)}
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
      {jobs.length > 0 && <label>选择任务日志<select value={activeJob?.job_id || ''} onChange={e => e.target.value && loadJobLog(e.target.value).catch(err => setToast(err.message))}><option value="">登录日志 / 当前任务</option>{jobs.map(job => <option key={job.job_id} value={job.job_id}>{job.kind} {job.id} · {stateText[job.state] || job.state}</option>)}</select></label>}
      {activeJob && <p className="muted">当前任务：{activeJob.kind} {activeJob.id} · {stateText[activeJob.state] || activeJob.state}</p>}
      {!activeJob && loginState.state !== 'idle' && <p className="muted">当前登录流程：{stateText[loginState.state] || loginState.state}</p>}
      <pre className="expanded">{logText || '暂无日志'}</pre>
    </div>
  </section>;
}

function title(tab: Tab) {
  return ({ accounts: '账号', download: '下载', library: '游戏库', jobs: '任务', settings: '设置' } as Record<Tab, string>)[tab];
}
