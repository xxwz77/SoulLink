/*
 * AAA协同跑图 v1.0.0
 *
 * 4 人协同跑同一条路线。
 *   · 房主：开世界 → 白名单放人 → 满员后自动开跑
 *   · 队员：主动加入房主世界 → 满员后自动开跑
 *
 * 和「AAA狗粮联机团购」的区别：
 *   · 不做占位 —— 所有人进入世界后一起跑，各采各的
 *   · 不需要 1P/2P/3P/4P 分路线
 *   · 不需要 mojang 重新上线
 *
 * 白名单识别（模板匹配 + OCR 兜底）参考自 ArtsGroupPurchasing，作者 mno / 汐
 */

// ═══════════════════════════════════════════════════
//  配置
// ═══════════════════════════════════════════════════

const myRole        = settings.myRole || "队员";
const isHost        = myRole === "房主";
const hostUID       = (settings.hostUID || "").trim();
const hostName      = (settings.hostName || "").trim();
const permissionMode = settings.permissionMode || "白名单";
/**
 * 路线（内置，随脚本一起分发）。
 *
 * ★ 和原脚本一样的做法：路线放在脚本自己的 assets/routes/ 目录下，
 *   用相对路径读，拷到 A1/A2/A3/A4 哪个目录都能直接跑。
 */
const ROUTE_DIR = "assets/routes";

/** 内置的路线文件，按这个顺序执行。 */
const FIXED_ROUTE_FILES = [
    "07【额外】至冬-旧基捷城西盗罐者6.json",
    "08【额外】至冬-旧基捷城东盗罐者17.json",
    "09【额外】至冬-旧基捷城东北盗罐者6.json",
    "10【额外】至冬-旧基捷城西南盗罐者5.json",
    "99-ZZZZ996璃月-明蕴镇神像.json"
];

/** 生成要跑的路线列表（相对路径）。设置里填了就用设置的。 */
function buildRoutePath() {
    const custom = (settings.routePath || "").trim();
    if (custom) return custom;

    return FIXED_ROUTE_FILES.map(n => ROUTE_DIR + "/" + n).join("\n");
}

const routePath = buildRoutePath();
const waitSeconds   = Math.max(30, parseInt(settings.waitSeconds) || 180);
const startDelay    = Math.max(0, parseInt(settings.startDelaySeconds) || 8);
const autoPickup    = settings.autoPickup !== false;
const runExtra      = settings.runExtra === true;
const useAllTargetImages = settings.useAllTargetImages === true
                        || settings.useAllTargetImages === "true";

// ★ 白名单固定 3 个位置，留空就是"少一个人"。
//   房主：填几个就是几个人（不算房主自己）。
//   队员：白名单没用，靠 teamSize 知道该等几个人。
const whitelist = [
    settings.nameToPermit1,
    settings.nameToPermit2,
    settings.nameToPermit3
].map(v => (v || "").trim())
 .filter(v => v.length > 0);

/**
 * 目标人数（含房主自己）。
 * 房主：白名单填了几个 + 我自己
 * 队员：直接读 teamSize（因为队员侧白名单是空的）
 *
 * ★ 房主白名单全空 = 配置错误，不能当成"1 人队"直接开跑。
 *   这种情况按 0 处理，主流程会报错退出，不会自己一个人跑起来。
 */
const memberCountFromList = whitelist.length;   // 白名单里有几个人
const targetCount = (settings.myRole === "房主")
    ? memberCountFromList + 1
    : Math.max(2, Math.min(4, parseInt(settings.teamSize) || 4));

// ═══════════════════════════════════════════════════
//  全局状态
// ═══════════════════════════════════════════════════

let state = { running: false };
let enteredPlayers = [];          // 已放进来的玩家
let templates = [];               // 白名单模板
let selectedTargets = [];         // 选中的模板

// 识别区域（队伍申请列表位置，1080p 坐标）
const LIST_REGION = { x: 664, y: 481, w: 1355 - 668, h: 588 - 484 };

// ═══════════════════════════════════════════════════
//  通用工具
// ═══════════════════════════════════════════════════

