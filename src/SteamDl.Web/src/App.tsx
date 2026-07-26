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

type LibraryGame = { app_id: string; name: string; header_image?: string };
type AccountDetail = { username: string; logged_in: boolean; remember_password: boolean; has_saved_password: boolean; last_used_at?: string };
type LoginState = { username?: string; state: string; prompt?: string; prompt_secret?: boolean; error?: string; log?: string; remember_password?: boolean };
type DownloadSeed = { kind: string; id: string; name?: string } | null;
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
    const [jobsRes, accountsRes, settingsRes] = await Promise.all([
      api<{ jobs: Job[] }>('/api/jobs'),
      api<{ accounts: string[]; account_details?: AccountDetail[]; login?: LoginState }>('/api/accounts').catch(() => ({ accounts: [], account_details: [], login: { state: 'idle' } })),
      api<Settings>('/api/settings'),
    ]);
    setJobs(jobsRes.jobs);
    setAccounts(accountsRes.accounts);
    setAccountDetails(accountsRes.account_details || []);
    setLoginState(accountsRes.login || { state: 'idle' });
    if (accountsRes.login?.state === 'done' && accountsRes.login.username && accountsRes.accounts.includes(accountsRes.login.username)) {
      setSelectedAccount(accountsRes.login.username);
    }
    setSettings(settingsRes);
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
      <header className="topbar">
        <div>
          <h2>{title(tab)}</h2>
          <p>{locked ? '登录后可解析链接、浏览游戏库并创建下载任务' : '支持任务持久化、后台恢复与 APK/桌面共用界面'}</p>
          <small className="swipeHint">左右滑动可切换页面</small>
        </div>
        <div className="statusCluster">
          <button className={`iconButton iconStatus ${serviceOnline ? 'online' : 'offline'}`} onClick={() => setToast(serviceOnline ? '服务已连接' : '服务连接中断，正在等待恢复')} title={serviceOnline ? '服务已连接' : '连接中断'} aria-label={serviceOnline ? '服务已连接' : '连接中断'}>{serviceOnline ? '●' : '!'}</button>
          <button className={`iconButton iconStatus ${running ? 'busy' : 'idle'}`} onClick={() => setToast(running ? '当前有下载任务运行或等待输入' : '当前没有运行中的任务')} title={running ? '有任务运行' : '空闲'} aria-label={running ? '有任务运行' : '空闲'}>{running ? '↻' : '✓'}</button>
          <button className="iconButton" onClick={manualRefresh} title="刷新" aria-label="刷新">⟳</button>
        </div>
      </header>

      {toast && <div className="toast" onClick={() => setToast('')}>{toast}</div>}
      {tab === 'accounts' && <AccountsPage accounts={accounts} accountDetails={accountDetails} loginState={loginState} selectedAccount={selectedAccount} setSelectedAccount={setSelectedAccount} refresh={refresh} setToast={setToast} />}
      {tab === 'download' && (locked ? <LoginGate setTab={openTab} /> : <DownloadPage settings={settings} selectedAccount={selectedAccount} seed={downloadSeed} clearSeed={() => setDownloadSeed(null)} setToast={setToast} refresh={refresh} />)}
      {tab === 'library' && (locked ? <LoginGate setTab={openTab} /> : <LibraryPage selectedAccount={selectedAccount} setToast={setToast} openDownload={openDownload} />)}
      {tab === 'jobs' && <JobsPage jobs={jobs} activeJob={activeJob} setActiveJob={setActiveJob} refresh={refresh} setToast={setToast} />}
      {tab === 'settings' && <SettingsPage settings={settings} setSettings={setSettings} setToast={setToast} />}
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

