const paths = {
  sync: 'M4 7h15l-4-4M20 17H5l4 4M20 7v5M4 17v-5',
  devices: 'M3 4h14v11H3zM7 19h6M10 15v4M20 9h3v12h-7v-3',
  history: 'M3 11a9 9 0 1 1 2 7M3 4v7h7M12 7v5l3 2',
  settings: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M9 2h6l1 3 3 1 3 4-2 2 1 3-3 4-3-1-3 2-4-2-1-3-3-1v-5l3-1z',
  plus: 'M12 5v14M5 12h14', folder: 'M3 6h7l2 2h9v12H3z',
  arrow: 'M4 12h16l-5-5M20 12l-5 5', lock: 'M6 10h12v11H6zM8 10V6a4 4 0 0 1 8 0v4',
  check: 'M5 12l4 4L19 6', close: 'M6 6l12 12M18 6L6 18',
  refresh: 'M20 4v6h-6M20 10a8 8 0 1 0 0 6', download: 'M12 3v12l-4-4M12 15l4-4M4 17v4h16v-4',
  laptop: 'M5 4h14v12H5zM2 20h20l-3-4H5z', more: 'M5 12h.01M12 12h.01M19 12h.01',
  shield: 'M12 2l8 3v7c0 5-8 10-8 10S4 17 4 12V5zM8 12l3 3 5-6',
  edit: 'M4 16L16 4l4 4L8 20H4zM13 7l4 4', trash: 'M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7',
  clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 7v5l3 2',
  network: 'M12 5v7M5 19v-7h14v7M9 2h6v6H9zM2 18h6v4H2zM16 18h6v4h-6z',
  file: 'M5 2h9l5 5v15H5zM14 2v6h5M8 12h8M8 16h6', backup: 'M4 7h16v14H4zM2 3h20v4H2zM9 11h6'
};

export function icon(name, className = '') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', class: `icon ${className}` })) svg.setAttribute(key, value);
  const path = document.createElementNS(svg.namespaceURI, 'path');
  path.setAttribute('d', paths[name] || paths.file);
  svg.append(path);
  return svg;
}

export function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'class') node.className = value;
    else if (key.startsWith('aria-') || key.startsWith('data-') || key === 'role') node.setAttribute(key, value);
    else node[key] = value;
  }
  for (const child of [children].flat()) if (child !== null && child !== undefined) node.append(child);
  return node;
}

export function button(label, action, options = {}) {
  return el('button', { type: 'button', class: `button ${options.class || ''}`, onclick: action,
    title: options.title || label, 'aria-label': options.title || label, disabled: options.disabled || false },
  [options.icon ? icon(options.icon) : null, label ? el('span', {}, label) : null]);
}

export function heading(title, subtitle, action) {
  return el('div', { class: 'page-heading' }, [el('div', {}, [el('div', { class: 'eyebrow' }, 'LOCAL NETWORK · 本地连接'), el('h1', {}, title), el('p', {}, subtitle)]), action]);
}

export function empty(title, description, action, name = 'folder') {
  return el('div', { class: 'empty-state' }, [el('div', { class: 'empty-icon' }, icon(name)), el('h3', {}, title), el('p', {}, description), action]);
}

export function field(label, input, hint) {
  return el('label', { class: 'field' }, [el('span', {}, label), input, hint ? el('small', {}, hint) : null]);
}

export function input(value = '', props = {}) { return el('input', { value, ...props }); }

export function select(items, value) {
  const node = el('select', {}, items.map(item => el('option', { value: item.value }, item.label)));
  if (value) node.value = value;
  return node;
}

export function showDialog(title, content, actions = []) {
  const dialog = document.querySelector('#modal');
  if (dialog.open) dialog.close();
  dialog.replaceChildren(el('div', { class: 'dialog-heading' }, [el('h2', { id: 'modal-title' }, title),
    button('', () => dialog.close(), { icon: 'close', class: 'icon-button', title: '关闭窗口' })]),
  el('div', { class: 'dialog-body' }, content), el('div', { class: 'dialog-actions' }, actions));
  dialog.showModal();
  return dialog;
}

let toastTimer;
export function toast(message, error = false) {
  const node = document.querySelector('#toast');
  clearTimeout(toastTimer);
  node.textContent = message;
  node.className = error ? 'toast-error' : '';
  node.hidden = false;
  toastTimer = setTimeout(() => { node.hidden = true; }, error ? 12000 : 4500);
}

export function bytes(value) {
  if (!value) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toLocaleString('zh-CN', { maximumFractionDigits: 1 })} ${units[index]}`;
}

export function date(value) { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '尚未同步'; }

export function note(text, name = 'shield') { return el('div', { class: 'notice' }, [icon(name), el('span', {}, text)]); }
