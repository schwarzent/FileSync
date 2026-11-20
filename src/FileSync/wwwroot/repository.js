import { el, button, showDialog, note, date, bytes, input } from './ui.js';

const labels = { add: '新增', update: '修改', delete: '删除', replace: '替换', metadata: '时间修改' };
const pageSize = 20;

export async function repositoryHistory(context, profile) {
  const result = await context.api(`/profiles/${profile.id}/repository`);
  const search = input('', { type: 'search', placeholder: '按文件路径筛选提交', 'aria-label': '按文件路径筛选提交' });
  const rows = el('div', { class: 'repository-history' });
  let page = 0;
  const render = () => {
    const commits = result.commits.filter(c => !search.value || c.changes.some(change => change.path.includes(search.value)));
    const visible = commits.slice(page * pageSize, (page + 1) * pageSize);
    rows.replaceChildren(...visible.map(commit => el('article', { class: 'repository-commit' }, [
      el('h3', {}, `#${commit.sequence} · ${commit.publisher}`),
      el('p', {}, `${date(commit.uploadedUtc)} · ${commit.changes.length} 项变化`),
      el('small', { class: 'path' }, commit.id),
      button('查看文件修改', context.act(() => commitDetails(context, profile, commit)), { icon: 'history' })
    ])));
    if (!visible.length) rows.append(el('p', {}, '没有符合条件的提交记录。'));
    if (commits.length > pageSize) rows.append(el('div', { class: 'pagination' }, [
      button('上一页', () => { page--; render(); }, { disabled: page === 0 }),
      `${page + 1} / ${Math.ceil(commits.length / pageSize)}`,
      button('下一页', () => { page++; render(); }, { disabled: (page + 1) * pageSize >= commits.length })
    ]));
  };
  search.addEventListener('input', () => { page = 0; render(); });
  render();
  const recover = result.interrupted ? button('继续完成中继写入', context.act(async () => {
    await context.api(`/profiles/${profile.id}/repository/recover`, {});
    await repositoryHistory(context, profile);
    await context.refresh();
  }), { class: 'primary' }) : null;
  showDialog(`${profile.name} · 仓库历史`, [
    note('查看 OneDrive 文件中继的提交记录及历史内容。提交时间是写入本机中继的时间，文件原始时间单独保留。'),
    result.interrupted ? note('检测到尚未完成的发布。先等待 OneDrive 同步；确认发布程序中断后再恢复发布。') : null,
    el('p', { class: 'path' }, profile.relayPath), search, rows
  ].filter(Boolean), [recover, button('刷新', context.act(() => repositoryHistory(context, profile))),
    button('关闭', () => document.querySelector('#modal').close())].filter(Boolean));
}

function commitDetails(context, profile, commit) {
  const container = el('div');
  let page = 0;
  const render = () => {
    const start = page * pageSize;
    container.replaceChildren(...commit.changes.slice(start, start + pageSize).map((change, offset) =>
      el('article', { class: 'repository-change' }, [
        el('h3', {}, `${labels[change.kind]} · ${change.path}`),
        fileMetadata(change.after || change.before),
        button('查看修改前后', context.act(() => compare(context, profile, commit, start + offset)), { icon: 'file' })
      ])));
    if (!commit.changes.length) container.append(el('p', {}, '本次提交没有文件变化。'));
    if (commit.changes.length > pageSize) container.append(el('div', { class: 'pagination' }, [
      button('上一页', () => { page--; render(); }, { disabled: page === 0 }),
      `${page + 1} / ${Math.ceil(commit.changes.length / pageSize)}`,
      button('下一页', () => { page++; render(); }, { disabled: (page + 1) * pageSize >= commit.changes.length })
    ]));
  };
  render();
  showDialog(`提交 #${commit.sequence}`, [el('h3', {}, commit.publisher),
    el('p', {}, `上传时间：${commit.uploadedUtc}`), el('p', { class: 'path' }, commit.id), container],
  [button('返回仓库历史', context.act(() => repositoryHistory(context, profile)))]);
}

function fileMetadata(file) {
  if (!file) return el('p', {}, '不存在');
  return el('dl', { class: 'repository-metadata' }, [
    ['类型／大小', file.directory ? '文件夹' : bytes(file.length)],
    ['创建时间（UTC）', file.createdUtc], ['修改时间（UTC）', file.modifiedUtc], ['SHA-256', file.hash]
  ].flatMap(([label, value]) => [el('dt', {}, label), el('dd', {}, value || '无')]));
}

async function compare(context, profile, commit, index) {
  const result = await context.api(`/profiles/${profile.id}/repository/${commit.id}/changes/${index}`);
  const version = (label, file, content) => el('section', {}, [el('h3', {}, label), fileMetadata(file),
    content?.kind === 'text' ? el('pre', { class: 'repository-text', tabIndex: 0 }, content.text) :
      el('p', {}, content?.kind === 'binary' ? '二进制内容：以上哈希和大小记录此版本，可正常拉取。' : content ? '文件夹元数据' : '此版本没有该文件')]);
  showDialog(`${labels[result.change.kind]} · ${result.change.path}`, [
    el('div', { class: 'repository-comparison' }, [
      version('修改前', result.change.before, result.before), version('修改后', result.change.after, result.after)
    ])
  ], [button('返回提交', () => commitDetails(context, profile, commit))]);
}
