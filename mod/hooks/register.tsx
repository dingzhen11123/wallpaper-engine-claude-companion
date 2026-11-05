import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, RenderSurface } from 'claude-code'

import type { Change, Info, Pet, PetAction, PetPhase } from '../types'
import { PET_ALT, peekLine, petBlocks, petSvg, petTail, pickLine } from './pet'
import type { WallpaperLimit } from './wallpaper'
import {
  WALLPAPER_HEARTBEAT_MS,
  WALLPAPER_LIMITS_MS,
  WALLPAPER_RETRY_MS,
  WALLPAPER_TIMEOUT_MS,
  WALLPAPER_URL,
  pokesOf,
  titleOf,
  isAutoTitle,
  toLimit,
  wallpaperBody,
} from './wallpaper'

const PANE = 'desktop-feel-changes'
const NOTIFY_AFTER_MS = 30_000
const WARN_RATIO = 0.8
const TICK_MS = 400
const IDLE_FRAME_MS = 2_400
const CHEER_MS = 4_000
const GREET_MS = 5_000
const PEEK_MS = 3_000
const SLEEPY_AFTER_MS = 120_000
const LONG_TURN_MS = 120_000
const SWEATY_PERCENT = 80

const changes = atom({ plugin: 'desktop-feel', key: 'changes' } as const, [])
const budget = atom({ plugin: 'desktop-feel', key: 'budget' } as const, null)
const spent = atom({ plugin: 'desktop-feel', key: 'spent' } as const, 0)
const isBandHidden = atom({ plugin: 'desktop-feel', key: 'isBandHidden' } as const, false)
const warned = atom({ plugin: 'desktop-feel', key: 'warned' } as const, 0)
const pet = atom({ plugin: 'desktop-feel', key: 'pet' } as const, {
  phase: 'idle',
  action: 'none',
  label: '',
  line: '',
  since: 0,
  turnSince: 0,
  isBig: false,
} as Pet)
const frame = atom({ plugin: 'desktop-feel', key: 'frame' } as const, 0)
const info = atom({ plugin: 'desktop-feel', key: 'info' } as const, null)

const usd = (n: number) => `$${n.toFixed(2)}`

const shortModel = (id: string) =>
  id.replace(/\[.*\]$/, '').replace(/^claude-/, '').replace(/-(\d+)-(\d+)$/, ' $1.$2')

const baseName = (path: string) => path.split(/[\\/]/).pop() ?? path

// ---- 小玩偶 ----

const BUSY: readonly PetPhase[] = ['thinking', 'working', 'asking']
// 这些状态过一会儿自动回到空闲
const FLEETING: Partial<Record<PetPhase, number>> = {
  greet: GREET_MS,
  done: CHEER_MS,
  stopped: CHEER_MS,
  error: CHEER_MS,
  peeking: PEEK_MS,
}

// 命令行里跑的是哪个程序：取第一个词，去掉路径
function commandName(command: unknown) {
  if (typeof command !== 'string') return ''
  // 带引号的程序路径里可能有空格，先整段取出引号里的内容
  const match = /^\s*(?:"([^"]+)"|'([^']+)'|(\S+))/.exec(command)
  const first = match?.[1] ?? match?.[2] ?? match?.[3] ?? ''
  return baseName(first).slice(0, 14)
}

// 工具 → 玩偶的道具和说的话；问你问题的工具单独成「提问」状态
function toolPose(tool: string, input: Record<string, unknown>): { phase: PetPhase; action: PetAction; label: string } {
  const file = typeof input.file_path === 'string' ? baseName(input.file_path) : ''
  const work = (action: PetAction, label: string) => ({ phase: 'working' as const, action, label })
  switch (tool) {
    case 'Edit':
    case 'MultiEdit':
    case 'NotebookEdit':
      return work('edit', file ? `在改 ${file}` : '在改文件')
    case 'Write':
      return work('edit', file ? `在写 ${file}` : '在写文件')
    case 'Read':
      return work('read', file ? `在看 ${file}` : '在看文件')
    case 'Grep':
    case 'Glob':
      return work('search', '在翻找代码')
    case 'Bash':
    case 'PowerShell': {
      const name = commandName(input.command)
      return work('run', name ? `在跑 ${name}` : '在跑命令')
    }
    case 'WebFetch':
    case 'WebSearch':
      return work('web', '在查资料')
    case 'Agent':
    case 'Task':
      return work('agent', '派小助手去干活')
    case 'TodoWrite':
      return work('plan', '在列计划')
    case 'AskUserQuestion':
      return { phase: 'asking', action: 'none', label: '' }
    case 'ExitPlanMode':
      return { phase: 'asking', action: 'none', label: '方案写好了，等你看看' }
    default:
      return work('tool', tool.startsWith('mcp__') ? '在用外部工具' : `在用 ${tool}`)
  }
}

