import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import SavesPage from '../src/pages/SavesPage.vue'
import { useAppStore } from '../src/stores/app'
import type { DesktopApi, HostSnapshot, SaveBackupCatalog } from '../src/types'

describe('save archive page', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('lists every managed backup with a restore action', async () => {
    const catalog: SaveBackupCatalog = {
      status: {
        enabled: true, maximumBackups: 20, backupCount: 2, backupRoot: 'C:\\Backups',
        latestBackup: 'second', lastMessage: 'ready', pending: false, busy: false,
      },
      backups: [
        { id: '第02章-第004关-20260901-130000', chapter: 2, level: 4, createdAt: '2026-09-01T13:00:00+08:00', fileCount: 5, totalBytes: 2048, isLatest: true },
        { id: '第01章-第003关-20260901-120000', chapter: 1, level: 3, createdAt: '2026-09-01T12:00:00+08:00', fileCount: 4, totalBytes: 1024, isLatest: false },
      ],
    }
    window.loopstructorDesktop = {
      listSaveBackups: vi.fn(async () => catalog),
      openSaveBackups: vi.fn(),
    } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    const wrapper = mount(SavesPage, { global: { directives: { tooltip: () => undefined } } })
    await flushPromises()

    expect(wrapper.text()).toContain('第 2 章 · 第 4 关')
    expect(wrapper.text()).toContain('第 1 章 · 第 3 关')
    expect(wrapper.findAll('.save-step-node')).toHaveLength(0)
    expect(wrapper.findAll('.restore-save-button')).toHaveLength(2)
  })
})

function snapshot(): HostSnapshot {
  return {
    protocolVersion: 1,
    version: '0.6.69',
    settings: {
      gameRoot: '', profileName: 'Default', continueExistingProfile: false, gameMode: 'normal',
      overrideGameSpeed: false, speedState: 0, maxRunMinutes: 60, skipStory: false,
      decisionPriority: 0, uiScaleMode: 'system', customUiScalePercent: 100, characterCfgIndex: 0,
      automaticSaveBackupEnabled: true, maximumSaveBackups: 20,
      activeRoute: 'saves', sidebarCollapsed: false, skinId: 'skyspine', gitHubOwner: 'yingyu4451',
      gitHubRepository: 'Loopstructor-2-QA-Tool',
    },
    connection: { trusted: false, label: '等待游戏连接', reason: '', cheatAvailable: false, autoplayActive: false },
    logs: [],
  }
}
