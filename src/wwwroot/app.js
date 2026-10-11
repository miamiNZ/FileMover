'use strict';

// ── State ────────────────────────────────────────────────────────
let connection     = null;
let srcFiles       = [];
let _opStart       = null;
let _timeTimer     = null;
let _copying       = false;
let _sshAuth       = { src: false, dst: false };
let _dstFreeSpace  = -1;
let _dstFreeTimer  = null;

// ── Access token ─────────────────────────────────────────────────
// The server prints a URL containing ?token=… on startup. Keep it for this tab only and
// strip it from the address bar so it doesn't linger in history or get shared by accident.
function getAccessToken() {
  const params = new URLSearchParams(location.search);
  const fromUrl = params.get('token');
  if (fromUrl) {
    try { sessionStorage.setItem('fmToken', fromUrl); } catch { /* storage unavailable */ }
    params.delete('token');
    const qs = params.toString();
    history.replaceState(null, '', location.pathname + (qs ? '?' + qs : '') + location.hash);
    return fromUrl;
  }
  try { return sessionStorage.getItem('fmToken') || ''; } catch { return ''; }
}

const _accessToken = getAccessToken();

// ── SignalR connection ───────────────────────────────────────────
function buildConnection() {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/copyhub', { accessTokenFactory: () => _accessToken })
    .withAutomaticReconnect()
    .build();

  connection.on('Log', ({ text, color }) => appendLog(text, color));

  connection.on('Progress', p => {
    setBar('pbOverall',  'txtOverallPct',  p.overallPct);
    setBar('pbCurrent',  'txtCurrentPct',  p.currentPct);
    setText('txtCurrentFile', p.currentFileName || '—');
    setText('txtSpeed',  p.speed  || '—');
    setText('txtETA',    p.eta    || '—');
    setText('txtFiles',  p.files  || '—');
    setText('txtBytes',  p.bytes  || '—');
  });

  connection.on('StateChanged', state => {
    if (state === 'copying') {
      _opStart = Date.now();
      _timeTimer = setInterval(tickTime, 500);
      setUiState('copying');
    } else {
      clearInterval(_timeTimer);
      _timeTimer = null;
      setUiState('ready');
    }
  });

  connection.on('Busy', () => {
    appendLog('[' + now() + '] Server busy — another operation in progress.', '#ffc53d');
  });

  connection.onreconnecting(() => setText('txtStatus', 'Reconnecting…'));
  connection.onreconnected(() => { setText('txtStatus', 'Ready'); refreshStartButton(); });
  connection.onclose(()     => { setText('txtStatus', 'Disconnected'); refreshStartButton(); });

  connection.start()
    .then(() => { setText('txtStatus', 'Ready'); refreshStartButton(); })
    .catch(err => {
      setText('txtStatus', 'Connection failed');
      appendLog('[' + now() + '] SignalR connect error: ' + err, '#f44747');
      if (!_accessToken)
        appendLog('[' + now() + '] No access token — open the URL printed in the FileMover console (it ends with ?token=…).', '#ffc53d');
    });
}

// ── UI state helpers ─────────────────────────────────────────────
function setUiState(state) {
  const copying = state === 'copying';
  _copying = copying;
  if (copying) document.getElementById('btnStart').disabled = true;
  else         refreshStartButton();
  document.getElementById('btnCancel').disabled = !copying;
  setText('txtStatus', copying ? 'Copying…' : 'Ready');
  if (!copying) resetProgress();
}

function resetProgress() {
  setBar('pbOverall', 'txtOverallPct', 0);
  setBar('pbCurrent', 'txtCurrentPct', 0);
  setText('txtCurrentFile', '—');
  setText('txtSpeed',  '—');
  setText('txtETA',    '—');
  setText('txtTime',   '—');
  setText('txtFiles',  '—');
  setText('txtBytes',  '—');
}

function setBar(barId, pctId, pct) {
  const v = Math.min(100, Math.max(0, pct || 0));
  document.getElementById(barId).style.width = v + '%';
  setText(pctId, v.toFixed(0) + '%');
}

function setText(id, val) {
  const el = document.getElementById(id);
  if (el) el.textContent = val;
}

