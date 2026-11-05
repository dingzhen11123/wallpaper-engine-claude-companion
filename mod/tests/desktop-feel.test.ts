import type { On, RenderSurface } from 'claude-code'
import type { Engine, MockClock } from 'claude-code/testing'
import { expect, mock, test } from 'claude-code/testing'

const PLUGIN = 'desktop-feel'
const SURFACES = ['terminal', 'desktop'] as const
const above = (isWorking = false) =>
  ({
    component: 'AbovePrompt',
    // 只给本 mod 用到的属性，其余引擎属性用不到
    props: { hasSurvey: false, isWorking, maxRows: 4 } as never,
  }) as const

type World = {
  cost: number
  isSubscription: boolean
  surfaces: RenderSurface[]
  toasts: string[]
  notified: string[]
  processes: string[][]
  notifyChannel: 'kitty' | 'terminal_bell'
  // 设了就表示通知没发出去，以及原因
  notifyRefusal: 'disabled' | 'no-channel' | 'no-surface' | 'refused' | null
  percent: number
  draft: string
  clock: MockClock
}

// 引擎底下的替身：花费、模型、通知、子进程都记到 world 里
function world(on: On, notifyChannel: World['notifyChannel'] = 'kitty'): World {
  const w: World = {
    cost: 0,
    isSubscription: false,
    surfaces: ['terminal'],
    toasts: [],
    notified: [],
    processes: [],
    notifyChannel,
    notifyRefusal: null,
    percent: 42.4,
    draft: '',
    clock: mock.clock(on, { now: 1_000_000 }),
  }
  mock.store(on)
  on('session.usage', () => ({
    value: {
      startedAt: 0,
      context: { window: 200_000, percent: w.percent },
      rateLimits: w.isSubscription ? [{ kind: 'five_hour', percentUsed: 10 }] : [],
      cost: { usd: w.cost },
    },
  }))
  on('session.model', () => ({ value: 'claude-opus-5-5[1m]' }))
  on('session.surfaces', () => ({ value: w.surfaces }))
  on('prompt.read', () => ({ value: { text: w.draft, cursor: w.draft.length } }))
  on('ui.status', () => ({ value: undefined }))
  on('ui.toast', ($, e) => {
    w.toasts.push(e.text)
    return { value: undefined }
  })
  on('ui.notify', ($, e) => {
    w.notified.push(e.text)
    return {
      value:
        w.notifyRefusal === null
          ? { isSent: true as const, channel: w.notifyChannel }
          : { isSent: false as const, reason: w.notifyRefusal },
    }
  })
  on('process.run', ($, e) => {
    w.processes.push([...e.argv])
    return {
      value: { exitCode: 0, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false },
    }
  })
  on('command.register', ($, e) => ({ value: { command: e.name } }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('turn.start', ($, e) => ({ turnId: e.turnId }))
  on('turn.complete', ($, e) => ({ text: e.answer }))
  on('session.start', ($, e) => ({ cwd: e.cwd }))
  // 引擎自己画的部分：一个空 Box
  on('ui.render', ($, e) => {
    const { Box } = $.ui.resolve(e)
    return h(Box, { key: 'engine' }) as never
  })
  return w
}

const start = ($: Engine) =>
  $.session.start({ cwd: '/proj', surface: 'terminal', isInteractive: true })

const run = ($: Engine, command: string, args: string) =>
  $.command.run({
    command,
    args,
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })

const turn = (durationMs: number, reason: 'answer' | 'error' | 'aborted' = 'answer') => ({
  answer: 'ok',
  durationMs,
  isAborted: reason === 'aborted',
  turnId: `t${durationMs}`,
  reason,
})

// 画一次输入框上方那行，取出全部文字
const bandText = async ($: Engine, surface: RenderSurface, isWorking = false) => {
  const ui = await $.ui.mount({ plugin: PLUGIN, surface, ...above(isWorking) })
  const texts = (await ui.findAll({ type: 'Text' })).map(one => one.text).join('|')
  await ui.unmount()
  return texts
}

// 桌面版画的像素小怪物：读出它的 alt 文字，顺便确认是一张会动的 SVG
const petAlt = async ($: Engine, surface: 'desktop', isWorking = false) => {
  const ui = await $.ui.mount({ plugin: PLUGIN, surface, ...above(isWorking) })
  const svg = await ui.find({ type: 'Svg' })
  await ui.unmount()
  const props = (svg?.props ?? {}) as { alt?: string; source?: string; isInteractive?: boolean }
  expect(props.source).toContain('@keyframes')
  expect(props.isInteractive).toBe(true)
  return props.alt
}

// 桌面版小怪物的 SVG 源码，用来确认道具、汗滴、彩纸有没有画出来
const petSource = async ($: Engine) => {
  const ui = await $.ui.mount({ plugin: PLUGIN, surface: 'desktop', ...above(true) })
  const svg = await ui.find({ type: 'Svg' })
  await ui.unmount()
  return String((svg?.props as { source?: string } | undefined)?.source ?? '')
}

test('API Key 登录：显示模型、本次花费和上下文', async ($, on) => {
  const w = world(on)
  w.cost = 1.234
  await start($)

  for (const surface of SURFACES) {
    expect(await bandText($, surface)).toContain('opus 5.5 · 本次 $1.23 · 上下文 42%')
  }
})

test('还没花钱、看不出登录方式时，用中性文案', async ($, on) => {
  world(on)
  await start($)

  const text = await bandText($, 'terminal')
  expect(text).toContain('花费 $0.00')
  expect(text).not.toContain('订阅')
})

test('订阅登录：显示约合 API 金额并注明不扣费', async ($, on) => {
  const w = world(on)
  w.cost = 2
  w.isSubscription = true
  await start($)

  const text = await bandText($, 'desktop')
  expect(text).toContain('约合 API $2.00（订阅不扣费）')
  expect(text).not.toContain('本次')
})

test('小玩偶：工作时说在干什么，完成后开心，几秒后回到空闲', async ($, on) => {
  const w = world(on)
  // 工具调用先卡住，等测试看完玩偶再放行
  let reached = () => {}
  const releases: (() => void)[] = []
  on('tool.call', () => {
    reached()
    return new Promise(done => {
      releases.push(() => done({ result: {}, text: 'ok' } as never))
    })
  })
  const enter = async (call: () => Promise<unknown>) => {
    const isReached = new Promise<void>(done => {
      reached = done
    })
    const running = call()
    await isReached
    // 包一层，免得 async 函数自动等这个调用结束
    return { running }
  }
  const during = async (call: () => Promise<unknown>) => {
    const { running } = await enter(call)
    const text = await bandText($, 'desktop', true)
    releases.shift()?.()
    await running
    return text
  }
  await start($)

  expect(await bandText($, 'terminal')).toContain('▝▜█████▛▘')
  // 刚打开先打招呼，过几秒回到等待
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在跟你打招呼')
  await w.clock.advance(6_000)
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在等你')

  // 每轮开始进入思考
  await $.turn.start({ text: '帮我改个 bug', turnId: 't-start' })
  expect(await bandText($, 'desktop', true)).toContain('思考中')

  expect(
    await during(() =>
      $.tool.call({ tool: 'Edit', file_path: 'E:\\proj\\src\\app.ts', old_string: 'a', new_string: 'b' }),
    ),
  ).toContain('在改 app.ts')
  expect(await during(() => $.tool.call({ tool: 'Bash', command: 'npm test' }))).toContain('在跑 npm')

  // 两个工具同时跑：先结束一个，玩偶仍在工作
  const { running: first } = await enter(() => $.tool.call({ tool: 'Read', file_path: '/proj/a.ts' }))
  const { running: second } = await enter(() => $.tool.call({ tool: 'Grep', pattern: 'x' }))
  releases.shift()?.()
  await first
  expect(await bandText($, 'desktop', true)).not.toContain('思考中')
  releases.shift()?.()
  await second
  expect(await bandText($, 'desktop', true)).toContain('思考中')

  // 工具跑完回到思考；引擎还在干活时显示「思考中」
  expect(await bandText($, 'desktop', true)).toContain('思考中')
  // 引擎已经停了就不再装忙
  expect(await bandText($, 'desktop', false)).not.toContain('思考中')

  await $.turn.complete(turn(1000))
  expect(await bandText($, 'terminal')).toContain('完成啦')

  expect(await petAlt($, 'desktop')).toBe('小 Claude 完成啦')
  await w.clock.advance(5_000)
  expect(await bandText($, 'terminal')).not.toContain('完成啦')
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在等你')

  // 很久没动静就打瞌睡
  await w.clock.advance(150_000)
  expect(await bandText($, 'terminal')).toContain('z')
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在打瞌睡')

  await $.turn.complete(turn(1000, 'error'))
  expect(await bandText($, 'terminal')).toContain('出错了')
})

test('/budget 设置、查看、取消预算', async ($, on) => {
  const w = world(on)
  w.cost = 1
  await start($)

  const set = await run($, 'budget', '5')
  expect(set.text).toContain('预算已设为 $5.00')
  expect(await bandText($, 'terminal')).toContain('预算 $5.00')

  const shown = await run($, 'budget', '')
  expect(shown.text).toContain('20%')

  const bad = await run($, 'budget', 'abc')
  expect(bad.text).toContain('看不懂')

  const off = await run($, 'budget', 'off')
  expect(off.text).toContain('已取消')
  expect(await bandText($, 'terminal')).not.toContain('预算')
})

test('花费到 80% 出现提醒，超出预算发通知', async ($, on) => {
  const w = world(on)
  await start($)
  await run($, 'budget', '10')

  for (const surface of SURFACES) {
    expect(await bandText($, surface)).not.toContain('快到预算了')
  }

  w.cost = 8.5
  await $.turn.complete(turn(1000))
  expect(w.toasts.at(-1)).toContain('85%')
  expect(w.notified).toEqual([])

  for (const surface of SURFACES) {
    expect(await bandText($, surface)).toContain('快到预算了')
  }

  w.cost = 10.5
  await $.turn.complete(turn(1000))
  expect(w.notified.at(-1)).toContain('已超出预算')

  // 只提醒一次
  await $.turn.complete(turn(1000))
  expect(w.notified.filter(t => t.includes('已超出预算')).length).toBe(1)

  const ui = await $.ui.mount({ plugin: PLUGIN, surface: 'terminal', ...above() })
  expect(await ui.find({ text: /已超出预算/ })).toBeDefined()
  await ui.press({ key: 'hide' })
  expect(await ui.find({ text: /已超出预算/ })).toBeUndefined()
  await ui.unmount()
})

test('命令行：长任务完成才通知，短任务不通知', async ($, on) => {
  const w = world(on)
  await start($)

  await $.turn.complete(turn(5_000))
  expect(w.notified).toEqual([])

  await $.turn.complete(turn(45_000))
  expect(w.notified.at(-1)).toContain('任务完成，用时 45 秒')
  expect(w.processes).toEqual([])

  await $.turn.complete(turn(2_000, 'error'))
  expect(w.notified.at(-1)).toContain('出错')
})

test('命令行：终端只能响铃时，改用 Windows 原生通知', async ($, on) => {
  const w = world(on, 'terminal_bell')
  await start($)

  await $.turn.complete(turn(60_000))
  expect(w.processes.length).toBe(1)
  expect(w.processes[0]?.[0]).toBe('powershell.exe')
})

test('命令行：用户关掉了通知或通知被拒绝时，不弹 Windows 通知', async ($, on) => {
  const w = world(on)
  await start($)

  for (const reason of ['disabled', 'refused'] as const) {
    w.notifyRefusal = reason
    await $.turn.complete(turn(60_000))
  }
  expect(w.processes).toEqual([])

  w.notifyRefusal = 'no-channel'
  await $.turn.complete(turn(60_000))
  expect(w.processes.length).toBe(1)
})

test('桌面版：不弹系统通知，只显示小提示', async ($, on) => {
  const w = world(on, 'terminal_bell')
  w.surfaces = ['desktop']
  await start($)

  await $.turn.complete(turn(60_000))
  expect(w.notified).toEqual([])
  expect(w.processes).toEqual([])
  expect(w.toasts.at(-1)).toContain('任务完成')
})

test('记录改过的文件，失败的不记', async ($, on) => {
  world(on)
  on('tool.call', ($, e) =>
    e.tool === 'Write' && e.file_path.endsWith('bad.txt')
      ? ({ isError: true, result: 'failed', text: 'failed' } as never)
      : ({ result: {}, text: 'ok' } as never),
  )
  await start($)

  await $.tool.call({ tool: 'Edit', file_path: '/proj/a.ts', old_string: 'a', new_string: 'b' })
  await $.tool.call({ tool: 'Write', file_path: '/proj/b.ts', content: 'x' })
  await $.tool.call({ tool: 'Edit', file_path: '/proj/a.ts', old_string: 'b', new_string: 'c' })
  await $.tool.call({ tool: 'Write', file_path: '/proj/bad.txt', content: 'x' })

  const opened = await run($, 'changes', '')
  expect(opened.text).toContain('共 2 个文件')

  const ui = await $.ui.mount({
    plugin: PLUGIN,
    surface: 'terminal',
    component: 'Pane',
    props: { title: '改动文件' } as never,
    requestId: 'desktop-feel-changes',
  })
  expect(await ui.find({ text: /\/proj\/b\.ts/ })).toBeDefined()
  expect(await ui.find({ text: /bad\.txt/ })).toBeUndefined()
  await ui.unmount()
})

test('干活时按工具换道具和台词', async ($, on) => {
  const w = world(on)
  const releases: (() => void)[] = []
  let reached = () => {}
  on('tool.call', () => {
    reached()
    return new Promise(done => {
      releases.push(() => done({ result: {}, text: 'ok' } as never))
    })
  })
  // 工具卡住期间看一眼台词和 SVG，再放行
  const look = async (call: () => Promise<unknown>) => {
    const isReached = new Promise<void>(done => {
      reached = done
    })
    const running = { call: call() }
    await isReached
    const result = { text: await bandText($, 'desktop', true), svg: await petSource($) }
    releases.shift()?.()
    await running.call
    return result
  }
  await start($)
  await $.turn.start({ text: '干活', turnId: 't1' })

  const run = await look(() => $.tool.call({ tool: 'Bash', command: '"C:/Program Files/Git/bin/git.exe" status' }))
  expect(run.text).toContain('在跑 git.exe')
  expect(run.svg).toContain('class="cursor"')

  const edit = await look(() => $.tool.call({ tool: 'Write', file_path: '/proj/a.md', content: 'x' }))
  expect(edit.text).toContain('在写 a.md')
  expect(edit.svg).toContain('class="pencil"')

  const readFile = await look(() => $.tool.call({ tool: 'Read', file_path: '/proj/b.ts' }))
  expect(readFile.svg).toContain('class="txt"')

  const agent = await look(() => $.tool.call({ tool: 'Agent', description: 'x', prompt: 'y' } as never))
  expect(agent.text).toContain('小助手')
  expect(agent.svg).toContain('class="mini"')

  const ask = await look(() => $.tool.call({ tool: 'AskUserQuestion', questions: [] } as never))
  expect(ask.svg).toContain('class="q"')
  expect(await bandText($, 'desktop', true)).toMatch(/思考中|让我想想|嗯|整理|琢磨/)
  w.draft = ''
})

test('被打断时耸肩，大工程完成撒彩纸', async ($, on) => {
  const w = world(on)
  await start($)

  await $.turn.complete(turn(1000, 'aborted'))
  expect(await petAlt($, 'desktop')).toBe('小 Claude 停下来了')
  expect(await bandText($, 'desktop')).toMatch(/停|听你的/)

  await w.clock.advance(5_000)
  await $.turn.complete(turn(150_000))
  expect(await petSource($)).toContain('class="conf')
  expect(await bandText($, 'desktop')).toMatch(/搞定|完成|做完/)

  await w.clock.advance(5_000)
  await $.turn.complete(turn(2_000))
  expect(await petSource($)).not.toContain('class="conf')
})

test('上下文快满时冒汗并提醒 /compact', async ($, on) => {
  const w = world(on)
  w.percent = 86
  await start($)
  await w.clock.advance(6_000)

  expect(await bandText($, 'desktop')).toContain('/compact')
  expect(await petSource($)).toContain('class="sweat"')
})

test('偷看输入框：闲着探头，忙着说一声，停手几秒后缩回去', async ($, on) => {
  const w = world(on)
  await start($)
  await w.clock.advance(6_000)

  w.draft = '帮我看看这个报错'
  await w.clock.advance(400)
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在偷看你打字')
  expect(await bandText($, 'desktop')).not.toContain('报错')

  w.draft = '帮我看看这个报错是什么意思吗'
  await w.clock.advance(400)
  expect(await bandText($, 'desktop')).toContain('是要问我问题吗？')

  w.draft = ''
  await w.clock.advance(400)
  expect(await bandText($, 'desktop')).toContain('删掉啦？')

  await w.clock.advance(4_000)
  expect(await petAlt($, 'desktop')).toBe('小 Claude 在等你')

  await $.turn.start({ text: 'x', turnId: 't2' })
  w.draft = '再加一个'
  await w.clock.advance(400)
  expect(await bandText($, 'desktop', true)).toContain('我还在忙')
  expect(await petAlt($, 'desktop', true)).toBe('小 Claude 在思考')
})

test('忙的时候打字说「我还在忙」，停手几秒后恢复原来的台词', async ($, on) => {
  const w = world(on)
  await start($)
  await $.turn.start({ text: 'x', turnId: 't3' })
  const before = await bandText($, 'desktop', true)

  w.draft = '再加一个'
  await w.clock.advance(400)
  expect(await bandText($, 'desktop', true)).toContain('我还在忙')

  await w.clock.advance(4_000)
  const after = await bandText($, 'desktop', true)
  expect(after).not.toContain('我还在忙')
  expect(after).toBe(before)
})

test('两个工具同时跑：先结束的那个，道具不会留在手上', async ($, on) => {
  world(on)
  const releases = new Map<string, () => void>()
  let reached = () => {}
  on('tool.call', ($, e) => {
    reached()
    return new Promise(done => {
      releases.set(String(e.tool), () => done({ result: {}, text: 'ok' } as never))
    })
  })
  const enter = async (call: () => Promise<unknown>) => {
    const isReached = new Promise<void>(done => {
      reached = done
    })
    const running = { call: call() }
    await isReached
    return running
  }
  await start($)
  await $.turn.start({ text: 'x', turnId: 't4' })

  const longRead = await enter(() => $.tool.call({ tool: 'Read', file_path: '/proj/big.ts' }))
  const shortGrep = await enter(() => $.tool.call({ tool: 'Grep', pattern: 'x' }))
  expect(await bandText($, 'desktop', true)).toContain('在翻找代码')

  releases.get('Grep')?.()
  await shortGrep.call
  expect(await bandText($, 'desktop', true)).toContain('在看 big.ts')

  releases.get('Read')?.()
  await longRead.call
  expect(await bandText($, 'desktop', true)).toMatch(/思考中|让我想想|嗯|整理|琢磨/)
})

// ---- 桌面壁纸联动 ----

type Wall = {
  posts: { path: string; body: Record<string, unknown> | string }[]
  pokes: number
  isDown: boolean
  id: string
  status: number
  // 设了就先卡住 /session 的回复，等测试放行
  hold: (() => void)[] | null
}

// 壁纸桥接服务的替身：记下发过去的内容，按需回「被戳了几下」，或者装作没开
function wallWorld(on: On): Wall {
  const wall: Wall = { posts: [], pokes: 0, isDown: false, id: 's1', status: 200, hold: null }
  on('session.id', () => ({ value: wall.id }))
  on('session.cwd', () => ({ value: 'C:\\code\\my-app' }))
  on('session.surface', () => ({ value: 'desktop' as const }))
  on('session.end', ($, e) => ({ sessionId: e.sessionId }))
  on('http.fetch', async ($, e) => {
    if (wall.isDown) return { deny: 'ECONNREFUSED' }
    const raw = e.init?.body ?? ''
    const path = e.url.replace('http://127.0.0.1:47321', '')
    wall.posts.push({ path, body: path === '/session' ? (JSON.parse(raw) as Record<string, unknown>) : raw })
    const held = wall.hold
    if (path === '/session' && held !== null) await new Promise<void>(done => held.push(done))
    const text = JSON.stringify({ pokes: wall.pokes })
    wall.pokes = 0
    return { value: { status: wall.status, ok: wall.status < 300, headers: {}, text } }
  })
  return wall
}

const sessionPosts = (wall: Wall) =>
  wall.posts.filter(one => one.path === '/session').map(one => one.body as Record<string, unknown>)

// 定时器每 400 毫秒触发一次同步；多走几步，让没等待的请求也跑完
const step = async (w: World, ms = 400) => {
  for (let n = 0; n < ms / 400; n++) await w.clock.advance(400)
  await w.clock.advance(0)
}

test('壁纸联动：推送玩偶状态，没变化时只按心跳发', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  w.isSubscription = true
  await start($)
  await step(w)

  const first = sessionPosts(wall).at(-1)
  expect(first?.id).toBe('s1')
  expect(first?.project).toBe('my-app')
  expect(first?.phase).toBe('greet')
  expect(String(first?.svg)).toContain('@keyframes')
  expect(first?.limits).toEqual([{ kind: 'five_hour', percent: 10, resetsAt: null }])

  // 状态没变：3 秒内不重复发
  const count = sessionPosts(wall).length
  await step(w, 1_600)
  expect(sessionPosts(wall).length).toBe(count)
  await step(w, 2_000)
  expect(sessionPosts(wall).length).toBe(count + 1)

  // 状态一变马上发
  await $.turn.start({ turnId: 't1', text: 'hi' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.phase).toBe('thinking')
})

test('壁纸联动：服务没开时退避，不影响玩偶', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  wall.isDown = true
  await start($)
  await step(w, 2_000)
  expect(wall.posts.length).toBe(0)

  // 服务起来后，退避时间一过就重新推送
  wall.isDown = false
  await step(w, 2_000)
  expect(wall.posts.length).toBe(0)
  await step(w, 15_000)
  expect(sessionPosts(wall).length).toBeGreaterThan(0)
  expect(await bandText($, 'terminal')).toContain('▝▜█████▛▘')
})

test('壁纸联动：在壁纸上戳一下，闲着的玩偶跳出来打招呼', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  await start($)
  await step(w, 6_000)
  expect(await bandText($, 'desktop')).not.toContain('戳我')

  wall.pokes = 1
  await step(w, 3_200)
  expect(await bandText($, 'desktop')).toContain('诶？你在桌面上戳我！')
})

