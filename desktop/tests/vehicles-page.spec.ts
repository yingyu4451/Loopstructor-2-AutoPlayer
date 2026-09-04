import { createPinia, setActivePinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it } from 'vitest'
import VehiclesPage from '../src/pages/VehiclesPage.vue'
import { useAppStore } from '../src/stores/app'

describe('vehicle catalog presentation', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('shows only the two public shapes and renders the game catalog image', () => {
    const store = useAppStore()
    store.catalog = {
      vehicles: [
        vehicle('Shell_ShadowRift_L1', 1, 'data:image/png;base64,AA=='),
        vehicle('Shell_ShadowRift_L2', 2, 'data:image/png;base64,BB=='),
        vehicle('Shell_ShadowRift_L3', 3, 'data:image/png;base64,CC=='),
      ],
      enchantments: [],
    }

    const wrapper = mount(VehiclesPage, {
      global: {
        stubs: { CatalogCard: true, VirtualCatalogGrid: true },
        directives: { tooltip: () => undefined },
      },
    })

    expect(wrapper.text()).toContain('初始形态')
    expect(wrapper.text()).toContain('升级形态')
    expect(wrapper.text()).not.toContain('内部过渡形态')
    expect(wrapper.findAll('.level-buttons button')).toHaveLength(2)
    expect(wrapper.get('.vehicle-game-icon img').attributes('src')).toBe('data:image/png;base64,AA==')
    expect(wrapper.get('.vehicle-family').classes()).toContain('card')
    expect(wrapper.find('.vehicle-family > .card-body').exists()).toBe(true)
    for (const button of wrapper.findAll('.level-buttons button')) {
      expect(button.classes()).toEqual(expect.arrayContaining(['btn', 'btn-sm']))
    }
  })
})

function vehicle(id: string, level: number, iconDataUrl: string) {
  return {
    id,
    enumName: id,
    name: '暗影裂隙',
    typeKey: 'Shell',
    typeName: '炮弹',
    typeOrder: 0,
    familyKey: 'Shell_ShadowRift',
    familyOrder: 0,
    itemOrder: level,
    level,
    iconDataUrl,
  }
}