function appendLog(text, color) {
  const list = document.getElementById('logList');
  const div  = document.createElement('div');
  div.className   = 'log-entry';
  div.style.color = color || 'var(--text)';
  div.textContent = text;
  list.appendChild(div);
  list.scrollTop  = list.scrollHeight;
}

function tickTime() {
  if (!_opStart) return;
  const ms  = Date.now() - _opStart;
  const s   = Math.floor(ms / 1000);
  const h   = Math.floor(s / 3600);
  const m   = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  setText('txtTime', pad(h) + ':' + pad(m) + ':' + pad(sec));
}

function pad(n) { return String(n).padStart(2, '0'); }
function now()  { return new Date().toTimeString().slice(0, 8); }

function formatBytes(b) {
  if (b >= 1073741824) return (b / 1073741824).toFixed(2) + ' GB';
  if (b >= 1048576)    return (b / 1048576).toFixed(2) + ' MB';
  if (b >= 1024)       return (b / 1024).toFixed(2) + ' KB';
  return b + ' B';
}

// ── Destination free space ───────────────────────────────────────

function clearDstFreeSpace() {
  _dstFreeSpace = -1;
  const el  = document.getElementById('txtDstFreeSpace');
  const row = document.getElementById('dstFreeSpaceRow');
  if (el)  { el.textContent = '—'; el.className = 'free-space-text'; }
  if (row) row.classList.add('hidden');
}

function scheduleDstFreeSpace() {
  clearTimeout(_dstFreeTimer);
  _dstFreeTimer = setTimeout(refreshDestFreeSpace, 600);
}

async function refreshDestFreeSpace() {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
  const dstType = selVal('dstType');
  const path    = (dstType === 'SSH' ? val('dstSshPath') : val('dstPath')).trim();
  const el  = document.getElementById('txtDstFreeSpace');
  const row = document.getElementById('dstFreeSpaceRow');

  if (!path || (dstType === 'SSH' && !_sshAuth.dst)) {
    clearDstFreeSpace();
    return;
  }

  if (row) row.classList.remove('hidden');
  if (el)  { el.textContent = 'Checking…'; el.className = 'free-space-text'; }

  try {
    const endpoint = buildEndpoint('dst');
    const bytes = await connection.invoke('GetDestFreeSpace', endpoint, path);
    _dstFreeSpace = bytes;
    if (el) {
      if (bytes < 0) {
        el.textContent = 'Unknown';
        el.className = 'free-space-text';
      } else {
        el.textContent = formatBytes(bytes) + ' available';
        el.className = 'free-space-text free-space-ok';
      }
    }
  } catch {
    _dstFreeSpace = -1;
    if (el) { el.textContent = 'Error'; el.className = 'free-space-text free-space-err'; }
  }
}

// ── Mode / type change handlers ──────────────────────────────────
function onModeChange() {
  syncSrcRows();
  refreshStartButton();
}

function onSrcTypeChange() {
  const ssh = selVal('srcType') === 'SSH';
  document.getElementById('srcSSHPanel').classList.toggle('hidden', !ssh);
  if (ssh) resetSshAuth('src');
  syncSrcRows();
  refreshStartButton();
}

function onDstTypeChange() {
  const ssh = selVal('dstType') === 'SSH';
  document.getElementById('dstSSHPanel').classList.toggle('hidden', !ssh);
  if (ssh) resetSshAuth('dst');
  syncDstRows();
  refreshStartButton();
  scheduleDstFreeSpace();
}

function resetSshAuth(side) {
  _sshAuth[side] = false;
  const S = side === 'src' ? 'Src' : 'Dst';
  const pathEl   = document.getElementById(side + 'SshPath');
  const browseEl = document.getElementById('btn' + S + 'SshBrowse');
  const statusEl = document.getElementById(side + 'ConnectStatus');
  if (pathEl)   { pathEl.disabled = true; pathEl.placeholder = 'Connect first, then enter remote path…'; }
  if (browseEl)   browseEl.disabled = true;
  if (statusEl) { statusEl.textContent = ''; statusEl.className = 'ssh-status'; statusEl.title = ''; }
  if (side === 'dst') clearDstFreeSpace();
  refreshStartButton();
}

