import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import {
  Activity, AppWindow, ArrowDownToLine, AudioLines, Bell, Boxes, ChevronRight,
  CircleHelp, Clock3, Cpu, Database, Gauge, HardDrive, Home, Library, Minus,
  Monitor, MoreHorizontal, Plus, Search, Settings, ShieldCheck, SlidersHorizontal, Sparkles,
  Square, Star, Trash2, X, Zap,
} from "lucide-react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { open } from "@tauri-apps/plugin-dialog";
import { invoke, isTauri } from "@tauri-apps/api/core";
import { useCallback, useEffect, useMemo, useState } from "react";

type PageId = "home" | "library" | "scenes" | "monitor" | "blackbox" | "vault" | "automation" | "activity" | "settings" | "license";
type Metrics = { cpuPercent: number; memoryUsed: number; memoryTotal: number; memoryPercent: number; uptimeSeconds: number; diskUsed: number; diskTotal: number; gpuStatus: string; sampledAt: string };
type LibraryApp = { id: number; name: string; path: string; arguments: string; category: string; favorite: boolean; totalMinutes: number; lastLaunched: string | null };
type Scene = { id: number; name: string; description: string; active: boolean };
type ActivityRow = { id: number; kind: string; detail: string; occurredAt: string };
type LicenseStatus = { deviceId: string; state: "notActivated" | "active" | "expired" | "invalid" | "deviceMismatch"; licenseType: "seven_day" | "thirty_day" | "permanent" | null; expiresAt: string | null };

const navigation: { id: PageId; label: string; icon: typeof Home }[] = [
  { id: "home", label: "HOME", icon: Home }, { id: "library", label: "LIBRARY", icon: Library },
  { id: "scenes", label: "SCENES", icon: Boxes }, { id: "monitor", label: "MONITOR", icon: Gauge },
  { id: "blackbox", label: "BLACK BOX", icon: Database }, { id: "vault", label: "CONFIG VAULT", icon: ShieldCheck },
  { id: "automation", label: "AUTOMATION", icon: SlidersHorizontal }, { id: "activity", label: "ACTIVITY", icon: Activity },
  { id: "license", label: "LICENSE", icon: ShieldCheck },
];
const proPages: PageId[] = ["blackbox", "vault", "automation"];
const licenseTerms: Record<NonNullable<LicenseStatus["licenseType"]>, string> = {
  seven_day: "7-DAY LICENSE",
  thirty_day: "30-DAY LICENSE",
  permanent: "PERMANENT LICENSE",
};

const invokeError = (reason: unknown) => reason instanceof Error ? reason.message : String(reason);
const bytes = (value: number) => value >= 1e9 ? `${(value / 1e9).toFixed(1)} GB` : `${(value / 1e6).toFixed(0)} MB`;

