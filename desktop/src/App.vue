<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, watch } from 'vue'
import { gsap } from 'gsap'
import TitleBar from './components/TitleBar.vue'
import RailSidebar from './components/RailSidebar.vue'
import AppToast from './components/AppToast.vue'
import AppModal from './components/AppModal.vue'
import GamePage from './pages/GamePage.vue'
import SavesPage from './pages/SavesPage.vue'
import AutoplayPage from './pages/AutoplayPage.vue'
import VehiclesPage from './pages/VehiclesPage.vue'
import ItemsPage from './pages/ItemsPage.vue'
import RelicsPage from './pages/RelicsPage.vue'
import BattlePage from './pages/BattlePage.vue'
import ObjectsPage from './pages/ObjectsPage.vue'
import SpawnPage from './pages/SpawnPage.vue'
import DiagnosticsPage from './pages/DiagnosticsPage.vue'
import SettingsPage from './pages/SettingsPage.vue'
import UpdaterPage from './pages/UpdaterPage.vue'
import stationArt from './assets/skins/skyspine/station.png'
import { useAppStore } from './stores/app'
import { useUiStore } from './stores/ui'

const store = useAppStore()
const ui = useUiStore()
const updaterMode = window.loopstructorDesktop.isUpdater === true
const pages = {
  game: GamePage, saves: SavesPage, autoplay: AutoplayPage, vehicles: VehiclesPage, items: ItemsPage,
  relics: RelicsPage, battle: BattlePage, objects: ObjectsPage, spawn: SpawnPage,
  diagnostics: DiagnosticsPage, settings: SettingsPage,
}
const pageTitles: Record<keyof typeof pages, string> = {
  game: '主控 · 游戏与插件', saves: '档案 · 存档保险库', autoplay: '策略 · 自动游玩', vehicles: '工坊 · 战车装配', items: '仓储 · 道具与弹射点',
  relics: '藏品 · 遗物目录', battle: '指挥 · 战斗控制', objects: '校准 · 对象属性', spawn: '实验 · 怪物生成',
  diagnostics: '监测 · 日志与状态', settings: '维护 · 界面与更新',
}
const activePage = computed(() => pages[store.route])
const activeTitle = computed(() => pageTitles[store.route])
let pageTween: gsap.core.Tween | undefined
let busyTween: gsap.core.Tween | undefined

function prefersReducedMotion(): boolean {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
}

async function animatePage(): Promise<void> {
  await nextTick()
  const target = document.querySelector('.page-host > *')
  if (!target || prefersReducedMotion()) return
  pageTween?.kill()
  pageTween = gsap.fromTo(target, { opacity: 0, y: 8 }, {
    opacity: 1,
    y: 0,
    duration: 0.24,
    ease: 'power2.out',
    clearProps: 'transform',
  })
  gsap.to('.title-cog, .nav-item.active .rail-cog', {
    rotation: '+=45',
    duration: 0.34,
    stagger: 0.035,
    ease: 'power2.out',
  })
}

watch(() => store.route, animatePage)
watch(() => ui.busy, (busy) => {
  if (prefersReducedMotion()) return
  busyTween?.kill()
  busyTween = busy
    ? gsap.fromTo('.busy-indicator', { opacity: 0, y: 6 }, { opacity: 1, y: 0, duration: 0.2, ease: 'power2.out' })
    : gsap.to('.busy-indicator', { opacity: 0, y: 6, duration: 0.16, ease: 'power1.in' })
})

onMounted(async () => {
  if (updaterMode) return
  await store.initialize()
  const settings = store.settings
  if (settings?.uiScaleMode === 'custom') await window.loopstructorDesktop.setZoom(settings.customUiScalePercent / 100)
})
onBeforeUnmount(() => {
  store.removeHostEvent?.()
  pageTween?.kill()
  busyTween?.kill()
})
</script>

<template>
  <div class="app-shell" :data-skin="store.settings?.skinId ?? 'skyspine'" :data-route="store.route" :class="{ 'updater-mode': updaterMode }">
    <TitleBar :updater-mode="updaterMode" />
    <UpdaterPage v-if="updaterMode" />
    <div v-else class="app-body" :class="{ 'sidebar-collapsed': store.settings?.sidebarCollapsed }">
      <RailSidebar />
      <main class="content-shell">
        <img :src="stationArt" alt="" width="256" height="256" class="workspace-station" aria-hidden="true" />
        <div class="workbench-caption" aria-hidden="true">{{ activeTitle }}</div>
        <div class="page-host">
          <component :is="activePage" />
        </div>
      </main>
    </div>
    <AppToast />
    <AppModal />
    <div v-if="ui.busy" class="busy-indicator" aria-label="正在处理"><span /><span /><span /></div>
  </div>
</template>
