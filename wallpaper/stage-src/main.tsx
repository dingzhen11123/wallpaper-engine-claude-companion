// 歌词舞台：壁纸里用 iframe 打开的小页面，只负责把歌词画出来，不播放声音。
// 写法照着 Folia 的 OBS 浏览器源（src/components/obs/ObsBrowserSourceApp.tsx），
// 区别是歌词和时钟不走 EventSource，而是由外层壁纸页面用 postMessage 传进来。
//
// 外层发来的消息：
//   { type: 'song', key, title, artist, duration, lrc, yrc, tlyric, lines }
//   { type: 'cover', url }
//   { type: 'clock', pos, playing }      pos 是毫秒，playing 为 true 时本页自己往前走
//   { type: 'pause', value }             壁纸被暂停：停止走动和重画
//   { type: 'config', mode, fontScale }
//   { type: 'audio', power, bass, lowMid, mid, vocal, treble, spectrum }   Wallpaper Engine 听到的声音，给样式做律动
// 本页发给外层：{ type: 'stage-ready' }、{ type: 'stage-error', message }
import './shims';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import ReactDOM from 'react-dom/client';
import { useMotionValue } from 'framer-motion';
import '@folia/i18n/config';
import '@folia/index.css';
import '@folia/stores/useMotionSettingsStore';
import VisualizerRenderer from '@folia/components/visualizer/VisualizerRenderer';
import { DEFAULT_THEME } from '@folia/services/baseThemes';
import { parseLyricsByFormat } from '@folia/utils/lyrics/parserCore';
import { NO_LYRIC_LINES } from '@folia/utils/lyrics/noLyricLines';
import { findLatestActiveLineIndex } from '@folia/utils/appPlaybackHelpers';
import { installGlobalVisualizerFrameRateLimiter } from '@folia/utils/frameRateLimiter';
import type { VisualizerSharedProps } from '@folia/components/visualizer/definition';
import type { Line, Word } from '@folia/types';

// 片头的演职员行不当歌词
const CREDIT = /^(作词|作曲|编曲|制作|制作人|混音|和声|吉他|贝斯|鼓|录音|人声录音|人声编辑|母带|出品|监制|策划|词|曲|Lyrics|Composer|Arranger|Producer)\s*[:：]/i;
const EMPTY_SPECTRUM = new Uint8Array(0) as Uint8Array<ArrayBuffer>;
// 背景透明：歌词直接浮在壁纸的天空上（放大模糊的封面又糊又不像一层）。自带场景的样式（雾笺、町景、花火……）不受影响
// 字幕用单独的主题：默认的次要色是深灰，浮在天空上看不清
const SUBTITLE_THEME = { ...DEFAULT_THEME, secondaryColor: '#e7e5e4' };
const BACKGROUND = { mode: 'common', transparent: true } as VisualizerSharedProps['background'];

interface Song {
    key: string;
    title: string;
    artist: string;
    duration: number;
    lines: Line[];
}

const str = (v: unknown) => (typeof v === 'string' ? v : '');
const num = (v: unknown, fallback = 0) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);

// Folia 歌词接口（/v1/lyric）给的行：text → fullText，其余字段同名
function linesFromApi(raw: unknown): Line[] {
    if (!Array.isArray(raw)) return [];
    const out: Line[] = [];
    for (const one of raw) {
        if (!one || typeof one !== 'object') continue;
        const l = one as Record<string, unknown>;
        const fullText = str(l.text);
        const startTime = num(l.startTime, NaN);
        const endTime = num(l.endTime, NaN);
        if (!fullText || !Number.isFinite(startTime) || !Number.isFinite(endTime)) continue;
        const words: Word[] = [];
        if (Array.isArray(l.words)) {
            for (const w of l.words) {
                const word = w as Record<string, unknown>;
                if (!word || typeof word.text !== 'string') continue;
                words.push({ text: word.text, startTime: num(word.startTime, startTime), endTime: num(word.endTime, endTime) });
            }
        }
        out.push({
            fullText,
            startTime,
            endTime,
            words: words.length ? words : [{ text: fullText, startTime, endTime }],
            translation: str(l.translation) || undefined,
            romanization: str(l.romanization) || undefined,
        });
    }
    return out;
}

function parseLines(msg: Record<string, unknown>): Line[] {
    const fromApi = linesFromApi(msg.lines);
    if (fromApi.length) return fromApi.filter(l => !CREDIT.test(l.fullText.trim()));
    const yrc = str(msg.yrc);
    const lrc = str(msg.lrc);
    const tlyric = str(msg.tlyric);
    let lines: Line[] = [];
    try {
        // 有逐字歌词优先用逐字；解析不出东西再退回逐行
        if (yrc) lines = parseLyricsByFormat('yrc', yrc, tlyric).lines;
        if (!lines.length && lrc) lines = parseLyricsByFormat('lrc', lrc, tlyric).lines;
    } catch (err) {
        report(err);
    }
    return lines.filter(l => !CREDIT.test(l.fullText.trim()));
}

function report(err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    console.error('[stage]', err);
    try { window.parent.postMessage({ type: 'stage-error', message }, '*'); } catch { /* 外层已关闭 */ }
}

