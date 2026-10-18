import { el, icon, button, field, input, select, showDialog, toast, note } from './ui.js';

export function folderInput(value, context) {
  const node = input(value, { placeholder: '例如 D:\\Documents\\项目资料', required: true });
  const browse = button('浏览', context.act(async () => {
    const parent = document.querySelector('#modal');
    const saved = [...parent.childNodes];
    const choose = async path => {
      const data = await context.api(`/folders?path=${encodeURIComponent(path || '')}`);
      const list = el('div', { class: 'folder-list' }, data.folders.map(folder =>
        button(folder.name, context.act(() => choose(folder.path)), { icon: 'folder', class: 'folder-item' })));
      const back = button('上一级', context.act(() => choose(data.parent)), { icon: 'arrow', disabled: !data.path });
      const dialog = showDialog('选择本机文件夹', [el('p', { class: 'path' }, data.path || '本机磁盘'), back, list], [
        button('返回', () => { dialog.close(); dialog.replaceChildren(...saved); dialog.showModal(); }),
        button('选择此文件夹', () => { node.value = data.path; dialog.close(); dialog.replaceChildren(...saved); dialog.showModal(); }, { class: 'primary', disabled: !data.path })
      ]);
    };
    await choose('');
  }), { icon: 'folder' });
  return { node, row: el('div', { class: 'input-row' }, [node, browse]) };
}

export function shareForm(context) {
  const name = input('', { required: true, maxLength: 80, placeholder: '例如 工作资料' });
  const path = folderInput('', context);
  const save = context.act(async () => {
    if (!name.reportValidity() || !path.node.reportValidity()) return;
    await context.api('/shares', { name: name.value, path: path.node.value });
    document.querySelector('#modal').close();
    toast('源文件夹已发布，配对设备可读取。');
    await context.refresh();
  });
  showDialog('发布本机源文件夹', [note('已配对的设备可读取此文件夹。文件只在手动同步时传输。'), field('名称', name), field('源文件夹', path.row)],
    [button('发布文件夹', save, { class: 'primary', icon: 'plus' })]);
}

export async function profileForm(context, existing) {
  const state = context.getState();
  if (!state.settings.peers.length) { pairingForm(context); return; }
  const name = input(existing?.name || '', { required: true, maxLength: 80, placeholder: '例如 从笔记本同步工作资料' });
  const peer = select(state.settings.peers.map(p => ({ value: p.id, label: p.name })), existing?.peerId);
  const share = select([{ value: '', label: '正在获取远端源文件夹…' }]);
  const target = folderInput(existing?.targetPath || '', context);
  const backup = folderInput(existing?.backupPath || '', context);
  const enabled = input('', { type: 'checkbox', checked: Boolean(existing?.backupPath) });
  const backupField = field('完整备份位置', backup.row, '每次同步前备份整个目标文件夹。历史备份保留在所选位置。');
  backupField.hidden = !enabled.checked;
  enabled.addEventListener('change', () => { backupField.hidden = !enabled.checked; });
  const loadShares = async () => {
    share.disabled = true;
    const shares = await context.api(`/peers/${peer.value}/shares`);
    share.replaceChildren(...shares.map(s => el('option', { value: s.id }, s.name)));
    if (!shares.length) share.append(el('option', { value: '' }, '远端尚未发布源文件夹'));
    if (existing && peer.value === existing.peerId) share.value = existing.shareId;
    share.disabled = false;
  };
  peer.addEventListener('change', context.act(loadShares));
  const save = context.act(async () => {
    if (!name.reportValidity() || !target.node.reportValidity()) return;
    if (!share.value || share.disabled) throw new Error('请先在远端发布源文件夹，再选择它。');
    if (enabled.checked && !backup.node.value.trim()) throw new Error('请选择完整备份位置。');
    await context.api('/profiles', { id: existing?.id, name: name.value, peerId: peer.value, shareId: share.value,
      targetPath: target.node.value, backupPath: enabled.checked ? backup.node.value : null,
      sourceName: share.selectedOptions[0].textContent });
    document.querySelector('#modal').close();
    toast('同步任务已保存。');
    await context.refresh();
  });
  showDialog(existing ? '编辑同步任务' : '新建同步任务', [note('远端源文件夹 → 本机目标文件夹。执行时，本机独有文件将移入版本库。'),
    field('任务名称', name), field('来源设备', peer), field('远端源文件夹', share), field('本机目标文件夹', target.row),
    el('label', { class: 'checkbox-row' }, [enabled, '启用同步前完整备份']), backupField,
    el('small', {}, '被覆盖和删除的文件始终保留在版本库，可撤销最近一次同步。')],
  [button('保存任务', save, { class: 'primary', icon: 'check' })]);
  await loadShares();
}

export function pairingForm(context) {
  const addresses = context.getState().addresses;
  const address = select(addresses.map(ip => ({ value: ip, label: ip })));
  const generated = el('div');
  const code = el('textarea', { placeholder: '粘贴另一台设备生成的完整邀请', spellcheck: false, 'aria-label': '另一台设备的配对邀请' });
  const create = button('生成配对邀请', context.act(async () => {
    if (!address.value) throw new Error('未检测到局域网地址，请检查网络连接。');
    const invite = await context.api('/invite', { address: address.value });
    const output = el('textarea', { value: invite.code, readOnly: true, class: 'code-area', 'aria-label': '本机配对邀请' });
    generated.replaceChildren(output, button('复制邀请', context.act(async () => {
      await navigator.clipboard.writeText(invite.code); toast('配对邀请已复制。');
    }), { icon: 'file' }), el('small', {}, ' 5 分钟有效，仅能使用一次。请通过可信渠道传递。'));
  }), { icon: 'plus' });
  const join = context.act(async () => {
    if (!code.value.trim()) throw new Error('请粘贴另一台设备的配对邀请。');
    await context.api('/pair', { code: code.value });
    document.querySelector('#modal').close();
    toast('设备配对成功。');
    await context.refresh();
  });
  showDialog('配对局域网设备', [note('在两台电脑上启动 FileSync，将其中一台的配对邀请复制到另一台。'),
    el('h3', {}, '在本机生成邀请'), field('本机局域网地址', address), create, generated,
    el('div', { class: 'pair-divider' }), el('h3', {}, '连接另一台设备'), field('对方的配对邀请', code)],
  [button('连接并配对', join, { class: 'primary', icon: 'devices' })]);
}

export function confirmAction(title, message, action, context) {
  const dialog = showDialog(title, [note(message)], [button('确认', context.act(async () => {
    await action(); dialog.close(); await context.refresh();
  }), { class: 'danger' })]);
}
