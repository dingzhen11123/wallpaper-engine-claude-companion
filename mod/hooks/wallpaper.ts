import type { RenderSurface, SessionRateLimit } from 'claude-code'

import type { Info, Pet, PetPhase } from '../types'
import { petSvg, pickLine } from './pet'

// 桌面壁纸联动：把小玩偶的状态拼成一条 JSON，由 register.tsx 推给本机的壁纸桥接服务
// （~/.claude/wallpaper-bridge），Wallpaper Engine 壁纸从那里取。这里只放纯函数。

export const WALLPAPER_URL = 'http://127.0.0.1:47321'
// 状态没变时的心跳间隔；桥接服务 20 秒收不到心跳就当会话已关闭
export const WALLPAPER_HEARTBEAT_MS = 3_000
// 连不上服务时，隔这么久再试
export const WALLPAPER_RETRY_MS = 15_000
// 额度变化很慢，隔这么久才重新读一次
export const WALLPAPER_LIMITS_MS = 30_000
// 请求最多等这么久，免得关闭会话时被卡住
export const WALLPAPER_TIMEOUT_MS = 2_000

const LONG_TURN_MS = 120_000
const SWEATY_PERCENT = 80

export type WallpaperLimit = { kind: string; percent: number; resetsAt: string | null }

export const toLimit = (one: SessionRateLimit): WallpaperLimit => ({
  kind: one.kind,
  percent: one.percentUsed,
  resetsAt: one.resetsAt ?? null,
})

const baseName = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path

// 桌面版没选文件夹的会话跑在临时目录里，名字没意义
export function projectName(cwd: string) {
  if (/[\\/]scratch-workspaces[\\/]/i.test(cwd)) return '临时会话'
  return baseName(cwd) || cwd
}

// 同一个样子的 SVG 只拼一次
let svgKey = ''
let svg = ''
function cachedSvg(phase: PetPhase, p: Pet, isSweaty: boolean) {
  const key = `${phase}|${p.action}|${p.isBig}|${isSweaty}`
  if (key !== svgKey) {
    svgKey = key
    svg = petSvg(phase, p.action, { isBig: p.isBig, isSweaty })
  }
  return svg
}

// 会话标题：取你发的第一句话的第一行，去掉标签和多余空白，最多 40 个字；斜杠命令不算
// 自动生成的文字（上下文压缩后的续接说明、命令输出提示、中断提示）不当标题
const AUTO_TITLE = /^(This session is being continued|Caveat:|\[Request interrupted)/i
export function isAutoTitle(text: string) {
  return AUTO_TITLE.test(text.trim())
}

export function titleOf(text: string | undefined) {
  const line = String(text ?? '')
    .replace(/<[^>]*>/g, ' ')
    .split(/\r?\n/)
    .map(one => one.replace(/\s+/g, ' ').trim())
    .find(one => one !== '')
  if (line === undefined || line.startsWith('/') || isAutoTitle(line)) return ''
  const chars = [...line]
  return chars.length > 40 ? chars.slice(0, 40).join('') + '…' : line
}

export type WallpaperArgs = {
  id: string
  title: string
  cwd: string
  surface: RenderSurface | null
  pet: Pet
  info: Info | null
  limits: readonly WallpaperLimit[]
  now: number
}

export function wallpaperBody({ id, title, cwd, surface, pet, info, limits, now }: WallpaperArgs) {
  const percent = info?.percent ?? null
  const isSweaty = percent !== null && percent >= SWEATY_PERCENT
  const isThinkingLong = pet.phase === 'thinking' && now - pet.turnSince > LONG_TURN_MS
  return JSON.stringify({
    id,
    title,
    project: projectName(cwd),
    cwd,
    surface,
    phase: pet.phase,
    action: pet.action,
    label: pet.label,
    line: isThinkingLong ? pickLine('thinkingLong', pet.turnSince) : pet.line,
    isBig: pet.isBig,
    since: pet.since,
    turnSince: pet.turnSince,
    model: info?.model ?? '',
    cost: info?.cost ?? null,
    isSubscription: info?.isSubscription ?? null,
    percent,
    limits,
    svg: cachedSvg(pet.phase, pet, isSweaty),
  })
}

// 桥接服务的回复：{"pokes": n}，n 是壁纸上戳了几下
export function pokesOf(text: string) {
  try {
    const n = Number((JSON.parse(text) as { pokes?: unknown }).pokes)
    return Number.isFinite(n) && n > 0 ? n : 0
  } catch {
    return 0
  }
}