test('壁纸联动：会话结束和 /clear 换 id 时撤掉旧卡片', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  await start($)
  await step(w)

  wall.id = 's2'
  await step(w, 3_200)
  expect(wall.posts.some(one => one.path === '/end' && one.body === 's1')).toBe(true)
  expect(sessionPosts(wall).at(-1)?.id).toBe('s2')

  await $.session.end({ reason: 'other', sessionId: 's2', resume: undefined as never })
  expect(wall.posts.at(-1)).toEqual({ path: '/end', body: 's2' })
})

test('壁纸联动：服务回错误码时按失败退避，不当成发送成功', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  wall.status = 500
  await start($)
  await step(w, 2_000)
  // 第一次失败后 15 秒内不再重试
  expect(sessionPosts(wall).length).toBe(1)

  wall.status = 200
  await step(w, 15_200)
  expect(sessionPosts(wall).length).toBe(2)
})

test('壁纸联动：第一次推送还在路上时会话结束，卡片不会复活', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  wall.hold = []
  await start($)
  await step(w)
  expect(sessionPosts(wall).length).toBe(1)

  const ending = $.session.end({ reason: 'other', sessionId: 's1', resume: undefined as never })
  // 放行在途的推送，结束流程等它落地后再撤卡片
  for (const done of wall.hold.splice(0)) done()
  await ending
  expect(wall.posts.at(-1)).toEqual({ path: '/end', body: 's1' })

  // 结束后不再推送
  wall.hold = null
  const count = wall.posts.length
  await step(w, 4_000)
  expect(wall.posts.length).toBe(count)
})

