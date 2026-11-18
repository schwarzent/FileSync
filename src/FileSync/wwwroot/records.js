import { el, button, heading, empty, showDialog, note, date, bytes, select, field, input, toast } from './ui.js';
import { recordForm, exchangeRecords, removeRecordRepository } from './record-form.js';

const PAGE_SIZE = 30;
const UNIX_EPOCH_TICKS = 621355968000000000;
const TICKS_PER_MILLISECOND = 10000;
const statuses = { 'local-only': '仅本机有', 'other-only': '仅对方有', 'local-newer': '本机修改时间较新',
  'other-newer': '对方修改时间较新', conflict: '内容或结构冲突', metadata: '内容一致，时间不同' };
const changes = { add: '新增', update: '修改', delete: '删除', replace: '替换', metadata: '时间修改' };

export function recordsView(context) {
  const repositories = context.getState().settings.recordRepositories;
  const create = () => recordForm(context);
  return [heading('文件记录仓库', '独立生成记录，只比对清单，资料保留在原电脑。',
    button('新建记录仓库', create, { icon: 'plus', class: 'primary' })),
  note('先在各电脑生成本机记录，再交换局域网记录或等待 OneDrive 同步，最后仅比对记录。生成时读取本机文件计算 SHA-256，比对时不扫描资料、不上传文件。'),
  el('div', { class: 'stack section-gap' }, repositories.length ? repositories.map(r => recordRow(context, r)) :
    [empty('建立一个记录仓库', '为两台电脑的资料保存独立清单，无需事先上传全部文件。', button('新建记录仓库', create, { icon: 'plus' }))])];
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
      button('生成本机记录', context.act(async () => {
        const result = await context.api(`/record-repositories/${repository.id}/generate`, {});
        toast(`已记录 ${result.files} 个文件、${bytes(result.bytes)}；${result.changes} 项变化。未复制资料文件。`);
      }), { icon: 'file', disabled: busy }),
      button('交换局域网记录', context.act(() => exchangeRecords(context, repository)), { icon: 'devices', disabled: busy }),
      button('仅比对记录', context.act(() => compareRecords(context, repository)), { icon: 'sync', class: 'primary', disabled: busy }),
      button('', () => removeRecordRepository(context, repository), { icon: 'trash', class: 'icon-button', title: '移除记录仓库' })
    ])]);
}

async function compareRecords(context, repository) {
  const devices = await context.api(`/record-repositories/${repository.id}`);
  const own = devices.find(d => d.deviceId === context.getState().deviceId);
  if (!own) throw new Error('请先生成本机记录。');
  const others = devices.filter(d => d.deviceId !== own.deviceId);
  if (!others.length) throw new Error('尚无另一台设备的记录，请先交换局域网记录或等待 OneDrive 同步。');
  const choice = select(others.map(d => ({ value: d.deviceId, label: `${d.deviceName} · ${date(d.recordedUtc)}` })));
  const body = el('div');
  const load = async () => {
    const result = await context.api(`/record-repositories/${repository.id}/compare?deviceId=${encodeURIComponent(choice.value)}`);
    body.replaceChildren(...comparisonView(result));
  };
  choice.addEventListener('change', context.act(load));
  showDialog(`${repository.name} · 仅比对记录`, [
    note('结果对应各自生成记录的时刻，不代表当前磁盘状态。修改时间用于提示新旧；记录比较不会覆盖文件，实际同步仍会重新校验。'),
    field('对比设备', choice), body
  ], [button('关闭', () => document.querySelector('#modal').close())]);
  await load();
}

function comparisonView(result) {
  const search = input('', { type: 'search', placeholder: '按文件路径筛选', 'aria-label': '按文件路径筛选记录' });
  const rows = el('div');
  let page = 0;
  const render = () => {
    const matches = result.differences.filter(row => row.path.includes(search.value));
    rows.replaceChildren(...matches.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE).map(row => differenceRow(result, row)));
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
    search, rows].filter(Boolean);
}

function differenceRow(result, row) {
  return el('article', { class: 'repository-change' }, [el('h3', { class: 'path' }, `${statuses[row.status]} · ${row.path}`),
    el('div', { class: 'repository-comparison' }, [
      metadata(result.local.deviceName, row.local, row.localChange), metadata(result.other.deviceName, row.other, row.otherChange)
    ])]);
}

function metadata(name, entry, change) {
  const ticksToUtc = ticks => new Date((ticks - UNIX_EPOCH_TICKS) / TICKS_PER_MILLISECOND).toISOString();
  return el('section', {}, [el('h3', {}, `${name}${change ? ` · 本次记录${changes[change]}` : ''}`), entry ?
    el('dl', { class: 'repository-metadata' }, [
      ['类型 / 大小', entry.directory ? '文件夹' : bytes(entry.length)], ['创建时间（UTC）', ticksToUtc(entry.createdTicks)],
      ['修改时间（UTC）', ticksToUtc(entry.modifiedTicks)], ['SHA-256', entry.hash || '文件夹']
    ].flatMap(([key, value]) => [el('dt', {}, key), el('dd', {}, value)])) : el('p', {}, '记录中不存在')]);
}