export default function App() {
  const [page, setPage] = useState<PageId>("home");
  const [metrics, setMetrics] = useState<Metrics | null>(null);
  const [history, setHistory] = useState<Metrics[]>([]);
  const [apps, setApps] = useState<LibraryApp[]>([]);
  const [scenes, setScenes] = useState<Scene[]>([]);
  const [activity, setActivity] = useState<ActivityRow[]>([]);
  const [license, setLicense] = useState<LicenseStatus | null>(null);
  const [query, setQuery] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(() => new Date());
  const reduceMotion = useReducedMotion();
  const desktopRuntime = isTauri();

  const refresh = useCallback(async () => {
    if (!isTauri()) {
      return;
    }
    try {
      const [nextMetrics, nextApps, nextScenes, nextActivity] = await Promise.all([
        invoke<Metrics>("get_metrics"), invoke<LibraryApp[]>("list_library"),
        invoke<Scene[]>("list_scenes"), invoke<ActivityRow[]>("list_activity"),
      ]);
      const currentLicense = await invoke<LicenseStatus>("get_license_status");
      setMetrics(nextMetrics);
      setHistory(samples => [...samples, nextMetrics].slice(-60));
      setApps(nextApps); setScenes(nextScenes); setActivity(nextActivity); setLicense(currentLicense);
    } catch (reason) { setError(invokeError(reason)); }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), 2500);
    return () => window.clearInterval(timer);
  }, [refresh]);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const navigate = (nextPage: PageId) => {
    if (proPages.includes(nextPage) && license?.state !== "active") {
      setPage("license");
      setError("Activate a valid PULSE license to unlock this PRO feature.");
      return;
    }
    setPage(nextPage);
  };

  const activateLicense = async (licenseCode: string): Promise<boolean> => {
    try {
      const updated = await invoke<LicenseStatus>("activate_license", { licenseCode });
      setLicense(updated);
      setError("");
      return true;
    } catch (reason) { setError(invokeError(reason)); }
    return false;
  };

  const importLicenseFile = async () => {
    setBusy(true);
    try {
      const path = await open({ multiple: false, filters: [{ name: "PULSE license", extensions: ["pulse-license"] }] });
      if (typeof path !== "string") return;
      const updated = await invoke<LicenseStatus>("import_license_file", { path });
      setLicense(updated);
      setError("");
    } catch (reason) { setError(invokeError(reason)); }
    finally { setBusy(false); }
  };

  const removeLicense = async () => {
    if (!window.confirm("Remove the license saved on this device?")) return;
    try {
      setLicense(await invoke<LicenseStatus>("remove_license"));
      setError("");
    } catch (reason) { setError(invokeError(reason)); }
  };

  const openLicenseWebsite = async () => {
    if (!license?.deviceId) return;
    try {
      await invoke("open_license_website", { deviceId: license.deviceId });
      setError("");
    } catch (reason) { setError(invokeError(reason)); }
  };

  const addProgram = async () => {
    setBusy(true);
    try {
      const selection = await open({ multiple: false, filters: [{ name: "Windows application", extensions: ["exe"] }] });
      if (typeof selection !== "string") return;
      const suggested = selection.split(/[\\/]/).pop()?.replace(/\.exe$/i, "") ?? "";
      const name = window.prompt("Name this Library item", suggested)?.trim();
      if (!name) return;
      await invoke("add_library_app", { input: { name, path: selection, arguments: "", category: "Application" } });
      await refresh();
    } catch (reason) { setError(invokeError(reason)); }
    finally { setBusy(false); }
  };

  const runApp = async (id: number) => {
    try { await invoke("launch_library_app", { id }); await refresh(); }
    catch (reason) { setError(invokeError(reason)); }
  };

  const pageTitle = navigation.find(item => item.id === page)?.label ?? page.toUpperCase();
  const favoriteApps = apps.filter(item => item.favorite);
  const filteredApps = useMemo(() => apps.filter(item => `${item.name} ${item.category}`.toLowerCase().includes(query.toLowerCase())), [apps, query]);
  return <main className="window">
    <header className="titlebar">
      <div className="brand" data-tauri-drag-region><img src="/pulse-mark.png" alt="" /><span>PULSE</span><i /> <small>PRECISION PC MANAGEMENT</small></div>
      <div className="titlebar-right"><span className="local-pill"><span /> {desktopRuntime?"LOCAL SESSION":"LOCAL PREVIEW"}</span><button type="button" aria-label="Minimize" onClick={event => { event.preventDefault(); event.stopPropagation(); void getCurrentWindow().minimize().catch(reason => setError(invokeError(reason))); }}><Minus size={15}/></button><button type="button" aria-label="Maximize" onClick={event => { event.preventDefault(); event.stopPropagation(); void getCurrentWindow().toggleMaximize().catch(reason => setError(invokeError(reason))); }}><Square size={13}/></button><button type="button" aria-label="Close" onClick={event => { event.preventDefault(); event.stopPropagation(); void getCurrentWindow().close().catch(reason => setError(invokeError(reason))); }}><X size={15}/></button></div>
    </header>
    <div className="layout">
      <aside className="sidebar">
        <div className="sidebar-brand"><img src="/pulse-mark.png" alt="" /><div><b>PULSE</b><span>DESKTOP CONTROL</span></div></div>
        <div className="nav-caption">WORKSPACE</div>
        <nav>{navigation.map(item => <button key={item.id} className={`nav-item ${page === item.id ? "selected" : ""}`} onClick={() => navigate(item.id)}><item.icon size={16}/><span>{item.label}</span>{proPages.includes(item.id) && <small className="pro-badge">PRO</small>}{page === item.id && <motion.i layoutId="nav-indicator" transition={{ duration: reduceMotion ? 0 : .25 }}/>}</button>)}</nav>
        <div className="sidebar-bottom">
          <button className="license-state" onClick={() => navigate("license")}><span className={`status-dot ${license?.state === "active" ? "" : "inactive"}`}/><div><b>{license?.state === "active" ? "PRO ACTIVE" : "FREE PLAN"}</b><small>{license?.state === "active" && license.licenseType ? licenseTerms[license.licenseType] : "LICENSE REQUIRED FOR PRO"}</small></div><ChevronRight size={14}/></button>
          <button className={`nav-item ${page === "settings" ? "selected" : ""}`} onClick={() => navigate("settings")}><Settings size={16}/><span>SETTINGS</span></button>
        </div>
      </aside>
      <section className="workspace">
        <div className="page-top"><div><div className="eyebrow">PULSE / WORKSPACE / {pageTitle}</div><h1>{page === "home" ? "Good to have you back." : pageTitle}</h1></div><div className="top-actions"><span className="clock"><Clock3 size={14}/>{now.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span><button className="icon-button" aria-label="View recent activity" onClick={() => navigate("activity")}><Bell size={16}/></button><div className="avatar">P</div></div></div>
        {error && <div className="error-banner" role="alert"><CircleHelp size={16}/><span>{error}</span><button onClick={() => setError("")}>DISMISS</button></div>}
        <div className="page-viewport">
          <AnimatePresence mode="wait" initial={false}>
            <motion.div key={page} className="page-content"
              initial={{ opacity: 0, x: reduceMotion ? 0 : 14 }}
              animate={{ opacity: 1, x: 0 }}
              exit={{ opacity: 0, x: reduceMotion ? 0 : -10 }}
              transition={{ duration: reduceMotion ? 0 : .28, ease: [.22, 1, .36, 1] }}>
              {page === "home" && <HomePage metrics={metrics} apps={apps} favorites={favoriteApps} scenes={scenes} activity={activity} onLaunch={runApp} onNavigate={navigate}/>}
              {page === "library" && <LibraryPage apps={filteredApps} query={query} setQuery={setQuery} onAdd={() => void addProgram()} onLaunch={runApp} onRefresh={() => void refresh()} onError={setError} busy={busy}/>}
              {page === "scenes" && <ScenesPage scenes={scenes} onActivate={async id => { try { await invoke("activate_scene", { id }); await refresh(); } catch (reason) { setError(invokeError(reason)); } }} onCreate={async (name,description)=>{try {await invoke("create_scene",{name,description}); await refresh();} catch(reason) {setError(invokeError(reason));}}}/>}
              {page === "monitor" && <MonitorPage metrics={metrics} history={history}/>}
              {page === "activity" && <ActivityPage activity={activity}/>}
              {page === "blackbox" && <ComingPage icon={Database} title="BLACK BOX" message="Session recording is being built on the local metrics pipeline. No unsupported FPS or frame-time readings are shown."/>}
              {page === "vault" && <ComingPage icon={ShieldCheck} title="CONFIG VAULT" message="Backups will be limited to files you explicitly choose. No configuration is modified without confirmation."/>}
              {page === "automation" && <ComingPage icon={SlidersHorizontal} title="AUTOMATION" message="Automation is not active yet. PULSE will never run background actions without your consent."/>}
              {page === "settings" && <ComingPage icon={Settings} title="SETTINGS" message="System preferences are being migrated. Manage this device's offline license from the LICENSE page."/>}
              {page === "license" && <LicensePage license={license} desktopRuntime={desktopRuntime} onActivate={activateLicense} onImport={() => void importLicenseFile()} onOpenWebsite={() => void openLicenseWebsite()} onRemove={() => void removeLicense()}/>}
            </motion.div>
          </AnimatePresence>
        </div>
        <footer className="statusbar"><span><span className={`status-dot ${desktopRuntime ? "" : "inactive"}`}/> {desktopRuntime?(license?.state === "active" ? "PRO LICENSE VERIFIED LOCALLY" : "FREE PLAN · LICENSES VERIFIED LOCALLY"):"DESKTOP RUNTIME NOT ATTACHED"}</span><span>GPU SENSORS · {desktopRuntime?(metrics?.gpuStatus ?? "CHECKING"):"UNAVAILABLE IN PREVIEW"}</span><span>BUILD 1.0.0</span></footer>
      </section>
    </div>
  </main>;
}

