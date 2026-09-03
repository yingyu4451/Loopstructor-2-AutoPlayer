import { createPinia, setActivePinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TitleBar from '../src/components/TitleBar.vue'
import { useAppStore } from '../src/stores/app'
import type { DesktopApi, HostSnapshot } from '../src/types'

describe('title bar update action', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('exposes an available release as a directly clickable update button', async () => {
    window.loopstructorDesktop = {
      showSystemMenu: vi.fn(),
      minimize: vi.fn(),
      toggleMaximize: vi.fn(),
      close: vi.fn(),
    } as unknown as DesktopApi
    const store = useAppStore()
    store.snapshot = snapshot()
    const install = vi.spyOn(store, 'installUpdate').mockResolvedValue(undefined)
    const wrapper = mount(TitleBar)

    await wrapper.get('button[aria-label="安装可用更新"]').trigger('click')

    expect(install).toHaveBeenCalledOnce()
  })
})

function snapshot(): HostSnapshot {
  return {
    protocolVersion: 1,
    version: '0.6.65',
    settings: {
      gameRoot: '', profileName: 'Default', continueExistingProfile: false, gameMode: 'normal',
      overrideGameSpeed: false, speedState: 0, maxRunMinutes: 60, skipStory: false,
      decisionPriority: 0, uiScaleMode: 'system', customUiScalePercent: 100, characterCfgIndex: 0,
      activeRoute: 'settings', sidebarCollapsed: false, skinId: 'skyspine', gitHubOwner: 'yingyu4451',
      gitHubRepository: 'Loopstructor-2-QA-Tool',
    },
    connection: { trusted: false, label: '等待游戏连接', reason: '', cheatAvailable: false, autoplayActive: false },
    update: {
      success: true,
      updateAvailable: true,
      currentVersion: '0.6.65',
      latestVersion: '0.6.65',
      message: '发现新版本',
    },
    logs: [],
  }
}
