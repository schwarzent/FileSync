import { el, icon, button, heading, empty, input, field, bytes, date, note } from './ui.js';
import { profileForm, shareForm, pairingForm, confirmAction } from './forms.js';

function card(title, body, action, subtitle) {
  return el('section', { class: 'card' }, [el('div', { class: 'card-heading' }, [el('div', {}, [el('h2', {}, title), subtitle ? el('small', {}, subtitle) : null]), action]), body]);
}

function stat(name, value, symbol, unit = '') {
  return el('div', { class: 'stat' }, [el('div', { class: 'stat-icon' }, icon(symbol)), el('div', {}, [el('small', {}, name),
    el('strong', { class: symbol === 'clock' ? 'date-stat' : '' }, value), unit ? el('span', { class: 'unit' }, unit) : null])]);
}

function profileRow(profile, context) {
  const state = context.getState();
  const peer = state.settings.peers.find(p => p.id === profile.peerId);
  return el('article', { class: 'profile' }, [el('div', { class: 'profile-title' }, [el('h3', {}, profile.name),
    el('div', { class: 'profile-actions' }, [button('', context.act(() => profileForm(context, profile)), { icon: 'edit', class: 'icon-button', title: '编辑任务' }),
      button('', () => confirmAction('移除同步任务', '移除后保留已有文件和版本记录。', () => context.api(`/profiles/${profile.id}`, null, 'DELETE'), context), { icon: 'trash', class: 'icon-button', title: '移除任务' })])]),
    el('div', { class: 'route' }, [el('div', {}, [el('small', {}, '远端源文件夹'), el('strong', {}, peer?.name || '设备已移除'), el('div', { class: 'path' }, `源 ID：${profile.shareId.slice(0, 8)}`)]),
      icon('arrow'), el('div', {}, [el('small', {}, '本机目标文件夹'), el('strong', {}, state.settings.name), el('div', { class: 'path' }, profile.targetPath)])]),
    el('div', { class: 'profile-bottom' }, [el('small', {}, profile.backupPath ? '完整备份已启用' : '版本记录已启用'),
      button('预览同步', context.act(() => context.preview(profile.id)), { icon: 'sync', class: 'primary', disabled: state.busy })])]);
}

function sourceList(context) {
  const shares = context.getState().settings.shares;
  return card('本机源文件夹', shares.length ? el('div', {}, shares.map(share => el('div', { class: 'source-row' }, [icon('folder'),
    el('div', {}, [el('h3', {}, share.name), el('div', { class: 'path' }, share.path)]),
    button('', () => confirmAction('取消发布文件夹', '配对设备将无法继续读取此源文件夹，本机文件保留。', () => context.api(`/shares/${share.id}`, null, 'DELETE'), context), { icon: 'close', class: 'icon-button', title: '取消发布' })]))) :
    el('div', { class: 'source-empty' }, '发布一个本机文件夹，供配对设备手动同步。'),
  button('', () => shareForm(context), { icon: 'plus', class: 'icon-button', title: '发布源文件夹' }));
}

function localCard(context) {
  const state = context.getState();
  const details = [['局域网地址', state.addresses.join(' / ') || '未检测到'], ['传输端口', String(state.port)], ['设备指纹', `${state.deviceId.slice(0, 8)} · ${state.deviceId.slice(-8)}`]];
  return card('当前设备', el('div', { class: 'card-body' }, [el('div', { class: 'device-local' }, [el('div', {}, icon('laptop')),
    el('div', {}, [el('h3', {}, state.settings.name), el('p', {}, 'Windows · 本机')])]), ...details.map(([label, value]) =>
    el('div', { class: 'detail-line' }, [el('span', {}, label), el('b', {}, value)]))]), el('span', { class: 'pill' }, '本机'));
}