function LicensePage({ license, desktopRuntime, onActivate, onImport, onOpenWebsite, onRemove }: { license: LicenseStatus | null; desktopRuntime: boolean; onActivate: (code:string)=>Promise<boolean>; onImport: ()=>void; onOpenWebsite: ()=>void; onRemove: ()=>void }) {
  const [licenseCode, setLicenseCode] = useState("");
  const [copied, setCopied] = useState(false);
  const isActive = license?.state === "active";
  const stateLabels: Record<LicenseStatus["state"], string> = {
    notActivated: "NOT ACTIVATED",
    active: "ACTIVE",
    expired: "EXPIRED",
    invalid: "INVALID",
    deviceMismatch: "DEVICE MISMATCH",
  };
  const stateLabel = license ? stateLabels[license.state] : "CHECKING";
  const [copyError, setCopyError] = useState("");

  const copyDeviceId = async () => {
    if (!license?.deviceId) return;
    try {
      await navigator.clipboard.writeText(license.deviceId);
      setCopied(true);
      setCopyError("");
      window.setTimeout(() => setCopied(false), 1800);
    } catch {
      setCopied(false);
      setCopyError("Could not copy the Device ID. Select the ID above and copy it manually.");
    }
  };

  return <div className="page-stack license-page">
    <section className="panel activation-panel">
      <div className="panel-heading"><div><span className="eyebrow">OFFLINE DEVICE LICENSE</span><h2>DEVICE ACTIVATION</h2></div><span className={`activation-state ${isActive ? "active" : ""}`}><i/>{desktopRuntime ? stateLabel : "DESKTOP APP REQUIRED"}</span></div>
      <p className="activation-copy">Licenses are verified on this PC. The signing key stays with the issuer and is never placed on the website.</p>
      <label className="activation-label" htmlFor="device-id">DEVICE ID</label>
      <div className="device-id-row"><code id="device-id">{license?.deviceId ?? "Available in the PULSE desktop app"}</code><button className="secondary-button" disabled={!desktopRuntime || !license?.deviceId} onClick={() => void copyDeviceId()}>{copied ? "COPIED" : "COPY ID"}</button></div>
      {copyError && <p className="license-notice" role="alert">{copyError}</p>}
      {license?.state === "active" && license.licenseType && <div className="license-facts"><span>LICENSE TYPE</span><strong>{licenseTerms[license.licenseType]}</strong><span>EXPIRES</span><strong>{license.expiresAt ? new Date(license.expiresAt).toLocaleString() : "NEVER"}</strong></div>}
      {license?.state === "expired" && <p className="license-notice">This license has expired. Request a new license and import the updated file.</p>}
      {license?.state === "invalid" && <p className="license-notice">The saved license is invalid or has an unreadable signature. Import a valid license for this device.</p>}
      {license?.state === "deviceMismatch" && <p className="license-notice">The saved license is signed for a different device. Request a license using this Device ID.</p>}
    </section>
    {!isActive && <section className="panel activation-panel">
      <div className="panel-heading"><div><span className="eyebrow">LICENSE REQUEST</span><h2>Get a device-bound license</h2></div><ShieldCheck size={18}/></div>
      <p className="activation-copy">Open the PULSE license page to submit this Device ID. After approval, download your .pulse-license file and import it below.</p>
      <button className="primary-button" disabled={!desktopRuntime || !license?.deviceId} onClick={onOpenWebsite}><ArrowDownToLine size={15}/> OPEN LICENSE WEBSITE</button>
      <div className="license-code-section"><label className="activation-label" htmlFor="license-code">SIGNED LICENSE CODE</label><textarea id="license-code" value={licenseCode} maxLength={1024} onChange={event => setLicenseCode(event.target.value)} placeholder="Paste the PULSE1 license code, or import the downloaded file" rows={3}/><div className="license-actions"><button className="secondary-button" disabled={!desktopRuntime} onClick={onImport}>IMPORT LICENSE FILE</button><button className="primary-button" disabled={!desktopRuntime || !licenseCode.trim()} onClick={async()=>{if(await onActivate(licenseCode))setLicenseCode("");}}>ACTIVATE LICENSE</button></div></div>
    </section>}
    {isActive && <section className="panel activation-panel"><div className="panel-heading"><div><span className="eyebrow">LOCAL VERIFICATION</span><h2>License active on this device</h2></div><ShieldCheck size={18}/></div><p className="activation-copy">The signed license has been checked against the PULSE public key and this device ID. Core verification works offline.</p><button className="secondary-button" onClick={onRemove}>REMOVE LOCAL LICENSE</button></section>}
    {!desktopRuntime && <p className="activation-footnote">Open this page in the installed PULSE app to create or activate a device-bound license.</p>}
  </div>;
}

