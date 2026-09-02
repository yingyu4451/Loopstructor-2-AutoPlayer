export type SkinId = 'mechanical' | 'signal'

export interface SkinDefinition {
  id: SkinId
  label: string
  description: string
  swatches: readonly string[]
}

export const skinOptions: readonly SkinDefinition[] = [
  {
    id: 'mechanical',
    label: '齿轨工坊',
    description: '铜框、蓝钢与暖色仪表灯。',
    swatches: ['#0b1220', '#203448', '#d39a50', '#7fe16d'],
  },
  {
    id: 'signal',
    label: '信号夜航',
    description: '深海底板与青紫信号层。',
    swatches: ['#080c18', '#1c2744', '#68d8f2', '#d66bb0'],
  },
]

export function isSkinId(value: unknown): value is SkinId {
  return value === 'mechanical' || value === 'signal'
}

export function normalizeSkinId(value: unknown): SkinId {
  return isSkinId(value) ? value : 'mechanical'
}