export function syncView(context) {
  const state = context.getState();
  const profiles = state.settings.profiles;
  const latest = context.history.find(r => r.status === 'completed');
  return [heading('文件同步', '连接你的设备，让每一份文件保持最新。', button('新建同步任务', context.act(() => profileForm(context)), { class: 'primary', icon: 'plus' })),
    el('div', { class: 'stats' }, [stat('已配对设备', String(state.settings.peers.length).padStart(2, '0'), 'devices', '台设备'),
      stat('同步任务', String(profiles.length).padStart(2, '0'), 'folder', '个任务'), stat('最近一次同步', latest ? date(latest.createdUtc) : '尚未同步', 'clock')]),
    el('div', { class: 'dashboard-grid' }, [el('div', { class: 'stack' }, [card('我的同步任务', el('div', {}, [profiles.length ? el('div', {}, profiles.map(p => profileRow(p, context))) :
      empty('从一个文件夹开始', '配对局域网设备，选择来源与目标文件夹。每次同步前，都可以先查看文件变化。', button('创建第一个任务', context.act(() => profileForm(context)), { icon: 'plus', class: 'primary' })),
      el('div', { class: 'hint-strip' }, [icon('clock'), '手动同步 · 仅在你点击执行时更改文件'])]), el('span', { class: 'count' }, `${profiles.length} 个任务`)),
    el('div', { class: 'bottom-note' }, [icon('history'), '每次修改都有记录，被覆盖和删除的文件可从版本记录中恢复。'])]),
    el('div', { class: 'stack' }, [localCard(context), sourceList(context), el('section', { class: 'card security-card' }, [el('h3', {}, [icon('shield'), '安全地连接，安心地同步']),
      el('p', {}, 'TLS 加密传输，配对证书验证设备身份。文件留在你的设备之间，无需云端账号。')])])])];
}

export function devicesView(context) {
  const state = context.getState();
  const paired = state.settings.peers;
  const found = state.discovered.filter(d => !paired.some(p => p.id === d.id));
  const rows = paired.map(peer => el('article', { class: 'card device-card' }, [icon('laptop'), el('h3', {}, peer.name),
    el('p', {}, `${state.discovered.find(d => d.id === peer.id)?.address || peer.address} : ${peer.port}`), el('span', { class: 'pill' }, '已配对'),
    el('div', { class: 'fingerprint' }, peer.id), button('撤销配对', () => confirmAction('撤销设备配对', '撤销后立即停止此设备读取本机文件，并移除关联同步任务。已有文件和版本保留。',
      () => context.api(`/peers/${peer.id}`, null, 'DELETE'), context), { icon: 'close', class: 'text' })]));
  return [heading('局域网设备', '与你的 Windows 设备建立安全连接。', button('配对设备', () => pairingForm(context), { icon: 'plus', class: 'primary' })),
    state.discoveryError ? note(state.discoveryError) : null, !state.discoveryEnabled ? note('自动发现已通过启动参数关闭。可使用配对邀请连接。') : null,
    el('h2', { class: 'section-title' }, `已配对设备 · ${paired.length}`), paired.length ? el('div', { class: 'grid-devices' }, rows) :
      el('div', { class: 'card' }, empty('尚未配对设备', '在另一台电脑上运行 FileSync，使用配对邀请建立连接。', button('配对设备', () => pairingForm(context), { class: 'primary', icon: 'devices' }), 'devices')),
    el('h2', { class: 'section-title section-gap' }, ['发现的设备', button('刷新', context.act(context.refresh), { icon: 'refresh', class: 'text' })]),
    found.length ? el('div', { class: 'grid-devices' }, found.map(device => el('article', { class: 'card device-card' }, [icon('laptop'), el('h3', {}, device.name), el('p', {}, `${device.address}:${device.port}`),
      button('通过邀请配对', () => pairingForm(context), { class: 'text', icon: 'devices' })]))) : note('暂未发现其他设备。请确认两台电脑在同一局域网，并允许 FileSync 通过 Windows 专用网络防火墙。', 'network'),
    el('div', { class: 'section-gap' }, note('自动发现仅提供设备位置。配对通过一次性邀请与证书指纹验证身份。'))];
}

export const statusLabels = { completed: '已完成', reverted: '已撤销', failed: '失败', staging: '传输中断', writing: '等待恢复', 'recovery-needed': '需要恢复', reverting: '撤销中断' };

