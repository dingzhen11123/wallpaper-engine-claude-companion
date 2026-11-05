// 打包歌词舞台：node stage-src/build.mjs（设 STAGE_REPORT=1 时列出体积情况）
// 源码和依赖都用 Folia 仓库里的（用 FOLIA_DIR 指定，默认是上一级的 folia-major；不改那个仓库），产物放到 stage/。
// Wallpaper Engine 里的壁纸只能可靠地打开本地文件（iframe 指向 http 时画面出不来），file:// 下又用不了 ES module，
// 所以 17 种样式打成一个 iife 单文件。只在放歌时才加载，脚本是惰性编译的，没用到的样式基本不占内存。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const FOLIA = path.resolve(process.env.FOLIA_DIR || '../folia-major');
const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.join(here, '..', 'stage');
const from = rel => pathToFileURL(path.join(FOLIA, rel)).href;
const mod = rel => path.join(FOLIA, 'node_modules', rel);
const slash = p => p.replace(/\\/g, '/');

const { build } = await import(from('node_modules/vite/dist/node/index.js'));
const { default: react } = await import(from('node_modules/@vitejs/plugin-react/dist/index.js'));
const { default: tailwind } = await import(from('node_modules/@tailwindcss/postcss/dist/index.mjs'));

// 舞台上没有界面文字，不带五种语言的语言包
const SWAPS = [
    [/\/src\/i18n\/config(\.ts)?$/, slash(path.join(here, 'i18n-stub.ts'))],
    // 全量图标会生成 1900 个小文件，舞台用不到
    [/services\/lucideIconResolver(\.ts)?$/, slash(path.join(here, 'lucide-stub.ts'))],
    // 舞台直接在主线程解析歌词，不用 Worker（file:// 下起不来）
    [/utils\/lyrics\/workerClient(\.ts)?$/, slash(path.join(here, 'worker-stub.ts'))],
];
const swap = {
    name: 'stage-swap',
    enforce: 'pre',
    resolveId(source, importer) {
        if (!importer) return null;
        const full = source.startsWith('.')
            ? slash(path.resolve(path.dirname(importer.split('?')[0]), source))
            : source.startsWith('@folia/') ? slash(path.join(FOLIA, 'src', source.slice(7)))
            : /^[A-Za-z]:[\\/]/.test(source) ? slash(source) : null;
        if (!full) return null;
        for (const [re, to] of SWAPS) if (re.test(full)) return to;
        return null;
    },
};

// 样式入口（entry.tsx）顺带引用了设置面板和快捷控件，它们会牵出音乐平台服务、语言包。
// 舞台没有这些界面：保留导出的名字，内容全换成空组件，入口文件本身不用改。
// VideoLayer 是视频背景，舞台不用，它会牵出播放状态、全部音乐平台服务和繁简转换库（压缩前约 1.2MB）
const UI_ONLY = /(SettingsPanels?|SettingsCard|settingsPanels|BackgroundEffectPanel|BackgroundEffectSettings|DitheringSettings|FlutedGlassSettings|HalftoneDotsSettings|LensDistortionSettings|PaperTextureSettings|QuickControlChip|videoLayer\/VideoLayer|createCoverUrlResolver|analysis\/trackAudio)\.tsx?$/;
const stubUi = {
    name: 'stage-stub-ui',
    enforce: 'pre',
    load(id) {
        const file = id.split('?')[0];
        if (!UI_ONLY.test(slash(file)) || !fs.existsSync(file)) return null;
        const code = fs.readFileSync(file, 'utf8');
        const names = new Set();
        for (const m of code.matchAll(/export\s+(?:async\s+)?(?:const|let|var|function\*?|class)\s+([A-Za-z_$][\w$]*)/g)) names.add(m[1]);
        for (const m of code.matchAll(/export\s*\{([^}]*)\}/g)) {
            for (const part of m[1].split(',')) {
                const t = part.trim();
                if (!t || t.startsWith('type ')) continue;
                names.add(t.split(/\s+as\s+/).pop().trim());
            }
        }
        names.delete('default');
        const hasDefault = /export\s+default\b/.test(code) || /\bas\s+default\b/.test(code);
        const body = ['const Stub = () => null;'];
        for (const n of names) body.push(`export const ${n} = Stub;`);
        if (hasDefault) body.push('export default Stub;');
        return { code: body.join('\n'), moduleSideEffects: false };
    },
};

// 雾笺的笔顺数据 4MB，file:// 下 fetch 不到本地文件：从桥接服务取，用到这个样式才下载
const strokesUrl = {
    name: 'stage-strokes-url',
    enforce: 'pre',
    transform(code, id) {
        if (!/nebbiaStrokes\.ts/.test(id)) return null;
        const out = code.replace(/`\$\{import\.meta\.env\.BASE_URL\}nebbia-strokes\.json`/, "'http://127.0.0.1:47321/stage/nebbia-strokes.json'");
        if (out === code) this.warn('没能改写 nebbia-strokes.json 的地址');
        return { code: out, map: null };
    },
};