/** 归一化编辑距离，返回 0-1 的相似度。 */
function stringSimilarity(left, right) {
    const a = [...String(left || "").toLocaleLowerCase().replace(/\s+/g, "")];
    const b = [...String(right || "").toLocaleLowerCase().replace(/\s+/g, "")];
    if (a.join("") === b.join("")) return 1;
    if (!a.length || !b.length) return 0;

    const previous = Array.from({ length: b.length + 1 }, (_, i) => i);
    for (let i = 1; i <= a.length; i++) {
        const current = [i];
        for (let j = 1; j <= b.length; j++) {
            current[j] = a[i - 1] === b[j - 1]
                ? previous[j - 1]
                : Math.min(previous[j - 1], previous[j], current[j - 1]) + 1;
        }
        for (let j = 0; j <= b.length; j++) previous[j] = current[j];
    }
    return 1 - previous[b.length] / Math.max(a.length, b.length);
}

/** 递归读取目录下的指定扩展名文件。 */
async function readFolder(folderPath, ext = "") {
    const targetExt = ext ? (ext.startsWith(".") ? ext : "." + ext).toLowerCase() : "";
    const stack = [folderPath];
    const files = [];

    while (stack.length > 0) {
        const current = stack.pop();
        let entries = [];
        try { entries = file.ReadPathSync(current); }
        catch (e) { log.error("读取目录失败 " + current + "：" + e.message); continue; }

        const subFolders = [];
        for (const p of entries) {
            if (file.IsFolder(p)) { subFolders.push(p); continue; }

            if (targetExt) {
                const e2 = p.toLowerCase().slice(p.lastIndexOf("."));
                if (e2 !== targetExt) continue;
            }

            files.push({
                fullPath: p,
                fileName: p.split("\\").pop()
            });
        }
        stack.push(...subFolders.reverse());
    }
    return files;
}

/** 找图并点击。 */
async function findAndClick(ro, match = false, maxAttempts = 3, delay = 100) {
    for (let i = 0; i < maxAttempts; i++) {
        try {
            const region = captureGameRegion();
            const result = region.find(ro);
            region.dispose();
            if (result.isExist()) {
                result.click();
                return true;
            }
        } catch { }
        await sleep(delay);
    }
    return false;
}

/** 只找不点。 */
async function findExist(ro) {
    try {
        const region = captureGameRegion();
        const result = region.find(ro);
        region.dispose();
        return result.isExist();
    } catch { return false; }
}

// ═══════════════════════════════════════════════════
//  白名单模板加载与匹配
// ═══════════════════════════════════════════════════

/** 加载 targets 目录下的所有 PNG 模板。 */
async function loadTemplates() {
    templates = [];
    try {
        const pngs = await readFolder("targets", ".png");
        for (const f of pngs) {
            const baseName = f.fileName.replace(/\.png$/i, "");

            // 跳过备注图
            if (baseName.endsWith("备注")) continue;

            const mat = file.ReadImageMatSync(f.fullPath);
            const ro = RecognitionObject.TemplateMatch(
                mat, LIST_REGION.x, LIST_REGION.y, LIST_REGION.w, LIST_REGION.h);
            templates.push({ ro, baseName });
        }

        // 长名字优先匹配，避免短名抢先
        templates.sort((a, b) => b.baseName.length - a.baseName.length);
        log.info("加载 " + templates.length + " 个名字模板");
    } catch (e) {
        log.error("加载模板失败：" + e.message);
    }
}

/** 给每个白名单名字挑选最像的模板。 */
function selectClosestTemplates(names, tpls) {
    const pairs = [];
    for (const targetName of names) {
        for (let i = 0; i < tpls.length; i++) {
            pairs.push({
                targetName,
                templateIndex: i,
                nameSimilarity: stringSimilarity(targetName, tpls[i].baseName)
            });
        }
    }

    pairs.sort((a, b) =>
        b.nameSimilarity - a.nameSimilarity || a.templateIndex - b.templateIndex);

    const usedNames = new Set();
    const usedTemplates = new Set();
    const selected = [];

    for (const pair of pairs) {
        if (usedNames.has(pair.targetName) || usedTemplates.has(pair.templateIndex)) continue;
        usedNames.add(pair.targetName);
        usedTemplates.add(pair.templateIndex);
        selected.push({
            ...tpls[pair.templateIndex],
            targetName: pair.targetName,
            nameSimilarity: pair.nameSimilarity
        });
        log.info("白名单 " + pair.targetName + " → 模板 " + tpls[pair.templateIndex].baseName
               + "（相似度 " + (pair.nameSimilarity * 100).toFixed(0) + "%）");
    }
    return selected;
}