async function sshConnect(side) {
  if (!connection) { alert('Not connected to server.'); return; }
  const S = side === 'src' ? 'Src' : 'Dst';
  const btn      = document.getElementById('btn' + S + 'Connect');
  const statusEl = document.getElementById(side + 'ConnectStatus');

  btn.disabled = true;
  statusEl.textContent = 'Connecting…';
  statusEl.className = 'ssh-status';
  statusEl.title = '';

  const useKey = selVal(side + 'Auth') === 'key';
  const dto = {
    type: 'SSH', path: '',
    host:        val(side + 'Host'),
    port:        parseInt(val(side + 'Port') || '22', 10),
    username:    val(side + 'User'),
    password:    useKey ? '' : val(side + 'Pwd'),
    useKeyFile:  useKey,
    keyFilePath: useKey ? val(side + 'Key') : '',
  };

  try {
    const result = await connection.invoke('TestSshConnection', dto);
    _sshAuth[side] = result.success;
    if (result.success) {
      statusEl.textContent = '✓ Connected';
      statusEl.className   = 'ssh-status ssh-ok';
      const pathEl   = document.getElementById(side + 'SshPath');
      const browseEl = document.getElementById('btn' + S + 'SshBrowse');
      pathEl.disabled   = false;
      pathEl.placeholder = 'Enter remote path…';
      browseEl.disabled = false;
      if (side === 'dst') scheduleDstFreeSpace();
    } else {
      statusEl.textContent = '✗ ' + (result.error || 'Failed');
      statusEl.className   = 'ssh-status ssh-error';
      statusEl.title       = result.error || '';
    }
  } catch (err) {
    _sshAuth[side] = false;
    statusEl.textContent = '✗ Connection error';
    statusEl.className   = 'ssh-status ssh-error';
    statusEl.title       = String(err);
  } finally {
    btn.disabled = false;
    refreshStartButton();
  }
}

function syncSrcRows() {
  const multi = selVal('mode') === 'MultipleFiles';
  const ssh   = selVal('srcType') === 'SSH';
  document.getElementById('srcLocalRow').classList.toggle('hidden', multi || ssh);
  document.getElementById('srcSshRow').classList.toggle('hidden',   multi || !ssh);
  document.getElementById('srcMultiPanel').classList.toggle('hidden', !multi);
}

function syncDstRows() {
  const ssh = selVal('dstType') === 'SSH';
  document.getElementById('dstLocalRow').classList.toggle('hidden',  ssh);
  document.getElementById('dstSshRow').classList.toggle('hidden', !ssh);
}

function onSrcAuthChange() {
  const key = selVal('srcAuth') === 'key';
  document.getElementById('srcPwd').disabled = key;
  document.getElementById('srcKey').disabled = !key;
  resetSshAuth('src');
}

function onDstAuthChange() {
  const key = selVal('dstAuth') === 'key';
  document.getElementById('dstPwd').disabled = key;
  document.getElementById('dstKey').disabled = !key;
  resetSshAuth('dst');
}

function selVal(name) {
  const el = document.querySelector(`input[name="${name}"]:checked`);
  return el ? el.value : null;
}

// ── Multi-file list ───────────────────────────────────────────────
function addSrcFile() {
  const input = document.getElementById('srcAddInput');
  const path  = input.value.trim();
  if (!path) return;
  if (!srcFiles.includes(path)) {
    srcFiles.push(path);
    renderSrcFiles();
    refreshStartButton();
  }
  input.value = '';
  input.focus();
}

function removeSrcFiles() {
  const items = document.querySelectorAll('#srcFileList li.selected');
  const paths = Array.from(items).map(li => li.dataset.path);
  srcFiles = srcFiles.filter(f => !paths.includes(f));
  renderSrcFiles();
  refreshStartButton();
}

function clearSrcFiles() {
  srcFiles = [];
  renderSrcFiles();
  refreshStartButton();
}

function renderSrcFiles() {
  const ul = document.getElementById('srcFileList');
  ul.innerHTML = '';
  srcFiles.forEach(path => {
    const li = document.createElement('li');
    li.textContent = path;
    li.dataset.path = path;
    li.addEventListener('click', () => li.classList.toggle('selected'));
    ul.appendChild(li);
  });
}

