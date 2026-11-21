import { el, button, heading, empty, showDialog, note, date, bytes, select, field, input, toast } from './ui.js';
import { recordForm, exchangeRecords, removeRecordRepository } from './record-form.js';
import { recordSelection } from './record-selection.js';
import { previewRecordPlan, recordPlans } from './record-plans.js';
import { shareForm } from './forms.js';

const PAGE_SIZE = 30;
const UNIX_EPOCH_TICKS = 621355968000000000;
const TICKS_PER_MILLISECOND = 10000;
const statuses = { 'local-only': '仅本机有', 'other-only': '仅对方有', 'local-newer': '本机修改时间较新',
  'other-newer': '对方修改时间较新', conflict: '内容或结构冲突', metadata: '内容一致，时间不同' };
const changes = { add: '新增', update: '修改', delete: '删除', replace: '替换', metadata: '时间修改' };

export function recordsView(context) {
  const repositories = context.getState().settings.recordRepositories;
  const create = () => recordForm(context);
  return [heading('清单与方案', '先比较两端文件清单，离线选定方案，连接后只同步选定文件。',
    button('添加资料文件夹', create, { icon: 'plus', class: 'primary' })),
  note('① 扫描生成清单　② 通过 OneDrive 或局域网交换清单　③ 比较并保存方案　④ 局域网连接后执行方案。清单只含路径、时间、大小和哈希。'),
  el('div', { class: 'stack section-gap' }, repositories.length ? repositories.map(r => recordRow(context, r)) :
    [empty('添加要比较的资料文件夹', '资料留在各自电脑，只交换清单即可查看差异。', button('添加资料文件夹', create, { icon: 'plus' }))])];
}

function recordRow(context, repository) {
  const busy = context.getState().busy;
  const recordPath = repository.repositoryPath ? `${repository.repositoryPath}\\.filesync-records` :
    `${context.getState().dataPath}\\records\\${repository.repositoryId}`;
  return el('article', { class: 'card form-card' }, [el('h2', {}, repository.name),
    el('p', { class: 'path section-gap' }, `本机资料：${repository.workspacePath}`),
    el('p', { class: 'path' }, `记录位置：${recordPath}`),
    el('small', { class: 'path' }, `仓库编号：${repository.repositoryId}`),
    el('div', { class: 'profile-actions section-gap' }, [
      button('扫描并更新清单', context.act(async () => {
        const result = await context.api(`/record-repositories/${repository.id}/generate`, {});
        toast(`已记录 ${result.files} 个文件、${bytes(result.bytes)}；${result.changes} 项变化。未复制资料文件。`);
      }), { icon: 'file', disabled: busy }),
      button('局域网交换清单', context.act(() => exchangeRecords(context, repository)), { icon: 'devices', disabled: busy }),
      button('比较清单并制定方案', context.act(() => compareRecords(context, repository)), { icon: 'sync', class: 'primary', disabled: busy }),
      button('已保存方案', context.act(() => recordPlans(context, repository)), { icon: 'file', disabled: busy }),
      permissionButton(context, repository),
      button('', () => removeRecordRepository(context, repository), { icon: 'trash', class: 'icon-button', title: '移除记录仓库' })
    ])]);
}

function permissionButton(context, repository) {
  const state = context.getState();
  const share = state.settings.shares.find(s => s.path.toLowerCase() === repository.workspacePath.toLowerCase());
  if (share?.allowWrite) return button('局域网共享权限', () => shareForm(context, share),
    { disabled: state.busy, title: '当前允许配对设备读写，点击可修改权限' });
  return button('允许局域网执行', context.act(async () => {
    await context.api(`/record-repositories/${repository.id}/enable-lan`, {});
    await context.refresh(); toast('已允许配对设备读写此资料夹，点击“局域网共享权限”可关闭写入。');
  }), { disabled: state.busy, title: '允许已配对设备读取并写入此资料夹，包含修改和删除' });
}