/** 全部图片模式：每张图归到最像的白名单名字。 */
function mapAllTemplatesToClosestNames(names, tpls) {
    return tpls.map(tpl => {
        let targetName = null;
        let nameSimilarity = 0;
        for (const name of names) {
            const s = stringSimilarity(name, tpl.baseName);
            if (s > nameSimilarity) { targetName = name; nameSimilarity = s; }
        }
        return { ...tpl, targetName, nameSimilarity };
    });
}

/** 识别当前申请列表里的人是谁。 */
async function recognizeRequest() {
    // ① 模板匹配
    try {
        const region = captureGameRegion();
        const matched = [];
        for (const { ro, targetName, nameSimilarity } of selectedTargets) {
            if (region.find(ro).isExist()) {
                matched.push({ name: targetName, nameSimilarity });
            }
        }
        region.dispose();

        if (matched.length > 0) {
            matched.sort((a, b) => b.nameSimilarity - a.nameSimilarity);
            const candidate = matched.find(item =>
                whitelist.includes(item.name) && !enteredPlayers.includes(item.name));
            if (candidate) {
                log.info("模板匹配 → " + candidate.name
                       + "（" + (candidate.nameSimilarity * 100).toFixed(0) + "%）");
                return { method: "模板匹配", result: candidate.name };
            }
        }
    } catch { }

    // ② OCR 兜底
    try {
        const region = captureGameRegion();
        const resList = region.findMulti(
            RecognitionObject.ocr(LIST_REGION.x, LIST_REGION.y, LIST_REGION.w, LIST_REGION.h));
        region.dispose();

        for (const res of resList) {
            const txt = res.text.trim();
            if (whitelist.includes(txt)) {
                return { method: "OCR识别", result: txt };
            }
        }
        resList.forEach(r => {
            const t = r.text.trim();
            if (t.length > 0) log.warn('识别到 "' + t + '"，不在白名单');
        });
    } catch { }

    return { method: "无", result: null };
}

// ═══════════════════════════════════════════════════
//  队伍人数 / 自己编号
// ═══════════════════════════════════════════════════

/**
 * 识别自己当前是几号位。
 * ★ 直接在**主界面**识别左上角的 0P/1P/2P/3P/4P 标签，不用开队伍界面。
 *   区域 200,10,400,70 是原脚本实测的位置。
 */
async function getPlayerSign() {
    const REGION = { x: 200, y: 10, w: 400, h: 70 };

    for (let attempt = 0; attempt < 6; attempt++) {
        const ros = [];
        for (let i = 0; i <= 4; i++) {
            try {
                const ro = RecognitionObject.TemplateMatch(
                    file.ReadImageMatSync("assets/RecognitionObject/" + i + "P.png"),
                    REGION.x, REGION.y, REGION.w, REGION.h);
                ro.Threshold = 0.95;
                ro.InitTemplate();
                ros.push({ i, ro });
            } catch { }
        }

        moveMouseTo(1555, 860);   // 移开鼠标，避免遮挡

        const region = captureGameRegion();
        for (const { i, ro } of ros) {
            try {
                if (region.find(ro).isExist()) {
                    region.dispose();
                    return i;
                }
            } catch { }
        }
        region.dispose();

        await sleep(400);
    }
    return -1;
}

/**
 * 数当前世界里有几个人。
 * ★ 主界面左上角会显示一串 1P/2P/3P/4P，有几个标签就是几个人。
 *   单人是 0P（返回 1）。
 */
