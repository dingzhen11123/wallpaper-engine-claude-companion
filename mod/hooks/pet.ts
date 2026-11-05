import type { PetAction, PetPhase } from '../types'

// 小 Claude：橙色像素小怪物。桌面版画成带 CSS 动画的 SVG，命令行用方块字符拼

const ORANGE = '#D97757'
const INK = '#1F1E1D'
const PAPER = '#EDE6DA'
const GREEN = '#7BC67B'
const BLUE = '#6FA8DC'
const GOLD = '#F0B37E'

// ---- 身体零件（单位是像素格，小怪物占左边 x 2~13，右边留给道具）----

const TORSO = '<rect x="4" y="3" width="7" height="6"/>'
const ARM_L = '<rect class="armL" x="2" y="5" width="2" height="2"/>'
const ARM_R = '<rect class="armR" x="11" y="5" width="2" height="2"/>'
const LEG_A = '<rect x="4" y="9" width="1" height="2"/><rect x="8" y="9" width="1" height="2"/>'
const LEG_B = '<rect x="6" y="9" width="1" height="2"/><rect x="10" y="9" width="1" height="2"/>'

const EYES_OPEN = '<rect class="eye" x="5" y="4" width="1" height="1"/><rect class="eye" x="9" y="4" width="1" height="1"/>'
const EYES_SHUT = '<rect x="5" y="4.4" width="1" height="0.3"/><rect x="9" y="4.4" width="1" height="0.3"/>'
const EYES_SAD = '<rect x="5" y="4.5" width="1" height="0.5"/><rect x="9" y="4.5" width="1" height="0.5"/>'
const EYES_HAPPY =
  '<rect x="5" y="4" width="1" height="0.4"/><rect x="9" y="4" width="1" height="0.4"/>' +
  '<rect x="4.7" y="4.3" width="0.4" height="0.4"/><rect x="5.9" y="4.3" width="0.4" height="0.4"/>' +
  '<rect x="8.7" y="4.3" width="0.4" height="0.4"/><rect x="9.9" y="4.3" width="0.4" height="0.4"/>'

type Eyes = 'open' | 'shut' | 'sad' | 'happy'
const EYES: Record<Eyes, string> = { open: EYES_OPEN, shut: EYES_SHUT, sad: EYES_SAD, happy: EYES_HAPPY }

// ---- 道具 ----

const BOOK =
  `<g class="prop"><rect x="5" y="5.4" width="5" height="2.8" fill="${PAPER}"/>` +
  '<rect x="7.4" y="5.4" width="0.2" height="2.8" fill="#B9A88F"/>' +
  '<rect class="txt" x="5.5" y="6" width="1.5" height="0.25" fill="#9A8F7E"/>' +
  '<rect class="txt t2" x="5.5" y="6.7" width="1.3" height="0.25" fill="#9A8F7E"/>' +
  '<rect class="txt t3" x="8" y="6" width="1.5" height="0.25" fill="#9A8F7E"/>' +
  '<rect class="txt t4" x="8" y="6.7" width="1.2" height="0.25" fill="#9A8F7E"/></g>'

const PENCIL =
  `<rect x="13.5" y="9.6" width="5.5" height="1.6" fill="${PAPER}"/>` +
  `<rect class="write" x="14" y="10.2" width="4" height="0.3" fill="${INK}"/>` +
  '<g class="pencil">' +
  '<rect x="14" y="8" width="0.9" height="0.9" fill="#E8B04B"/>' +
  '<rect x="14.8" y="7.2" width="0.9" height="0.9" fill="#E8B04B"/>' +
  '<rect x="15.6" y="6.4" width="0.9" height="0.9" fill="#E8B04B"/>' +
  '<rect x="16.4" y="5.6" width="0.9" height="0.9" fill="#E58A8A"/>' +
  `<rect x="13.5" y="8.8" width="0.6" height="0.6" fill="${INK}"/></g>`

const TERMINAL =
  '<rect x="14" y="3.5" width="5.6" height="5.6" fill="#2B2A27"/>' +
  '<rect x="14" y="3.5" width="5.6" height="0.8" fill="#45423D"/>' +
  `<rect class="ln" x="14.6" y="5" width="3" height="0.4" fill="${GREEN}"/>` +
  `<rect class="ln l2" x="14.6" y="6" width="2" height="0.4" fill="${GREEN}"/>` +
  `<rect class="ln l3" x="14.6" y="7" width="3.6" height="0.4" fill="${GREEN}"/>` +
  `<rect class="cursor" x="14.6" y="8" width="0.6" height="0.5" fill="${GREEN}"/>`

