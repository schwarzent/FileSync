import { el, button, field, input, select, showDialog, note, toast } from './ui.js';
import { folderInput, ignoreInput, confirmAction } from './forms.js';

export function recordForm(context) {
  const name = input('', { required: true, maxLength: 80, placeholder: '例如 工作资料记录' });
  const workspace = folderInput('', context);
  const folder = folderInput('', context);
  folder.node.required = false;
  const ignored = ignoreInput();
  const peer = select([{ value: '', label: '独立生成 / 本机或 OneDrive 文件夹' },
    ...context.getState().settings.peers.map(p => ({ value: p.id, label: `加入局域网仓库：${p.name}` }))]);
  const remote = select([{ value: '', label: '创建新记录仓库' }]);
  const remoteField = field('远端记录仓库', remote);
  remoteField.hidden = true;
  peer.addEventListener('change', context.act(async () => {
    remoteField.hidden = !peer.value;
    remote.disabled = Boolean(peer.value);
    remote.replaceChildren(el('option', { value: '' }, '创建新记录仓库'));
    if (!peer.value) return;
    const repositories = await context.api(`/record-repositories/peers/${peer.value}`);
    remote.append(...repositories.map(r => el('option', { value: r.repositoryId }, r.name)));
    if (repositories.length) remote.value = repositories[0].repositoryId;
    remote.disabled = false;
  }));
  const dialog = showDialog('新建文件记录仓库', [
    note('只保存文件清单和修改记录，不复制资料。另一台电脑可通过局域网加入，或选择对应的同一个 OneDrive 记录文件夹。'),
    field('仓库名称', name), field('本机资料文件夹', workspace.row), field('连接方式', peer), remoteField,
    field('记录文件夹（可选）', folder.row, '留空时只保存在本机程序数据目录。跨电脑使用 OneDrive 时，选择对应的同一云端文件夹，与资料文件夹分开。'), ignored.field
  ], [button('保存仓库', context.act(async () => {
    if (!name.reportValidity() || !workspace.node.reportValidity()) return;
    if (remote.disabled) throw new Error('请等待远端仓库列表读取完成。');
    await context.api('/record-repositories', { name: name.value, workspacePath: workspace.node.value,
      repositoryId: remote.value || null, repositoryPath: folder.node.value || null,
      peerId: peer.value || null, ignorePatterns: ignored.node.value });
    dialog.close(); await context.refresh(); toast('记录仓库已保存，可独立生成本机记录。');
  }), { class: 'primary' })]);
}

export function exchangeRecords(context, repository) {
  const peers = context.getState().settings.peers;
  if (!peers.length) throw new Error('请先在局域网设备页面配对另一台电脑。');
  const peer = select(peers.map(p => ({ value: p.id, label: p.name })), repository.peerId);
  const dialog = showDialog('交换局域网记录', [note('两端需先加入同一记录仓库并各自生成本机记录。此操作只交换清单，不传输资料。'),
    field('已配对设备', peer)], [button('交换记录', context.act(async () => {
    const result = await context.api(`/record-repositories/${repository.id}/exchange`, { peerId: peer.value });
    dialog.close(); await context.refresh(); toast(result.message);
  }), { class: 'primary' })]);
}

export function removeRecordRepository(context, repository) {
  confirmAction('移除记录仓库', '移除本机配置，保留资料文件和已有记录文件。',
    () => context.api(`/record-repositories/${repository.id}`, null, 'DELETE'), context);
}