// Allow Enter to add file
document.addEventListener('DOMContentLoaded', () => {
  const input = document.getElementById('srcAddInput');
  if (input) input.addEventListener('keydown', e => { if (e.key === 'Enter') addSrcFile(); });

  ['srcPath', 'srcSshPath', 'dstPath', 'dstSshPath', 'srcHost', 'srcUser', 'dstHost', 'dstUser'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', refreshStartButton);
  });

  ['dstPath', 'dstSshPath'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', scheduleDstFreeSpace);
  });

  ['srcHost', 'srcPort', 'srcUser', 'srcPwd', 'srcKey'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', () => resetSshAuth('src'));
  });
  ['dstHost', 'dstPort', 'dstUser', 'dstPwd', 'dstKey'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', () => resetSshAuth('dst'));
  });

  refreshStartButton();
});

// ── Theme toggle ─────────────────────────────────────────────────
function toggleTheme() {
  const root    = document.documentElement;
  const isDark  = root.getAttribute('data-theme') === 'dark';
  root.setAttribute('data-theme', isDark ? 'light' : 'dark');
  document.getElementById('themeBtn').textContent = isDark ? 'Dark Mode' : 'Light Mode';
}

// ── Operation / Start button ─────────────────────────────────────
function onOperationChange() {
  const op  = selVal('operation');
  const btn = document.getElementById('btnStart');
  if (btn) btn.innerHTML = op === 'Move' ? '&#x25B6; Start Move' : '&#x25B6; Start Copy';
}

function canStart() {
  if (!connection) return false;
  if (typeof signalR === 'undefined' || connection.state !== signalR.HubConnectionState.Connected) return false;
  const mode    = selVal('mode');
  const srcType = selVal('srcType');
  const dstType = selVal('dstType');
  const isMulti = mode === 'MultipleFiles';
  if (isMulti && srcFiles.length === 0) return false;
  if (!isMulti && !val(srcType === 'SSH' ? 'srcSshPath' : 'srcPath').trim()) return false;
  if (!val(dstType === 'SSH' ? 'dstSshPath' : 'dstPath').trim()) return false;
  if (srcType === 'SSH' && !_sshAuth.src) return false;
  if (dstType === 'SSH' && !_sshAuth.dst) return false;
  return true;
}

function refreshStartButton() {
  if (_copying) return;
  const el = document.getElementById('btnStart');
  if (el) el.disabled = !canStart();
}

// ── Validate & build job ─────────────────────────────────────────
function validate() {
  const mode    = selVal('mode');
  const srcType = selVal('srcType');
  const dstType = selVal('dstType');
  const isMulti = mode === 'MultipleFiles';

  if (isMulti && srcFiles.length === 0) {
    alert('No source files added.');
    return null;
  }
  if (!isMulti && !val(srcType === 'SSH' ? 'srcSshPath' : 'srcPath').trim()) {
    alert('Source path is required.');
    return null;
  }
  if (!val(dstType === 'SSH' ? 'dstSshPath' : 'dstPath').trim()) {
    alert('Destination path is required.');
    return null;
  }
  if (srcType === 'SSH') {
    if (!val('srcHost').trim()) { alert('Source SSH host required.'); return null; }
    if (!val('srcUser').trim()) { alert('Source SSH username required.'); return null; }
  }
  if (dstType === 'SSH') {
    if (!val('dstHost').trim()) { alert('Destination SSH host required.'); return null; }
    if (!val('dstUser').trim()) { alert('Destination SSH username required.'); return null; }
  }

  return {
    source: buildEndpoint('src'),
    destination: buildEndpoint('dst'),
    mode,
    sourceFiles: isMulti ? [...srcFiles] : [],
    operationMode: selVal('operation'),
    verifyFiles: chk('chkVerify'),
    overwriteExisting: chk('chkOverwrite'),
    recursive: chk('chkRecursive'),
    maxThreads: parseInt(document.getElementById('sldThreads').value, 10),
    setPermissions: chk('chkPerms'),
    permissionMode: val('txtPermMode').trim() || '644',
  };
}

