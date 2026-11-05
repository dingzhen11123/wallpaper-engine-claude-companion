// 必须最先执行：Folia 的部分模块在加载时就会用到这些全局对象
import { Buffer } from 'buffer';

(globalThis as unknown as { Buffer: typeof Buffer }).Buffer = Buffer;

// 舞台里固定按 1920×1080 排版，再整体缩小到卡片大小。canvas 和 WebGL 样式按 devicePixelRatio 决定画布大小：
// 告诉它们真实的缩放比，就不用画一张比显示大得多的图再缩小，显卡负担按面积下降
const wanted = Number(new URLSearchParams(location.search).get('dpr'));
if (wanted > 0.2 && wanted <= 3) {
    try {
        Object.defineProperty(window, 'devicePixelRatio', { get: () => wanted, configurable: true });
    } catch { /* 改不了就按原样画 */ }
}