const MAGNIFIER =
  '<g class="mag">' +
  `<circle cx="16.3" cy="5" r="1.5" fill="rgba(160,200,240,0.25)" stroke="${PAPER}" stroke-width="0.5"/>` +
  `<line x1="17.4" y1="6.1" x2="18.8" y2="7.6" stroke="${PAPER}" stroke-width="0.7"/></g>`

const MINI =
  '<g transform="translate(14.6 5.2) scale(0.42)"><g class="mini">' +
  `<g fill="${ORANGE}"><rect x="4" y="3" width="7" height="6"/><rect x="2" y="5" width="11" height="2"/>${LEG_A}${LEG_B}</g>` +
  `<g fill="${INK}">${EYES_OPEN}</g></g></g>`

const CHECKLIST =
  `<rect x="14" y="2.8" width="5" height="6.6" fill="${PAPER}"/>` +
  [4, 5.8, 7.6]
    .map(
      (y, n) =>
        `<rect x="14.5" y="${y}" width="0.9" height="0.9" fill="#CFC6B6"/>` +
        `<rect class="tick k${n}" x="14.65" y="${y + 0.15}" width="0.6" height="0.6" fill="${GREEN}"/>` +
        `<rect x="15.8" y="${y + 0.3}" width="2.6" height="0.35" fill="#9A8F7E"/>`,
    )
    .join('')

const QUESTION = `<text class="q" x="14.6" y="4.6" fill="${ORANGE}" font-family="monospace" font-weight="bold" font-size="4.2">?</text>`

const SPARKS =
  `<rect class="spark" x="14" y="2" width="1" height="1" fill="${GOLD}"/>` +
  `<rect class="spark s2" x="0.6" y="2.5" width="1" height="1" fill="${GOLD}"/>` +
  `<rect class="spark s3" x="15.5" y="5" width="0.8" height="0.8" fill="${GOLD}"/>`

const CONFETTI = ['#F0B37E', '#7BC67B', '#6FA8DC', '#E58A8A', '#C9A0DC', '#F2D16B']
  .map(
    (color, n) =>
      `<rect class="conf c${n}" x="${1 + n * 3}" y="-1" width="0.8" height="0.8" fill="${color}"/>`,
  )
  .join('')

const ZZ =
  `<text class="z" x="13" y="3.5" fill="${ORANGE}" font-family="monospace" font-weight="bold" font-size="3">z</text>` +
  `<text class="z z2" x="14.5" y="3" fill="${ORANGE}" font-family="monospace" font-weight="bold" font-size="2.4">z</text>`

const DOTS = [0, 1, 2]
  .map(n => `<rect class="dot d${n}" x="${14 + n * 1.5}" y="2" width="0.8" height="0.8" fill="${ORANGE}"/>`)
  .join('')

const TEAR = `<rect class="tear" x="5" y="5.2" width="0.7" height="0.9" fill="${BLUE}"/>`
const SWEAT = `<rect class="sweat" x="11.4" y="2.2" width="0.7" height="1" fill="${BLUE}"/>`
const ELLIPSIS = [0, 1, 2].map(n => `<rect x="${14 + n * 1.2}" y="8" width="0.6" height="0.6" fill="#8A847A"/>`).join('')

// ---- 动画 ----

