/* TxAgent / web / recipe-sidebar.js
 *
 * 配方侧边栏。把已经跑稳的脚本固化成"选对象 → 点执行"的离线工具，
 * 不经过模型、不花 token。
 *
 * ── 与宿主的约定 ──
 *   web → host : window.chrome.webview.postMessage(JSON字符串)
 *   host → web : 调用 window.txRecipes.onHostMessage(对象)
 *
 * 所有消息都带 type 字段。请求带 seq，响应原样带回，
 * 这样迟到的响应不会盖掉新的界面状态 —— 点两次"取选择"时尤其重要。
 *
 * ── 这里刻意不做的事 ──
 *   1. 不在前端缓存对象 Id 去做"智能恢复"。绑定过期就显示过期，让用户重选。
 *      拿一个可能已经指向别的对象的 Id 去执行，是这个功能最坏的失败方式。
 *   2. 不在前端判断参数够不够就直接跑。够不够由宿主再校验一次 ——
 *      前端只负责把按钮置灰，不负责保证。
 */
(function () {
    'use strict';

    var seq = 0;
    var pending = {};          // seq -> 回调
    var _pickTimers = {};      // "recipeId:param" -> 取选择超时定时器
    var _runTimers = {};       // recipeId -> 执行超时定时器
    var state = {
        recipes: [],           // 配方列表
        candidates: [],        // 可固化为配方的片段
        study: null,           // 当前 study 名，换 study 时绑定全部作废
        bindings: {},          // recipeId -> { paramName: {id,name,type,study} }
        open: {},              // recipeId -> 是否展开
        running: {},           // recipeId -> 是否执行中
        picking: {},           // "recipeId:param" -> 是否正在取选择
        pickError: {}          // "recipeId:param" -> 最近一次取选择的错误文本
    };

    var root, bodyEl;

    // ── 与宿主通信 ──

    function send(type, payload, cb) {
        var msg = payload || {};
        msg.type = type;
        msg.seq = ++seq;
        if (cb) pending[msg.seq] = cb;
        try {
            window.chrome.webview.postMessage(JSON.stringify(msg));
        } catch (e) {
            // 宿主不在（比如浏览器里单独打开调试）时不要静默 ——
            // 界面会一直转圈，看不出是通信断了还是宿主卡住。
            delete pending[msg.seq];
            if (cb) cb({ ok: false, error: '未连接到 Process Simulate 宿主。' });
        }
    }

    function onHostMessage(msg) {
        if (!msg) return;
        if (msg.seq && pending[msg.seq]) {
            var cb = pending[msg.seq];
            delete pending[msg.seq];
            cb(msg);
            return;
        }
        // 迟到的执行结果:超时兜底已把界面解锁,但宿主可能还在跑、结果刚到。
        // 靠 type 兜底投递,把真实结果补写到卡片上 —— 不能让"执行超时"冤枉宿主。
        if (msg.type === 'recipe.run.result' && msg.recipeId) {
            var rid = msg.recipeId;
            state.running[rid] = false;
            flash(rid, !!msg.ok, msg.text || msg.error || (msg.ok ? '执行完成。' : '执行失败。'));
            state.open[rid] = true;
            render();
            send('recipe.list', {}, function (r2) {
                if (r2 && r2.ok !== false) {
                    state.recipes = r2.recipes || state.recipes;
                    state.candidates = r2.candidates || state.candidates;
                    render();
                }
            });
            return;
        }
        // 无 seq 的是宿主主动推送
        if (msg.type === 'recipe.changed') refresh();
        else if (msg.type === 'recipe.studyChanged') {
            // 换 study：所有绑定作废。不尝试按名字重新解析 ——
            // 同名对象在一个 study 里都可能有多个，跨 study 猜就是纯赌。
            state.study = msg.study || null;
            state.bindings = {};
            render();
            refresh();
        }
    }

    // ── 数据 ──

    function refresh() {
        send('recipe.list', {}, function (r) {
            if (!r || r.ok === false) { renderError(r && r.error); return; }
            state.recipes = r.recipes || [];
            state.candidates = r.candidates || [];
            state.study = r.study || null;
            render();
        });
    }

    function bindingOf(recipeId, paramName) {
        var b = state.bindings[recipeId];
        if (!b) return null;
        var v = b[paramName];
        if (!v) return null;
        // study 对不上就当没绑
        if (state.study && v.study && v.study !== state.study) {
            return { stale: true, name: v.name };
        }
        return v;
    }

    function setBinding(recipeId, paramName, val) {
        if (!state.bindings[recipeId]) state.bindings[recipeId] = {};
        if (val === null) delete state.bindings[recipeId][paramName];
        else {
            val.study = state.study;
            state.bindings[recipeId][paramName] = val;
        }
    }

    function readyToRun(rec) {
        var ps = rec.params || [];
        for (var i = 0; i < ps.length; i++) {
            var p = ps[i];
            if (!p.required) continue;
            if (isObjectKind(p.kind)) {
                var b = bindingOf(rec.id, p.name);
                if (!b || b.stale) return false;
            } else {
                var b2 = bindingOf(rec.id, p.name);
                if (!b2 || b2.value === '' || b2.value === undefined) return false;
            }
        }
        return true;
    }

    function isObjectKind(kind) { return kind === 'object' || kind === 'objects'; }

    // 任一配方在执行中(执行/取选择期间冻结其它按钮,避免并行占用 PS)
    function anyRecipeRunning() {
        for (var k in state.running) if (state.running[k]) return true;
        return false;
    }

    // ── 渲染 ──

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function render() {
        if (!bodyEl) return;
        bodyEl.innerHTML = '';

        if (!state.recipes.length && !state.candidates.length) {
            var em = el('div', 'rcp-empty');
            em.appendChild(el('div', null, '还没有配方。'));
            em.appendChild(el('div', null,
                '让 AI 把跑通的脚本用 save_recipe 固化，之后就能在这里选对象直接执行，不用再问模型。'));
            bodyEl.appendChild(em);
            return;
        }

        state.recipes.forEach(function (rec) { bodyEl.appendChild(renderCard(rec)); });

        if (state.candidates.length) {
            bodyEl.appendChild(el('div', 'rcp-section-title', '可固化为配方'));
            state.candidates.forEach(function (c) { bodyEl.appendChild(renderCandidate(c)); });
        }
    }

    function renderError(text) {
        if (!bodyEl) return;
        bodyEl.innerHTML = '';
        var em = el('div', 'rcp-empty', text || '加载配方失败。');
        bodyEl.appendChild(em);
    }

    function renderCard(rec) {
        var card = el('div', 'rcp-card' + (state.open[rec.id] ? ' rcp-open' : ''));

        // 头
        var head = el('div', 'rcp-card-head');
        head.appendChild(el('span', 'rcp-caret', state.open[rec.id] ? '▼' : '▶'));
        head.appendChild(el('span', 'rcp-name', rec.name));
        head.appendChild(el('span', 'rcp-lang', rec.lang === 'python' ? 'PY' : 'C#'));
        head.onclick = function () {
            state.open[rec.id] = !state.open[rec.id];
            render();
        };
        card.appendChild(head);

        // 体
        var body = el('div', 'rcp-card-body');
        if (rec.description) body.appendChild(el('p', 'rcp-desc', rec.description));

        (rec.params || []).forEach(function (p) {
            body.appendChild(renderParam(rec, p));
        });

        // 底部动作
        var act = el('div', 'rcp-actions');
        var running = !!state.running[rec.id];
        var runBtn = el('button', 'rcp-run' + (running ? ' rcp-running' : ''), running ? '执行中…' : '执行');
        runBtn.disabled = anyRecipeRunning() || !readyToRun(rec);
        runBtn.title = anyRecipeRunning()
            ? '已有配方在执行中，等它完成'
            : (readyToRun(rec) ? '执行' : '还有必填参数未选取');
        runBtn.onclick = function () { run(rec); };
        act.appendChild(runBtn);

        var stat = el('span', 'rcp-stat');
        if ((rec.runCount || 0) + (rec.failCount || 0) > 0) {
            var ok = el('span', 'rcp-ok', String(rec.runCount || 0));
            var bad = el('span', 'rcp-bad', String(rec.failCount || 0));
            stat.appendChild(ok);
            stat.appendChild(document.createTextNode(' / '));
            stat.appendChild(bad);
        }
        act.appendChild(stat);

        var codeBtn = el('button', 'rcp-iconbtn', '⟨⟩');
        codeBtn.title = '在对话里查看这段代码';
        codeBtn.onclick = function () { send('recipe.reveal', { recipeId: rec.id }); };
        act.appendChild(codeBtn);

        body.appendChild(act);

        // 上次执行结果
        var last = state.lastResult && state.lastResult[rec.id];
        if (last) {
            var m = el('div', 'rcp-msg ' + (last.ok ? 'rcp-msg-ok' : 'rcp-msg-bad'), last.text);
            body.appendChild(m);
        }

        card.appendChild(body);
        return card;
    }

    function renderParam(rec, p) {
        var wrap = el('div', 'rcp-param');

        var lab = el('label', 'rcp-param-label');
        lab.appendChild(document.createTextNode(p.label || p.name));
        if (p.required) lab.appendChild(el('span', 'rcp-req', '*'));
        if (p.typeHint) lab.appendChild(el('span', 'rcp-hint', '(' + p.typeHint + ')'));
        wrap.appendChild(lab);

        if (isObjectKind(p.kind)) {
            var row = el('div', 'rcp-pick');
            var b = bindingOf(rec.id, p.name);
            var key = rec.id + ':' + p.name;
            var picking = !!state.picking[key];
            var pickErr = state.pickError[key];
            var busy = anyRecipeRunning();

            var slotCls = 'rcp-slot';
            var slotText;
            if (picking) { slotCls += ' rcp-picking'; slotText = '取选择中…'; }
            else if (pickErr) { slotCls += ' rcp-pick-err'; slotText = pickErr; }
            else if (!b) { slotCls += ' rcp-unset'; slotText = '未选取'; }
            else if (b.stale) { slotCls += ' rcp-stale'; slotText = '绑定已失效（' + (b.name || '') + '）请重选'; }
            else slotText = b.name + (b.count > 1 ? '  等 ' + b.count + " 项" : '');

            var slot = el('div', slotCls, slotText);
            if (b && !b.stale && b.id) slot.title = 'Id: ' + b.id;
            else if (pickErr) slot.title = pickErr;
            row.appendChild(slot);

            var pick = el('button', 'rcp-btn', picking ? '取选中…' : '取选择');
            pick.title = '在 PS 里选中对象后点这里';
            pick.disabled = picking || busy;
            pick.onclick = function () { beginPick(rec, p); };
            row.appendChild(pick);

            if (b && !picking && !pickErr) {
                var clr = el('button', 'rcp-btn', '×');
                clr.title = '清除';
                clr.disabled = busy;
                clr.onclick = function () { setBinding(rec.id, p.name, null); render(); };
                row.appendChild(clr);
            }
            wrap.appendChild(row);
        } else {
            var cur = bindingOf(rec.id, p.name);
            var inp = el('input', 'rcp-input');
            inp.type = (p.kind === 'number') ? 'number' : 'text';
            if (p.kind === 'bool') inp.type = 'checkbox';
            inp.placeholder = p.help || '';
            if (anyRecipeRunning()) inp.disabled = true;
            if (cur && cur.value !== undefined) {
                if (p.kind === 'bool') inp.checked = !!cur.value;
                else inp.value = cur.value;
            } else if (p.def !== undefined && p.def !== null && p.def !== '') {
                if (p.kind === 'bool') inp.checked = (p.def === true || p.def === 'true');
                else inp.value = p.def;
                setBinding(rec.id, p.name, { value: p.kind === 'bool' ? inp.checked : p.def });
            }
            inp.onchange = function () {
                setBinding(rec.id, p.name, { value: p.kind === 'bool' ? inp.checked : inp.value });
                render();
            };
            wrap.appendChild(inp);
        }

        if (p.help && !isObjectKind(p.kind)) {
            // 对象类的说明已经在 title 里，这里只给标量补一行
        }
        return wrap;
    }

    function renderCandidate(c) {
        var row = el('div', 'rcp-cand');
        row.appendChild(el('span', 'rcp-name', c.name));
        var s = el('span', 'rcp-stat');
        s.appendChild(el('span', 'rcp-ok', String(c.successCount || 0)));
        s.appendChild(document.createTextNode(' 次成功'));
        row.appendChild(s);

        var btn = el('button', 'rcp-btn', '固化');
        btn.title = '让 AI 把这段片段整理成带参数的配方';
        btn.onclick = function () {
            btn.disabled = true;
            send('recipe.promote', { snippetName: c.name }, function (r) {
                btn.disabled = false;
                if (r && r.ok === false) alert(r.error || '固化失败。');
                // 固化走的是一轮对话（AI 要给参数命名、写说明），
                // 结果由宿主推 recipe.changed 回来刷新，这里不自作主张改列表。
            });
        };
        row.appendChild(btn);
        return row;
    }

    // ── 执行 ──

    function run(rec) {
        if (state.running[rec.id]) return;   // 防双击/超时后重复执行(宿主侧另有在飞标志兜底)

        var args = {};
        var b = state.bindings[rec.id] || {};
        (rec.params || []).forEach(function (p) {
            var v = b[p.name];
            if (!v) return;
            if (isObjectKind(p.kind)) { if (!v.stale) args[p.name] = v.id; }
            else args[p.name] = v.value;
        });

        state.running[rec.id] = true;
        state.open[rec.id] = true;           // 自动展开：执行中/结果直接可见，不再藏在折叠卡片里
        if (state.lastResult) delete state.lastResult[rec.id];
        render();

        send('recipe.run', { recipeId: rec.id, args: args }, function (r) {
            clearTimeout(_runTimers[rec.id]);
            delete _runTimers[rec.id];
            state.running[rec.id] = false;
            var ok = !!(r && r.ok);
            flash(rec.id, ok, (r && r.text) || (ok ? '执行完成。' : '执行失败。'));
            state.open[rec.id] = true;       // 保持展开，让结果消息留在卡片里
            render();
            // 计数由宿主那边落盘，刷一次拿最新的
            send('recipe.list', {}, function (r2) {
                if (r2 && r2.ok !== false) {
                    state.recipes = r2.recipes || state.recipes;
                    state.candidates = r2.candidates || state.candidates;
                    render();
                }
            });
        });

        // 超时兜底：正常执行完宿主必回消息；若一直没回（宿主崩溃/PS 卡死），
        // 至少要恢复按钮，不能永远停在“执行中”。
        // 【10 分钟】重配方(STL→CATIA→cscript 染色)跑 3 分钟以上是常态,180 秒会误报超时。
        // 超时不清 pending:迟到的 recipe.run.result 仍会经 seq 配对回填真实结果。
        _runTimers[rec.id] = setTimeout(function () {
            if (state.running[rec.id]) {
                delete _runTimers[rec.id];
                state.running[rec.id] = false;
                flash(rec.id, false, '已等待 10 分钟仍未返回。执行可能仍在后台进行，完成后结果会自动补显在这里；重复点击执行会被拒绝。');
                state.open[rec.id] = true;
                render();
            }
        }, 600000);
    }

    function beginPick(rec, p) {
        var key = rec.id + ':' + p.name;
        state.picking[key] = true;
        delete state.pickError[key];
        render();

        send('recipe.pickSelection',
            { recipeId: rec.id, param: p.name, multi: p.kind === 'objects' },
            function (r) {
                clearTimeout(_pickTimers[key]);
                delete _pickTimers[key];
                delete state.picking[key];
                if (!r || r.ok === false) {
                    state.pickError[key] = (r && r.error) || '取选择失败。';
                } else {
                    state.pickError[key] = null;
                    setBinding(rec.id, p.name, {
                        id: r.id, name: r.name, type: r.type, count: r.count || 1
                    });
                }
                render();
            });

        // 超时兜底：宿主没响应时也要恢复按钮并给出提示
        _pickTimers[key] = setTimeout(function () {
            if (state.picking[key]) {
                delete _pickTimers[key];
                delete state.picking[key];
                state.pickError[key] = '取选择超时：宿主未响应。请确认 Process Simulate 已连接。';
                render();
            }
        }, 5000);
    }

    function flash(recipeId, ok, text) {
        if (!state.lastResult) state.lastResult = {};
        state.lastResult[recipeId] = { ok: ok, text: text };
    }

    // ── 挂载 ──

    function mount(container) {
        root = el('div', 'rcp-root');

        var head = el('div', 'rcp-head');
        head.appendChild(el('span', 'rcp-title', '配方'));

        var reload = el('button', 'rcp-iconbtn rcp-reload', '⟳');
        reload.title = '刷新';
        reload.onclick = refresh;
        head.appendChild(reload);

        root.appendChild(head);

        bodyEl = el('div', 'rcp-body');
        root.appendChild(bodyEl);

        container.appendChild(root);

        // 展开/收起把手：独立于面板、固定在聊天区右侧垂直居中。
        // 展开/收起都在同一位置切换，避免“展开在中间、收起在左上角”的跳变。
        // 必须 append 在 root 之后，配合 .rcp-root.rcp-collapsed + .rcp-toggle 取位。
        var toggle = el('button', 'rcp-toggle', '⟩');
        toggle.title = '收起 / 展开配方栏';
        toggle.onclick = function () {
            root.classList.toggle('rcp-collapsed');
            var collapsed = root.classList.contains('rcp-collapsed');
            toggle.textContent = collapsed ? '⟨' : '⟩';
            try { localStorage.setItem('txRecipeCollapsed', collapsed ? '1' : '0'); } catch (e) { }
        };
        container.appendChild(toggle);

        try {
            if (localStorage.getItem('txRecipeCollapsed') === '1') {
                root.classList.add('rcp-collapsed');
                toggle.textContent = '⟨';
            }
        } catch (e) { }

        refresh();
    }

    window.txRecipes = {
        mount: mount,
        refresh: refresh,
        onHostMessage: onHostMessage
    };
})();
