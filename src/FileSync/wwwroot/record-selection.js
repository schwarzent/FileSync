import { el, button, select, input } from './ui.js';

function selectionState(result) {
  const values = new Map();
  const checked = new Set();
  const controls = new Map();
  const checks = new Map();
  const summary = el('p', { role: 'status' });
  const options = [
    { value: '', label: '暂不处理' },
    { value: 'local', label: `采用 ${result.local.deviceName}` },
    { value: 'other', label: `采用 ${result.other.deviceName}` }
  ];
  const update = () => { summary.textContent = `已选择 ${values.size} 项；其余差异暂不处理。`; };
  const choose = (path, value) => {
    if (value) values.set(path, value); else values.delete(path);
    for (const control of controls.get(path) || []) control.value = value;
  };
  const apply = value => {
    for (const path of checked) choose(path, value);
    update();
  };
  const latest = () => {
    for (const row of result.differences) {
      if (!row.newerSide) continue;
      checked.add(row.path);
      choose(row.path, row.newerSide);
      for (const check of checks.get(row.path) || []) check.checked = true;
    }
    update();
  };
  const all = checkedValue => {
    checked.clear();
    if (checkedValue) for (const row of result.differences) checked.add(row.path);
    for (const [path, nodes] of checks) for (const node of nodes) node.checked = checked.has(path);
  };
  update();
  return { values, checked, controls, checks, summary, options, update, apply, all, latest };
}

export function recordSelection(result) {
  const { values, checked, controls, checks, summary, options, update, apply, all, latest } = selectionState(result);
  return {
    values: () => Object.fromEntries(values),
    toolbar: el('div', { class: 'selection-toolbar' }, [
      button('勾选并采用较新版本', latest, { class: 'primary', disabled: !result.differences.some(row => row.newerSide),
        title: '对所有分页中两端都有的同名文件，勾选并采用修改时间较新的一端。时间相同的冲突保留手动选择。' }),
      button('勾选全部差异', () => all(true)), button('清除勾选', () => all(false)),
      button(`勾选项采用 ${result.local.deviceName}`, () => apply('local')),
      button(`勾选项采用 ${result.other.deviceName}`, () => apply('other')),
      button('勾选项暂不处理', () => apply('')), summary
    ]),
    control(row) {
      const check = input('', { type: 'checkbox', checked: checked.has(row.path), 'aria-label': `勾选 ${row.path}` });
      check.addEventListener('change', () => { if (check.checked) checked.add(row.path); else checked.delete(row.path); });
      checks.set(row.path, [check]);
      const choice = select(options, values.get(row.path));
      choice.setAttribute('aria-label', `${row.path} 的处理方式`);
      choice.addEventListener('change', () => {
        if (choice.value) values.set(row.path, choice.value); else values.delete(row.path);
        update();
      });
      controls.set(row.path, [choice]);
      const deletion = !row.local || !row.other;
      return el('div', { class: 'selection-control' }, [check, choice,
        el('small', {}, row.local?.directory || row.other?.directory ? '目录选择包含记录中的子项。' :
          deletion ? '采用不存在的一端会删除另一端此项。' : '仅处理此路径。')]);
    }
  };
}
