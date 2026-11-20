import { el, button, input, field, showDialog, note, date, toast } from './ui.js';
import { changesList } from './views.js';
import { showPreview } from './preview.js';

const endpoint = repository => `/record-repositories/${repository.id}/plans`;

export async function previewRecordPlan(context, repository, selection) {
  const { comparison, choices } = selection;
  const name = input(`${repository.name} · 同步方案`, { required: true, maxLength: 80 });
  const request = () => ({ name: name.value, deviceId: comparison.other.deviceId,
    localRecordId: comparison.local.recordId, otherRecordId: comparison.other.recordId, choices });
  const details = await context.api(`${endpoint(repository)}/preview`, request());
  const dialog = showDialog('预览并保存方案', [field('方案名称', name),
    note('保存只写入方案记录。局域网连接后再次检查文件，再执行下面列出的修改。'), ...planBody(details)], [
    button('关闭预览', () => dialog.close()), button('保存方案', context.act(async () => {
      if (!name.reportValidity()) return;
      const saved = await context.api(endpoint(repository), request());
      toast('方案已保存，未修改资料。');
      showSavedPlan(context, repository, saved);
    }), { class: 'primary', icon: 'check' })
  ]);
}

export async function recordPlans(context, repository) {
  const plans = await context.api(endpoint(repository));
  const rows = plans.map(plan => el('article', { class: 'repository-change' }, [
    el('h3', {}, plan.name), el('p', {}, `${date(plan.createdUtc)} · ${plan.choices.length} 个路径`),
    el('p', {}, `${plan.local.device.deviceName} ↔ ${plan.other.device.deviceName}`),
    button('查看方案', context.act(async () => showSavedPlan(context, repository,
      await context.api(`${endpoint(repository)}/${plan.id}`))), { icon: 'file' })
  ]));
  showDialog(`${repository.name} · 已保存方案`, [note('方案保存在记录目录，可通过 OneDrive 交换。查看不扫描资料，也不需要对方在线。'),
    ...(rows.length ? rows : [el('p', {}, '尚无方案。先比较清单，再选择要处理的差异。')])],
  [button('关闭', () => document.querySelector('#modal').close())]);
}

function planBody(details) {
  const plan = details.plan;
  const names = new Map([plan.local, plan.other].map(side => [side.device.deviceId, side.device.deviceName]));
  return [el('p', {}, `方案包含 ${plan.choices.length} 个路径，其余路径保留。缺少的父目录已列入。`),
    ...[plan.local, plan.other].map(side => el('p', {}, `${side.device.deviceName} 清单：${date(side.device.recordedUtc)}`)),
    el('details', {}, [el('summary', {}, '查看每个路径采用哪端'), el('div', { class: 'plan-paths' },
      plan.choices.map(choice => el('p', { class: 'path' }, `${choice.path} ← ${names.get(choice.sourceDeviceId)}`)))]),
    el('h3', {}, `${plan.local.device.deviceName} 将发生的修改`), changesList(details.localChanges),
    el('h3', {}, `${plan.other.device.deviceName} 将发生的修改`), changesList(details.otherChanges)];
}

function showSavedPlan(context, repository, details) {
  const plan = details.plan;
  const ownId = context.getState().deviceId;
  const other = [plan.local, plan.other].find(side => side.device.deviceId !== ownId)?.device;
  const involved = [plan.local, plan.other].some(side => side.device.deviceId === ownId);
  showDialog(plan.name, [note('只传输并修改方案列出的项目。选定文件变化时停止；目录中新增的未选文件会保留。'),
    ...planBody(details)], [button('关闭', () => document.querySelector('#modal').close()),
    button('连接局域网并检查方案', context.act(async () => {
      const prepared = await context.api(`${endpoint(repository)}/${plan.id}/prepare`, { peerId: other.deviceId });
      showPreview(context, prepared, async ready => {
        await context.api(`/plans/${ready.id}/execute`, {});
        toast('方案执行完成，其他文件保留。');
        await context.refresh();
      });
    }), { class: 'primary', icon: 'devices', disabled: !involved || context.getState().busy })]);
}