async function countPlayers() {
    const REGION = { x: 200, y: 10, w: 400, h: 70 };

    const ros = [];
    for (let i = 0; i <= 4; i++) {
        try {
            const ro = RecognitionObject.TemplateMatch(
                file.ReadImageMatSync("assets/RecognitionObject/" + i + "P.png"),
                REGION.x, REGION.y, REGION.w, REGION.h);
            ro.Threshold = 0.95;
            ro.InitTemplate();
            ros.push({ i, ro });
        } catch { }
    }

    moveMouseTo(1555, 860);

    const region = captureGameRegion();
    let found = [];
    for (const { i, ro } of ros) {
        try { if (region.find(ro).isExist()) found.push(i); }
        catch { }
    }
    region.dispose();

    if (found.length === 0) return 0;

    // 有 0P 说明是单人
    if (found.includes(0)) return 1;

    // 否则就是找到的标签数（1P~4P）
    return Math.max(1, found.length);
}

// ═══════════════════════════════════════════════════
//  房主：等人
// ═══════════════════════════════════════════════════

/**
 * 房主放人循环。
 * 白名单模式下只放名单里的人；无条件模式看到申请就放。
 * 满 targetCount 人后返回 true。
 */
async function hostWaitForTeam() {
    // ★ 目标人数是 1（只有自己）= 单人模式，不用等人
    if (targetCount < 2) {
        log.info("单人模式，不等待队友，直接开始");
        return true;
    }

    // ★ 限定在右侧申请列表区域，避免误点界面上的其他按钮
    const allowEnterRo = RecognitionObject.TemplateMatch(
        file.ReadImageMatSync("assets/RecognitionObject/allowEnter.png"),
        1480, 300, 280, 600);

    log.info("房主等待队友，目标 " + targetCount + " 人，最长 " + waitSeconds + " 秒");
    notification.send("等待队友加入（目标 " + targetCount + " 人）");

    const start = Date.now();
    const timeoutMs = waitSeconds * 1000;

    while (Date.now() - start < timeoutMs) {
        // 先看现在几个人
        const current = await countPlayers();
        if (current >= targetCount) {
            log.info("已满员 " + current + " 人");
            notification.send("队伍已满 " + current + " 人");
            return true;
        }
        if (current < 0) {
            // 识别失败，歇一下重试，不要误判
            await sleep(1500);
            continue;
        }

        // 按 Y 看有没有人申请
        await genshin.returnMainUi();
        await keyPress("Y");
        await sleep(400);

        const region = captureGameRegion();
        let hasRequest = false;
        try {
            hasRequest = region.find(allowEnterRo).isExist();
        } catch { }
        region.dispose();

        if (hasRequest) {
            let allowed = false;

            if (permissionMode === "无条件通过") {
                allowed = await findAndClick(allowEnterRo);
                if (allowed) log.info("无条件放行一位");
            } else {
                const rec = await recognizeRequest();
                if (rec.result) {
                    allowed = await findAndClick(allowEnterRo);
                    if (allowed) {
                        enteredPlayers = [...new Set([...enteredPlayers, rec.result])];
                        log.info(rec.method + " 放行 " + rec.result
                               + "（已进 " + enteredPlayers.length + " 人）");
                        notification.send("放行 " + rec.result);
                    }
                } else {
                    log.info("有人申请但不在白名单，跳过");
                }
            }

            if (allowed) {
                await waitForMainUI(true, 20000);   // 等人进来
                await sleep(1500);
                continue;
            }
        }

        // 关掉 Y 界面
        try { await genshin.returnMainUi(); } catch { }
        await sleep(1000);
    }

    log.warn("等待超时，当前 " + (await countPlayers()) + " 人，未达 " + targetCount);
    notification.error("等人超时，未凑齐 " + targetCount + " 人");
    return false;
}

/** 等人进入主界面（避免界面还没切回来就开始操作）。 */
async function waitForMainUI(allowTeam = false, timeoutMs = 15000) {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
        try {
            const region = captureGameRegion();
            const mainUI = RecognitionObject.TemplateMatch(
                file.ReadImageMatSync("assets/RecognitionObject/MainUI.png"));
            if (region.find(mainUI).isExist()) { region.dispose(); return true; }
            region.dispose();
        } catch { }
        await sleep(300);
    }
    return false;
}

// ═══════════════════════════════════════════════════
//  队员：加入房主世界
// ═══════════════════════════════════════════════════