async function compareRecords(context, repository) {
  const devices = await comparisonDevices(context, repository);
  const others = devices.filter(d => d.deviceId !== context.getState().deviceId);
  if (!others.length) {
    showDialog(`${repository.name} · 等待对方清单`, [
      note('本机清单已就绪。请在另一台电脑扫描并更新清单，再通过 OneDrive 或局域网交换清单。'),
      el('p', {}, '收到对方清单后，再点击“比较清单并制定方案”。')
    ], [button('关闭', () => document.querySelector('#modal').close())]);
    return;
  }
  const choice = select(others.map(d => ({ value: d.deviceId, label: `${d.deviceName} · ${date(d.recordedUtc)}` })));
  const body = el('div');
  let result;
  let selection;
  const preview = button('预览方案', context.act(() => previewRecordPlan(context, repository,
    { comparison: result, choices: selection.values() })), { class: 'primary', disabled: true });
  const load = async () => {
    preview.disabled = true;
    const deviceId = choice.value;
    const loaded = await context.api(`/record-repositories/${repository.id}/compare?deviceId=${encodeURIComponent(deviceId)}`);
    if (deviceId !== choice.value) return;
    result = loaded;
    selection = recordSelection(result);
    body.replaceChildren(...comparisonView(result, selection));
    preview.disabled = false;
  };
  choice.addEventListener('change', context.act(load));
  showDialog(`${repository.name} · 比较清单`, [
    note('比较已保存的清单，不扫描资料。选择采用哪端；未选择的差异暂不处理。修改时间提示新旧，执行前会检查选定文件。'),
    field('对比设备', choice), body
  ], [button('关闭', () => document.querySelector('#modal').close()), preview]);
  await load();
}

async function comparisonDevices(context, repository) {
  const endpoint = `/record-repositories/${repository.id}`;
  const devices = await context.api(endpoint);
  if (devices.some(d => d.deviceId === context.getState().deviceId)) return devices;
  const dialog = showDialog(`${repository.name} · 首次生成本机清单`, [
    note('正在扫描本机资料并生成清单，完成后继续比较。只读取资料，不复制或修改文件。')
  ]);
  try {
    await context.api(`${endpoint}/generate`, {});
    return await context.api(endpoint);
  } finally { dialog.close(); }
}

function comparisonView(result, selection) {
  const search = input('', { type: 'search', placeholder: '按文件路径筛选', 'aria-label': '按文件路径筛选记录' });
  const rows = el('div');
  let page = 0;
  const render = () => {
    const matches = result.differences.filter(row => row.path.includes(search.value));
    rows.replaceChildren(...matches.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE).map(row => differenceRow(result, row, selection)));
    if (!matches.length) rows.append(el('p', {}, '没有符合条件的差异。'));
    if (matches.length > PAGE_SIZE) rows.append(el('div', { class: 'pagination' }, [
      button('上一页', () => { page--; render(); }, { disabled: page === 0 }), `${page + 1} / ${Math.ceil(matches.length / PAGE_SIZE)}`,
      button('下一页', () => { page++; render(); }, { disabled: (page + 1) * PAGE_SIZE >= matches.length })]));
  };
  search.addEventListener('input', () => { page = 0; render(); });
  render();
  return [el('p', {}, `${result.equal} 项一致，${result.differences.length} 项差异。`),
    ...[result.local, result.other].map(device => el('p', {}, `${device.deviceName}：${date(device.recordedUtc)} · ${device.files} 个文件 · ${bytes(device.bytes)}`)),
    result.differentRules ? note('两端忽略规则不同，独有条目可能来自过滤范围差异。') : null,
    selection.toolbar, search, rows].filter(Boolean);
}

function differenceRow(result, row, selection) {
  return el('article', { class: 'repository-change' }, [el('h3', { class: 'path' }, `${statuses[row.status]} · ${row.path}`),
    el('p', { class: 'record-summary' }, `${result.local.deviceName}：${summary(row.local)}　|　${result.other.deviceName}：${summary(row.other)}`),
    selection.control(row), el('details', {}, [el('summary', {}, '查看时间、大小与哈希'), el('div', { class: 'repository-comparison' }, [
      metadata(result.local.deviceName, row.local, row.localChange), metadata(result.other.deviceName, row.other, row.otherChange)
    ])])]);
}

function summary(entry) {
  if (!entry) return '不存在';
  const modified = date(new Date((entry.modifiedTicks - UNIX_EPOCH_TICKS) / TICKS_PER_MILLISECOND));
  return `${entry.directory ? '文件夹' : bytes(entry.length)} · ${modified}`;
}

function metadata(name, entry, change) {
  const ticksToUtc = ticks => new Date((ticks - UNIX_EPOCH_TICKS) / TICKS_PER_MILLISECOND).toISOString();
  return el('section', {}, [el('h3', {}, `${name}${change ? ` · 本次记录${changes[change]}` : ''}`), entry ?
    el('dl', { class: 'repository-metadata' }, [
      ['类型 / 大小', entry.directory ? '文件夹' : bytes(entry.length)], ['创建时间（UTC）', ticksToUtc(entry.createdTicks)],
      ['修改时间（UTC）', ticksToUtc(entry.modifiedTicks)], ['SHA-256', entry.hash || '文件夹']
    ].flatMap(([key, value]) => [el('dt', {}, key), el('dd', {}, value)])) : el('p', {}, '记录中不存在')]);
}