function HomePage({ metrics, apps, favorites, scenes, activity, onLaunch, onNavigate }: { metrics: Metrics | null; apps: LibraryApp[]; favorites: LibraryApp[]; scenes: Scene[]; activity: ActivityRow[]; onLaunch: (id:number)=>void; onNavigate: (page:PageId)=>void }) {
  return <div className="page-stack">
    <section className="hero-card"><div className="hero-copy"><span className="eyebrow">YOUR PC, IN FOCUS</span><h2>Precision.<br/>Control. Clarity.</h2><p>A calm, local-first workspace for your PC.</p><button className="primary-button" onClick={() => onNavigate("library")}><Plus size={15}/> ADD TO LIBRARY</button></div><div className="hero-emblem"><img src="/pulse-mark.png" alt="PULSE symbol"/></div><span className="hero-index">01 / CONTROL CENTER</span></section>
    <section className="metric-grid">
      <MetricCard icon={Cpu} label="PROCESSOR" value={metrics ? `${metrics.cpuPercent.toFixed(0)}%` : "—"} detail="Current total load" percent={metrics?.cpuPercent ?? 0}/>
      <MetricCard icon={Database} label="MEMORY" value={metrics ? `${metrics.memoryPercent.toFixed(0)}%` : "—"} detail={metrics ? `${bytes(metrics.memoryUsed)} / ${bytes(metrics.memoryTotal)}` : "Waiting for sample"} percent={metrics?.memoryPercent ?? 0}/>
      <MetricCard icon={HardDrive} label="STORAGE" value={metrics && metrics.diskTotal ? `${(metrics.diskUsed / metrics.diskTotal * 100).toFixed(0)}%` : "—"} detail={metrics ? `${bytes(metrics.diskUsed)} / ${bytes(metrics.diskTotal)}` : "Waiting for sample"} percent={metrics && metrics.diskTotal ? metrics.diskUsed / metrics.diskTotal * 100 : 0}/>
      <MetricCard icon={Monitor} label="GRAPHICS" value={metrics?.gpuStatus ?? "—"} detail="Hardware sensor not available" percent={0}/>
    </section>
    <div className="home-columns">
      <section className="panel"><div className="panel-heading"><div><span className="eyebrow">QUICK ACCESS</span><h3>Favorites</h3></div><button className="text-button" onClick={() => onNavigate("library")}>OPEN LIBRARY <ChevronRight size={14}/></button></div>{favorites.length ? <div className="favorite-list">{favorites.slice(0,4).map(app => <button key={app.id} className="favorite-row" onClick={() => onLaunch(app.id)}><span className="app-monogram">{app.name.slice(0,1).toUpperCase()}</span><span><b>{app.name}</b><small>{app.category}</small></span><ChevronRight size={15}/></button>)}</div> : <EmptyState icon={Star} title="Your favorites will appear here" text="Mark a Library item with a star for quick access."/>}</section>
      <section className="panel"><div className="panel-heading"><div><span className="eyebrow">LOCAL PROFILES</span><h3>Scenes</h3></div><button className="text-button" onClick={() => onNavigate("scenes")}>VIEW SCENES <ChevronRight size={14}/></button></div><div className="scene-summary">{scenes.filter(scene => scene.active).map(scene => <div key={scene.id} className="active-scene"><Sparkles size={16}/><div><b>{scene.name}</b><small>{scene.description}</small></div></div>)}{!scenes.some(scene=>scene.active) && <EmptyState icon={Boxes} title="No active scene" text="Choose a local profile in Scenes."/>}</div></section>
    </div>
    <section className="panel"><div className="panel-heading"><div><span className="eyebrow">ON THIS DEVICE</span><h3>Recent activity</h3></div><button className="text-button" onClick={() => onNavigate("activity")}>VIEW ALL <ChevronRight size={14}/></button></div>{activity.length ? <ActivityList rows={activity.slice(0,4)}/> : <EmptyState icon={Activity} title="Nothing recorded yet" text="Actions performed in PULSE will be recorded locally."/>}</section>
    <section className="panel"><div className="panel-heading"><div><span className="eyebrow">LIBRARY</span><h3>Your programs</h3></div><button className="text-button" onClick={() => onNavigate("library")}>OPEN LIBRARY <ChevronRight size={14}/></button></div>{apps.length ? <div className="recent-apps">{apps.slice(0,4).map(app=><button key={app.id} className="recent-app" onClick={()=>onLaunch(app.id)}><AppWindow size={17}/><span>{app.name}</span><ChevronRight size={14}/></button>)}</div> : <EmptyState icon={Library} title="Your Library is empty" text="Add a program to keep it close at hand."/>}</section>
  </div>;
}