/** 队员加入房主世界。成功返回 true。 */
async function memberJoinHost() {
    if (!hostUID) {
        log.error("队员模式必须填房主 UID");
        notification.error("没有填房主 UID");
        return false;
    }

    const enterUIDRo  = RecognitionObject.TemplateMatch(
        file.ReadImageMatSync("assets/RecognitionObject/enterUID.png"));
    const searchRo    = RecognitionObject.TemplateMatch(
        file.ReadImageMatSync("assets/RecognitionObject/search.png"));
    const requestRo   = RecognitionObject.TemplateMatch(
        file.ReadImageMatSync("assets/RecognitionObject/requestEnter.png"));

    log.info("队员模式，准备加入房主 " + hostUID + "（" + hostName + "）");

    const start = Date.now();
    const timeoutMs = waitSeconds * 1000;
    let attempt = 0;

    while (Date.now() - start < timeoutMs) {
        attempt++;

        // 已经进去了？
        const sign = await getPlayerSign();
        if (sign >= 2) {
            log.info("已在房主世界，队伍编号 " + sign);
            return true;
        }

        log.info("第 " + attempt + " 次尝试加入");
        await genshin.returnMainUi();
        await sleep(500);

        // F2 打开好友 → 搜 UID
        await keyPress("F2");
        await sleep(2000);

        if (!await findAndClick(enterUIDRo)) {
            log.warn("没找到 UID 输入框");
            await genshin.returnMainUi();
            await sleep(1500);
            continue;
        }

        await sleep(800);
        inputText(hostUID);
        await sleep(800);

        if (!await findAndClick(searchRo)) {
            log.warn("搜索失败");
            await genshin.returnMainUi();
            await sleep(1500);
            continue;
        }
        await sleep(600);

        if (!await findAndClick(requestRo)) {
            log.warn("没找到申请加入按钮（房主可能不在线 / 已满员 / 关闭了联机）");
            await genshin.returnMainUi();
            await sleep(2000);
            continue;
        }

        log.info("已发送申请，等待房主放行…");
        // 等一会儿看进没进去
        const waitStart = Date.now();
        while (Date.now() - waitStart < 30000) {
            await sleep(2000);
            const s = await getPlayerSign();
            if (s >= 2) {
                log.info("加入成功，队伍编号 " + s);
                notification.send("已进入房主世界");
                return true;
            }
        }

        log.warn("申请没被放行，重试");
        await genshin.returnMainUi();
        await sleep(2000);
    }

    log.error("加入房主世界超时");
    notification.error("加不进房主世界，请检查 UID / 房主是否在线");
    return false;
}

// ═══════════════════════════════════════════════════
//  路线执行
// ═══════════════════════════════════════════════════

/**
 * 收集要跑的路线。
 *
 * 支持三种写法（每行一条，可以混用）：
 *   · 具体 json 文件 → 直接跑
 *   · 文件夹        → 里面所有 json 按文件名顺序跑
 *   · 空行 / # 开头 → 忽略（当注释）
 *
 * 路径写绝对路径或相对 BGI 根目录都行。
 */
async function collectRoutes() {
    const list = [];
    const lines = (routePath || "").split(/[\r\n]+/);

    for (let raw of lines) {
        const line = raw.trim();
        if (!line || line.startsWith("#")) continue;

        // 统一斜杠
        const p = line.replace(/\\/g, "/");

        // 是文件夹还是文件？
        let isDir = false;
        try { isDir = file.IsFolder(p); } catch { }

        if (isDir) {
            try {
                const entries = file.ReadPathSync(p);
                const jsons = entries
                    .filter(x => x.toLowerCase().endsWith(".json"))
                    .sort();
                for (const j of jsons) list.push(j);
                log.info("文件夹 " + p + " → " + jsons.length + " 个路线");
            } catch (e) {
                log.error("读取文件夹失败 " + p + "：" + e.message);
            }
        } else {
            // 文件：确认一下真的存在，避免路径写错还硬跑
            let exists = true;
            try { exists = file.Exists(p); } catch { }
            if (exists) {
                list.push(p);
            } else {
                log.error("路线文件不存在：" + p);
            }
        }
    }

    log.info("共收集到 " + list.length + " 条路线");
    return list;
}

