export type SkinId = 'skyspine'

export interface SkinDefinition {
  id: SkinId
  label: string
  description: string
  swatches: readonly string[]
}

export const skinOptions: readonly SkinDefinition[] = [
  {
    id: 'skyspine',
    label: '天穹机械终端',
    description: '游戏原生铜木机壳、齿轨与荧光仪表。',
    swatches: ['#100c08', '#3b2416', '#d89b46', '#70d84b'],
  },
]

export function isSkinId(value: unknown): value is SkinId {
  return value === 'skyspine'
}

export function normalizeSkinId(value: unknown): SkinId {
  return isSkinId(value) ? value : 'skyspine'
}