function MetricCard({icon:Icon,label,value,detail,percent}:{icon:typeof Cpu;label:string;value:string;detail:string;percent:number}) {
  return <article className="metric-card"><div className="metric-top"><Icon size={15}/><span>{label}</span><MoreHorizontal size={17}/></div><div className="metric-value">{value}</div><div className="metric-detail">{detail}</div><div className="meter"><i style={{width:`${Math.max(0,Math.min(percent,100))}%`}}/></div></article>;
}

function LibraryPage({apps,query,setQuery,onAdd,onLaunch,onRefresh,busy,onError}:{apps:LibraryApp[];query:string;setQuery:(value:string)=>void;onAdd:()=>void;onLaunch:(id:number)=>void;onRefresh:()=>void;busy:boolean;onError:(message:string)=>void}) {
  const toggleFavorite = async (id:number) => {
    try { await invoke("toggle_favorite",{id}); onRefresh(); }
    catch (reason) { onError(invokeError(reason)); }
  };
  const removeApp = async (app:LibraryApp) => {
    if(!window.confirm(`Remove ${app.name} from your Library? The program will not be deleted.`))return;
    try { await invoke("remove_library_app",{id:app.id}); onRefresh(); }
    catch (reason) { onError(invokeError(reason)); }
  };
  return <div className="page-stack"><div className="section-lead"><div><span className="eyebrow">YOUR COLLECTION</span><p>Keep the programs you use in one place. Items stay on this device.</p></div><button className="primary-button" disabled={busy} onClick={onAdd}><Plus size={15}/> ADD PROGRAM</button></div><div className="library-toolbar"><label className="search-field"><Search size={16}/><input value={query} onChange={event=>setQuery(event.target.value)} placeholder="Search your Library"/></label><button className="icon-button" onClick={onRefresh} aria-label="Refresh Library"><ArrowDownToLine size={16}/></button></div>{apps.length ? <div className="library-grid">{apps.map(app=><article className="library-card" key={app.id}><div className="library-card-top"><span className="app-monogram large">{app.name.slice(0,1).toUpperCase()}</span><button className={`favorite-button ${app.favorite?"on":""}`} aria-label={`Toggle ${app.name} favorite`} onClick={()=>void toggleFavorite(app.id)}><Star size={16}/></button></div><h3>{app.name}</h3><span className="category-label">{app.category} · {app.totalMinutes} MIN USED</span><p className="path-label" title={app.path}>{app.path}</p><button className="secondary-button" onClick={()=>onLaunch(app.id)}><Zap size={14}/> LAUNCH</button><button className="remove-button" aria-label={`Remove ${app.name}`} onClick={()=>void removeApp(app)}><Trash2 size={14}/></button></article>)}</div>:<EmptyState icon={Library} title={query?"No matching programs":"Your Library is empty"} text={query?"Try another name or category.":"Add an executable you trust. PULSE will not scan your disk."}/>}</div>;
}