/** 跑一条路线，同时并行拾取。 */
async function runOneRoute(path) {
    state = { running: true };

    const pathingTask = (async () => {
        await genshin.returnMainUi();
        log.info("开始跑：" + path);
        try {
            await pathingScript.runFile(path);
        } catch (e) {
            log.error("路线执行出错：" + e.message);
        }
        state.running = false;
    })();

    const tasks = [pathingTask];

    // 自动拾取
    if (autoPickup) {
        tasks.push((async () => {
            await autoPickupLoop();
        })());
    }

    await Promise.allSettled(tasks);
}

// ── 拾取相关配置 ──
let targetItems = [];          // 物品模板列表
let pickupDelay = 100;

/** 加载拾取物品模板（递归读 assets/targetItems 下所有 png）。 */
async function loadTargetItems() {
    const items = await readFolder("assets/targetItems", ".png");
    const loaded = [];
    for (const it of items) {
        try {
            loaded.push({
                template: file.ReadImageMatSync(it.fullPath),
                itemName: it.fileName.replace(/\.png$/i, "")
            });
        } catch { }
    }
    log.info("加载 " + loaded.length + " 个拾取物品模板");
    return loaded;
}

/**
 * 沿途拾取循环。
 *
 * 完整移植自原脚本（ArtifactsGroupPurchasing）的做法：
 *   ① 在左侧竖条找 F 图标（模板匹配，不是 OCR）
 *   ② 找到 F 后，在它右边匹配 itemName 物品图
 *   ③ 匹配上才按 F，避免乱捡
 *   ④ 找不到 F 就滚轮扫视野
 *
 * 没加载到物品图时退化为"看到 F 就按"，聊胜于无。
 */
async function autoPickupLoop() {
    const F_REGION = { x: 1102, y: 335, w: 34, h: 400 };
    const fIcon = file.ReadImageMatSync("assets/F_Dialogue.png");

    const fRo = RecognitionObject.TemplateMatch(fIcon, F_REGION.x, F_REGION.y,
                                                F_REGION.w, F_REGION.h);
    fRo.Threshold = 0.95;
    fRo.InitTemplate();

    let lastY = 0;
    let lastName = "";
    let lastRoll = new Date();
    let scrollUp = true;

    while (state.running) {
        // ① 找 F
        let centerY = 0;
        try {
            const region = captureGameRegion();
            const res = region.find(fRo);
            if (res.isExist()) centerY = Math.round(res.y + res.height / 2);
            region.dispose();
        } catch { }

        if (!centerY) {
            // 没有 F → 滚轮扫视野
            if (new Date() - lastRoll >= 200) {
                lastRoll = new Date();
                try {
                    await keyMouseScript.runFile(scrollUp
                        ? "assets/滚轮上翻.json"
                        : "assets/滚轮下翻.json");
                    scrollUp = !scrollUp;
                } catch { }
            }
            await sleep(60);
            continue;
        }

        // ② 在 F 右边匹配物品名
        const itemName = await matchItemAt(centerY);

        if (itemName) {
            // 同名 + 相近位置 = 已经捡过，别重复按
            if (Math.abs(lastY - centerY) <= 20 && lastName === itemName) {
                await sleep(pickupDelay * 2);
                lastY = -20;
                lastName = "";
            } else {
                keyPress("F");
                log.info('拾取："' + itemName + '"');
                lastY = centerY;
                lastName = itemName;
                await sleep(pickupDelay);
            }
        } else {
            // 不认识这个物品 → 也按一下（可能是宝箱、npc 对话之类）
            keyPress("F");
            lastY = centerY;
            lastName = "";
            await sleep(pickupDelay);
        }

        // ③ 滚轮扫一下，别死盯一个点
        const now = Date.now();
        if (now - lastRoll >= 200) {
            lastRoll = new Date(now);
            try {
                await keyMouseScript.runFile(scrollUp
                    ? "assets/滚轮上翻.json"
                    : "assets/滚轮下翻.json");
                scrollUp = !scrollUp;
            } catch { }
        }
    }

    /** 在 F 图标右边区域匹配物品名。 */
    async function matchItemAt(centerY) {
        if (targetItems.length === 0) return null;

        try {
            const region = captureGameRegion();
            for (const item of targetItems) {
                // 宽度按中文名长度估算（原脚本的做法）
                const cnLen = Math.min(
                    [...item.itemName].filter(ch => ch >= "\u4e00" && ch <= "\u9fff").length, 5);
                const w = 12 + 28 * cnLen + 2;

                const ro = RecognitionObject.TemplateMatch(
                    item.template, 1219, centerY - 15, w, 30);
                ro.Threshold = 0.9;
                ro.InitTemplate();

                if (region.find(ro).isExist()) {
                    region.dispose();
                    return item.itemName;
                }
            }
            region.dispose();
        } catch { }
        return null;
    }
}

