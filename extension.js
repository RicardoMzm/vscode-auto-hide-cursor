const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const path = require('path');

let child = null;
let quitting = false;
let lastHideAt = 0;
let lastShowAt = 0;
let extensionPath = '';
let logging = false;

function cfg() {
    return vscode.workspace.getConfiguration('autoHideCursor');
}

function isEnabled() {
    return cfg().get('enabled', true);
}

function clamp(value, min, max) {
    const number = Number(value);
    if (!isFinite(number)) {
        return min;
    }
    return Math.min(max, Math.max(min, number));
}

function log() {
    if (!logging) {
        return;
    }
    const parts = Array.prototype.slice.call(arguments).map(function (value) {
        return typeof value === 'string' ? value : String(value);
    });
    console.log('[auto-hide-cursor] ' + parts.join(' '));
}

function helperPath() {
    return path.join(extensionPath, 'helper', 'AutoHideCursorHelper.exe');
}

function spawnRestore() {
    if (quitting) {
        return;
    }
    try {
        const proc = cp.spawn(helperPath(), ['--restore'], {
            windowsHide: true,
            stdio: 'ignore'
        });
        proc.unref();
    } catch (err) {
        log('restore spawn failed', err && err.message);
    }
}

function ensureHelper() {
    if (child && child.exitCode === null && !child.killed) {
        return child;
    }

    const exePath = helperPath();
    if (!fs.existsSync(exePath)) {
        log('helper not found:', exePath);
        return null;
    }

    const threshold = clamp(cfg().get('movementThreshold', 3), 1, 50);
    const poll = clamp(cfg().get('pollInterval', 20), 10, 200);
    const exeBase64 = Buffer.from(process.execPath, 'utf8').toString('base64');

    let proc;
    try {
        proc = cp.spawn(exePath, [
            String(process.pid),
            exeBase64,
            String(threshold),
            String(poll)
        ], {
            stdio: ['pipe', 'ignore', 'pipe'],
            windowsHide: true
        });
    } catch (err) {
        log('helper spawn failed', err && err.message);
        return null;
    }

    child = proc;
    log('helper started pid=' + proc.pid);
    if (proc.stdin) {
        proc.stdin.on('error', function (err) {
            log('helper stdin error', err && err.message);
        });
    }


    proc.on('error', function (err) {
        log('helper error', err && err.message);
        if (child === proc) {
            child = null;
        }
    });

    proc.on('exit', function (code, signal) {
        log('helper exited code=' + code + ' signal=' + signal);
        if (child === proc) {
            child = null;
        }
        spawnRestore();
    });

    if (proc.stderr) {
        proc.stderr.on('data', function (data) {
            log('helper stderr', String(data).trim());
        });
        proc.stderr.on('error', function (err) {
            log('helper stderr error', err && err.message);
        });
    }

    return proc;
}

function send(command) {
    const proc = ensureHelper();
    if (!proc) {
        return false;
    }
    try {
        if (proc.stdin && proc.stdin.writable) {
            proc.stdin.write(command + '\n');
            return true;
        }
    } catch (err) {
        log('helper write failed', err && err.message);
    }
    return false;
}

function hideCursor() {
    if (!isEnabled()) {
        return;
    }
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
        return;
    }
    const now = Date.now();
    if (now - lastHideAt < 50) {
        return;
    }
    lastHideAt = now;
    log('hide');
    send('hide');
}

function showCursor() {
    const now = Date.now();
    if (now - lastShowAt < 50) {
        return;
    }
    lastShowAt = now;
    if (child) {
        log('show');
        send('show');
    }
}

function stopHelper() {
    if (!child) {
        return;
    }
    const proc = child;
    child = null;
    try {
        if (proc.stdin && proc.stdin.writable) {
            proc.stdin.write('quit\n');
        }
    } catch (err) {
        log('helper quit write failed', err && err.message);
    }
    setTimeout(function () {
        try {
            proc.kill();
        } catch (err) {
        }
        spawnRestore();
    }, 500);
}

function activate(context) {
    if (process.platform !== 'win32') {
        return;
    }

    extensionPath = context.extensionPath;
    logging = cfg().get('debug', false);

    context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(function (event) {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document !== event.document) {
            return;
        }
        if (!event.contentChanges || event.contentChanges.length === 0) {
            return;
        }
        hideCursor();
    }));

    context.subscriptions.push(vscode.window.onDidChangeTextEditorSelection(function (event) {
        if (event.kind !== vscode.TextEditorSelectionChangeKind.Keyboard) {
            return;
        }
        if (event.textEditor !== vscode.window.activeTextEditor) {
            return;
        }
        hideCursor();
    }));

    context.subscriptions.push(vscode.window.onDidChangeWindowState(function (state) {
        if (!state.active) {
            showCursor();
        }
    }));

    context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(function (editor) {
        if (!editor) {
            showCursor();
        }
    }));

    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(function (event) {
        if (!event.affectsConfiguration('autoHideCursor')) {
            return;
        }
        logging = cfg().get('debug', false);
        showCursor();
        stopHelper();
    }));

    context.subscriptions.push(vscode.commands.registerCommand('autoHideCursor.toggle', async function () {
        const next = !isEnabled();
        await cfg().update('enabled', next, vscode.ConfigurationTarget.Global);
        vscode.window.setStatusBarMessage(
            next ? '$(eye) 自动隐藏鼠标已开启' : '$(eye-closed) 自动隐藏鼠标已关闭',
            2500
        );
        if (!next) {
            showCursor();
            spawnRestore();
        }
    }));

    context.subscriptions.push(vscode.commands.registerCommand('autoHideCursor.show', function () {
        showCursor();
    }));

    context.subscriptions.push(vscode.commands.registerCommand('autoHideCursor.restoreSystemCursor', function () {
        showCursor();
        spawnRestore();
        vscode.window.setStatusBarMessage('$(check) 系统光标已恢复', 2000);
    }));

    context.subscriptions.push({
        dispose: function () {
            quitting = true;
            showCursor();
            if (child) {
                try {
                    child.stdin.write('quit\n');
                } catch (err) {
                }
                try {
                    child.kill();
                } catch (err) {
                }
                child = null;
            }
        }
    });
}

function deactivate() {
    quitting = true;
    if (child) {
        try {
            child.stdin.write('quit\n');
        } catch (err) {
        }
        try {
            child.kill();
        } catch (err) {
        }
        child = null;
    }
}

module.exports = {
    activate: activate,
    deactivate: deactivate
};