type PoseArgs = {
  action?: PetAction
  label?: string
  line?: string
  isBig?: boolean
  isTurnStart?: boolean
  // 写入那一刻再确认一次，不满足就不换（避免慢一步的回调覆盖新状态）
  when?: (p: Pet) => boolean
}

const BUSY_TYPING = '我还在忙，等我一下哈'

// 撤掉临时台词后，恢复这个状态原本该说的话（种子和换状态时一样，所以是同一句）
const restoreLine = (p: Pet) =>
  p.label !== '' ? p.label : pickLine(p.phase === 'done' && p.isBig ? 'doneBig' : p.phase, p.since)

// 换状态，同时挑好这一次要说的台词
async function setPhase($: EngineInterface, phase: PetPhase, args: PoseArgs = {}) {
  const now = await $.clock.now()
  const action = args.action ?? 'none'
  const label = args.label ?? ''
  const isBig = args.isBig ?? false
  const line =
    args.line ??
    (phase === 'working' || label !== ''
      ? label
      : pickLine(phase === 'done' && isBig ? 'doneBig' : phase, now))
  await update($, pet, (p): Pet =>
    args.when && !args.when(p)
      ? p
      : { ...p, phase, action, label, line, since: now, isBig, turnSince: args.isTurnStart ? now : p.turnSince },
  )
}

// 定时器：短暂状态到点回到空闲，空闲久了打瞌睡；顺便推进命令行字符画的帧
async function tick($: EngineInterface) {
  const now = await $.clock.now()
  const p = await read($, pet)

  const limit = FLEETING[p.phase]
  const isOver = limit !== undefined && now - p.since > limit
  const isSleepy = p.phase === 'idle' && now - p.since > SLEEPY_AFTER_MS
  if (isOver || isSleepy) {
    const phase: PetPhase = isOver ? 'idle' : 'sleepy'
    const line = pickLine(phase, now)
    // 写入时再确认一次：期间若已换了状态，就不要覆盖
    await update($, pet, (one): Pet =>
      one.phase === p.phase && one.since === p.since
        ? { ...one, phase, action: 'none', label: '', line, since: now, isBig: false }
        : one,
    )
  }

  // 忙的时候你打过字：停手一会儿就撤掉「我还在忙」，恢复原来的台词
  if (BUSY.includes(p.phase) && p.line === BUSY_TYPING && now - lastTypedAt > PEEK_MS) {
    await update($, pet, (one): Pet =>
      one.since === p.since && one.line === BUSY_TYPING ? { ...one, line: restoreLine(one) } : one,
    )
  }

  const isBusy = BUSY.includes(p.phase)
  const next = Math.floor(now / (isBusy ? TICK_MS : IDLE_FRAME_MS))
  if (next !== (await read($, frame))) await update($, frame, () => next)

  // 同步到桌面壁纸（不等它，失败也不影响这里）
  void syncWallpaper($).catch(() => undefined)
}

// ---- 桌面壁纸联动 ----

const wall = {
  sentBody: '',
  sentKey: '',
  sentAt: 0,
  sentId: '',
  failedAt: 0,
  isSyncing: false,
  // 会话正在结束：不再推送，在途的结果也不再记下
  isEnded: false,
  // 底层请求还没结束就不发新的（超时只是不再等它，请求本身取消不了）
  inflight: null as Promise<unknown> | null,
  limits: [] as WallpaperLimit[],
  limitsAt: 0,
  // 会话标题：你在这个会话里发的第一句话（壁纸卡片上显示，比项目名好认）
  title: '',
  titleTried: false,
  // 会话代次：/clear 或换会话时加一，过期的标题查询结果直接丢掉
  gen: 0,
}

