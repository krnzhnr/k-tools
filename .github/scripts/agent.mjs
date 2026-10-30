// Shared runner for opencode GitHub workflows (k-tools).
// Modes: triage | ondemand | review
// Env: GH_TOKEN, OPENCODE_AUTH_JSON, MODEL, GITHUB_* (actions context)
const { spawnSync, execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const MODE = process.argv[2];
const REPO = process.env.GITHUB_REPOSITORY || 'krnzhnr/k-tools';
const MODEL = process.env.MODEL || 'opencode-go/deepseek-v4.1-flash';
const MARKERS = { triage: '<!-- ktools-triage -->', ondemand: '<!-- ktools-oc -->', review: '<!-- ktools-review -->' };

function ghJson(args) {
  return JSON.parse(execFileSync('gh', ['api', ...args], { encoding: 'utf8' }));
}
function ghPostComment(num, body) {
  execFileSync('gh', ['api', `repos/${REPO}/issues/${num}/comments`, '-X', 'POST', '-f', `body=${body}`], { encoding: 'utf8' });
}
function ghPatchComment(id, body) {
  execFileSync('gh', ['api', `repos/${REPO}/issues/comments/${id}`, '-X', 'PATCH', '-f', `body=${body}`], { encoding: 'utf8' });
}

function setupAuth() {
  const raw = process.env.OPENCODE_AUTH_JSON || '';
  if (!raw.trim()) {
    console.log('NOTICE: OPENCODE_AUTH_JSON secret is empty — agent step skipped.');
    process.exit(0);
  }
  const dir = path.join(os.homedir(), '.local', 'share', 'opencode');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'auth.json'), raw, { mode: 0o600 });
}

function runAgent(promptFile, instruction) {
  const r = spawnSync('opencode', ['run', '--auto', '-m', MODEL, '-f', promptFile, instruction],
    { encoding: 'utf8', timeout: 280000, maxBuffer: 16 * 1024 * 1024 });
  if (r.error) {
    console.log('Agent spawn failed: ' + String(r.error.message || r.error).slice(0, 300));
    return '';
  }
  if (r.status !== 0) console.log('Agent exit ' + r.status + ', stderr: ' + String(r.stderr || '').slice(0, 500));
  return String(r.stdout || '').trim();
}

function findMarker(num, marker) {
  const comments = ghJson([`repos/${REPO}/issues/${num}/comments?per_page=100`]);
  return comments.find(c => (c.body || '').includes(marker)) || null;
}

function postOrUpdate(num, marker, text, footer) {
  const body = `${marker}\n${text}\n\n_${footer}_`;
  const mine = findMarker(num, marker);
  if (mine) ghPatchComment(mine.id, body);
  else ghPostComment(num, body);
}

function cap(s, n) {
  s = String(s || '').trim();
  return s.length > n ? s.slice(0, n) + '\n\n_(ответ обрезан по длине)_' : s;
}

function readTemplate(name) {
  return fs.readFileSync(path.join('.github', 'opencode', name + '.md'), 'utf8');
}
function writePrompt(name, content) {
  const p = path.join(os.tmpdir(), `ktools-${name}-prompt.md`);
  fs.writeFileSync(p, content);
  return p;
}

async function main() {
  setupAuth();
  const ev = JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));

  if (MODE === 'triage') {
    const issue = ev.issue;
    const full = ghJson([`repos/${REPO}/issues/${issue.number}`]);
    const labels = (full.labels || []).map(l => l.name).join(', ');
    const prompt = readTemplate('triage')
      + `\n\n---\n\n# Issue #${full.number}: ${full.title}\nЯрлыки: ${labels || '(нет)'}\n\n${full.body || '(пустое тело)'}\n`;
    const out = runAgent(writePrompt('triage', prompt), 'Выполни инструкцию из приложенного файла и верни только текст результата.');
    if (!out) { console.log('Empty agent output, no comment.'); return; }
    postOrUpdate(full.number, MARKERS.triage, cap(out, 2500), `Авто-триаж: opencode (${MODEL}). Коммент обновляется при перепроверке.`);
    console.log('Triage comment posted/updated on #' + full.number);
  } else if (MODE === 'ondemand') {
    const num = ev.issue.number;
    const req = (ev.comment.body || '').replace(/\/opencode|\/oc/g, '').trim() || 'Помоги с этим ишью.';
    const full = ghJson([`repos/${REPO}/issues/${num}`]);
    const prompt = readTemplate('ondemand')
      + `\n\n---\n\n# Просьба:\n${req}\n\n# Контекст (ишью/PR #${num}):\nЗаголовок: ${full.title}\nТело:\n${full.body || '(пустое)'}\n`;
    const out = runAgent(writePrompt('ondemand', prompt), 'Выполни просьбу из приложенного файла.');
    if (!out) { console.log('Empty agent output, no comment.'); return; }
    postOrUpdate(num, MARKERS.ondemand, cap(out, 5000), `Выполнено opencode (${MODEL}) по команде.`);
    console.log('On-demand reply posted on #' + num);
  } else if (MODE === 'review') {
    const num = ev.pull_request.number;
    const prompt = readTemplate('review') + `\n\n---\n\nНомер PR: ${num}\nЗаголовок: ${ev.pull_request.title}\n`;
    const out = runAgent(writePrompt('review', prompt), 'Выполни инструкцию из приложенного файла и верни только текст ревью.');
    if (!out) { console.log('Empty agent output, no comment.'); return; }
    postOrUpdate(num, MARKERS.review, cap(out, 4000), `Код-ревью: opencode (${MODEL}). Обновляется при новых коммитах.`);
    console.log('Review posted/updated on PR #' + num);
  } else {
    console.log('Unknown mode: ' + MODE);
    process.exit(1);
  }
}

main().catch(e => { console.log('Runner failed: ' + String((e && e.message) || e).slice(0, 300)); });