function buildEndpoint(prefix) {
  const type   = selVal(prefix + 'Type');
  const useKey = selVal(prefix + 'Auth') === 'key';
  const path   = type === 'SSH' ? val(prefix + 'SshPath') : val(prefix + 'Path');
  return {
    type,
    path:        path.trim(),
    host:        val(prefix + 'Host').trim(),
    port:        parseInt(val(prefix + 'Port') || '22', 10),
    username:    val(prefix + 'User').trim(),
    password:    useKey ? '' : val(prefix + 'Pwd'),
    useKeyFile:  useKey,
    keyFilePath: useKey ? val(prefix + 'Key').trim() : '',
  };
}

function val(id)  { const el = document.getElementById(id); return el ? el.value : ''; }
function chk(id)  { const el = document.getElementById(id); return el ? el.checked : false; }

// ── Start / Cancel ───────────────────────────────────────────────
async function startCopy() {
  if (!connection) {
    appendLog('[' + now() + '] Not connected to server.', '#f44747');
    return;
  }
  const job = validate();
  if (!job) return;

  if (_dstFreeSpace > 0) {
    try {
      const srcBytes = await connection.invoke('GetSourceSize', job);
      if (srcBytes > 0 && srcBytes > _dstFreeSpace) {
        const ok = confirm(
          'Warning: Source size (' + formatBytes(srcBytes) + ') exceeds destination free space (' +
          formatBytes(_dstFreeSpace) + ').\n\nProceed anyway?'
        );
        if (!ok) return;
      }
    } catch { /* skip check on error */ }
  }

  resetProgress();
  try {
    await connection.invoke('StartCopy', job);
  } catch (err) {
    appendLog('[' + now() + '] Error starting copy: ' + err, '#f44747');
  }
}

async function cancelCopy() {
  if (!connection) return;
  try {
    await connection.invoke('Cancel');
  } catch (err) {
    appendLog('[' + now() + '] Error cancelling: ' + err, '#f44747');
  }
}

// ── Bootstrap ────────────────────────────────────────────────────
buildConnection();

// ── Draggable browse modal ────────────────────────────────────────
{
  let _dd = null, _ox = 0, _oy = 0;

  document.addEventListener('mousedown', e => {
    if (!e.target.closest('.modal-header') || e.target.closest('.btn-icon')) return;
    const dialog = document.querySelector('.modal-dialog');
    const r = dialog.getBoundingClientRect();
    dialog.style.cssText += ';position:fixed;left:' + r.left + 'px;top:' + r.top + 'px;margin:0';
    _dd = dialog; _ox = e.clientX - r.left; _oy = e.clientY - r.top;
    e.preventDefault();
  }, true);

  document.addEventListener('mousemove', e => {
    if (!_dd) return;
    const x = Math.max(0, Math.min(window.innerWidth  - _dd.offsetWidth,  e.clientX - _ox));
    const y = Math.max(0, Math.min(window.innerHeight - _dd.offsetHeight, e.clientY - _oy));
    _dd.style.left = x + 'px';
    _dd.style.top  = y + 'px';
  });

  document.addEventListener('mouseup', () => { _dd = null; });
}

// ── File browser ─────────────────────────────────────────────────
let _browseTarget   = null;  // 'srcPath' | 'dstPath' | 'srcMulti'
let _browseSide     = null;  // 'src' | 'dst'
let _browseIsSSH    = false;
let _browseSshDto   = null;
let _browseCurPath  = '';
let _browseParent   = null;
let _browseSelected = null;

function openBrowse(target, side) {
  if (!connection) { alert('Not connected to server.'); return; }

  const isSSH   = selVal(side + 'Type') === 'SSH';
  const isMulti = target === 'srcMulti';

  if (isSSH && !_sshAuth[side]) {
    alert('SSH not authenticated. Click Connect in the SSH settings first.');
    return;
  }

  _browseTarget   = target;
  _browseSide     = side;
  _browseIsSSH    = isSSH;
  _browseSelected = null;

  if (isSSH) {
    const useKey = selVal(side + 'Auth') === 'key';
    _browseSshDto = {
      type: 'SSH', path: '',
      host:        val(side + 'Host'),
      port:        parseInt(val(side + 'Port') || '22', 10),
      username:    val(side + 'User'),
      password:    useKey ? '' : val(side + 'Pwd'),
      useKeyFile:  useKey,
      keyFilePath: useKey ? val(side + 'Key') : '',
    };
    _browseCurPath = '/';
  } else {
    _browseSshDto  = null;
    _browseCurPath = (isMulti ? val('srcPath') : val(target)).trim() || '';
  }

  document.getElementById('browseTitle').textContent = isMulti ? 'Add Files' : 'Browse';
  document.getElementById('btnBrowseOk').textContent = isMulti ? 'Add to List' : 'Select';
  document.getElementById('btnBrowseFolder').style.display = isMulti ? 'none' : '';

  const dlg = document.querySelector('.modal-dialog');
  dlg.style.position = dlg.style.left = dlg.style.top = dlg.style.margin = '';

  document.getElementById('browseModal').classList.remove('hidden');
  browseNavigate(_browseCurPath);
}