// ═══════════════════════════════════════════════════
//  主流程
// ═══════════════════════════════════════════════════

(async () => {
    try {
        setGameMetrics(1920, 1080, 1);

        log.info("═══ AAA协同跑图 v1.0.0 ═══");
        log.info("角色：" + myRole + " · 目标人数：" + targetCount);
        log.info("路线：" + (routePath || "(未配置)"));

        // 检查路线配置
        if (!routePath) {
            log.error("没有配置路线，无法开始");
            notification.error("请先在设置里填路线路径");
            return;
        }

        // ★ 房主白名单全空 = 单人模式（自测用），直接跑，不等人
        if (isHost && permissionMode === "白名单" && whitelist.length === 0) {
            log.warn("白名单为空 → 单人模式，不组队直接跑");
            notification.send("单人模式开始");
        }

        // 房主加载白名单模板
        if (isHost && permissionMode === "白名单" && whitelist.length > 0) {
            await loadTemplates();
            if (templates.length === 0) {
                log.warn("targets 目录下没有名字图片，只能靠 OCR 识别");
            }
            selectedTargets = useAllTargetImages
                ? mapAllTemplatesToClosestNames(whitelist, templates)
                : selectClosestTemplates(whitelist, templates);
        }

        // ── 阶段 1：组队 ──
        let ready = false;
        if (isHost && targetCount < 2) {
            // 单人自测：不组队
            log.info("═══ 单人模式 ═══");
            log.info("白名单为空，不组队，直接跑路线");
            log.info("如果只想验证路线能不能跑通，这样就行");
            notification.send("单人模式开始");
            ready = true;
        } else if (isHost) {
            ready = await hostWaitForTeam();
        } else {
            ready = await memberJoinHost();
            if (ready) {
                // 队员进来后再等其他人
                log.info("等待其他队友…");
                const start = Date.now();
                while (Date.now() - start < waitSeconds * 1000) {
                    const n = await countPlayers();
                    if (n >= targetCount) { log.info("满员 " + n + " 人"); ready = true; break; }
                    await sleep(2000);
                }
            }
        }

        if (!ready) {
            log.warn("组队未完成，本次不跑");
            notification.error("组队失败，未开始跑图");
            return;
        }

        // ── 阶段 2：对齐起跑 ──
        const soloMode = (isHost && targetCount < 2);
        if (startDelay > 0 && !soloMode) {
            log.info(startDelay + " 秒后开跑（全队同时等待，用于对齐）");
            notification.send(startDelay + " 秒后开跑");
            await sleep(startDelay * 1000);
        }

        // ── 阶段 3：跑路线 ──
        const routes = await collectRoutes();
        if (routes.length === 0) {
            log.error("没有可执行的路线文件");
            notification.error("路线文件不存在");
            return;
        }

        // 加载拾取物品模板
        if (autoPickup) {
            targetItems = await loadTargetItems();
            if (targetItems.length === 0) {
                log.warn("assets/targetItems 下没有物品图，拾取时会看到 F 就按");
            }
        }

        log.info("共 " + routes.length + " 条路线，开始");
        notification.send("开始跑图（" + routes.length + " 条路线）");

        for (let i = 0; i < routes.length; i++) {
            log.info("[" + (i + 1) + "/" + routes.length + "] " + routes[i]);
            await runOneRoute(routes[i]);
            await sleep(1000);
        }

        log.info("全部路线完成");
        notification.send("协同跑图完成");

    } catch (e) {
        log.error("脚本异常：" + (e && e.message ? e.message : e));
        notification.error("脚本出错：" + (e && e.message ? e.message : e));
    } finally {
        try { await genshin.returnMainUi(); } catch { }
    }
})();
