import { el, button, input, note, bytes } from './ui.js';

const PAGE_SIZE = 30;
const sides = { local: '本机', remote: '远端' };

export function conflictChoices(plan, onReady) {
  const choices = new Map();
  const selected = new Set();
  const indexes = { local: new Map(plan.local.entries.map(e => [e.path.toLowerCase(), e])),
    remote: new Map(plan.remote.entries.map(e => [e.path.toLowerCase(), e])) };
  const panel = el('section', { class: 'conflict-panel', 'aria-label': '选择冲突版本' });
  const list = el('div', { class: 'conflict-list' });
  const status = el('p', { class: 'conflict-status', role: 'status' });
  const pager = el('div', { class: 'pagination' });
  let page = 0;
  const all = input('', { type: 'checkbox', 'aria-label': '全选所有冲突（包括其他页）' });
  const setChoices = (paths, side) => { for (const path of paths) choices.set(path, side); render(); };
  const toggle = path => { if (selected.has(path)) selected.delete(path); else selected.add(path); render(); };
  all.addEventListener('change', () => {
    for (const path of plan.conflicts) { if (all.checked) selected.add(path); else selected.delete(path); }
    render();
  });
  const batch = side => button(`所选保留${sides[side]}`, () => setChoices(selected, side));
  const localBatch = batch('local');
  const remoteBatch = batch('remote');
  const toolbar = el('div', { class: 'conflict-toolbar' }, [
    el('label', { class: 'checkbox-row' }, [all, '全选所有冲突']), localBatch, remoteBatch,
    button('全部保留本机', () => setChoices(plan.conflicts, 'local')),
    button('全部保留远端', () => setChoices(plan.conflicts, 'remote'))
  ]);

  function render() {
    all.checked = selected.size === plan.conflicts.length;
    all.indeterminate = selected.size > 0 && !all.checked;
    localBatch.disabled = remoteBatch.disabled = selected.size === 0;
    status.textContent = `已选择版本 ${choices.size} / ${plan.conflicts.length} 项 · 已勾选 ${selected.size} 项`;
    list.replaceChildren(...plan.conflicts.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE).map(path =>
      conflictRow(path, { selected: selected.has(path), side: choices.get(path), indexes, toggle, setChoices })));
    const pages = Math.ceil(plan.conflicts.length / PAGE_SIZE);
    pager.hidden = pages <= 1;
    pager.replaceChildren(button('上一页', () => { page--; render(); }, { disabled: page === 0 }),
      el('span', {}, `${page + 1} / ${pages}`),
      button('下一页', () => { page++; render(); }, { disabled: page + 1 >= pages }));
    onReady(choices.size === plan.conflicts.length);
  }

  panel.append(note('为每项冲突选择保留版本，再查看处理后的同步预览。目录冲突将同时采用所选端的整个目录内容；选择已删除的一端会同步删除。'), toolbar, status, list, pager);
  render();
  return { node: panel, values: () => Object.fromEntries(choices) };
}

function conflictRow(path, context) {
  const checked = input('', { type: 'checkbox', checked: context.selected, 'aria-label': `选择 ${path}` });
  checked.addEventListener('change', () => context.toggle(path));
  const version = (side, label) => {
    const choose = button(`保留${label}`, () => context.setChoices([path], side),
      { class: context.side === side ? 'primary' : '', title: `${path}：保留${label}` });
    choose.setAttribute('aria-pressed', String(context.side === side));
    return el('div', {}, [el('small', {}, `${label} · ${describe(context.indexes[side].get(path.toLowerCase()))}`), choose]);
  };
  return el('article', { class: 'conflict-row' }, [
    el('div', { class: 'conflict-path' }, [checked, el('strong', {}, path)]),
    el('div', { class: 'conflict-versions' }, Object.entries(sides).map(([side, label]) => version(side, label))),
    el('small', { class: 'conflict-choice' }, context.side ? `已选择保留${sides[context.side]}` : '待选择')
  ]);
}

function describe(entry) {
  if (!entry) return '已删除 / 不存在';
  if (entry.directory) return '文件夹（包含子项）';
  const TICKS_PER_MILLISECOND = 10000;
  const UNIX_EPOCH_TICKS = 621355968000000000;
  const modified = new Date((entry.modifiedTicks - UNIX_EPOCH_TICKS) / TICKS_PER_MILLISECOND);
  return `${bytes(entry.length)} · ${modified.toLocaleString('zh-CN', { hour12: false })}`;
}