function ScenesPage({scenes,onActivate,onCreate}:{scenes:Scene[];onActivate:(id:number)=>void;onCreate:(name:string,description:string)=>void}) {
  const createScene=()=>{
    const name=window.prompt("Name your custom Scene")?.trim();
    if(!name)return;
    const description=window.prompt("Add an optional description")?.trim()??"";
    onCreate(name,description);
  };
  return <div className="page-stack"><div className="section-lead"><div><span className="eyebrow">LOCAL PROFILES</span><p>Choose a workspace label. Selecting a Scene does not modify Windows settings.</p></div><button className="primary-button" onClick={createScene}><Plus size={15}/> NEW SCENE</button></div><div className="scene-grid">{scenes.map(scene=><article className={`scene-card ${scene.active?"active":""}`} key={scene.id}><div className="scene-card-icon"><AudioLines size={18}/></div><span className="eyebrow">{scene.active?"CURRENT SCENE":"PULSE SCENE"}</span><h3>{scene.name}</h3><p>{scene.description}</p><button className="secondary-button" disabled={scene.active} onClick={()=>onActivate(scene.id)}>{scene.active?"ACTIVE":"SELECT SCENE"}</button></article>)}</div></div>;
}

function MonitorPage({metrics,history}:{metrics:Metrics|null;history:Metrics[]}) {
  if(!metrics)return <EmptyState icon={Gauge} title="Collecting system metrics" text="PULSE is waiting for its first operating-system sample."/>;
  const cpuValues=history.map(item=>item.cpuPercent);
  const average=cpuValues.length?cpuValues.reduce((sum,value)=>sum+value,0)/cpuValues.length:0;
  const min=cpuValues.length?Math.min(...cpuValues):0;
  const max=cpuValues.length?Math.max(...cpuValues):0;
  const points=cpuValues.map((value,index)=>`${cpuValues.length<2?0:index/(cpuValues.length-1)*100},${100-Math.max(0,Math.min(value,100))}`).join(" ");
  return <div className="page-stack"><div className="section-lead"><div><span className="eyebrow">LIVE · REFRESHES EVERY 2.5 SECONDS</span><p>Values are read from Windows. Unsupported sensors are identified, not estimated.</p></div></div><section className="metric-grid monitor-grid"><MetricCard icon={Cpu} label="CPU LOAD" value={`${metrics.cpuPercent.toFixed(1)}%`} detail={`Sampled ${new Date(metrics.sampledAt).toLocaleTimeString()}`} percent={metrics.cpuPercent}/><MetricCard icon={Database} label="MEMORY" value={`${metrics.memoryPercent.toFixed(1)}%`} detail={`${bytes(metrics.memoryUsed)} / ${bytes(metrics.memoryTotal)}`} percent={metrics.memoryPercent}/><MetricCard icon={HardDrive} label="DISK SPACE" value={metrics.diskTotal?`${(metrics.diskUsed/metrics.diskTotal*100).toFixed(1)}%`:"NOT AVAILABLE"} detail={`${bytes(metrics.diskUsed)} used across available disks`} percent={metrics.diskTotal?metrics.diskUsed/metrics.diskTotal*100:0}/><MetricCard icon={Monitor} label="GPU / TEMPERATURE" value="NOT AVAILABLE" detail="No supported hardware sensor provider configured" percent={0}/></section><section className="panel monitor-chart-panel"><div className="panel-heading"><div><span className="eyebrow">RECENT SAMPLES · CURRENT APP SESSION</span><h3>CPU utilization</h3></div><span className="category-label">{history.length} SAMPLES</span></div><div className="chart-wrap">{history.length>1?<svg viewBox="0 0 100 100" preserveAspectRatio="none" role="img" aria-label="Recent CPU utilization chart"><line x1="0" y1="0" x2="100" y2="0"/><line x1="0" y1="50" x2="100" y2="50"/><line x1="0" y1="100" x2="100" y2="100"/><polyline points={points}/></svg>:<EmptyState icon={Gauge} title="Collecting samples" text="A trend appears after at least two measurements."/>}</div><div className="chart-stats"><span>MIN <b>{min.toFixed(1)}%</b></span><span>AVG <b>{average.toFixed(1)}%</b></span><span>MAX <b>{max.toFixed(1)}%</b></span></div></section><section className="panel"><div className="panel-heading"><div><span className="eyebrow">SYSTEM</span><h3>Uptime</h3></div></div><strong className="uptime">{Math.floor(metrics.uptimeSeconds/86400)}d {Math.floor(metrics.uptimeSeconds%86400/3600)}h {Math.floor(metrics.uptimeSeconds%3600/60)}m</strong></section></div>;
}