// 发给壁纸桥接服务；最多等 timeoutMs，非 2xx 也算失败
async function postWallpaper($: EngineInterface, path: string, body: string, timeoutMs = WALLPAPER_TIMEOUT_MS) {
  const request = $.http.fetch(`${WALLPAPER_URL}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body,
  })
  const settled = request.then(
    () => undefined,
    () => undefined,
  )
  wall.inflight = settled
  void settled.then(() => {
    if (wall.inflight === settled) wall.inflight = null
  })
  const timeout = $.clock.sleep(timeoutMs).then((): never => {
    throw new Error('timeout')
  })
  const res = await Promise.race([request, timeout])
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res
}

// 状态变了马上发，没变就隔一会儿发一次心跳；服务没开就退避，不影响别的
async function syncWallpaper($: EngineInterface) {
  // 第一个 await 之前就占住，定时器再快也不会并发
  if (wall.isSyncing || wall.isEnded || wall.inflight !== null) return
  wall.isSyncing = true
  let now = 0
  try {
    now = await $.clock.now()
    if (wall.failedAt > 0 && now - wall.failedAt < WALLPAPER_RETRY_MS) return

    if (now - wall.limitsAt >= WALLPAPER_LIMITS_MS) {
      wall.limitsAt = now
      const usage = await $.session.usage().catch(() => undefined)
      if (usage !== undefined) wall.limits = usage.rateLimits.map(toLimit)
    }

    // 先比一个小指纹：没变化、也没到心跳时间就什么都不做（每 400 毫秒都会走到这里）
    const [p, i] = await Promise.all([read($, pet), read($, info)])
    const key = JSON.stringify([p, i, wall.limits, wall.title])
    if (key === wall.sentKey && now - wall.sentAt < WALLPAPER_HEARTBEAT_MS) return

    const [id, cwd, surface] = await Promise.all([
      $.session.id(),
      $.session.cwd(),
      $.session.surface().catch(() => null),
    ])

    // /clear 之后会话 id 会变，旧的那张卡片先撤掉
    if (wall.sentId !== '' && wall.sentId !== id) {
      await postWallpaper($, '/end', wall.sentId).catch(() => undefined)
      wall.sentId = ''
      wall.sentBody = ''
      wall.sentKey = ''
      wall.title = ''
      wall.titleTried = false
      wall.gen++
    }

    // 还没有标题（比如 mod 刚重新加载）：先查存档，再翻聊天记录
    if (wall.title === '' && !wall.titleTried) {
      wall.titleTried = true
      const gen = wall.gen
      const found = await findTitle($, id).catch(() => '')
      if (gen === wall.gen && wall.title === '' && found !== '') wall.title = found
    }

    const body = wallpaperBody({ id, title: wall.title, cwd, surface, pet: p, info: i, limits: wall.limits, now })
    if (body === wall.sentBody && now - wall.sentAt < WALLPAPER_HEARTBEAT_MS) {
      wall.sentKey = key
      return
    }

    // 前面几步 await 期间会话可能已经结束：别再把卡片推回去
    if (wall.isEnded) return
    const res = await postWallpaper($, '/session', body)
    // 等回复期间会话结束了：这条结果不算数
    if (wall.isEnded) return
    wall.sentBody = body
    wall.sentKey = key
    wall.sentAt = now
    wall.sentId = id
    wall.failedAt = 0

    if (pokesOf(res.text) > 0) await onPoke($)
  } catch {
    wall.failedAt = now
    // 服务重启后要整条重发
    wall.sentBody = ''
    wall.sentKey = ''
  } finally {
    wall.isSyncing = false
  }
}

// 会话结束：先等在途的推送落地，再撤卡片，免得卡片被晚到的推送「复活」
// 会话标题：先看按会话 ID 存下的；没有就翻聊天记录取第一句话。
// 记录超过上限（只拿得到最近的一部分）时不猜，返回空，壁纸上退回显示项目名
const MESSAGE_LIMIT = 4096
async function findTitle($: EngineInterface, id: string) {
  const saved = await $.store.get('titles').catch(() => undefined)
  const map = saved !== null && typeof saved === 'object' ? (saved as Record<string, unknown>) : {}
  const hit = map[id]
  if (typeof hit === 'string' && !isAutoTitle(hit)) return hit
  const messages = await $.session.messages().catch(() => [])
  if (!Array.isArray(messages) || messages.length >= MESSAGE_LIMIT) return ''
  const first = messages.find(m => m.role === 'user' && titleOf(m.text) !== '')
  return first === undefined ? '' : titleOf(first.text)
}

// 按会话 ID 存标题，只留最近 50 个
async function saveTitle($: EngineInterface, id: string, title: string) {
  const saved = await $.store.get('titles').catch(() => undefined)
  const map = saved !== null && typeof saved === 'object' ? { ...(saved as Record<string, string>) } : {}
  delete map[id]
  map[id] = title
  const keys = Object.keys(map)
  for (const key of keys.slice(0, Math.max(0, keys.length - 50))) delete map[key]
  await $.store.set('titles', map)
}

async function endWallpaper($: EngineInterface, sessionId: string, isClear: boolean) {
  wall.isEnded = true
  // 不管是关会话还是 /clear，标题都属于旧会话了
  wall.title = ''
  wall.titleTried = false
  wall.gen++
  const late = wall.inflight
  if (late !== null) await Promise.race([late, $.clock.sleep(WALLPAPER_TIMEOUT_MS)])
  const ids = [...new Set([sessionId, wall.sentId].filter(one => one !== ''))]
  wall.sentId = ''
  wall.sentBody = ''
  wall.sentKey = ''
  for (const id of ids) await postWallpaper($, '/end', id, 1_000).catch(() => undefined)
  // 还有推送没回来：等它落地后再撤一次，免得它晚到把卡片「复活」
  if (late !== null && wall.inflight === late) {
    void late.then(() => {
      for (const id of ids) {
        void $.http.fetch(`${WALLPAPER_URL}/end`, { method: 'POST', body: id }).catch(() => undefined)
      }
    })
  }
  // /clear 之后进程还在，换个 id 继续同步
  if (isClear) wall.isEnded = false
}

const POKED = '诶？你在桌面上戳我！'

// 在壁纸上戳了玩偶：闲着就跳出来打个招呼，忙着就不打扰
async function onPoke($: EngineInterface) {
  const p = await read($, pet)
  if (BUSY.includes(p.phase)) return
  await setPhase($, 'greet', { line: POKED, when: one => !BUSY.includes(one.phase) })
}

// 定时读一下输入框：内容变了就当作在打字。没有输入框的地方读到的是空字符串，
// 只有之前读到过内容、现在被清空时才算「删光了」
let lastDraft = ''
let lastTypedAt = 0
async function pollDraft($: EngineInterface) {
  const { text } = await $.prompt.read()
  if (text === lastDraft) return
  lastDraft = text
  await onDraft($, text)
}

// 你在输入框里打字：闲着就探头偷看，忙着就说一声。
// 按写入那一刻的最新状态一次算完，不会因为和定时器同时写而被丢掉
async function onDraft($: EngineInterface, draft: string) {
  const now = await $.clock.now()
  lastTypedAt = now
  const isEmpty = draft.trim() === ''

  await update($, pet, (one): Pet => {
    if (BUSY.includes(one.phase)) {
      if (isEmpty) return one.line === BUSY_TYPING ? { ...one, line: restoreLine(one) } : one
      return one.line === BUSY_TYPING ? one : { ...one, line: BUSY_TYPING }
    }
    // 偷看期间 since 记最后一次打字的时间；turnSince 借来存这次偷看开始的时间，
    // 给台词当种子，所以台词只在「问句/很长/删光」这些情况变化时才换
    const seed = one.phase === 'peeking' ? one.turnSince : now
    return {
      ...one,
      phase: 'peeking',
      action: 'none',
      label: '',
      line: peekLine(draft, seed),
      since: now,
      isBig: false,
      turnSince: seed,
    }
  })
}

// ---- 通知 ----

// Windows 自带 PowerShell 5.1 弹原生通知；标题和正文走环境变量，避免拼接进脚本
const WINDOWS_TOAST = [
  '[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null',
  '$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)',
  "$n = $t.GetElementsByTagName('text')",
  '$n.Item(0).AppendChild($t.CreateTextNode($env:DF_TITLE)) > $null',
  '$n.Item(1).AppendChild($t.CreateTextNode($env:DF_TEXT)) > $null',
  "$app = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe'",
  '[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show([Windows.UI.Notifications.ToastNotification]::new($t))',
].join('; ')

// 桌面版自己会发通知，这里只弹个小提示。命令行先走 Claude Code 的通知；
// 只有终端只能响铃、或找不到能弹通知的终端时，才改用 Windows 原生通知。
// 用户关掉了通知（disabled）或被别的钩子拒绝（refused、报错）时，什么都不做
async function notify($: EngineInterface, title: string, text: string) {
  const surfaces = await $.session.surfaces().catch((): readonly RenderSurface[] => [])
  if (surfaces.includes('desktop')) {
    $.ui.toast(text)
    return
  }

  const sent = await $.ui.notify(text, { title }).catch(() => undefined)
  if (sent === undefined) return
  const isFallback = sent.isSent
    ? sent.channel === 'terminal_bell'
    : sent.reason === 'no-channel' || sent.reason === 'no-surface'
  if (!isFallback) return

  await $.process
    .run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', WINDOWS_TOAST], {
      env: { DF_TITLE: title, DF_TEXT: text },
      timeoutMs: 15_000,
    })
    .catch(() => undefined)
}

// ---- 花费 ----

// 读取花费、模型和上下文。订阅登录时 rateLimits 有读数；
// 没有读数又已经花了钱，说明是 API Key；还没花钱时看不出来
async function refresh($: EngineInterface) {
  const [usage, model] = await Promise.all([$.session.usage(), $.session.model()])
  const cost = usage.cost?.usd ?? null
  const isSubscription =
    usage.rateLimits.length > 0 ? true : cost !== null && cost > 0 ? false : null
  const next: Info = {
    model: shortModel(model),
    cost,
    percent: usage.context.percent === undefined ? null : Math.round(usage.context.percent),
    isSubscription,
  }
  await update($, spent, () => cost ?? 0)
  await update($, info, () => next)

  return { cost, limit: await read($, budget) }
}

function costText(i: Info) {
  if (i.cost === null) return '花费未知'
  if (i.isSubscription === true) return `约合 API ${usd(i.cost)}（订阅不扣费）`
  if (i.isSubscription === false) return `本次 ${usd(i.cost)}`
  return `花费 ${usd(i.cost)}`
}

function infoText(i: Info | null, limit: number | null) {
  if (i === null) return ''
  const parts = [i.model, costText(i)]
  if (i.percent !== null) parts.push(`上下文 ${i.percent}%`)
  if (limit !== null) parts.push(`预算 ${usd(limit)}`)
  return parts.join(' · ')
}

const warnLevel = (cost: number, limit: number) =>
  cost >= limit ? 2 : cost >= limit * WARN_RATIO ? 1 : 0

export const register: Register = on => {
  // 主线程上正在运行的工具和各自的姿势；一个结束就换成还在跑的那个，全部结束才回到「思考中」
  const running = new Map<string, ReturnType<typeof toolPose>>()
  const isHandsBusy = (p: Pet) => p.phase === 'working' || p.phase === 'asking'

  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: 'changes',
      description: '打开「改动文件」面板，列出本次会话改过的文件',
    })
    await $.command.register({
      name: 'budget',
      description: '设置本次会话的花费预算（美元），如 /budget 5；/budget off 取消',
    })

    const saved = await $.store.get('budget')
    if (typeof saved === 'number' && saved > 0) {
      await update($, budget, () => saved)
    }
    const ran = await next(e)
    // 旧版本用过底部状态栏，清掉它留下的旧内容
    $.ui.status(undefined)
    await refresh($)
    // 热重载也会走到这里：已有状态就保留，只在第一次初始化
    if ((await read($, pet)).since === 0) await setPhase($, 'greet')
    $.clock.every(TICK_MS, () => {
      void tick($).catch(() => undefined)
      void pollDraft($).catch(() => undefined)
    })

    return ran
  })

  on('session.end', async ($, e, next) => {
    // 通知壁纸撤掉这张卡片
    await endWallpaper($, e.sessionId, e.reason === 'clear').catch(() => undefined)
    return next(e)
  })

  // 每一轮真正开始时才进入思考（排队的消息也会单独开始一轮）
  on('turn.start', async ($, e, next) => {
    running.clear()
    lastDraft = ''
    // 还没有标题：先查存档和聊天记录（mod 重新加载后不能把这一句当成第一句）；
    // 都没有才说明这是第一轮，用这一句。查到后按会话 ID 存起来
    if (wall.title === '') {
      const gen = wall.gen
      void (async () => {
        const id = await $.session.id()
        let found = await findTitle($, id).catch(() => '')
        if (found === '') found = titleOf(e.text)
        if (gen !== wall.gen || wall.title !== '' || found === '') return
        wall.title = found
        wall.titleTried = true
        await saveTitle($, id, found).catch(() => undefined)
      })().catch(() => undefined)
    }
    await setPhase($, 'thinking', { isTurnStart: true })
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    const isMain = e.agentId === undefined
    if (isMain) {
      const pose = toolPose(String(e.tool), e as unknown as Record<string, unknown>)
      running.set(e.tool_use_id, pose)
      await setPhase($, pose.phase, { action: pose.action, label: pose.label })
    }

    let ran
    try {
      ran = await next(e)
    } finally {
      if (isMain) {
        running.delete(e.tool_use_id)
        const rest = [...running.values()].at(-1)
        if (rest !== undefined) {
          await setPhase($, rest.phase, {
            action: rest.action,
            label: rest.label,
            when: p => isHandsBusy(p) && running.size > 0,
          })
        } else {
          await setPhase($, 'thinking', { when: p => isHandsBusy(p) && running.size === 0 })
        }
      }
    }

    const path =
      e.tool === 'Edit' || e.tool === 'Write'
        ? e.file_path
        : e.tool === 'NotebookEdit'
          ? e.notebook_path
          : undefined

    if (path !== undefined && ran.deny === undefined && ran.isError !== true) {
      const at = await $.clock.now()
      const change: Change = { path, tool: String(e.tool), at }
      await update($, changes, list =>
        [...list.filter(one => one.path !== path), change].slice(-300),
      )
    }

    return ran
  })

  on('turn.complete', async ($, e, next) => {
    const ran = await next(e)
    if (e.agentId !== undefined) return ran

    running.clear()
    if (e.reason === 'answer') await setPhase($, 'done', { isBig: e.durationMs >= LONG_TURN_MS })
    else await setPhase($, e.reason === 'aborted' ? 'stopped' : 'error')
    const { cost, limit } = await refresh($)

    if (e.reason === 'answer' && e.durationMs >= NOTIFY_AFTER_MS) {
      const seconds = Math.round(e.durationMs / 1000)
      const tail = cost === null ? '' : `，本次累计 ${usd(cost)}`
      await notify($, 'Claude Code', `任务完成，用时 ${seconds} 秒${tail}`)
    } else if (e.reason === 'error') {
      await notify($, 'Claude Code', '任务因出错中断，请回去看看')
    }

    // 拿不到花费时不做预算判断
    if (limit !== null && cost !== null) {
      const level = warnLevel(cost, limit)
      const before = await read($, warned)
      if (level > before) {
        await update($, warned, () => level)
        if (level === 2) {
          await update($, isBandHidden, () => false)
          await notify($, 'Claude Code 预算', `已超出预算：花费 ${usd(cost)} / 预算 ${usd(limit)}`)
        } else {
          $.ui.toast(`花费已到预算的 ${Math.round((cost / limit) * 100)}%`)
        }
      }
    }

    return ran
  })

  // 你在主输入框里打字（命令行一定有；桌面版不一定触发，靠上面的定时读取兜底）
  on('prompt.edit', async ($, e, next) => {
    const box = await next(e)
    // 只移动光标、内容没变的不算打字
    if (box.text !== e.text) {
      lastDraft = box.text
      await onDraft($, box.text).catch(() => undefined)
    }
    return box
  })

  on('command.run', { command: 'changes' }, async $ => {
    await $.ui.open({ id: PANE, title: '改动文件' })
    const count = (await read($, changes)).length

    return { text: `已打开「改动文件」面板（共 ${count} 个文件）。` }
  })

  on('command.run', { command: 'budget' }, async ($, e) => {
    const arg = e.args.trim().replace(/^\$/, '')
    const cost = await read($, spent)

    if (arg === '') {
      const limit = await read($, budget)
      return {
        text:
          limit === null
            ? `未设置预算。本次已花费 ${usd(cost)}。用法：/budget 5`
            : `预算 ${usd(limit)}，本次已花费 ${usd(cost)}（${Math.round((cost / limit) * 100)}%）。`,
      }
    }

    if (arg === 'off' || arg === '关' || arg === '0') {
      await update($, budget, () => null)
      await update($, warned, () => 0)
      await $.store.delete('budget')
      await refresh($)
      return { text: '已取消预算提醒。' }
    }

    const value = Number(arg)
    if (!Number.isFinite(value) || value <= 0) {
      return { text: `看不懂「${e.args}」。用法：/budget 5（单位美元），或 /budget off` }
    }

    await update($, budget, () => value)
    await update($, isBandHidden, () => false)
    await update($, warned, () => warnLevel(cost, value))
    await $.store.set('budget', value)
    await refresh($)

    return { text: `预算已设为 ${usd(value)}（以后的会话也沿用），本次已花费 ${usd(cost)}。` }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)

    const [p, i, limit, cost, isHidden, now] = await Promise.all([
      read($, pet),
      read($, info),
      read($, budget),
      read($, spent),
      read($, isBandHidden),
      $.clock.now(),
    ])

    // 引擎已经不在干活了（比如被打断），玩偶就别再装忙
    const isStale = !e.props.isWorking && BUSY.includes(p.phase)
    const shown: Pet = isStale ? { ...p, phase: 'idle', action: 'none', label: '', line: '' } : p
    const isCostKnown = i !== null && i.cost !== null
    const showWarning = isCostKnown && limit !== null && cost >= limit * WARN_RATIO && !isHidden
    const isOver = limit !== null && cost >= limit
    const isContextFull = (i?.percent ?? 0) >= SWEATY_PERCENT
    const isSweaty = isContextFull || (isCostKnown && isOver)

    // 台词：一般用换状态时挑好的那句；想太久、上下文快满、超预算时换成对应的话
    let say = shown.line
    if (shown.phase === 'thinking' && now - shown.turnSince > LONG_TURN_MS) {
      say = pickLine('thinkingLong', shown.turnSince)
    }
    if ((shown.phase === 'idle' || shown.phase === 'sleepy') && isSweaty) {
      say = isContextFull ? '上下文快满了，可以 /compact 一下' : '预算超啦…'
    }

    const { Box, Button, Text } = $.ui.resolve(e)
    const words = (tail: string) => (
      <Box flexDirection="column">
        <Text key="info" dimColor>
          {infoText(i, limit)}
        </Text>
        {say + tail !== '' && (
          <Text key="say" color="claude">
            {say}
            {tail}
          </Text>
        )}
      </Box>
    )
    const warning = showWarning && limit !== null && (
      <Box>
        <Text color={isOver ? 'error' : 'warning'}>
          {isOver ? '已超出预算' : '快到预算了'}：{usd(cost)} / {usd(limit)}{' '}
        </Text>
        <Button key="hide" label="知道了" onPress={() => update($, isBandHidden, () => true)} />
      </Box>
    )

    // 命令行：三行方块字符画，靠帧推进
    if (e.surface === 'terminal') {
      const f = await read($, frame)
      const art = petBlocks(shown.phase, f)
      const isRoomy = e.props.maxRows >= 3
      return (
        <Box flexDirection="column">
          <Box alignItems="center" columnGap={1}>
            {isRoomy ? (
              <Box key="pet" flexDirection="column">
                {art.map((line, n) => (
                  <Text key={`row${n}`} color="claude">
                    {line}
                  </Text>
                ))}
              </Box>
            ) : (
              <Text key="pet" color="claude">
                {art[0]}
              </Text>
            )}
            {words(petTail(shown.phase, f))}
          </Box>
          {warning}
        </Box>
      )
    }

    // 桌面版等：带 CSS 动画的像素 SVG，同一状态的图不变，动画自己跑
    const { Svg } = $.ui.resolve(e)
    return (
      <Box flexDirection="column">
        <Box alignItems="center" columnGap={1}>
          <Svg
            key="pet"
            source={petSvg(shown.phase, shown.action, { isBig: shown.isBig, isSweaty })}
            alt={PET_ALT[shown.phase]}
            width={50}
            height={30}
            isInteractive
          />
          {words(shown.phase === 'thinking' ? '…' : '')}
        </Box>
        {warning}
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text } = $.ui.resolve(e)
    const list = await read($, changes)
    const room = Math.max(1, (e.viewport?.rows ?? 24) - 3)

    return (
      <Box flexDirection="column">
        <Text bold>本次会话改过 {list.length} 个文件</Text>
        {list.length === 0 && <Text dimColor>还没有改动。</Text>}
        {list
          .slice(-room)
          .reverse()
          .map(one => (
            <Text key={one.path}>
              <Text color={one.tool === 'Write' ? 'green' : 'yellow'}>
                {one.tool === 'Write' ? '新建/覆盖 ' : '修改 '}
              </Text>
              {one.path}
            </Text>
          ))}
      </Box>
    )
  })
}
