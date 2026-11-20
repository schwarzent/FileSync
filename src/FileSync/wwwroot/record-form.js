import { el, button, field, input, select, showDialog, note, toast } from './ui.js';
import { folderInput, ignoreInput, confirmAction } from './forms.js';

export function recordForm(context) {
  const name = input('', { required: true, maxLength: 80, placeholder: '例如 工作资料' });
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
  const dialog = showDialog('添加资料文件夹', [
    note('只保存清单与方案，资料保留在原电脑。两端选择同一个 OneDrive 记录文件夹，或通过局域网加入同一仓库。'),
    field('名称', name), field('本机资料文件夹', workspace.row), field('加入方式', peer), remoteField,
    field('清单与方案保存位置（可选）', folder.row, '选 OneDrive 子文件夹可跨电脑交换；留空则保存在本机程序数据目录。此处只写入记录，不上传资料。'), ignored.field
  ], [button('添加文件夹', context.act(async () => {
    if (!name.reportValidity() || !workspace.node.reportValidity()) return;
    if (remote.disabled) throw new Error('请等待远端仓库列表读取完成。');
    await context.api('/record-repositories', { name: name.value, workspacePath: workspace.node.value,
      repositoryId: remote.value || null, repositoryPath: folder.node.value || null,
      peerId: peer.value || null, ignorePatterns: ignored.node.value });
    dialog.close(); await context.refresh(); toast('已添加。点击“扫描并更新清单”记录本机文件。');
  }), { class: 'primary' })]);
}

export function exchangeRecords(context, repository) {
  const peers = context.getState().settings.peers;
  if (!peers.length) throw new Error('请先在局域网设备页面配对另一台电脑。');
  const peer = select(peers.map(p => ({ value: p.id, label: p.name })), repository.peerId);
  const dialog = showDialog('局域网交换清单', [note('两端先加入同一仓库，并各自扫描生成清单。此处只交换已保存清单，不扫描资料、不传输文件内容。'),
    field('已配对设备', peer)], [button('交换清单', context.act(async () => {
    const result = await context.api(`/record-repositories/${repository.id}/exchange`, { peerId: peer.value });
    dialog.close(); await context.refresh(); toast(result.message);
  }), { class: 'primary' })]);
}

export function removeRecordRepository(context, repository) {
  confirmAction('移除记录仓库', '移除本机配置，保留资料文件和已有记录文件。',
    () => context.api(`/record-repositories/${repository.id}`, null, 'DELETE'), context);
}