const report = {
    name: 'stage-report',
    generateBundle(_opts, bundle) {
        if (!process.env.STAGE_REPORT) return;
        const sizes = new Map();
        for (const chunk of Object.values(bundle)) {
            if (chunk.type !== 'chunk') continue;
            for (const [id, m] of Object.entries(chunk.modules)) {
                const norm = slash(id);
                let key;
                if (norm.includes('/node_modules/')) {
                    const parts = norm.split('/node_modules/').pop().split('/');
                    key = 'npm:' + (parts[0].startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]);
                } else key = norm.split('/src/').pop().split('/').slice(0, 2).join('/');
                sizes.set(key, (sizes.get(key) || 0) + m.renderedLength);
            }
        }
        for (const [k, v] of [...sizes].sort((a, b) => b[1] - a[1]).slice(0, 34)) {
            console.log(String(Math.round(v / 1024)).padStart(7) + ' KB  ' + k);
        }
        // STAGE_WHY=正则：打印从入口到匹配模块的最短引用链，查是谁拖进来的
        if (process.env.STAGE_WHY) {
            const res = process.env.STAGE_WHY.split('|').map(x => new RegExp(x));
            const short = id => slash(id).split('/src/').pop().split('/node_modules/').pop().split('?')[0];
            const entry = [...this.getModuleIds()].find(id => this.getModuleInfo(id)?.isEntry);
            const seen = new Map([[entry, null]]);
            const queue = [entry];
            const found = new Set();
            while (queue.length) {
                const id = queue.shift();
                res.forEach((re, n) => {
                    if (found.has(n) || !re.test(slash(id))) return;
                    found.add(n);
                    const chain = [];
                    for (let cur = id; cur; cur = seen.get(cur)) chain.unshift(short(cur));
                    console.log('WHY ' + chain.join(' -> '));
                });
                const info = this.getModuleInfo(id);
                for (const dep of [...(info?.importedIds || []), ...(info?.dynamicallyImportedIds || [])]) {
                    if (!seen.has(dep)) { seen.set(dep, id); queue.push(dep); }
                }
            }
        }
    },
};

const plugins = () => [swap, stubUi, strokesUrl];
process.chdir(FOLIA); // Tailwind 从当前目录扫描用到的类名

await build({
    configFile: false,
    root: here,
    base: './',
    logLevel: 'warn',
    mode: 'production',
    plugins: [...plugins(), react(), report],
    worker: { format: 'es', plugins },
    css: { postcss: { plugins: [tailwind()] } },
    define: {
        'process.env.NODE_ENV': JSON.stringify('production'),
        __COMMIT_HASH__: JSON.stringify('stage'),
        __GIT_BRANCH__: JSON.stringify('stage'),
        __BUILD_REPO__: JSON.stringify(''),
        __BUILD_COMMIT__: JSON.stringify(''),
        __BUILD_KIND__: JSON.stringify('stage'),
        __BUILD_HOST__: JSON.stringify(''),
        __APP_VERSION__: JSON.stringify('stage'),
        __APP_VERSION_LABEL__: JSON.stringify('stage'),
        __APP_RELEASE_CHANNEL__: JSON.stringify('stage'),
        __DOCKER_STACK_VERSION__: JSON.stringify(''),
    },
    resolve: {
        alias: [
            { find: /^@folia\/(.*)$/, replacement: slash(path.join(FOLIA, 'src')) + '/$1' },
            { find: /^react$/, replacement: mod('react') },
            { find: /^react\/(.*)$/, replacement: mod('react') + '/$1' },
            { find: /^react-dom$/, replacement: mod('react-dom') },
            { find: /^react-dom\/(.*)$/, replacement: mod('react-dom') + '/$1' },
            { find: /^framer-motion$/, replacement: mod('framer-motion') },
            { find: /^buffer$/, replacement: mod('buffer') },
            // 解析 TTML 才用得到，舞台只解析 LRC 和 YRC
            { find: /^@xmldom\/xmldom$/, replacement: slash(path.join(here, 'empty.ts')) },
            { find: /^i18next$/, replacement: mod('i18next') },
            { find: /^react-i18next$/, replacement: mod('react-i18next') },
        ],
    },
    build: {
        outDir,
        emptyOutDir: true,
        minify: true,
        sourcemap: false,
        chunkSizeWarningLimit: 1024 * 16,
        target: 'chrome120',
        assetsInlineLimit: 1024 * 32,
        cssCodeSplit: false,
        rollupOptions: {
            input: path.join(here, 'main.tsx'),
            output: {
                format: 'iife',
                inlineDynamicImports: true,
                entryFileNames: 'stage.js',
                assetFileNames: 'stage[extname]',
            },
        },
    },
});

fs.copyFileSync(path.join(here, 'index.html'), path.join(outDir, 'index.html'));
// 雾笺样式要用的笔顺数据：Folia 打包时生成，直接拿它的
const strokes = path.join(FOLIA, 'dist', 'nebbia-strokes.json');
if (fs.existsSync(strokes)) fs.copyFileSync(strokes, path.join(outDir, 'nebbia-strokes.json'));
else console.warn('没找到 nebbia-strokes.json，雾笺样式会缺字');
// Folia 是 AGPL-3.0：产物里带上许可证和来源说明
fs.copyFileSync(path.join(FOLIA, 'LICENSE'), path.join(outDir, 'LICENSE.txt'));
fs.cpSync(path.join(FOLIA, 'dist', 'licenses'), path.join(outDir, 'licenses'), { recursive: true });
fs.writeFileSync(path.join(outDir, 'NOTICE.txt'), [
    '本目录是用 Folia（https://github.com/chthollyphile/folia-major，AGPL-3.0）的歌词渲染代码打包出来的。',
    '打包入口和替换的模块在上一级的 stage-src/，Folia 源码未修改。',
    '如果要把这张壁纸分发给别人（比如上传创意工坊），需要遵守 AGPL-3.0，提供对应的完整源码。',
    '',
].join('\n'));
console.log('打包完成');
