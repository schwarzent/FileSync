import { el, button, field, input, select, showDialog, toast, note } from './ui.js';
import { folderInput, ignoreInput, confirmAction } from './forms.js';

export async function oneDriveForm(context, existing) {
  const accounts = await context.api('/onedrive/accounts');
  const name = input(existing?.name || '', { required: true, maxLength: 80, placeholder: '例如 跨电脑工作资料' });
  const target = folderInput(existing?.targetPath || '', context);
  const defaultRelay = accounts.length ? `${accounts[0].path}\\FileSync\\共享资料` : '';
  const relay = folderInput(existing?.relayPath || defaultRelay, context);
  const account = select(accounts.map(a => ({ value: a.path, label: `${a.name} · ${a.path}` })));
  account.addEventListener('change', () => { relay.node.value = `${account.value}\\FileSync\\共享资料`; });
  const ignored = ignoreInput(existing?.ignorePatterns);
  const backup = folderInput(existing?.backupPath || '', context);
  const enabled = input('', { type: 'checkbox', checked: Boolean(existing?.backupPath) });
  const backupField = field('接收前完整备份位置', backup.row);
  backupField.hidden = !enabled.checked;
  enabled.addEventListener('change', () => { backupField.hidden = !enabled.checked; });
  const save = context.act(async () => {
    if (![name, target.node, relay.node].every(n => n.reportValidity())) return;
    if (enabled.checked && !backup.node.reportValidity()) return;
    await context.api('/profiles', { id: existing?.id, name: name.value, peerId: '', shareId: '',
      transport: 'onedrive', mode: 'latest', targetPath: target.node.value, relayPath: relay.node.value,
      backupPath: enabled.checked ? backup.node.value : null, ignorePatterns: ignored.node.value });
    document.querySelector('#modal').close();
    toast('OneDrive 同步任务已保存。');
    await context.refresh();
  });
  showDialog(existing ? '编辑 OneDrive 任务' : '新建 OneDrive 任务', [
    note('两台电脑使用同一个 OneDrive 中继文件夹。来源端发布后，等待 OneDrive 上传和下载，再在另一端预览接收。'),
    field('任务名称', name), field('本机资料文件夹', target.row, '创建时间和修改时间将记录到中继清单，并在接收时恢复、校验。'),
    accounts.length ? field('检测到的本机 OneDrive', account) : note('未检测到 OneDrive 路径，请确认客户端已登录并手动填写中继文件夹。'),
    field('OneDrive 中继文件夹', relay.row, '选择 OneDrive 内独立的子文件夹，与本机资料文件夹分开；两端路径可以不同，但必须对应同一个云端文件夹。'),
    ignored.field, el('label', { class: 'checkbox-row' }, [enabled, '接收前完整备份']), backupField,
    note('按清单中的原始修改时间保留较新版本；同时间异内容需选择版本。独有文件保留，删除不自动传播。发布仅确认本机中继写入完成。')
  ], [button('保存任务', save, { class: 'primary', icon: 'check' })]);
}

export function oneDriveRow(profile, context) {
  const busy = context.getState().busy;
  return el('article', { class: 'profile' }, [
    el('div', { class: 'profile-title' }, [el('h3', {}, profile.name), el('div', { class: 'profile-actions' }, [
      button('', context.act(() => oneDriveForm(context, profile)), { icon: 'edit', class: 'icon-button', title: '编辑 OneDrive 任务' }),
      button('', () => confirmAction('移除 OneDrive 任务', '移除任务后保留本机资料、中继文件和版本记录。',
        () => context.api(`/profiles/${profile.id}`, null, 'DELETE'), context), { icon: 'trash', class: 'icon-button', title: '移除任务' })])]),
    el('div', { class: 'route' }, [el('div', {}, [el('small', {}, '本机资料文件夹'), el('div', { class: 'path' }, profile.targetPath)]),
      el('strong', {}, '↔'), el('div', {}, [el('small', {}, 'OneDrive 中继'), el('div', { class: 'path' }, profile.relayPath)])]),
    el('div', { class: 'profile-bottom' }, [el('small', {}, '原始修改时间优先 · 创建与修改时间校验'),
      el('div', { class: 'profile-actions' }, [
        button('发布到 OneDrive', context.act(() => context.publish(profile.id)), { icon: 'plus', disabled: busy }),
        button('预览接收', context.act(() => context.preview(profile.id)), { icon: 'sync', class: 'primary', disabled: busy })])])
  ]);
}
