export type Change = { path: string; tool: string; at: number }

export type PetPhase =
  | 'greet'
  | 'idle'
  | 'sleepy'
  | 'peeking'
  | 'thinking'
  | 'working'
  | 'asking'
  | 'done'
  | 'stopped'
  | 'error'

// 干活时手上拿的道具：看文件、翻代码、改文件、跑命令、查资料、派小助手、列计划、其他工具
export type PetAction = 'none' | 'read' | 'search' | 'edit' | 'run' | 'web' | 'agent' | 'plan' | 'tool'

// 小玩偶：状态、道具、正在做的事、这次说的台词、进入状态的时间、这一轮开始的时间、是不是大工程
export type Pet = {
  phase: PetPhase
  action: PetAction
  label: string
  line: string
  since: number
  turnSince: number
  isBig: boolean
}

// 输入框上方那行信息。cost 为 null：拿不到花费；isSubscription 为 null：还看不出登录方式
export type Info = {
  model: string
  cost: number | null
  percent: number | null
  isSubscription: boolean | null
}

declare module 'claude-code' {
  interface PluginState {
    'desktop-feel': {
      changes: Change[]
      budget: number | null
      spent: number
      isBandHidden: boolean
      // 预算已提醒到哪一档：0 未提醒，1 到了 80%，2 超出预算
      warned: number
      pet: Pet
      // 命令行字符画的动画帧；桌面版的 SVG 自己会动，不读它
      frame: number
      info: Info | null
    }
  }
}
