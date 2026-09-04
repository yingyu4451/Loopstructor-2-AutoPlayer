import { createPinia, setActivePinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it } from 'vitest'
import GamePage from '../src/pages/GamePage.vue'
import { useAppStore } from '../src/stores/app'

describe('game page controls', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('places refresh at the right edge of the game build heading', () => {
    const store = useAppStore()
    store.snapshot = {
      protocolVersion: 1,
      version: '0.6.69',
      settings: {} as never,
      connection: { trusted: false, label: '等待选择游戏', reason: '', cheatAvailable: false, autoplayActive: false },
      logs: [],
    }

    const wrapper = mount(GamePage)
    const heading = wrapper.get('.game-location .section-heading')

    expect(wrapper.find('.page-heading').exists()).toBe(false)
    expect(heading.get('button').text()).toContain('刷新连接')
    expect(heading.get('button').classes()).toEqual(expect.arrayContaining(['btn', 'btn-outline']))
  })
})