export function historyView(context) {
  const rows = context.history.map(revision => el('article', { class: 'revision' }, [icon('history'), el('div', {}, [el('h3', {}, revision.profileName),
    el('p', {}, `${date(revision.createdUtc)} · ${revision.changes.length} 项变化`), revision.error ? el('div', { class: 'revision-error' }, revision.error) : null,
    revision.backupPath ? el('div', { class: 'path' }, `完整备份：${revision.backupPath}`) : null]),
    el('div', { class: 'revision-actions' }, [el('span', { class: `pill ${revision.status}` }, statusLabels[revision.status] || revision.status),
      button('详情', () => context.revisionDetails(revision), { class: 'text' }),
      revision.status === 'completed' ? button('撤销', () => confirmAction('撤销这次同步', '恢复同步前的文件和目录时间。同步后的本机修改会阻止撤销。', () => context.api(`/history/${revision.id}/undo`, {}), context), { class: 'text', icon: 'history' }) : null,
      ['staging', 'writing', 'recovery-needed', 'reverting'].includes(revision.status) ? button('恢复', () => confirmAction('恢复中断的同步', '将目标文件夹恢复至本次同步前的状态。', () => context.api(`/history/${revision.id}/recover`, {}), context), { class: 'danger' }) : null]) ]));
  return [heading('版本记录', '查看每次同步的变化，恢复需要保留的文件。', button('刷新', context.act(context.refresh), { icon: 'refresh' })),
    el('section', { class: 'card' }, [el('div', { class: 'card-heading' }, [el('h2', {}, '同步历史'), el('span', { class: 'count' }, `${rows.length} 条记录`)]),
      rows.length ? el('div', {}, rows) : empty('每次变化，都有迹可循', '完成第一次同步后，文件变化与恢复记录将显示在这里。', null, 'history')]),
    el('div', { class: 'section-gap' }, note('可逐次撤销同一任务的同步。版本内容与可选完整备份均保留在磁盘，不会自动清理。', 'backup'))];
}

export function settingsView(context) {
  const state = context.getState();
  const name = input(state.settings.name, { required: true, maxLength: 80 });
  return [heading('设置', '管理本机名称与文件共享。'), el('div', { class: 'stack' }, [el('section', { class: 'card form-card' }, [el('h2', {}, '本机设备'),
    el('div', { class: 'section-gap' }, field('设备名称', name)), button('保存名称', context.act(async () => { if (name.reportValidity()) { await context.api('/name', { name: name.value }); await context.refresh(); } }), { class: 'primary', icon: 'check' }),
    el('div', { class: 'section-gap' }, [el('small', {}, '程序数据与版本记录'), el('p', { class: 'path' }, state.dataPath)])]), sourceList(context),
    el('div', { class: 'notice' }, [icon('lock'), el('span', {}, '同步仅手动执行。设备身份由 Windows 当前用户加密保护；请在两端分别撤销不再使用的配对。')])])];
}

const changeLabels = { add: '新增', update: '更新', delete: '删除', replace: '替换', metadata: '时间' };
export function changesList(changes) {
  const pageSize = 100;
  let page = 0;
  const container = el('div');
  const render = () => {
    const items = changes.slice(page * pageSize, (page + 1) * pageSize);
    container.replaceChildren(...items.map(change => el('div', { class: 'change-row' }, [icon((change.after || change.before).directory ? 'folder' : 'file'),
      el('div', {}, change.path), el('span', { class: `change-badge ${change.kind}` }, changeLabels[change.kind]),
      el('small', {}, (change.after || change.before).directory ? '文件夹' : bytes((change.after || change.before).length))])));
    if (changes.length > pageSize) container.append(el('div', { class: 'pagination' }, [
      button('上一页', () => { page--; render(); }, { disabled: page === 0 }), `${page + 1} / ${Math.ceil(changes.length / pageSize)}`,
      button('下一页', () => { page++; render(); }, { disabled: (page + 1) * pageSize >= changes.length })]));
  };
  render();
  return container;
}