function DownloadPage({ settings, selectedAccount, seed, clearSeed, setToast, refresh }: { settings: Settings | null; selectedAccount: string; seed: DownloadSeed; clearSeed: () => void; setToast: (s: string) => void; refresh: () => Promise<void> }) {
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
    setAppInfo(seed.name ? { name: seed.name } : null);
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

  async function start() {
    if (!parsed) return;
    try {
      const res = await api<{ job: Job }>('/api/jobs', { kind: parsed.kind, id: parsed.id, username: selectedAccount, anonymous: false, os, depot, output_dir: outputDir });
      setToast(`任务已创建: ${res.job.job_id.slice(0, 8)}`);
      await refresh();
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
      <label>保存目录<input value={outputDir} onChange={e => setOutputDir(e.target.value)} /></label>
      <button className="primary block" disabled={!parsed} onClick={start}>使用 {selectedAccount} 开始下载</button>
    </div>
  </section>;
}

function JobsPage({ jobs, activeJob, setActiveJob, refresh, setToast }: { jobs: Job[]; activeJob: Job | null; setActiveJob: (job: Job | null) => void; refresh: () => Promise<void>; setToast: (s: string) => void }) {
  const [logExpanded, setLogExpanded] = useState(false);
  useEffect(() => setLogExpanded(false), [activeJob?.job_id]);
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
        <div className="logHead"><strong>任务日志</strong><button className="ghost" onClick={() => setLogExpanded(x => !x)}>{logExpanded ? '收起' : '展开'}</button></div>
        <pre className={logExpanded ? 'expanded' : 'collapsed'}>{activeJob.log || '暂无日志'}</pre>
        <div className="actions"><button className="danger" onClick={() => action(activeJob, 'cancel')}>取消</button><button onClick={() => action(activeJob, 'retry')}>重试/继续</button></div>
      </>}
    </div>
  </section>;
}

function LibraryPage({ selectedAccount, setToast, openDownload }: { selectedAccount: string; setToast: (s: string) => void; openDownload: (seed: DownloadSeed) => void }) {
  const [games, setGames] = useState<LibraryGame[]>([]);
  const [message, setMessage] = useState('');
  const [manualId, setManualId] = useState('');
  async function sync() {
    try {
      const res = await api<any>(`/api/library?username=${encodeURIComponent(selectedAccount)}`);
      setGames((res.items || []).map((x: any) => ({ app_id: String(x.app_id || x.id), name: x.name || `App ${x.app_id || x.id}`, header_image: x.header_image })));
      setMessage(res.message || '库同步完成');
    } catch (e: any) { setToast(e.message); }
  }
  return <section className="grid two">
    <div className="card span2">
      <h3>游戏库</h3>
      <p className="muted">当前账号：{selectedAccount}。可以从库中选择游戏下载，也可以输入 AppID 快速跳转到下载页。</p>
      <div className="row"><button onClick={sync}>同步库</button><input value={manualId} onChange={e => setManualId(e.target.value)} placeholder="输入 AppID，例如 730" /><button disabled={!manualId.trim()} onClick={() => openDownload({ kind: 'app', id: manualId.trim() })}>下载此 AppID</button></div>
      {message && <p className="muted">{message}</p>}
    </div>
    {games.length === 0 ? <div className="card span2"><p className="muted">游戏库详情接口仍在预留阶段。当前可先输入 AppID，或到下载页解析 Steam 商店链接。</p></div> : games.map(game => <div className="card game" key={game.app_id}>
      {game.header_image && <img src={game.header_image} />}
      <h3>{game.name}</h3><p className="muted">AppID {game.app_id}</p>
      <button className="primary block" onClick={() => openDownload({ kind: 'app', id: game.app_id, name: game.name })}>选择并下载</button>
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
      {loginState.log && <pre>{loginState.log}</pre>}
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

function SettingsPage({ settings, setSettings, setToast }: { settings: Settings | null; setSettings: (s: Settings) => void; setToast: (s: string) => void }) {
  const [draft, setDraft] = useState<Settings | null>(settings);
  useEffect(() => setDraft(settings), [settings]);
  if (!draft) return <div className="card">加载设置中…</div>;
  async function save() { try { const saved = await api<Settings>('/api/settings', draft); setSettings(saved); setToast('设置已保存'); } catch (e: any) { setToast(e.message); } }
  return <div className="card form"><h3>设置</h3><label>默认下载目录<input value={draft.default_download_dir} onChange={e => setDraft({ ...draft, default_download_dir: e.target.value })} /></label><label>默认平台<select value={draft.default_platform_os} onChange={e => setDraft({ ...draft, default_platform_os: e.target.value })}><option value="windows">Windows</option><option value="linux">Linux</option><option value="any">全部平台</option></select></label><label>最大下载线程<input type="number" min={1} value={draft.max_downloads} onChange={e => setDraft({ ...draft, max_downloads: Number(e.target.value) })} /></label><label className="check"><input type="checkbox" checked={draft.auto_resume} onChange={e => setDraft({ ...draft, auto_resume: e.target.checked })} />服务重启后自动恢复未完成任务</label><button onClick={save}>保存设置</button></div>;
}

function title(tab: Tab) {
  return ({ accounts: '账号', download: '下载', library: '游戏库', jobs: '任务', settings: '设置' } as Record<Tab, string>)[tab];
}