const Stage: React.FC = () => {
    const [song, setSong] = useState<Song | null>(null);
    const [cover, setCover] = useState('');
    const [wePaused, setWePaused] = useState(false);
    const [mode, setMode] = useState('claddagh');
    const [fontScale, setFontScale] = useState(1);
    const [playing, setPlaying] = useState(false);
    const [lineIndex, setLineIndex] = useState(-1);
    const [duration, setDuration] = useState(0);
    const lineRef = useRef(-1);
    const linesRef = useRef<Line[]>([]);
    // 时钟：上次对时的位置（毫秒）和本页时间；放的时候每帧按流逝的时间往前走
    const clock = useRef({ pos: 0, at: performance.now(), playing: false });

    const currentTime = useMotionValue(0);
    const audioPower = useMotionValue(0);
    const bass = useMotionValue(0);
    const lowMid = useMotionValue(0);
    const mid = useMotionValue(0);
    const vocal = useMotionValue(0);
    const treble = useMotionValue(0);
    const spectrum = useMotionValue(EMPTY_SPECTRUM);
    const audioBands = useMemo(() => ({ bass, lowMid, mid, vocal, treble, spectrum }), [bass, lowMid, mid, vocal, treble, spectrum]);

    useEffect(() => {
        const onMessage = (ev: MessageEvent) => {
            if (ev.source !== window.parent || !ev.data || typeof ev.data !== 'object') return;
            const msg = ev.data as Record<string, unknown>;
            try {
                if (msg.type === 'song') {
                    const lines = parseLines(msg);
                    linesRef.current = lines;
                    lineRef.current = -1;
                    setLineIndex(-1);
                    setDuration(num(msg.duration) / 1000);
                    setSong({
                        key: str(msg.key),
                        title: str(msg.title),
                        artist: str(msg.artist),
                        duration: num(msg.duration) / 1000,
                        lines,
                    });
                } else if (msg.type === 'cover') {
                    setCover(str(msg.url));
                } else if (msg.type === 'clock') {
                    const next = !!msg.playing;
                    const pos = num(msg.pos);
                    const c = clock.current;
                    const shown = c.playing ? c.pos + (performance.now() - c.at) : c.pos;
                    // 每秒对一次时：差得很少就不动，歌词不会抖
                    if (next !== c.playing || Math.abs(pos - shown) > 150) clock.current = { pos, at: performance.now(), playing: next };
                    setPlaying(next);
                } else if (msg.type === 'pause') {
                    setWePaused(!!msg.value);
                } else if (msg.type === 'audio') {
                    audioPower.set(num(msg.power));
                    bass.set(num(msg.bass));
                    lowMid.set(num(msg.lowMid));
                    mid.set(num(msg.mid));
                    vocal.set(num(msg.vocal));
                    treble.set(num(msg.treble));
                    if (msg.spectrum instanceof Uint8Array) spectrum.set(msg.spectrum as Uint8Array<ArrayBuffer>);
                } else if (msg.type === 'config') {
                    if (typeof msg.mode === 'string' && msg.mode) setMode(msg.mode);
                    if (typeof msg.fontScale === 'number' && msg.fontScale > 0) setFontScale(msg.fontScale);
                }
            } catch (err) {
                report(err);
            }
        };
        window.addEventListener('message', onMessage);
        window.parent.postMessage({ type: 'stage-ready' }, '*');
        return () => window.removeEventListener('message', onMessage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    useEffect(() => {
        if (wePaused) return undefined;
        let frame = 0;
        const tick = () => {
            const c = clock.current;
            const ms = c.playing ? c.pos + (performance.now() - c.at) : c.pos;
            const seconds = ms / 1000;
            currentTime.set(seconds);
            const lines = linesRef.current;
            const next = lines.length ? findLatestActiveLineIndex(lines, seconds) : -1;
            if (next !== lineRef.current) {
                lineRef.current = next;
                setLineIndex(next);
            }
            frame = window.requestAnimationFrame(tick);
        };
        frame = window.requestAnimationFrame(tick);
        return () => window.cancelAnimationFrame(frame);
    }, [currentTime, wePaused]);

    return (
        <div style={{ width: '100vw', height: '100vh', overflow: 'hidden', backgroundColor: 'transparent', color: DEFAULT_THEME.primaryColor }}>
            {song && (
                <VisualizerRenderer
                    mode={mode}
                    currentTime={currentTime}
                    duration={duration}
                    currentLineIndex={lineIndex}
                    lines={song.lines.length ? song.lines : NO_LYRIC_LINES}
                    theme={DEFAULT_THEME}
                    subtitleTheme={SUBTITLE_THEME}
                    subtitleOverlayBackground={false}
                    subtitleOverlayOpacity={1}
                    subtitleFontScale={1.5}
                    isDaylight={false}
                    audioPower={audioPower}
                    audioBands={audioBands}
                    songTitle={song.title}
                    songArtist={song.artist}
                    coverUrl={cover || null}
                    seed={song.key}
                    showText
                    paused={!playing || wePaused}
                    background={BACKGROUND}
                    lyricsFontScale={fontScale}
                    isPlayerChromeHidden
                    showHarmonySubtitle={false}
                    showSubtitleTranslation
                />
            )}
        </div>
    );
};

class Boundary extends React.Component<{ children: React.ReactNode }, { failed: boolean }> {
    state = { failed: false };
    static getDerivedStateFromError() { return { failed: true }; }
    componentDidCatch(err: unknown) { report(err); }
    render() { return this.state.failed ? null : this.props.children; }
}

window.addEventListener('error', ev => report(ev.error || ev.message));
window.addEventListener('unhandledrejection', ev => report(ev.reason));
// 帧率限制要在样式开始动之前装好；壁纸里不需要超过 60 帧
installGlobalVisualizerFrameRateLimiter(60);

ReactDOM.createRoot(document.getElementById('root')!).render(<Boundary><Stage /></Boundary>);
