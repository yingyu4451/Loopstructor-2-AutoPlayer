import { createPinia, setActivePinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import GamePage from '../src/pages/GamePage.vue'
import { useAppStore } from '../src/stores/app'
import type { DesktopApi } from '../src/types'

describe('game page controls', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    window.loopstructorDesktop = { listEditorInstances: vi.fn(async () => []) } as unknown as DesktopApi
  })

  it('places refresh at the right edge of the game build heading', () => {
    const store = useAppStore()
    store.snapshot = {
      protocolVersion: 1,
      version: '0.6.72',
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

  it('shows the selected Unity project, Bridge ownership, and Editor instances', () => {
    const store = useAppStore()
    store.snapshot = {
      protocolVersion: 1,
      version: '0.6.72',
      settings: {} as never,
      editorProject: {
        path: 'D:\\Unity Project\\Loopstructor2', valid: true, unityVersion: '2022.3.62f3c1',
        bridgeInstalled: true, message: 'Unity 工程可用，Editor 连接组件已安装。',
      },
      editorInstances: [{
        instanceId: 'editor-4321', kind: 'editor', processId: 4321,
        displayName: 'Unity Editor · Loopstructor2', projectPath: 'D:\\Unity Project\\Loopstructor2',
        unityVersion: '2022.3.62f3c1', gameVersion: '1.390', sceneName: 'StartGameScene',
        mode: 'editor-edit', runtimeReady: false, lastSeenAt: new Date().toISOString(),
      }],
      connection: { trusted: false, label: '等待选择游戏', reason: '', cheatAvailable: false, autoplayActive: false },
      logs: [],
    }

    const wrapper = mount(GamePage)

    expect(wrapper.get('.editor-section').text()).toContain('D:\\Unity Project\\Loopstructor2')
    expect(wrapper.get('.editor-section').text()).toContain('2022.3.62f3c1')
    expect(wrapper.get('.editor-instance-list').text()).toContain('Unity Editor · Loopstructor2')
    expect(wrapper.get('.editor-instance-list').text()).toContain('Edit Mode')
    expect(wrapper.get('.editor-instance-list').get('button').text()).toBe('连接')
  })
})