function ActivityPage({activity}:{activity:ActivityRow[]}) {
  return <div className="page-stack"><section className="panel"><div className="panel-heading"><div><span className="eyebrow">LOCAL HISTORY</span><h3>Activity log</h3></div><span className="category-label">LAST 100 EVENTS</span></div>{activity.length?<ActivityList rows={activity}/>:<EmptyState icon={Activity} title="No activity yet" text="PULSE actions will appear here when performed."/>}</section></div>;
}

function ActivityList({rows}:{rows:ActivityRow[]}) {
  return <div className="activity-list">{rows.map(row=><div className="activity-row" key={row.id}><span className="activity-icon"><Activity size={14}/></span><div><b>{row.detail}</b><small>{row.kind}</small></div><time>{new Date(row.occurredAt).toLocaleString()}</time></div>)}</div>;
}

function EmptyState({icon:Icon,title,text}:{icon:typeof Home;title:string;text:string}) {
  return <div className="empty-state"><Icon size={19}/><b>{title}</b><span>{text}</span></div>;
}

function ComingPage({icon:Icon,title,message}:{icon:typeof Home;title:string;message:string}) {
  return <section className="panel coming-card"><div className="coming-icon"><Icon size={22}/></div><span className="eyebrow">LOCAL-FIRST · IN DEVELOPMENT</span><h2>{title}</h2><p>{message}</p></section>;
}
