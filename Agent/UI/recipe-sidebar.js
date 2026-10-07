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
        activeTab: 'recipes',   // 默认展示已固化配方，候选片段独立显示
        recipes: [],           // 配方列表
        candidates: [],        // 可固化为配方的片段
        study: null,           // 当前 study 名，换 study 时绑定全部作废
        bindings: {},          // recipeId -> { paramName: {id,name,type,study} }
        open: {},              // recipeId -> 是否展开
        running: {},           // recipeId -> 是否执行中
        queries: {},          // recipeId:param -> session-only type/name conditions
        picking: {},           // "recipeId:param" -> 是否正在取选择
        pickError: {}          // "recipeId:param" -> 最近一次取选择的错误文本
    };

    var root, bodyEl, noticeEl;
    var tabButtons = {};

    function notice(ok, text) {
        if (!noticeEl) return;
        noticeEl.className = 'rcp-notice rcp-msg ' + (ok ? 'rcp-msg-ok' : 'rcp-msg-bad');
        noticeEl.textContent = text || '';
        noticeEl.hidden = !text;
    }

    function reveal(payload) {
        send('recipe.reveal', payload, function (r) {
            if (!r || r.ok === false) notice(false, (r && r.error) || '查看详情失败。');
        });
    }

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
        if (msg.type === 'recipe.sidebar.layout') {
            // 最大化或屏幕空间不足时使用覆盖式侧栏，保持聊天区原有布局宽度。
            if (root) root.classList.toggle('rcp-overlay', !!msg.overlay);
            return;
        }
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
                    state.hostBusy = !!r2.busy;
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
            if (state.study !== (r.study || null)) state.bindings = {};
            state.recipes = r.recipes || [];
            state.candidates = r.candidates || [];
            state.study = r.study || null;
            state.hostBusy = !!r.busy;
            state.savedArgs = r.savedArgs || {};
            state.schemas = state.schemas || {};
            state.recipes.forEach(function (rec) {
                var schema = JSON.stringify([(rec.params || []).map(function(p){ var copy = Object.assign({}, p); delete copy.objectTypes; delete copy.objectTypesLoaded; delete copy.typesLoading; return copy; }), rec.actions || []]);
                if (state.schemas[rec.id] && state.schemas[rec.id] !== schema) delete state.bindings[rec.id];
                state.schemas[rec.id] = schema;
            });
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

    function scalarValue(rec, p, action) {
        if (action && Object.prototype.hasOwnProperty.call(action.args || {}, p.name)) return action.args[p.name];
        var b = bindingOf(rec.id, p.name);
        if (b && b.value !== undefined) return b.value;
        var saved = (state.savedArgs || {})[rec.id] || {};
        if (saved[p.name] !== undefined) return saved[p.name];
        return p.def != null ? p.def : (p.kind === 'bool' ? false : '');
    }

    function fixedByActions(rec, p) {
        return (rec.actions || []).length > 0 && rec.actions.every(function (a) {
            return Object.prototype.hasOwnProperty.call(a.args || {}, p.name);
        });
    }

    function readyToRun(rec, action) {
        var ps = rec.params || [];
        for (var i = 0; i < ps.length; i++) {
            var p = ps[i];
            if (isObjectKind(p.kind)) {
                if (!p.required) continue;
                var b = bindingOf(rec.id, p.name);
                if (!b || b.stale) return false;
            } else {
                var value = scalarValue(rec, p, action);
                if (String(value).trim() === '') { if (p.required) return false; else continue; }
                if (p.kind === 'number' && !isFinite(Number(value))) return false;
                if (p.kind === 'color' && !/^#[0-9a-f]{6}$/i.test(String(value))) return false;
                if ((p.choices || []).length && !p.choices.some(function (c) { return c.value === String(value); })) return false;
            }
        }
        return true;
    }

    function isObjectKind(kind) { return kind === 'object' || kind === 'objects'; }

    var tagLabels = {
        robot: '机器人', label: '标注', location: '位置', transform: '变换', coordinate: '坐标',
        weld: '焊点', operation: '操作', query: '查询', selection: '选中对象', physical: '物理对象',
        mfg: '制造对象', scene: '场景', kinematics: '运动学', tcp: 'TCP', collision: '碰撞',
        export: '导出', simulation: '仿真', leadingpart: '主零件', traverse: '遍历', find: '查找',
        create: '创建', logging: '日志', rename: '重命名', align: '对齐', alignment: '对齐',
        device: '设备', batch: '批量处理', delete: '删除', inspect: '检查', scan: '扫描',
        dict: '数据映射', dedup: '去重'
    };

    // 任一配方在执行中(执行/取选择期间冻结其它按钮,避免并行占用 PS)
    function anyRecipeRunning() {
        if (state.hostBusy) return true;
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

    function renderDescription(cls, text) {
        // 复用聊天的 Markdown 渲染器（先转义 HTML），统一标题、列表和代码块格式。
        var description = el('div', cls + ' rcp-markdown msg-content');
        if (typeof window.renderMarkdown === 'function') {
            description.innerHTML = window.renderMarkdown(text);
        } else {
            description.textContent = text;
            description.className += ' rcp-markdown-fallback';
        }
        return description;
    }

    function selectTab(tab, focus) {
        state.activeTab = tab;
        render();
        if (focus && tabButtons[tab]) tabButtons[tab].focus();
    }

    function updateTabs() {
        ['recipes', 'candidates'].forEach(function (tab) {
            var button = tabButtons[tab];
            if (!button) return;
            var selected = state.activeTab === tab;
            var count = tab === 'recipes' ? state.recipes.length : state.candidates.length;
            button.textContent = (tab === 'recipes' ? '我的配方' : '待固化') + '（' + count + '）';
            button.className = 'rcp-tab rcp-tab-' + tab + (selected ? ' rcp-tab-active' : '');
            button.setAttribute('aria-selected', selected ? 'true' : 'false');
            button.tabIndex = selected ? 0 : -1;
        });
        if (bodyEl) bodyEl.setAttribute('aria-labelledby', 'rcp-tab-' + state.activeTab);
    }

    function candidateTitle(c) {
        // 使用已有说明作能力名称，不根据代码猜功能；原始片段名保留在副标题中。
        var lines = (c.description || '').split(/\r?\n/);
        var inFence = false;
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (/^(`{3,}|~{3,})/.test(line)) { inFence = !inFence; continue; }
            if (inFence) continue;
            line = line.replace(/^\s*(?:#{1,6}|[-*+]|\d+[.)])\s+/, '')
                .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
                .replace(/[*`]/g, '').trim();
            if (!line || /^(?:用途|功能|功能说明|说明|使用方法|代码)[：:]?$/.test(line)
                || /^未记录用途说明/.test(line) || /^</.test(line)) continue;
            var content = line.replace(/^\[[^\]]*\]\s*/, '');
            if (/^(?:var|using|return|if|for|foreach|while|class|public|private|static|const|int|double|bool|string|object|import|from)\b/.test(content)
                || /^[\w.]+\s*(?:=|\()/.test(content)) continue;
            var title = content.split('。')[0];
            return title.length > 48 ? title.substring(0, 48) + '…' : title;
        }
        if (c.name && !/^auto[_-]/i.test(c.name) && /[\u4e00-\u9fff]/.test(c.name)) return c.name;
        if (c.tags && c.tags.length)
            return '相关能力：' + c.tags.slice(0, 3).map(function (tag) { return tagLabels[tag] || tag; }).join('、');
        return '待整理的代码片段';
    }

    function render() {
        if (!bodyEl) return;
        updateTabs();
        bodyEl.innerHTML = '';

        if (state.activeTab === 'recipes') {
            if (state.recipes.length) {
                state.recipes.forEach(function (rec) { bodyEl.appendChild(renderCard(rec)); });
                return;
            }
            var em = el('div', 'rcp-empty');
            em.appendChild(el('div', null, '还没有已固化配方。'));
            em.appendChild(el('div', null,
                '可导入分享的配方，或将已成功运行的片段整理为配方。'));
            if (state.candidates.length) {
                var view = el('button', 'rcp-btn rcp-empty-action', '查看待固化（' + state.candidates.length + '）');
                view.onclick = function () { selectTab('candidates', true); };
                em.appendChild(view);
            }
            bodyEl.appendChild(em);
            return;
        }

        bodyEl.appendChild(el('div', 'rcp-candidate-note',
            '这些片段已成功运行，整理用途和参数后才能作为配方使用。'));
        if (!state.candidates.length) {
            bodyEl.appendChild(el('div', 'rcp-empty', '暂无待固化片段。已整理的配方可在“我的配方”中使用。'));
        } else {
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
        head.onclick = function () {
            state.open[rec.id] = !state.open[rec.id];
            render();
        };
        card.appendChild(head);

        var meta = el('div', 'rcp-card-meta');
        meta.appendChild(el('span', 'rcp-status rcp-status-ready', '已固化'));
        meta.appendChild(el('span', 'rcp-lang', rec.lang === 'python' ? 'PY' : 'C#'));
        card.appendChild(meta);

        // 体
        var body = el('div', 'rcp-card-body');
        if (rec.description) body.appendChild(renderDescription('rcp-desc', rec.description));

        (rec.params || []).forEach(function (p) {
            if (!fixedByActions(rec, p)) body.appendChild(renderParam(rec, p, function () { card.updateRunButtons(); }));
        });

        var reset = el('button', 'rcp-btn rcp-reset', '恢复默认值');
        reset.disabled = anyRecipeRunning();
        reset.onclick = function () {
            (rec.params || []).forEach(function (p) {
                if (!isObjectKind(p.kind)) setBinding(rec.id, p.name, { value: p.def != null ? p.def : (p.kind === 'bool' ? false : '') });
            });
            render();
        };
        if ((rec.params || []).some(function (p) { return !isObjectKind(p.kind) && !fixedByActions(rec, p); })) body.appendChild(reset);

        // 底部动作
        var act = el('div', 'rcp-actions');
        var running = !!state.running[rec.id];
        var runButtons = [];
        ((rec.actions || []).length ? rec.actions : [null]).forEach(function (action) {
            var runBtn = el('button', 'rcp-run' + (running ? ' rcp-running' : ''), running ? '执行中…' : (action ? action.label : '执行'));
            runBtn.onclick = function () { run(rec, action); };
            runButtons.push({ button: runBtn, action: action });
            act.appendChild(runBtn);
        });
        card.updateRunButtons = function () {
            runButtons.forEach(function (item) {
                item.button.disabled = anyRecipeRunning() || !readyToRun(rec, item.action);
                item.button.title = anyRecipeRunning() ? '已有配方在执行中，等它完成' : (readyToRun(rec, item.action) ? '执行' : '请选取对象并检查参数');
            });
        };
        card.updateRunButtons();

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
        codeBtn.title = '在对话里查看配方文档';
        codeBtn.onclick = function () { reveal({ recipeId: rec.id }); };
        act.appendChild(codeBtn);

        var exportBtn = el('button', 'rcp-btn', '导出');
        exportBtn.title = '导出 Markdown 文件，分享给其他人导入使用';
        exportBtn.onclick = function () {
            exportBtn.disabled = true;
            send('recipe.export', { recipeId: rec.id }, function (r) {
                exportBtn.disabled = false;
                if (r && r.cancelled) return;
                notice(!!(r && r.ok), (r && (r.text || r.error)) || '导出失败。');
            });
        };
        act.appendChild(exportBtn);

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

    function loadObjectTypes(rec, p) {
        if (p.typesLoading || anyRecipeRunning()) return;
        p.typesLoading = true;
        var study = state.study, finished = false;
        var timer = setTimeout(function () { if (finished) return; finished = true; p.typesLoading = false; p.objectTypesLoaded = true; notice(false, '读取类型超时，可点击刷新类型重试。'); render(); }, 5000);
        send('recipe.objectTypes', {recipeId:rec.id, param:p.name, study:study}, function (msg) {
            if (finished) return; finished = true; clearTimeout(timer); p.typesLoading = false;
            if (study !== state.study || !state.recipes.some(function(r){ return r === rec; })) return;
            p.objectTypesLoaded = true;
            if (!msg || !msg.ok) { notice(false, msg && msg.error || '读取场景类型失败。'); render(); return; }
            p.objectTypes = msg.objectTypes || [{label:'全部类型',value:'all'}];
            var query = objectQuery(rec,p);
            if (!p.objectTypes.some(function(c){ return c.value === query.type; })) { query.type = 'all'; setBinding(rec.id,p.name,null); }
            render();
        });
    }

    function objectQuery(rec, p) {
        var key = rec.id + ':' + p.name;
        return state.queries[key] || (state.queries[key] = { type: 'all', name: '' });
    }

    function renderParam(rec, p, updateButtons) {
        var wrap = el('div', 'rcp-param');

        var lab = el('label', 'rcp-param-label');
        lab.appendChild(document.createTextNode(p.label || p.name));
        if (p.required) lab.appendChild(el('span', 'rcp-req', '*'));
        lab.title = p.help || '';
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
            if (p.objectFilter) {
                var query = objectQuery(rec, p);
                var filters = el('div', 'rcp-object-filters');
                var typeLabel = el('label', 'rcp-filter-label', '对象类型');
                var type = el('select', 'rcp-input'); type.id = 'rcp-type-' + rec.id + '-' + p.name; typeLabel.htmlFor = type.id;
                (p.objectTypes && p.objectTypes.length ? p.objectTypes : [{label:'全部类型',value:'all'}]).forEach(function (choice) {
                    var option = el('option', null, choice.label); option.value = choice.value; type.appendChild(option);
                }); type.value = query.type; type.disabled = busy || picking;
                var nameLabel = el('label', 'rcp-filter-label', '名称包含');
                var name = el('input', 'rcp-input'); name.type = 'text'; name.id = 'rcp-name-' + rec.id + '-' + p.name; nameLabel.htmlFor = name.id;
                name.value = query.name; name.placeholder = '输入名称关键词，可留空'; name.maxLength = 200; name.disabled = busy || picking;
                var search = el('button', 'rcp-btn rcp-search', '按条件查找');
                search.disabled = busy || picking || (query.type === 'all' && !query.name.trim());
                function changeQuery() {
                    query.type = type.value; query.name = name.value;
                    setBinding(rec.id, p.name, null); delete state.pickError[key];
                    slot.textContent = '未选取'; slot.className = 'rcp-slot rcp-unset'; slot.title = '';
                    search.disabled = busy || picking || (query.type === 'all' && !query.name.trim());
                    updateButtons();
                }
                type.onchange = changeQuery; name.oninput = changeQuery;
                search.onclick = function () { beginPick(rec, p, true); };
                filters.appendChild(typeLabel); filters.appendChild(type); filters.appendChild(nameLabel); filters.appendChild(name); filters.appendChild(search);
                var refreshTypes = el('button', 'rcp-btn rcp-search', p.typesLoading ? '读取类型中…' : '刷新类型');
                refreshTypes.disabled = busy || picking || !!p.typesLoading; refreshTypes.onclick = function(){ loadObjectTypes(rec,p); render(); };
                filters.appendChild(refreshTypes);
                filters.appendChild(el('div', 'rcp-filter-help', '类型来自当前场景；名称按关键词匹配。'));
                if (p.objectTypesLoaded === false && state.open[rec.id] && !root.classList.contains('rcp-collapsed') && !busy && !p.typesLoading) setTimeout(function(){ if (state.open[rec.id] && !root.classList.contains("rcp-collapsed") && state.recipes.indexOf(rec) >= 0) loadObjectTypes(rec,p); }, 0);
                wrap.appendChild(filters);
            }
        } else {
            var value = scalarValue(rec, p);
            var choices = p.choices || [];
            var inp = el(choices.length ? 'select' : 'input', 'rcp-input' + (p.kind === 'color' ? ' rcp-color' : ''));
            if (p.kind === 'color' && !/^#[0-9a-f]{6}$/i.test(String(value))) { value = p.def || '#4F83CC'; setBinding(rec.id, p.name, { value: value }); }
            inp.id = 'rcp-param-' + rec.id + '-' + p.name;
            lab.htmlFor = inp.id;
            if (!choices.length) {
                inp.type = p.kind === 'color' ? 'color' : (p.kind === 'number') ? 'number' : (p.kind === 'bool' ? 'checkbox' : 'text');
                if (p.kind === 'number') inp.step = 'any';
            }
            if (choices.length) {
                inp.appendChild(el('option', null, '请选择…'));
                inp.children[0].value = '';
                choices.forEach(function (choice) {
                    var option = el('option', null, choice.label); option.value = choice.value; inp.appendChild(option);
                });
            }
            inp.placeholder = p.help || '';
            if (anyRecipeRunning()) inp.disabled = true;
            if (p.kind === 'bool') inp.checked = /^(true|1|on)$/i.test(String(value));
            else inp.value = value;
            inp.oninput = inp.onchange = function () {
                setBinding(rec.id, p.name, { value: p.kind === 'bool' ? inp.checked : inp.value });
                updateButtons();
            };
            wrap.appendChild(inp);
            if (p.kind === 'color') {
                var hex = el('span', 'rcp-color-value', String(value).toUpperCase());
                wrap.appendChild(hex);
                var palette = el('div', 'rcp-palette');
                ['#4F83CC','#E45B5B','#49A879','#F0BC4D','#AD73D4','#F28D4B','#FFFFFF','#333333'].forEach(function (color) {
                    var swatch = el('button', 'rcp-swatch'); swatch.type = 'button'; swatch.style.backgroundColor = color;
                    swatch.title = color; swatch.setAttribute('aria-label', '选择颜色 ' + color); swatch.disabled = anyRecipeRunning();
                    swatch.onclick = function () { inp.value = color; inp.oninput(); };
                    palette.appendChild(swatch);
                });
                var saveColor = inp.oninput;
                inp.oninput = inp.onchange = function () { saveColor(); hex.textContent = inp.value.toUpperCase(); };
                wrap.appendChild(palette);
            }
        }

        if (p.help && !isObjectKind(p.kind)) {
            // 对象类的说明已经在 title 里，这里只给标量补一行
        }
        return wrap;
    }

    function renderCandidate(c) {
        var row = el('div', 'rcp-cand');
        var head = el('div', 'rcp-cand-head');
        var name = el('span', 'rcp-name', candidateTitle(c));
        name.title = c.name || '';
        head.appendChild(name);
        head.appendChild(el('span', 'rcp-lang', c.lang === 'python' ? 'PY' : 'C#'));
        row.appendChild(head);

        row.appendChild(el('div', 'rcp-source-name', '片段：' + (c.name || '(未命名)')));
        row.appendChild(el('span', 'rcp-status rcp-status-pending', '待整理'));

        var description = renderDescription('rcp-cand-desc',
            c.description || '未记录用途说明，请查看代码确认功能。');
        row.appendChild(description);
        if (c.tags && c.tags.length) {
            var tags = el('div', 'rcp-tags');
            c.tags.forEach(function (tag) {
                var chip = el('span', 'rcp-tag', tagLabels[tag] || tag);
                chip.title = tag;
                tags.appendChild(chip);
            });
            row.appendChild(tags);
        }

        if (c.codePreview) {
            var details = el('details', 'rcp-preview');
            details.appendChild(el('summary', null, '代码预览' + (c.previewTruncated ? '（部分）' : '')));
            details.appendChild(el('pre', null, c.codePreview));
            row.appendChild(details);
        }

        var actions = el('div', 'rcp-cand-actions');
        var s = el('span', 'rcp-stat');
        s.appendChild(el('span', 'rcp-ok', String(c.successCount || 0)));
        s.appendChild(document.createTextNode(' 次成功'));
        if (c.failureCount) s.appendChild(el('span', 'rcp-bad', ' · ' + c.failureCount + ' 次失败'));
        actions.appendChild(s);

        var view = el('button', 'rcp-btn', '详情');
        view.title = '在对话里查看用途说明和完整代码';
        view.onclick = function () { reveal({ snippetName: c.name }); };
        actions.appendChild(view);

        var btn = el('button', 'rcp-btn rcp-promote', '整理为配方');
        btn.title = '让 AI 把这段片段整理成带参数的配方';
        btn.onclick = function () {
            btn.disabled = true;
            send('recipe.promote', { snippetName: c.name }, function (r) {
                btn.disabled = false;
                if (r && r.ok === false) notice(false, r.error || '固化失败。');
                // 固化走的是一轮对话（AI 要给参数命名、写说明），
                // 结果由宿主推 recipe.changed 回来刷新，这里不自作主张改列表。
            });
        };
        actions.appendChild(btn);
        row.appendChild(actions);
        return row;
    }

    // ── 执行 ──

    function run(rec, action) {
        if (anyRecipeRunning() || !readyToRun(rec, action)) return;

        var args = {};
        var b = state.bindings[rec.id] || {};
        (rec.params || []).forEach(function (p) {
            var v = b[p.name];
            if (isObjectKind(p.kind)) { if (v && !v.stale) args[p.name] = v.id; }
            else args[p.name] = scalarValue(rec, p, action);
        });

        state.running[rec.id] = true;
        state.open[rec.id] = true;           // 自动展开：执行中/结果直接可见，不再藏在折叠卡片里
        if (state.lastResult) delete state.lastResult[rec.id];
        render();

        send('recipe.run', { recipeId: rec.id, study: state.study, args: args, actionId: action ? action.id : null }, function (r) {
            clearTimeout(_runTimers[rec.id]);
            delete _runTimers[rec.id];
            state.running[rec.id] = false;
            state.hostBusy = false;
            state.savedArgs = state.savedArgs || {};
            state.savedArgs[rec.id] = args;
            var ok = !!(r && r.ok);
            flash(rec.id, ok, (r && (r.text || r.error)) || (ok ? '执行完成。' : '执行失败。'));
            state.open[rec.id] = true;       // 保持展开，让结果消息留在卡片里
            render();
            // 计数由宿主那边落盘，刷一次拿最新的
            send('recipe.list', {}, function (r2) {
                if (r2 && r2.ok !== false) {
                    state.recipes = r2.recipes || state.recipes;
                    state.candidates = r2.candidates || state.candidates;
                    state.hostBusy = !!r2.busy;
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

    function beginPick(rec, p, search) {
        if (anyRecipeRunning()) return;
        var query = objectQuery(rec, p);
        var signature = JSON.stringify(query);
        var key = rec.id + ':' + p.name;
        state.picking[key] = true;
        delete state.pickError[key];
        render();

        send('recipe.pickSelection',
            { recipeId: rec.id, param: p.name, multi: p.kind === 'objects', study: state.study, search: !!search, objectType: query.type, objectName: query.name },
            function (r) {
                clearTimeout(_pickTimers[key]);
                delete _pickTimers[key];
                delete state.picking[key];
                if (signature !== JSON.stringify(objectQuery(rec, p))) { render(); return; }
                if (!r || r.ok === false) {
                    state.pickError[key] = (r && r.error) || '取选择失败。';
                } else if ((r.study || null) !== state.study) {
                    state.pickError[key] = 'study 已切换，请重新选取对象。';
                } else {
                    state.pickError[key] = null;
                    setBinding(rec.id, p.name, {
                        id: r.id, name: r.name, type: r.objectType, count: r.count || 1
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

    function notifySidebarLayout(open) {
        var width = 300;
        if (typeof window.getComputedStyle === 'function') {
            width = parseFloat(window.getComputedStyle(root).getPropertyValue('--rcp-w')) || width;
        }
        // CSS 像素转为宿主窗体像素；devicePixelRatio 包含 WebView 的 DPI / 页面缩放。
        send('recipe.sidebar', { open: open, width: width, pixelRatio: window.devicePixelRatio || 1 });
    }

    function mount(container) {
        root = el('div', 'rcp-root rcp-collapsed');

        var head = el('div', 'rcp-head');
        head.appendChild(el('span', 'rcp-title', '配方'));

        var importBtn = el('button', 'rcp-btn rcp-import', '导入');
        importBtn.title = '导入配方 Markdown 文件，同名配方保存为新副本';
        importBtn.onclick = function () {
            importBtn.disabled = true;
            send('recipe.import', {}, function (r) {
                importBtn.disabled = false;
                if (r && r.cancelled) return;
                notice(!!(r && r.ok), (r && (r.text || r.error)) || '导入失败。');
                if (r && r.ok) {
                    state.activeTab = 'recipes';
                    if (r.recipeId) state.open[r.recipeId] = true;
                    refresh();
                }
            });
        };
        head.appendChild(importBtn);

        var reload = el('button', 'rcp-iconbtn rcp-reload', '⟳');
        reload.title = '刷新';
        reload.onclick = refresh;
        head.appendChild(reload);

        root.appendChild(head);

        var tabs = el('div', 'rcp-tabs');
        tabs.setAttribute('role', 'tablist');
        tabs.setAttribute('aria-label', '配方分类');
        ['recipes', 'candidates'].forEach(function (tab) {
            var button = el('button', 'rcp-tab');
            button.type = 'button';
            button.id = 'rcp-tab-' + tab;
            button.setAttribute('role', 'tab');
            button.setAttribute('aria-controls', 'rcp-tab-panel');
            button.onclick = function () { selectTab(tab); };
            button.onkeydown = function (e) {
                var next;
                if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') next = tab === 'recipes' ? 'candidates' : 'recipes';
                else if (e.key === 'Home') next = 'recipes';
                else if (e.key === 'End') next = 'candidates';
                if (next) { e.preventDefault(); selectTab(next, true); }
            };
            tabButtons[tab] = button;
            tabs.appendChild(button);
        });
        root.appendChild(tabs);

        noticeEl = el('div', 'rcp-notice');
        noticeEl.hidden = true;
        root.appendChild(noticeEl);

        bodyEl = el('div', 'rcp-body');
        bodyEl.id = 'rcp-tab-panel';
        bodyEl.setAttribute('role', 'tabpanel');
        root.appendChild(bodyEl);
        updateTabs();

        container.appendChild(root);

        // 展开/收起把手：独立于面板、固定在聊天区右侧垂直居中。
        // 展开/收起都在同一位置切换，避免“展开在中间、收起在左上角”的跳变。
        // 必须 append 在 root 之后，配合 .rcp-root.rcp-collapsed + .rcp-toggle 取位。
        var toggle = el('button', 'rcp-toggle', '⟨');
        toggle.title = '收起 / 展开配方栏';
        toggle.onclick = function () {
            root.classList.toggle('rcp-collapsed');
            var collapsed = root.classList.contains('rcp-collapsed');
            toggle.textContent = collapsed ? '⟨' : '⟩';
            notifySidebarLayout(!collapsed);
            if (!collapsed) render();
        };
        container.appendChild(toggle);

        // 页面重载后初始状态为收起，同步宿主以回收之前展开时增加的宽度。
        notifySidebarLayout(false);
        refresh();
    }

    window.txRecipes = {
        open: function () {
            if (!root) return;
            root.classList.remove('rcp-collapsed');
            var toggle = document.querySelector('.rcp-toggle');
            if (toggle) toggle.textContent = '⟩';
            notifySidebarLayout(true);
            refresh();
        },
        mount: mount,
        refresh: refresh,
        onHostMessage: onHostMessage
    };
})();