const BASE_CSS = `
* { transform-box: fill-box; }
.eye { transform-origin: center; animation: blink 4s infinite; }
.armL { transform-origin: 100% 50%; } .armR { transform-origin: 0% 50%; }
@keyframes blink { 0%, 92%, 100% { transform: scaleY(1); } 95% { transform: scaleY(0.1); } }
@keyframes bob { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-0.5px); } }
@keyframes idle { 0%, 100% { transform: translateY(0); } 25% { transform: translateY(-0.5px); } 50% { transform: translateY(0); }
  78% { transform: translateY(0); } 80% { transform: translateY(-1.6px); } 83% { transform: translateY(0); } }
@keyframes glance { 0%, 60%, 100% { transform: translateX(0); } 65%, 72% { transform: translateX(-0.6px); } 76%, 84% { transform: translateX(0.6px); } }
@keyframes look { 0%, 100% { transform: translateX(0); } 25% { transform: translateX(-0.6px); } 75% { transform: translateX(0.6px); } }
@keyframes scan { 0%, 100% { transform: translate(-0.5px, 0.4px); } 50% { transform: translate(0.5px, 0.4px); } }
@keyframes stepA { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-0.8px); } }
@keyframes jump { 0%, 60%, 100% { transform: translateY(0); } 25% { transform: translateY(-2.5px); } }
@keyframes shake { 0%, 100% { transform: translateX(0); } 25% { transform: translateX(-0.5px); } 75% { transform: translateX(0.5px); } }
@keyframes wave { from { transform: rotate(10deg); } to { transform: rotate(-60deg); } }
@keyframes scribble { 0%, 100% { transform: translate(0, 0); } 25% { transform: translate(0.6px, -0.3px); } 50% { transform: translate(1.2px, 0); } 75% { transform: translate(0.6px, 0.3px); } }
@keyframes grow { 0% { transform: scaleX(0); } 80%, 100% { transform: scaleX(1); } }
@keyframes pulse { 0%, 100% { opacity: 0.15; } 50% { opacity: 1; } }
@keyframes blinkOn { 0%, 49% { opacity: 1; } 50%, 100% { opacity: 0; } }
@keyframes sweep { 0%, 100% { transform: translateX(-1.5px); } 50% { transform: translateX(0.6px); } }
@keyframes twinkle { 0%, 100% { opacity: 0; transform: scale(0.4); } 50% { opacity: 1; transform: scale(1); } }
@keyframes drift { 0% { opacity: 0; transform: translate(0, 1px); } 30% { opacity: 1; } 100% { opacity: 0; transform: translate(1.5px, -3px); } }
@keyframes drip { 0% { opacity: 0; transform: translateY(0); } 20% { opacity: 1; } 100% { opacity: 0; transform: translateY(3px); } }
@keyframes fall { 0% { opacity: 0; transform: translateY(0) rotate(0); } 10% { opacity: 1; } 100% { opacity: 0; transform: translateY(13px) rotate(180deg); } }
@keyframes peek { 0%, 100% { transform: translate(0.6px, 0.4px) rotate(7deg); } 50% { transform: translate(0.8px, 0.6px) rotate(9deg); } }
@keyframes shrug { 0%, 100% { transform: translateY(0); } 30%, 60% { transform: translateY(-0.8px); } }
.txt { animation: pulse 1.6s infinite; } .t2 { animation-delay: 0.4s; } .t3 { animation-delay: 0.8s; } .t4 { animation-delay: 1.2s; }
.dot { animation: pulse 1.2s infinite; } .d1 { animation-delay: 0.2s; } .d2 { animation-delay: 0.4s; }
.spark { transform-origin: center; animation: twinkle 0.9s infinite; } .s2 { animation-delay: 0.3s; } .s3 { animation-delay: 0.6s; }
.z { animation: drift 3s infinite; } .z2 { animation-delay: 1.5s; }
.tear { animation: drip 1.4s infinite; } .sweat { animation: drip 2.2s infinite; }
.conf { transform-origin: center; animation: fall 1.8s linear infinite; }
.c1 { animation-delay: 0.3s; } .c2 { animation-delay: 0.9s; } .c3 { animation-delay: 0.5s; } .c4 { animation-delay: 1.2s; } .c5 { animation-delay: 0.7s; }
`

type Scene = { eyes: Eyes; css: string; legs?: string; front?: string; side?: string }

