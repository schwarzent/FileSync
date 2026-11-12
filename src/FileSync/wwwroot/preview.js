import { el, button, showDialog, note } from './ui.js';
import { changesList } from './views.js';
import { conflictChoices } from './conflicts.js';

export function showPreview(context, plan, execute) {
  if (plan.conflicts.length) { showConflicts(context, plan, execute); return; }
  const combined = [...plan.changes, ...plan.remoteChanges];
  const counts = Object.fromEntries(['add', 'update', 'delete', 'replace', 'metadata'].map(kind =>
    [kind, combined.filter(c => c.kind === kind).length]));
  const summary = el('div', { class: 'preview-summary' }, [
    `新增 ${counts.add}`, `更新 ${counts.update + counts.replace}`, `删除 ${counts.delete}`, `时间 ${counts.metadata}`
  ].map(text => el('span', {}, text)));
  const twoWay = plan.profile.mode === 'two-way';
  const oneDrive = plan.profile.transport === 'onedrive';
  const changes = [plan.changes.length ? changesList(plan.changes) : el('p', {}, '本机文件内容一致。')];
  if (twoWay) changes.unshift(el('h3', {}, '本机修改'));
  if (twoWay) changes.push(el('h3', {}, '远端修改'),
    plan.remoteChanges.length ? changesList(plan.remoteChanges) : el('p', {}, '远端文件内容一致。'));
  const dialog = showDialog('同步预览', [
    note(`${plan.profile.name}：${oneDrive ? '接收 OneDrive 清单中较新的版本，保留本机较新文件和独有文件，恢复并校验创建与修改时间。' : twoWay ? '将合并两端内容，以下分别列出本机与远端修改。' : '本机目标将与远端一致，忽略项保留。'}`),
    summary, el('p', { class: 'path' }, plan.profile.targetPath), ...changes
  ], [button('返回', () => dialog.close()), button('执行同步', context.act(async () => {
    dialog.close();
    await execute(plan);
  }), { class: 'primary', icon: 'sync' })]);
}

function showConflicts(context, plan, execute) {
  const apply = button('应用选择并查看预览', context.act(async () => {
    const resolved = await context.api(`/plans/${plan.id}/resolve`, { choices: choices.values() });
    showPreview(context, resolved, execute);
  }), { class: 'primary', disabled: true });
  const choices = conflictChoices(plan, ready => { apply.disabled = !ready; });
  const dialog = showDialog('处理同步冲突', [el('h3', {}, plan.profile.name), choices.node],
    [button('返回', () => dialog.close()), apply]);
}
