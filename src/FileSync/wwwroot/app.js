import { el, icon, button, showDialog, toast, date, note, bytes } from './ui.js';
import { syncView, devicesView, historyView, settingsView, changesList, statusLabels } from './views.js';
import { showPreview } from './preview.js';
import { recordsView } from './records.js';

const session = document.querySelector('meta[name="filesync-session"]').content;
const pages = { records: ['清单与方案', recordsView], sync: ['整目录同步', syncView], devices: ['局域网设备', devicesView], history: ['版本记录', historyView], settings: ['设置', settingsView] };
const phases = { idle: '等待操作', scanning: '正在比较文件', checking: '正在检查文件变化', downloading: '正在下载文件', uploading: '正在上传文件', backup: '正在完整备份', writing: '正在应用并校验修改' };
let state;
let currentPage = 'records';
let timer;
let disposed = false;
let refreshing;
let localOperation = false;
const context = { api, refresh, act, getState: () => state, history: [], preview, publish, revisionDetails };

async function api(path, body, method) {
  const response = await fetch(`/api${path}`, {
    method: method || (body === undefined ? 'GET' : 'POST'),
    headers: { 'X-FileSync-Session': session, ...(body != null ? { 'Content-Type': 'application/json' } : {}) },
    body: body != null ? JSON.stringify(body) : undefined
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || `操作失败（${response.status}）`);
  return data;
}

function act(action) {
  return async event => {
    const target = event?.currentTarget;
    const prior = target?.disabled;
    if (target && 'disabled' in target) target.disabled = true;
    try { await action(); }
    catch (error) { toast(error.message, true); }
    finally { if (target && 'disabled' in target) target.disabled = prior; }
  };
}

function render() {
  const page = location.hash.slice(1);
  currentPage = pages[page] ? page : 'records';
  document.title = `${pages[currentPage][0]} · FileSync`;
  document.querySelector('#page-name').textContent = pages[currentPage][0];
  document.querySelector('#navigation').replaceChildren(...Object.entries(pages).map(([key, [label]]) =>
    el('a', { href: `#${key}`, class: `nav-item ${key === currentPage ? 'active' : ''}`, 'aria-current': key === currentPage ? 'page' : 'false' }, [icon(key), label])));
  document.querySelector('#content').replaceChildren(...pages[currentPage][1](context).filter(Boolean));
  renderProgress();
}

function renderProgress() {
  const node = document.querySelector('#progress');
  const progress = state.progress;
  node.hidden = !state.busy || progress.phase === 'idle';
  node.replaceChildren(el('strong', {}, phases[progress.phase] || progress.phase), el('p', {}, progress.path || '正在处理同步任务'),
    el('progress', progress.totalBytes ? { value: progress.bytes, max: progress.totalBytes } : {}),
    el('p', {}, progress.totalBytes ? `${bytes(progress.bytes)} / ${bytes(progress.totalBytes)}` : '正在处理…'),
    progress.phase !== 'writing' ? button('取消操作', act(() => api('/cancel', {})), { class: 'text', icon: 'close' }) : null);
}

async function refresh() {
  if (refreshing) return refreshing;
  refreshing = (async () => {
    clearTimeout(timer);
    const wasBusy = state?.busy;
    const previousSettings = state ? JSON.stringify(state.settings) : null;
    const previousHistory = JSON.stringify(context.history);
    state = await api('/status');
    if (!state.busy || currentPage === 'history') context.history = await api('/history');
    if (wasBusy && !state.busy && !localOperation) toast('操作已结束，请查看版本记录。');
    if (previousSettings !== JSON.stringify(state.settings) || previousHistory !== JSON.stringify(context.history) ||
        wasBusy !== state.busy || currentPage === 'devices') render();
    else renderProgress();
    schedule();
  })();
  try { await refreshing; }
  finally { refreshing = null; }
}

function schedule() {
  clearTimeout(timer);
  if (disposed) return;
  const interval = state.busy || localOperation ? 1000 : 15000;
  timer = setTimeout(() => refresh().catch(error => toast(`${error.message}。请刷新页面重新连接。`, true)), interval);
}

async function preview(id) {
  localOperation = true;
  schedule();
  try {
    const plan = await api(`/profiles/${id}/preview`, {});
    showPreview(context, plan, async resolved => {
      localOperation = true;
      schedule();
      try {
        await api(`/plans/${resolved.id}/execute`, {});
        toast('同步完成，文件与目录时间已校验。');
      } finally { localOperation = false; await refresh(); }
    });
  } finally { localOperation = false; await refresh(); }
}

async function publish(id) {
  localOperation = true;
  schedule();
  try {
    const result = await api(`/profiles/${id}/publish`, {});
    showDialog('资料已写入本机 OneDrive 文件夹', [note(result.message),
      el('p', {}, `提交 #${result.sequence} · ${result.changedFiles} 个文件变化；仓库共 ${result.files} 个文件、${result.directories} 个文件夹。原始时间和哈希已记录。`)],
    [button('关闭', () => document.querySelector('#modal').close())]);
  } finally { localOperation = false; await refresh(); }
}

function revisionDetails(revision) {
  const dialog = showDialog('同步版本详情', [el('h3', {}, revision.profileName), el('p', {}, `${date(revision.createdUtc)} · ${statusLabels[revision.status] || revision.status}`),
    el('p', { class: 'path' }, revision.id), revision.error ? note(revision.error) : null,
    revision.backupPath ? note(`完整备份：${revision.backupPath}`, 'backup') : null,
    revision.mode === 'two-way' ? el('h3', {}, '本机修改') : null, changesList(revision.changes),
    ...(revision.mode === 'two-way' ? [el('h3', {}, '远端修改'), changesList(revision.remoteChanges)] : [])].filter(Boolean),
  [button('关闭', () => dialog.close())]);
}

window.addEventListener('hashchange', () => { if (state) render(); });
window.addEventListener('pagehide', () => { disposed = true; clearTimeout(timer); });
window.addEventListener('pageshow', () => { disposed = false; if (state) schedule(); });
refresh().catch(error => {
  document.querySelector('#content').replaceChildren(note(`连接失败：${error.message}。请确认 FileSync 正在运行并刷新页面。`));
  toast(error.message, true);
});