// 每种状态（干活时再按道具）的样子
function scene(phase: PetPhase, action: PetAction, isBig: boolean): Scene {
  switch (phase) {
    case 'greet':
      return {
        eyes: 'happy',
        css: '.body { animation: bob 1s ease-in-out infinite; } .armR { animation: wave 0.35s ease-in-out infinite alternate; }',
        side: SPARKS,
      }
    case 'sleepy':
      return { eyes: 'shut', css: '.body { animation: bob 3.2s ease-in-out infinite; }', side: ZZ }
    case 'peeking':
      return {
        eyes: 'open',
        css: '.all { animation: peek 1.4s ease-in-out infinite; } .eyes { transform: translate(0.5px, 0.5px); }',
      }
    case 'thinking':
      return {
        eyes: 'open',
        css: '.body { animation: bob 1.6s ease-in-out infinite; } .eyes { animation: look 2.4s steps(1) infinite; }',
        side: DOTS,
      }
    case 'asking':
      return {
        eyes: 'open',
        css: '.body { animation: bob 1.2s ease-in-out infinite; } .q { animation: pulse 1s infinite; }',
        side: QUESTION,
      }
    case 'working':
      return working(action)
    case 'done':
      return {
        eyes: 'happy',
        css: '.all { animation: jump 0.9s ease-out infinite; }',
        side: isBig ? CONFETTI + SPARKS : SPARKS,
      }
    case 'stopped':
      return {
        eyes: 'open',
        css: '.armL, .armR { animation: shrug 1.2s ease-in-out infinite; }',
        side: ELLIPSIS,
      }
    case 'error':
      return { eyes: 'sad', css: '.body { animation: shake 0.5s infinite; }', front: TEAR }
    default:
      // 等你输入：轻轻浮动，偶尔左右张望、原地小跳
      return {
        eyes: 'open',
        css: '.body { animation: idle 9s ease-in-out infinite; } .eyes { animation: glance 13s infinite; }',
      }
  }
}

const WALK = `<g class="legA">${LEG_A}</g><g class="legB">${LEG_B}</g>`
const WALK_CSS =
  '.legA { animation: stepA 0.4s steps(1) infinite; } .legB { animation: stepA 0.4s steps(1) infinite; animation-delay: 0.2s; }'

function working(action: PetAction): Scene {
  switch (action) {
    case 'read':
    case 'search':
      return {
        eyes: 'open',
        css: '.body { animation: bob 1.4s ease-in-out infinite; } .eyes { animation: scan 1.6s ease-in-out infinite; }',
        front: BOOK,
        side: action === 'search' ? MAGNIFIER + '<style>.mag { animation: sweep 1.4s ease-in-out infinite; }</style>' : '',
      }
    case 'edit':
      return {
        eyes: 'open',
        css: `.body { animation: bob 0.8s ease-in-out infinite; } .armR { animation: scribble 0.5s linear infinite; }
.pencil { animation: scribble 0.5s linear infinite; } .write { transform-origin: 0% 50%; animation: grow 2s steps(8) infinite; }`,
        side: PENCIL,
      }
    case 'run':
      return {
        eyes: 'open',
        css: `.body { animation: bob 0.6s ease-in-out infinite; } .eyes { transform: translateX(0.5px); }
.ln { animation: pulse 0.9s infinite; } .l2 { animation-delay: 0.3s; } .l3 { animation-delay: 0.6s; } .cursor { animation: blinkOn 0.8s infinite; }`,
        side: TERMINAL,
      }
    case 'web':
      return {
        eyes: 'open',
        css: '.body { animation: bob 1s ease-in-out infinite; } .mag { animation: sweep 1.4s ease-in-out infinite; } .eyes { animation: look 1.4s ease-in-out infinite; }',
        side: MAGNIFIER,
      }
    case 'agent':
      return {
        eyes: 'happy',
        css: '.body { animation: bob 0.8s ease-in-out infinite; } .mini { animation: jump 0.6s ease-out infinite; } .armR { animation: wave 0.5s ease-in-out infinite alternate; }',
        side: MINI,
      }
    case 'plan':
      return {
        eyes: 'open',
        css: `.body { animation: bob 1s ease-in-out infinite; } .eyes { transform: translateX(0.5px); }
.tick { opacity: 0; animation: pulse 2.4s infinite; } .k1 { animation-delay: 0.8s; } .k2 { animation-delay: 1.6s; }`,
        side: CHECKLIST,
      }
    default:
      return {
        eyes: 'open',
        css: `.body { animation: bob 0.4s ease-in-out infinite; } ${WALK_CSS}`,
        legs: WALK,
        side: SPARKS,
      }
  }
}

export type PetMood = { isBig: boolean; isSweaty: boolean }

// 桌面版的 SVG：同一状态的字符串不变，动画由 SVG 自己的 CSS 跑，不用重画
export function petSvg(phase: PetPhase, action: PetAction, mood: PetMood) {
  const s = scene(phase, phase === 'working' ? action : 'none', mood.isBig)
  const legs = s.legs ?? LEG_A + LEG_B
  const sweat = mood.isSweaty && phase !== 'error' ? SWEAT : ''
  return (
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 12" shape-rendering="crispEdges">' +
    `<style>${BASE_CSS}${s.css}</style>` +
    '<g class="all"><g class="body">' +
    `<g fill="${ORANGE}">${TORSO}${ARM_L}${ARM_R}${legs}</g>` +
    `<g class="eyes" fill="${INK}">${EYES[s.eyes]}</g>` +
    `${s.front ?? ''}${sweat}</g></g>${s.side ?? ''}</svg>`
  )
}

