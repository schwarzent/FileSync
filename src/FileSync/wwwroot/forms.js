import { el, button, field, input, select, showDialog, toast, note } from './ui.js';

export function folderInput(value, context) {
  const node = input(value, { placeholder: '例如 D:\\Documents\\项目资料', required: true });
  const browse = button('浏览', context.act(async () => {
    const result = await context.api('/folders/pick', { path: node.value.trim() || null });
    if (result.path !== null) { node.value = result.path; node.dispatchEvent(new Event('change', { bubbles: true })); }
  }), { icon: 'folder' });
  return { node, row: el('div', { class: 'input-row' }, [node, browse]) };
}

function ignoreInput(value = '') {
  const node = el('textarea', { value, spellcheck: false, class: 'ignore-patterns',
    placeholder: '# 每行一条，使用 / 分隔\n.git/\nnode_modules/\n/build/\n**/cache/\n*.tmp' });
  return { node, field: field('忽略规则', node, '相对同步根目录，每行一条；支持 *、**、?、# 注释。目录以 / 结尾；不支持 ! 或 []。忽略的目录不扫描、不传输，原有内容保留。') };
}

export function shareForm(context, existing) {
  const name = input(existing?.name || '', { required: true, maxLength: 80, placeholder: '例如 工作资料' });
  const path = folderInput(existing?.path || '', context);
  const allowWrite = input('', { type: 'checkbox', checked: Boolean(existing?.allowWrite) });
  const ignored = ignoreInput(existing?.ignorePatterns);
  const save = context.act(async () => {
    if (!name.reportValidity() || !path.node.reportValidity()) return;
    await context.api('/shares', { id: existing?.id, name: name.value, path: path.node.value,
      allowWrite: allowWrite.checked, ignorePatterns: ignored.node.value });
    document.querySelector('#modal').close();
    toast('共享设置已保存。');
    await context.refresh();
  });
  showDialog(existing ? '编辑共享文件夹' : '发布本机源文件夹', [note('已配对的设备可读取此文件夹。允许双向写入后，配对设备可手动同步修改和删除。'),
    field('名称', name), field('源文件夹', path.row), ignored.field,
    el('label', { class: 'checkbox-row' }, [allowWrite, '允许配对设备双向写入'])],
    [button('保存共享', save, { class: 'primary', icon: 'check' })]);
}

async function profileFields(context, existing) {
  const state = context.getState();
  const name = input(existing?.name || '', { required: true, maxLength: 80, placeholder: '例如 从笔记本同步工作资料' });
  const peer = select(state.settings.peers.map(p => ({ value: p.id, label: p.name })), existing?.peerId);
  const share = select([{ value: '', label: '正在获取远端源文件夹…' }]);
  const mode = select([{ value: 'one-way', label: '单向：远端 → 本机' }, { value: 'two-way', label: '双向：远端 ↔ 本机' }], existing?.mode || 'one-way');
  const target = folderInput(existing?.targetPath || '', context);
  const backup = folderInput(existing?.backupPath || '', context);
  const enabled = input('', { type: 'checkbox', checked: Boolean(existing?.backupPath) });
  const backupField = field('完整备份位置', backup.row, '每次同步前备份目标文件夹中参与同步的内容，忽略项不读取。历史备份保留在所选位置。');
  const ignored = ignoreInput(existing?.ignorePatterns);
  backupField.hidden = !enabled.checked;
  enabled.addEventListener('change', () => { backupField.hidden = !enabled.checked; });
  const loadShares = async () => {
    share.disabled = true;
    const shares = await context.api(`/peers/${peer.value}/shares`);
    share.replaceChildren(...shares.map(s => el('option', { value: s.id, 'data-writable': String(s.allowWrite) }, `${s.name}${s.allowWrite ? '（允许双向）' : '（只读）'}`)));
    if (!shares.length) share.append(el('option', { value: '' }, '远端尚未发布源文件夹'));
    if (existing && peer.value === existing.peerId) share.value = existing.shareId;
    share.disabled = false;
  };
  peer.addEventListener('change', context.act(loadShares));
  return { name, peer, share, mode, target, backup, enabled, backupField, ignored, loadShares };
}

export async function profileForm(context, existing) {
  if (!context.getState().settings.peers.length) { pairingForm(context); return; }
  const { name, peer, share, mode, target, backup, enabled, backupField, ignored, loadShares } = await profileFields(context, existing);
  const save = context.act(async () => {
    if (!name.reportValidity() || !target.node.reportValidity()) return;
    if (!share.value || share.disabled) throw new Error('请先在远端发布源文件夹，再选择它。');
    if (mode.value === 'two-way' && share.selectedOptions[0].dataset.writable !== 'true')
      throw new Error('请先在远端共享设置中启用“允许配对设备双向写入”。');
    if (enabled.checked && !backup.node.value.trim()) throw new Error('请选择完整备份位置。');
    await context.api('/profiles', { id: existing?.id, name: name.value, peerId: peer.value, shareId: share.value,
      targetPath: target.node.value, backupPath: enabled.checked ? backup.node.value : null,
      sourceName: share.selectedOptions[0].textContent.replace(/（允许双向）$|（只读）$/, ''), mode: mode.value,
      ignorePatterns: ignored.node.value });
    document.querySelector('#modal').close();
    toast('同步任务已保存。');
    await context.refresh();
  });
  showDialog(existing ? '编辑同步任务' : '新建同步任务', [note('单向使本机与远端一致；双向首次合并独有文件，后续传播两边的修改和删除。同一文件两边都改动时需先处理冲突。'),
    field('任务名称', name), field('同步方向', mode), field('远端设备', peer), field('远端源文件夹', share), field('本机目标文件夹', target.row),
    ignored.field,
    el('label', { class: 'checkbox-row' }, [enabled, '启用同步前完整备份']), backupField,
    el('small', {}, '被覆盖和删除的文件保留在各自设备的版本库。双向任务的撤销会恢复两端。')],
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