function closeBrowse() {
  document.getElementById('browseModal').classList.add('hidden');
  _browseTarget = null;
}

function onModalOverlayClick(e) {
  if (e.target === document.getElementById('browseModal')) closeBrowse();
}

function browseGoToPath() {
  const p = document.getElementById('browseCurPath').value.trim();
  if (p) browseNavigate(p);
}

async function browseNavigate(path) {
  _browseCurPath  = path;
  _browseSelected = null;
  document.getElementById('browseCurPath').value = path || '';
  document.getElementById('btnBrowseOk').disabled = true;
  document.getElementById('browseSelText').textContent = '';

  const list = document.getElementById('browseList');
  list.innerHTML = '<div class="browse-loading">Loading…</div>';

  try {
    const result = _browseIsSSH
      ? await connection.invoke('BrowseSsh',   _browseSshDto, path)
      : await connection.invoke('BrowseLocal', path);
    _browseParent = result.parentPath || null;
    renderBrowseEntries(result);
  } catch (err) {
    list.innerHTML = `<div class="browse-error">Error: ${escHtml(String(err))}</div>`;
  }
}

function renderBrowseEntries(result) {
  document.getElementById('browseCurPath').value = result.currentPath || '';

  const list = document.getElementById('browseList');
  list.innerHTML = '';

  if (result.error) {
    list.innerHTML = `<div class="browse-error">${escHtml(result.error)}</div>`;
    return;
  }
  if (!result.entries || result.entries.length === 0) {
    list.innerHTML = '<div class="browse-empty">Empty folder</div>';
    return;
  }

  result.entries.forEach(entry => {
    const div = document.createElement('div');
    div.className = 'browse-entry ' + (entry.isDirectory ? 'is-dir' : 'is-file');
    div.innerHTML =
      `<span class="browse-icon">${entry.isDirectory ? '📁' : '📄'}</span>` +
      `<span class="browse-name">${escHtml(entry.name)}</span>`;

    div.addEventListener('click', () => {
      document.querySelectorAll('.browse-entry.selected').forEach(e => e.classList.remove('selected'));
      div.classList.add('selected');
      _browseSelected = entry;
      document.getElementById('btnBrowseOk').disabled = false;
      document.getElementById('browseSelText').textContent = entry.fullPath;
    });

    div.addEventListener('dblclick', () => {
      if (entry.isDirectory) browseNavigate(entry.fullPath);
      else browseOk();
    });

    list.appendChild(div);
  });
}

function browseUp() {
  if (_browseParent != null) browseNavigate(_browseParent);
  else if (_browseCurPath && _browseCurPath !== '/') browseNavigate('/');
}

function browseSelectFolder() {
  if (_browseTarget && _browseTarget !== 'srcMulti')
    document.getElementById(_browseTarget).value = _browseCurPath;
  closeBrowse();
}

function browseOk() {
  if (!_browseSelected) return;
  if (_browseTarget === 'srcMulti') {
    if (!srcFiles.includes(_browseSelected.fullPath)) {
      srcFiles.push(_browseSelected.fullPath);
      renderSrcFiles();
    }
    document.querySelectorAll('.browse-entry.selected').forEach(e => e.classList.remove('selected'));
    _browseSelected = null;
    document.getElementById('btnBrowseOk').disabled = true;
    document.getElementById('browseSelText').textContent = '';
  } else {
    if (_browseTarget) document.getElementById(_browseTarget).value = _browseSelected.fullPath;
    closeBrowse();
  }
}

function escHtml(s) {
  return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}