test('壁纸联动：会话标题取你发的第一句话，斜杠命令不算', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  on('session.messages', () => ({ value: [] }))
  await start($)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('')

  await $.turn.start({ turnId: 't0', text: '/compact' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('')

  await $.turn.start({ turnId: 't1', text: '  帮我做一个可以和 Claude 联动的壁纸\n第二行不要' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('帮我做一个可以和 Claude 联动的壁纸')

  // 之后的话不改标题
  await $.turn.start({ turnId: 't2', text: '再加个播放器' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('帮我做一个可以和 Claude 联动的壁纸')
})

test('壁纸联动：/clear 之后标题换成新会话的第一句话', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  on('session.messages', () => ({ value: [] }))
  await start($)
  await $.turn.start({ turnId: 't1', text: '旧会话的第一句' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('旧会话的第一句')

  await $.session.end({ reason: 'clear', sessionId: 's1', resume: undefined as never })
  wall.id = 's2'
  await $.turn.start({ turnId: 't2', text: '新会话的第一句' } as never)
  await step(w, 3_200)
  const last = sessionPosts(wall).at(-1)
  expect(last?.id).toBe('s2')
  expect(last?.title).toBe('新会话的第一句')
})

test('壁纸联动：上下文压缩后的续接说明不当标题，取后面你真正说的话', async ($, on) => {
  const w = world(on)
  const wall = wallWorld(on)
  on('session.messages', () => ({
    value: [
      { role: 'user', text: 'This session is being continued from a previous conversation that ran out of context.' },
      { role: 'user', text: 'Caveat: The messages below were generated by the user while running local commands.' },
      { role: 'user', text: '背景太黑了，帮我做个昼夜变化' },
    ],
  }))
  await start($)
  await $.turn.start({ turnId: 't1', text: '[Request interrupted by user]' } as never)
  await step(w)
  expect(sessionPosts(wall).at(-1)?.title).toBe('背景太黑了，帮我做个昼夜变化')
})