export const PET_ALT: Record<PetPhase, string> = {
  greet: '小 Claude 在跟你打招呼',
  idle: '小 Claude 在等你',
  sleepy: '小 Claude 在打瞌睡',
  peeking: '小 Claude 在偷看你打字',
  thinking: '小 Claude 在思考',
  working: '小 Claude 在干活',
  asking: '小 Claude 有问题想问你',
  done: '小 Claude 完成啦',
  stopped: '小 Claude 停下来了',
  error: '小 Claude 出错了',
}

// ---- 台词 ----

const LINES: Record<string, readonly string[]> = {
  greet: ['你好呀！', '又见面啦～', '今天做点什么？', '准备好啦'],
  idle: ['', '', '', '有什么要我做的吗？', '随时待命～', '我在这儿哦'],
  sleepy: ['（睡着了）', '梦到在写代码…', '呼…'],
  thinking: ['思考中', '让我想想', '嗯……', '整理一下思路', '在琢磨'],
  thinkingLong: ['还在想', '这个有点难，再想想', '快想出来了'],
  asking: ['想问你一下…', '需要你拿个主意', '等你回答～'],
  done: ['完成啦！', '搞定～', '交给你检查啦', '呼，做完了', '好啦！'],
  doneBig: ['终于搞定了！', '大工程完成！', '累坏了，但做完了！'],
  stopped: ['好的，先停下', '收到，停手', '嗯，听你的'],
  error: ['出错了…', '呜，失败了', '遇到问题了…'],
  peek: ['在写什么呢？', '偷看一下～', '嗯嗯，我在听', '慢慢写，不急'],
}

// 用一个数（通常是进入状态的时间）挑一句，同一次状态里不会来回变
export function pickLine(key: string, seed: number) {
  const list = LINES[key] ?? ['']
  const n = Math.abs(Math.floor(seed / 7) ^ 0x5bd1e995) % list.length
  return list[n] ?? ''
}

// 偷看时的台词：只看草稿的长短和有没有问句，不显示内容
export function peekLine(draft: string, seed: number) {
  const text = draft.trim()
  if (text === '') return '删掉啦？'
  if (/[?？]\s*$|吗\s*$/.test(text)) return '是要问我问题吗？'
  if (text.length > 120) return '写了好长呀'
  return pickLine('peek', seed)
}

// ---- 命令行：三行方块字符 ----

const HEAD = ' ▐▛███▜▌ '
const HEAD_BLINK = ' ▐█▀█▀█▌ '
const ARMS = '▝▜█████▛▘'
const FEET = ['  ▘▘ ▝▝  ', '  ▝▘ ▘▝  ']

// 返回三行：头（含眼睛）、手臂、脚；frame 推动眨眼和走路
export function petBlocks(phase: PetPhase, frame: number): [string, string, string] {
  const feet = FEET[0] as string
  const hop = frame % 2 === 0 ? feet : '         '
  switch (phase) {
    case 'working':
      return [HEAD, ARMS, FEET[frame % 2] as string]
    case 'thinking':
      return [frame % 2 === 0 ? ' ▐▛███▜▌ ' : ' ▐▜███▛▌ ', ARMS, feet]
    case 'peeking':
      return [' ▐▙███▟▌ ', ARMS, feet]
    case 'sleepy':
      return [' ▐▀▀█▀▀▌ ', ARMS, feet]
    case 'error':
      return [' ▐▄███▄▌ ', ARMS, feet]
    case 'greet':
    case 'done':
      return [HEAD, ARMS, hop]
    default:
      return [frame % 4 === 3 ? HEAD_BLINK : HEAD, ARMS, feet]
  }
}

// 命令行旁边那句话的小尾巴：思考中的省略号、打瞌睡的 z、提问的问号
export function petTail(phase: PetPhase, frame: number) {
  if (phase === 'thinking') return '.'.repeat((frame % 3) + 1)
  if (phase === 'sleepy') return ['z', 'zZ', 'zZz'][frame % 3] ?? ''
  if (phase === 'asking') return frame % 2 === 0 ? ' ?' : ''
  return ''
}
