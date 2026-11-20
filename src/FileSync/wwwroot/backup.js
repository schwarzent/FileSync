import { el, button, input, showDialog, note, toast } from './ui.js';

export function taskBackupCard(context) {
  const file = input('', { type: 'file', accept: '.json,application/json', hidden: true, 'aria-label': '选择同步任务配置备份' });
  file.addEventListener('change', context.act(async () => {
    const selected = file.files[0];
    if (!selected) return;
    try {
      const backup = parseBackup(await selected.text());
      const preview = await context.api('/configuration/tasks/preview', backup);
      showImport(context, backup, preview);
    } finally { file.value = ''; }
  }));
  return el('section', { class: 'card form-card' }, [el('h2', {}, '整目录任务配置备份'),
    el('p', { class: 'section-gap' }, '备份整目录任务的路径、同步方式、过滤规则和备份设置。清单、方案和资料文件不包含在内。'),
    el('div', { class: 'section-gap profile-actions' }, [
      button('导出整目录任务', context.act(() => exportTasks(context)), { icon: 'download', disabled: context.getState().busy }),
      button('导入整目录任务', () => file.click(), { icon: 'plus', disabled: context.getState().busy })
    ]), file,
    el('small', {}, '导入按任务编号更新，其他已有任务保留。路径按原值恢复；局域网任务需已与原设备配对。')
  ]);
}

function parseBackup(text) {
  try { return JSON.parse(text); }
  catch (error) { throw new Error(`配置备份 JSON 格式无效：${error.message}`); }
}

async function exportTasks(context) {
  const backup = await context.api('/configuration/tasks/export');
  const blob = new Blob([JSON.stringify(backup, null, 2) + '\n'], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const name = `FileSync-tasks-${backup.exportedUtc.slice(0, 19).replaceAll(':', '-')}.json`;
  const link = el('a', { href: url, download: name });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
  toast(`已导出 ${backup.profiles.length} 个同步任务。`);
}

function showImport(context, backup, preview) {
  const dialog = showDialog('导入同步任务配置', [
    note(`共 ${preview.profiles.length} 个任务：新增 ${preview.added}，更新 ${preview.updated}，相同 ${preview.unchanged}。`),
    ...preview.profiles.map(profile => el('article', { class: 'repository-change' }, [
      el('h3', {}, profile.name), el('p', { class: 'path' }, profile.targetPath),
      profile.relayPath ? el('p', { class: 'path' }, `OneDrive：${profile.relayPath}`) : null
    ])),
    el('p', {}, '导入只恢复任务设置。资料文件和已有版本记录保留，同步仍需手动执行。')
  ], [button('取消', () => dialog.close()), button('导入任务', context.act(async () => {
    const result = await context.api('/configuration/tasks/import', { backup, configurationHash: preview.configurationHash });
    dialog.close();
    await context.refresh();
    toast(`任务配置已导入：新增 ${result.added}，更新 ${result.updated}，相同 ${result.unchanged}。`);
  }), { class: 'primary' })]);
}
