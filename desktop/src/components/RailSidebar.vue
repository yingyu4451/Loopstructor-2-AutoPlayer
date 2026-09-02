<script setup lang="ts">
import {
  Gamepad2, Bot, TrainFront, PackageOpen, Gem, Swords, ScanSearch, BugPlay,
  Activity, Settings, ChevronLeft, ChevronRight, ArchiveClock,
} from '../icons'
import type { Component } from 'vue'
import type { RouteKey } from '../types'
import { useAppStore } from '../stores/app'

const store = useAppStore()
const api = window.loopstructorDesktop
const groups: Array<{ label: string; items: Array<{ key: RouteKey; label: string; icon: Component }> }> = [
  { label: '系统', items: [
    { key: 'game', label: '游戏与插件', icon: Gamepad2 },
    { key: 'saves', label: '存档', icon: ArchiveClock },
    { key: 'autoplay', label: '自动游玩', icon: Bot },
  ] },
  { label: '作弊', items: [
    { key: 'vehicles', label: '战车', icon: TrainFront },
    { key: 'items', label: '道具', icon: PackageOpen },
    { key: 'relics', label: '遗物', icon: Gem },
    { key: 'battle', label: '战斗', icon: Swords },
    { key: 'objects', label: '对象属性', icon: ScanSearch },
    { key: 'spawn', label: '生成', icon: BugPlay },
  ] },
  { label: '诊断', items: [{ key: 'diagnostics', label: '日志与状态', icon: Activity }] },
  { label: '设置', items: [{ key: 'settings', label: '界面与更新', icon: Settings }] },
]

async function toggle() {
  if (!store.snapshot) return
  store.snapshot.settings.sidebarCollapsed = !store.snapshot.settings.sidebarCollapsed
  await api.saveSettings(store.snapshot.settings)
}
</script>

<template>
  <aside class="rail-sidebar" :class="{ collapsed: store.settings?.sidebarCollapsed }">
    <div class="rail-line" aria-hidden="true" />
    <nav aria-label="工具导航">
      <section v-for="group in groups" :key="group.label" class="nav-group">
        <h2>{{ group.label }}</h2>
        <button
          v-for="item in group.items"
          :key="item.key"
          class="nav-item"
          :class="{ active: store.route === item.key }"
          :aria-label="item.label"
          :aria-current="store.route === item.key ? 'page' : undefined"
          @click="store.setRoute(item.key)"
        >
          <span class="rail-node"><span class="rail-cog" aria-hidden="true" /><component :is="item.icon" class="rail-symbol" :size="18" /></span>
          <span class="nav-label">{{ item.label }}</span>
          <span v-if="item.key === 'autoplay'" class="tiny-status">尚未完成</span>
        </button>
      </section>
    </nav>
    <button class="collapse-button" :aria-label="store.settings?.sidebarCollapsed ? '展开侧栏' : '收起侧栏'" @click="toggle">
      <ChevronRight v-if="store.settings?.sidebarCollapsed" :size="18" />
      <ChevronLeft v-else :size="18" />
    </button>
  </aside>
</template>
